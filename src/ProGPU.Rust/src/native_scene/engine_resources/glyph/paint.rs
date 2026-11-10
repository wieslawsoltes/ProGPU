//! Original hinted-paint view/sampler selection; actual external owners survive
//! consumer submission. Nested picture textures are never inferred here.
use super::*;
use crate::native_scene::engine_resources::{
    NativeExternalImages, NativeImageTexture, native_image_sampler,
};
#[derive(Clone, Debug)]
pub struct NativeGlyphPaintTextureBinding {
    pub kind: u32,
    pub image: NativeImageTexture,
    pub sampler: wgpu::Sampler,
    pub binding: wgpu::BindGroup,
}
pub struct NativeGlyphPaintBindings {
    page: Arc<PreparedNativeGlyphPage>,
    external: NativeExternalImages,
    bindings: Arc<[wgpu::BindGroup]>,
    textures: Arc<[NativeGlyphPaintTextureBinding]>,
    layout: wgpu::BindGroupLayout,
}
impl NativeGlyphPaintBindings {
    pub fn preflight(
        device: &wgpu::Device,
        page: &PreparedNativeGlyphPage,
        external: &NativeExternalImages,
        sentinel: &NativeImageTexture,
    ) -> Result<(), Error> {
        if page.device() != device {
            return Err(Error::ResourceDomain);
        }
        Self::preflight_source(device, page.page(), external, sentinel)
    }
    /// Complete paint admission before the glyph atlas or any bind group is created.
    pub fn preflight_source(
        device: &wgpu::Device,
        page: &NativeGlyphRasterPage,
        external: &NativeExternalImages,
        sentinel: &NativeImageTexture,
    ) -> Result<(), Error> {
        if sentinel.device() != device {
            return Err(Error::ResourceDomain);
        }
        Self::preflight_metadata(device, page, external)
    }
    /// Whole original external-paint inventory, independent of the engine's
    /// one-texel sentinel allocation. Used by aggregate page preflight.
    pub fn preflight_metadata(
        device: &wgpu::Device,
        page: &NativeGlyphRasterPage,
        external: &NativeExternalImages,
    ) -> Result<(), Error> {
        let source = page.source();
        if external.device() != device || source.external_images() != external.metadata() {
            return Err(Error::ResourceDomain);
        }
        if source.paints().is_empty() {
            return Ok(());
        }
        let limits = device.limits();
        let required = source.paints().len() as u64 * 96;
        if limits.max_bind_groups < 4
            || limits.max_storage_buffers_per_shader_stage < 3
            || limits.max_vertex_attributes < 9
            || limits.max_vertex_buffer_array_stride < 96
            || limits.max_sampled_textures_per_shader_stage < 4
            || limits.max_samplers_per_shader_stage < 3
            || limits.max_texture_dimension_2d == 0
        {
            return Err(Error::UnsupportedExecution);
        }
        if required > limits.max_storage_buffer_binding_size
            || required > limits.max_buffer_size
            || source.materials().brushes().len() as u64 * 256
                > limits.max_storage_buffer_binding_size
            || source.materials().gradient_stops().len() as u64 * 32
                > limits.max_storage_buffer_binding_size
        {
            return Err(Error::Capacity);
        }
        for (paint, resource) in source.paints().iter().zip(source.paint_resources()) {
            if paint.kind == SCENE_GLYPH_PAINT_TEXTURE {
                let binding = external
                    .find(
                        resource.resource_id,
                        resource.generation,
                        NativeImageRole::Primary,
                    )
                    .ok_or(Error::InvalidInput)?;
                if binding
                    .image
                    .size()
                    .into_iter()
                    .any(|n| n == 0 || n > limits.max_texture_dimension_2d)
                {
                    return Err(Error::Capacity);
                }
            }
        }
        Ok(())
    }
    pub async fn prepare(
        device: &wgpu::Device,
        page: Arc<PreparedNativeGlyphPage>,
        external: &NativeExternalImages,
        texture_layout: &wgpu::BindGroupLayout,
        sentinel: &NativeImageTexture,
        sentinel_sampler: &wgpu::Sampler,
    ) -> Result<Arc<Self>, Error> {
        Self::preflight(device, &page, external, sentinel)?;
        let source = page.page().source();
        let mut bindings = allocate(source.paints().len())?;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        for (paint, resource) in source.paints().iter().zip(source.paint_resources()) {
            let (image, sampler) = if paint.kind == SCENE_GLYPH_PAINT_TEXTURE {
                let image = external
                    .find(
                        resource.resource_id,
                        resource.generation,
                        NativeImageRole::Primary,
                    )
                    .ok_or(Error::InvalidInput)?
                    .image
                    .clone();
                let mode = (paint.flags & SCENE_GLYPH_PAINT_SAMPLING_MASK) >> 8;
                let sampling = if mode == 0 {
                    IMAGE_SAMPLING_LINEAR
                } else if mode == 1 {
                    IMAGE_SAMPLING_NEAREST
                } else {
                    mode
                };
                let flags = (paint.sampling[2] as u32) << SCENE_IMAGE_ADDRESS_U_SHIFT
                    | (paint.sampling[3] as u32) << SCENE_IMAGE_ADDRESS_V_SHIFT;
                let sampler = native_image_sampler(
                    device,
                    &wire::ImageDraw {
                        sampling,
                        max_anisotropy: 1,
                        flags,
                        ..Default::default()
                    },
                )?;
                (image, sampler)
            } else {
                (sentinel.clone(), sentinel_sampler.clone())
            };
            let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU native original glyph paint texture"),
                layout: texture_layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(&sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(image.view()),
                    },
                ],
            });
            bindings.push(NativeGlyphPaintTextureBinding {
                kind: paint.kind,
                image,
                sampler,
                binding,
            });
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let mut groups = allocate(bindings.len())?;
        for binding in &bindings {
            groups.push(binding.binding.clone());
        }
        Ok(Arc::new(Self {
            page,
            external: external.clone(),
            bindings: groups.into(),
            textures: bindings.into(),
            layout: texture_layout.clone(),
        }))
    }
    pub fn page(&self) -> &Arc<PreparedNativeGlyphPage> {
        &self.page
    }
    pub fn device(&self) -> &wgpu::Device {
        self.page.device()
    }
    pub fn bindings(&self) -> &[wgpu::BindGroup] {
        &self.bindings
    }
    pub fn textures(&self) -> &[NativeGlyphPaintTextureBinding] {
        &self.textures
    }
    pub fn layout(&self) -> &wgpu::BindGroupLayout {
        &self.layout
    }
    /// Exact complete source bindings, including actual selected view identities.
    pub fn external_images(&self) -> &NativeExternalImages {
        &self.external
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
