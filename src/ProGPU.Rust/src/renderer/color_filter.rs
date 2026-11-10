//! Original retained color-filter captures and child-before-parent GPU execution.
use super::*;
use crate::{
    image::{
        ColorFilterComputeEngine, ColorFilterOperation, ColorFilterPlan, ColorFilteredImageDraw,
        GpuImage, ImageAlpha, ImageDraw, ImageFormat, ImageSource, PreparedColorFilterCompute,
    },
    image_effect::{ImageEffectDraw, ImageEffectUniforms},
};

/// Actual graph resources and work per replay, including original capture passes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ColorFilterMetrics {
    /// Final retained filtered-image draws.
    pub draws: u32,
    /// Original lowered filter operations, after exact immutable-node sharing.
    pub operations: u32,
    /// Full-source normalization, matrix/luma and constant-brush capture passes.
    pub render_passes: u32,
    /// Original table/nonlinear/overdraw/blend/arithmetic passes.
    pub compute_passes: u32,
    /// Original linear mip passes after completed filter evaluation.
    pub mip_passes: u32,
    /// Actual newly owned intermediate and mip-chain texel storage.
    pub texture_bytes: u64,
    /// Immutable parameter and capture/final frame payloads.
    pub uniform_bytes: u64,
    /// Actual capture and final draw vertex/index payloads.
    pub geometry_bytes: u64,
}
impl ColorFilterMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        macro_rules! add { ($($field:ident),*) => {$(self.$field = self.$field.checked_add(other.$field).ok_or(Error::Capacity)?;)*}; }
        add!(
            draws,
            operations,
            render_passes,
            compute_passes,
            mip_passes,
            texture_bytes,
            uniform_bytes,
            geometry_bytes
        );
        Ok(())
    }
}
#[derive(Clone)]
pub(super) struct DrawResources {
    pipeline: wgpu::RenderPipeline,
    groups: Vec<wgpu::BindGroup>,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    index_count: u32,
}
impl DrawResources {
    pub(super) fn draw_masked(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        pipeline: &wgpu::RenderPipeline,
        mask: &wgpu::BindGroup,
    ) {
        pass.set_pipeline(pipeline);
        for (index, group) in self.groups.iter().enumerate() {
            pass.set_bind_group(index as u32, group, &[]);
        }
        pass.set_bind_group(2, mask, &[]);
        pass.set_vertex_buffer(0, self.vertices.slice(..));
        pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..self.index_count, 0, 0..1);
    }
    pub(super) fn draw(&self, pass: &mut wgpu::RenderPass<'_>) {
        pass.set_pipeline(&self.pipeline);
        for (index, group) in self.groups.iter().enumerate() {
            pass.set_bind_group(index as u32, group, &[]);
        }
        pass.set_vertex_buffer(0, self.vertices.slice(..));
        pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..self.index_count, 0, 0..1);
    }
}
pub(super) struct Capture {
    draw: DrawResources,
    pub(super) output: GpuImage,
    view: wgpu::TextureView,
}
impl Capture {
    pub(super) fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ProGPU original full-source color-filter capture"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &self.view,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            ..Default::default()
        });
        self.draw.draw(&mut pass);
    }
}
enum FilterPass {
    Render(Capture),
    Compute(PreparedColorFilterCompute),
}
impl FilterPass {
    pub(super) fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        match self {
            Self::Render(p) => p.encode(encoder),
            Self::Compute(p) => p.encode_owned(encoder),
        }
    }
    pub(super) fn output(&self) -> &GpuImage {
        match self {
            Self::Render(p) => &p.output,
            Self::Compute(p) => p.output(),
        }
    }
}
struct MipPass {
    source: wgpu::BindGroup,
    target: wgpu::TextureView,
}
pub(super) struct Mips {
    input: GpuImage,
    pub(super) output: GpuImage,
    pipeline: wgpu::RenderPipeline,
    passes: Vec<MipPass>,
}
impl Mips {
    pub(super) fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        encoder.copy_texture_to_texture(
            wgpu::TexelCopyTextureInfo {
                texture: self.input.texture(),
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyTextureInfo {
                texture: self.output.texture(),
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            self.input.texture().size(),
        );
        for level in &self.passes {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU original filtered-image mip"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &level.target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &level.source, &[]);
            pass.draw(0..3, 0..1);
        }
    }
}
struct FilteredDraw {
    _source: ColorFilteredImageDraw,
    _input: GpuImage,
    _plan: ColorFilterPlan,
    passes: Vec<FilterPass>,
    mips: Option<Mips>,
    draw: DrawResources,
    masked_pipeline: wgpu::RenderPipeline,
}
pub(super) struct PreparedColorFilteredImages {
    draws: Vec<FilteredDraw>,
    metrics: ColorFilterMetrics,
}
impl PreparedColorFilteredImages {
    pub(super) fn encode_sources(&self, encoder: &mut wgpu::CommandEncoder) -> Result<(), Error> {
        for draw in &self.draws {
            for pass in &draw.passes {
                pass.encode(encoder);
            }
            if let Some(mips) = &draw.mips {
                mips.encode(encoder);
            }
        }
        Ok(())
    }
    pub(super) fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        index: usize,
        mask: Option<&wgpu::BindGroup>,
    ) -> Result<(), Error> {
        let draw = self.draws.get(index).ok_or(Error::ResourceDomain)?;
        if let Some(mask) = mask {
            pass.set_pipeline(&draw.masked_pipeline);
            for (index, group) in draw.draw.groups.iter().enumerate() {
                pass.set_bind_group(index as u32, group, &[]);
            }
            pass.set_bind_group(2, mask, &[]);
            pass.set_vertex_buffer(0, draw.draw.vertices.slice(..));
            pass.set_index_buffer(draw.draw.indices.slice(..), wgpu::IndexFormat::Uint32);
            pass.draw_indexed(0..draw.draw.index_count, 0, 0..1);
        } else {
            draw.draw.draw(pass);
        }
        Ok(())
    }
}
pub(super) struct ColorFilterState {
    compute: ColorFilterComputeEngine,
    copy: Option<wgpu::RenderPipeline>,
    matrix: Option<wgpu::RenderPipeline>,
    solid: Option<wgpu::RenderPipeline>,
    final_pipelines: [Option<wgpu::RenderPipeline>; 2],
    mip: Option<wgpu::RenderPipeline>,
}
impl ColorFilterState {
    fn new(device: &wgpu::Device, preference: ExecutionPreference) -> Result<Self, Error> {
        Ok(Self {
            compute: ColorFilterComputeEngine::new(device, preference)?,
            copy: None,
            matrix: None,
            solid: None,
            final_pipelines: [None, None],
            mip: None,
        })
    }
}
impl PreparedScene {
    /// Original filter-graph allocations and per-replay work for this target.
    pub fn color_filter_metrics(&self) -> ColorFilterMetrics {
        self.color_filters
            .as_ref()
            .map_or_else(Default::default, |f| f.metrics)
    }
}
pub(super) fn source_needs_capture(source: &ImageSource) -> bool {
    source.alpha() != ImageAlpha::Premultiplied
        || match source {
            ImageSource::Pixels(i) => i.format() == ImageFormat::R8,
            ImageSource::Gpu(i) => i.texture().format() == wgpu::TextureFormat::R8Unorm,
        }
}
pub(super) fn mip_storage(mut size: [u32; 2]) -> (u64, u32) {
    let mut bytes = 0;
    let mut levels = 0;
    loop {
        bytes += u64::from(size[0]) * u64::from(size[1]) * 4;
        levels += 1;
        if size == [1, 1] {
            break;
        }
        size = size.map(|v| (v / 2).max(1));
    }
    (bytes, levels)
}
impl Renderer {
    /// Select the original compute-node policy before first filter preparation.
    /// Matrix/luma/brush captures retain their independent original render route;
    /// unsupported forced image-compute routes reject without a CPU fallback.
    pub fn configure_color_filters(
        &mut self,
        preference: ExecutionPreference,
    ) -> Result<(), Error> {
        if self.color_filters.is_some() {
            return Err(Error::ResourceDomain);
        }
        self.color_filters = Some(ColorFilterState::new(&self.device, preference)?);
        Ok(())
    }
    fn color_filter_cost(&self, draw: &ColorFilteredImageDraw) -> Result<(u64, u32), Error> {
        draw.filter.preflight_source_devices(&self.device)?;
        draw.draw.validate()?;
        let plan = draw.plan()?;
        let size = draw.draw.image.size();
        let limits = self.device.limits();
        if size
            .iter()
            .any(|v| *v == 0 || *v > limits.max_texture_dimension_2d)
        {
            return Err(Error::Capacity);
        }
        if let ImageSource::Gpu(i) = &draw.draw.image {
            if i.device() != &self.device {
                return Err(Error::ResourceDomain);
            }
        }
        if limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_textures_per_shader_stage < 1
            || limits.max_sampled_textures_per_shader_stage < 3
            || limits.max_storage_buffers_per_shader_stage < 1
            || limits.max_uniform_buffers_per_shader_stage < 4
            || limits.max_bind_groups < 4
            || limits.max_uniform_buffer_binding_size < 288
            || limits.max_buffer_size < 4096
            || limits.max_storage_buffer_binding_size < 4096
        {
            return Err(Error::UnsupportedExecution);
        }
        let mut passes = u32::try_from(plan.steps().len()).map_err(|_| Error::Capacity)?;
        for step in plan.steps() {
            if matches!(step.operation, ColorFilterOperation::Blend { .. }) {
                passes = passes.checked_add(1).ok_or(Error::Capacity)?;
            }
        }
        passes = passes
            .checked_add(u32::from(source_needs_capture(&draw.draw.image)))
            .ok_or(Error::Capacity)?;
        let bytes = u64::from(size[0]) * u64::from(size[1]) * 4;
        let mut total = bytes
            .checked_mul(u64::from(passes))
            .ok_or(Error::Capacity)?;
        if draw.draw.sampling.uses_mipmaps() {
            let (storage, levels) = mip_storage(size);
            total = total.checked_add(storage).ok_or(Error::Capacity)?;
            passes = passes.checked_add(levels - 1).ok_or(Error::Capacity)?;
        }
        if total > 512 * 1024 * 1024 || passes > 16384 {
            return Err(Error::Capacity);
        }
        Ok((total, passes))
    }
    pub(super) fn preflight_color_filters(&self, scene: &Scene) -> Result<(u64, u32), Error> {
        let mut total = (0u64, 0u32);
        for draw in scene.color_filtered_images() {
            let cost = self.color_filter_cost(draw)?;
            total.0 = total.0.checked_add(cost.0).ok_or(Error::Capacity)?;
            total.1 = total.1.checked_add(cost.1).ok_or(Error::Capacity)?;
        }
        Ok(total)
    }
    pub(super) fn validate_color_filters(
        &self,
        compiled: &CompiledScene,
    ) -> Result<(u64, u32), Error> {
        let mut total = (0u64, 0u32);
        for matrix in &compiled.image_colors {
            matrix.validate_native()?;
        }
        for draw in &compiled.color_filtered_images {
            let mesh = draw
                .draw
                .compile(compiled.dpi_scale(), compiled.image_sampling)?;
            let vertex_bytes = std::mem::size_of_val(mesh.vertices()) as u64;
            let index_bytes = std::mem::size_of_val(mesh.indices()) as u64;
            if vertex_bytes > self.device.limits().max_buffer_size
                || index_bytes > self.device.limits().max_buffer_size
            {
                return Err(Error::Capacity);
            }
            if bytemuck::cast_slice::<_, f32>(mesh.vertices())
                .iter()
                .any(|v| !v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
            let cost = self.color_filter_cost(draw)?;
            total.0 = total.0.checked_add(cost.0).ok_or(Error::Capacity)?;
            total.1 = total.1.checked_add(cost.1).ok_or(Error::Capacity)?;
        }
        Ok(total)
    }
    pub(super) async fn prepare_color_filters(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.color_filtered_images.is_empty() {
            return Ok(());
        }
        self.validate_color_filters(compiled)?;
        if self.image_sampling != Some(compiled.image_sampling) {
            return Err(Error::UnsupportedExecution);
        }
        let mut state = match self.color_filters.take() {
            Some(s) => s,
            None => ColorFilterState::new(&self.device, ExecutionPreference::Automatic)?,
        };
        let mut images = self.images.take().unwrap_or_default();
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self
            .build_color_filters(&mut state, &mut images, compiled, frame, prepared)
            .await;
        let allocation_error = scope.pop().await;
        // Retain actual initialized domains even when an unpublished candidate fails.
        self.color_filters = Some(state);
        self.images = Some(images);
        if let Some(error) = allocation_error {
            return Err(error.into());
        }
        result
    }
    async fn build_color_filters(
        &self,
        state: &mut ColorFilterState,
        images: &mut image::ImageState,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        for masked in [false, true] {
            let slot = usize::from(masked);
            if state.final_pipelines[slot].is_none() {
                state.final_pipelines[slot] = Some(
                    image::pipeline(
                        &self.device,
                        self.format,
                        false,
                        crate::scene::LayerBlend::SrcOver,
                        masked,
                    )
                    .await?,
                );
            }
        }
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(compiled.color_filtered_images.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = ColorFilterMetrics::default();
        let mut source_textures = std::collections::HashSet::new();
        for source in &compiled.color_filtered_images {
            let plan = source.plan()?;
            let input = match &source.draw.image {
                ImageSource::Gpu(i) => i.clone(),
                ImageSource::Pixels(i) => {
                    let (image, bytes, passes) =
                        images.upload(&self.device, &self.queue, i, false).await?;
                    prepared.metrics.image_upload_bytes += bytes;
                    prepared.metrics.image_mip_passes += passes;
                    image
                }
            };
            let size = input.size();
            if source_textures.insert(input.identity()) {
                let bytes_per_pixel = if input.texture().format() == wgpu::TextureFormat::R8Unorm {
                    1
                } else {
                    4
                };
                prepared.metrics.image_texture_bytes +=
                    u64::from(size[0]) * u64::from(size[1]) * bytes_per_pixel;
            }
            let local_frame = Frame {
                width: size[0],
                height: size[1],
                dpi_scale: 1.0,
            };
            let mut passes = Vec::new();
            passes
                .try_reserve_exact(plan.steps().len() * 2 + 1)
                .map_err(|_| Error::Capacity)?;
            let mut results = Vec::new();
            results
                .try_reserve_exact(plan.steps().len() + 1)
                .map_err(|_| Error::Capacity)?;
            if source_needs_capture(&source.draw.image) {
                if state.copy.is_none() {
                    state.copy = Some(
                        image::pipeline(
                            &self.device,
                            wgpu::TextureFormat::Rgba8Unorm,
                            false,
                            crate::scene::LayerBlend::SrcOver,
                            false,
                        )
                        .await?,
                    );
                }
                let draw =
                    ImageDraw::new(input.clone(), [0.0, 0.0, size[0] as f32, size[1] as f32]);
                let capture = prepare_capture_image(
                    &self.device,
                    images,
                    state.copy.as_ref().ok_or(Error::ResourceDomain)?,
                    &draw,
                    local_frame,
                    compiled.image_sampling,
                    &mut metrics,
                )?;
                results.push(capture.output.clone());
                passes.push(FilterPass::Render(capture));
            } else {
                results.push(input.clone());
            }
            for step in plan.steps() {
                let background = results
                    .get(step.inputs[0] as usize)
                    .ok_or(Error::ResourceDomain)?
                    .clone();
                let pass = match &step.operation {
                    ColorFilterOperation::Matrix(matrix) => {
                        if state.matrix.is_none() {
                            state.matrix = Some(matrix_pipeline(&self.device).await?);
                        }
                        FilterPass::Render(prepare_matrix(
                            &self.device,
                            state.matrix.as_ref().ok_or(Error::ResourceDomain)?,
                            &background,
                            *matrix,
                            local_frame,
                            &mut metrics,
                        )?)
                    }
                    ColorFilterOperation::Blend { color, .. } => {
                        if state.solid.is_none() {
                            state.solid = Some(solid_pipeline(&self.device).await?);
                        }
                        let foreground = prepare_solid(
                            self,
                            state.solid.as_ref().ok_or(Error::ResourceDomain)?,
                            *color,
                            local_frame,
                            &mut metrics,
                        )?;
                        let compute = state
                            .compute
                            .prepare(&step.operation, &background, Some(&foreground.output))
                            .await?;
                        passes.push(FilterPass::Render(foreground));
                        add_compute_metrics(&mut metrics, &compute)?;
                        FilterPass::Compute(compute)
                    }
                    operation => {
                        let foreground = if matches!(operation, ColorFilterOperation::Lerp { .. }) {
                            Some(
                                results
                                    .get(step.inputs[1] as usize)
                                    .ok_or(Error::ResourceDomain)?,
                            )
                        } else {
                            None
                        };
                        let compute = state
                            .compute
                            .prepare(operation, &background, foreground)
                            .await?;
                        add_compute_metrics(&mut metrics, &compute)?;
                        FilterPass::Compute(compute)
                    }
                };
                results.push(pass.output().clone());
                passes.push(pass);
            }
            let mut output = results
                .get(plan.output() as usize)
                .ok_or(Error::ResourceDomain)?
                .clone();
            let mips = if source.draw.sampling.uses_mipmaps() {
                if state.mip.is_none() {
                    state.mip = Some(
                        image::pipeline(
                            &self.device,
                            wgpu::TextureFormat::Rgba8Unorm,
                            true,
                            crate::scene::LayerBlend::SrcOver,
                            false,
                        )
                        .await?,
                    );
                }
                let mips = prepare_mips(
                    &self.device,
                    state.mip.as_ref().ok_or(Error::ResourceDomain)?,
                    output,
                    &mut metrics,
                )?;
                output = mips.output.clone();
                Some(mips)
            } else {
                None
            };
            let mut final_draw = source.draw.clone();
            final_draw.image = output.into();
            let draw = prepare_image(
                &self.device,
                images,
                state.final_pipelines[0]
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?,
                &final_draw,
                frame,
                compiled.image_sampling,
                &mut metrics,
            )?;
            metrics.draws = metrics.draws.checked_add(1).ok_or(Error::Capacity)?;
            metrics.operations = metrics
                .operations
                .checked_add(plan.steps().len() as u32)
                .ok_or(Error::Capacity)?;
            draws.push(FilteredDraw {
                _source: source.clone(),
                _input: input,
                _plan: plan,
                passes,
                mips,
                draw,
                masked_pipeline: state.final_pipelines[1]
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .clone(),
            });
        }
        prepared.metrics.geometry_bytes = prepared
            .metrics
            .geometry_bytes
            .checked_add(metrics.geometry_bytes)
            .ok_or(Error::Capacity)?;
        prepared.metrics.uniform_bytes = prepared
            .metrics
            .uniform_bytes
            .checked_add(metrics.uniform_bytes)
            .ok_or(Error::Capacity)?;
        prepared.color_filters = Some(PreparedColorFilteredImages { draws, metrics });
        Ok(())
    }
}
pub(super) fn buffer(
    device: &wgpu::Device,
    label: &str,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: if bytes.is_empty() { &[0; 4] } else { bytes },
        usage,
    })
}
pub(super) fn output(
    device: &wgpu::Device,
    size: [u32; 2],
    levels: u32,
) -> Result<(GpuImage, wgpu::TextureView), Error> {
    output_format(device, size, levels, wgpu::TextureFormat::Rgba8Unorm)
}
pub(super) fn output_format(
    device: &wgpu::Device,
    size: [u32; 2],
    levels: u32,
    format: wgpu::TextureFormat,
) -> Result<(GpuImage, wgpu::TextureView), Error> {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU private original filter capture"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: levels,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::RENDER_ATTACHMENT
            | wgpu::TextureUsages::COPY_SRC
            | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    let view = texture.create_view(&wgpu::TextureViewDescriptor {
        base_mip_level: 0,
        mip_level_count: Some(1),
        ..Default::default()
    });
    Ok((
        GpuImage::from_texture(device, &texture, ImageAlpha::Premultiplied)?,
        view,
    ))
}
fn source_binding(
    device: &wgpu::Device,
    images: &mut image::ImageState,
    pipeline: &wgpu::RenderPipeline,
    draw: &ImageDraw,
) -> Result<wgpu::BindGroup, Error> {
    let ImageSource::Gpu(source) = &draw.image else {
        return Err(Error::ResourceDomain);
    };
    let view = source.texture().create_view(&Default::default());
    let sampler = images.sampler(
        device,
        image::SamplerKey {
            mode: draw.sampling,
            anisotropy: draw.max_anisotropy.max(1),
            address: draw.address,
        },
    );
    Ok(device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU retained filtered source"),
        layout: &pipeline.get_bind_group_layout(1),
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(&view),
            },
        ],
    }))
}
pub(super) fn prepare_image(
    device: &wgpu::Device,
    images: &mut image::ImageState,
    pipeline: &wgpu::RenderPipeline,
    draw: &ImageDraw,
    frame: RenderFrame,
    sampling: crate::image::ImageSamplingPath,
    metrics: &mut ColorFilterMetrics,
) -> Result<DrawResources, Error> {
    let mesh = draw.compile(frame.dpi_scale, sampling)?;
    let vertices = buffer(
        device,
        "ProGPU immutable filtered image vertices",
        bytemuck::cast_slice(mesh.vertices()),
        wgpu::BufferUsages::VERTEX,
    );
    let indices = buffer(
        device,
        "ProGPU immutable filtered image indices",
        bytemuck::cast_slice(mesh.indices()),
        wgpu::BufferUsages::INDEX,
    );
    let frame = advanced::frame_binding(
        device,
        pipeline,
        frame.uniforms(device.limits().max_texture_dimension_2d)?,
    );
    let source = source_binding(device, images, pipeline, draw)?;
    metrics.geometry_bytes += vertices.size() + indices.size();
    metrics.uniform_bytes += 224;
    Ok(DrawResources {
        pipeline: pipeline.clone(),
        groups: vec![frame, source],
        vertices,
        indices,
        index_count: u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?,
    })
}
pub(super) fn prepare_capture_image(
    device: &wgpu::Device,
    images: &mut image::ImageState,
    pipeline: &wgpu::RenderPipeline,
    draw: &ImageDraw,
    frame: Frame,
    sampling: crate::image::ImageSamplingPath,
    metrics: &mut ColorFilterMetrics,
) -> Result<Capture, Error> {
    let draw = prepare_image(
        device,
        images,
        pipeline,
        draw,
        frame.into(),
        sampling,
        metrics,
    )?;
    let (output, view) = output(device, [frame.width, frame.height], 1)?;
    metrics.render_passes += 1;
    metrics.texture_bytes += u64::from(frame.width) * u64::from(frame.height) * 4;
    Ok(Capture { draw, output, view })
}
fn add_compute_metrics(
    metrics: &mut ColorFilterMetrics,
    compute: &PreparedColorFilterCompute,
) -> Result<(), Error> {
    let value = compute.metrics();
    metrics.compute_passes = metrics
        .compute_passes
        .checked_add(value.compute_passes)
        .ok_or(Error::Capacity)?;
    metrics.texture_bytes = metrics
        .texture_bytes
        .checked_add(value.texture_bytes)
        .ok_or(Error::Capacity)?;
    metrics.uniform_bytes = metrics
        .uniform_bytes
        .checked_add(value.parameter_bytes)
        .ok_or(Error::Capacity)?;
    Ok(())
}
pub(super) fn prepare_solid(
    renderer: &Renderer,
    pipeline: &wgpu::RenderPipeline,
    color: [u8; 4],
    frame: Frame,
    metrics: &mut ColorFilterMetrics,
) -> Result<Capture, Error> {
    prepare_solid_target(renderer, pipeline, color, frame.into(), metrics)
}
pub(super) fn prepare_solid_target(
    renderer: &Renderer,
    pipeline: &wgpu::RenderPipeline,
    color: [u8; 4],
    frame: RenderFrame,
    metrics: &mut ColorFilterMetrics,
) -> Result<Capture, Error> {
    let mut mesh = Mesh::default();
    mesh.push(
        &crate::geometry::AnalyticPrimitive::rectangle(
            [0.0, 0.0, frame.width as f32, frame.height as f32],
            color.map(|v| f32::from(v) / 255.0),
        ),
        0.5,
    )?;
    let vertices = buffer(
        &renderer.device,
        "ProGPU original solid filter rectangle",
        bytemuck::cast_slice(mesh.vertices()),
        wgpu::BufferUsages::VERTEX,
    );
    let indices = buffer(
        &renderer.device,
        "ProGPU original solid filter indices",
        bytemuck::cast_slice(mesh.indices()),
        wgpu::BufferUsages::INDEX,
    );
    let uniforms = buffer(
        &renderer.device,
        "ProGPU immutable solid filter frame",
        bytemuck::bytes_of(&frame.uniforms(renderer.device.limits().max_texture_dimension_2d)?),
        wgpu::BufferUsages::UNIFORM,
    );
    let bindings = renderer
        .device
        .create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU actual solid brush capture"),
            layout: &pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniforms.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: renderer.brush.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: renderer.stops.as_entire_binding(),
                },
            ],
        });
    let (output, view) = output(&renderer.device, [frame.width, frame.height], 1)?;
    metrics.geometry_bytes += vertices.size() + indices.size();
    metrics.uniform_bytes += 224;
    metrics.render_passes += 1;
    metrics.texture_bytes += u64::from(frame.width) * u64::from(frame.height) * 4;
    Ok(Capture {
        draw: DrawResources {
            pipeline: pipeline.clone(),
            groups: vec![bindings, renderer.atlas.clone()],
            vertices,
            indices,
            index_count: 6,
        },
        output,
        view,
    })
}
pub(super) async fn solid_pipeline(device: &wgpu::Device) -> Result<wgpu::RenderPipeline, Error> {
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU original solid capture vector shader"),
        source: wgpu::ShaderSource::Wgsl(shaders::VECTOR.into()),
    });
    let layout = layout::pipeline(device, layout::Family::Vector, None);
    let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
    let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("ProGPU original full-size solid foreground"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs_main"),
            compilation_options: Default::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: 56,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &attributes,
            })],
        },
        primitive: Default::default(),
        depth_stencil: None,
        multisample: Default::default(),
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some("fs_main_unmasked"),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format: wgpu::TextureFormat::Rgba8Unorm,
                blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        multiview_mask: None,
        cache: None,
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(pipeline)
}
pub(super) async fn matrix_pipeline(device: &wgpu::Device) -> Result<wgpu::RenderPipeline, Error> {
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU original managed filter matrix shader"),
        source: wgpu::ShaderSource::Wgsl(shaders::IMAGE_EFFECT.into()),
    });
    let uniform = |size, visibility| {
        device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original filter capture uniform layout"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: std::num::NonZeroU64::new(size),
                },
                count: None,
            }],
        })
    };
    let frame = uniform(224, wgpu::ShaderStages::VERTEX_FRAGMENT);
    let params = uniform(288, wgpu::ShaderStages::FRAGMENT);
    let texture = |binding| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    };
    let textures = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU original filter capture source pair"),
        entries: &[
            wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count: None,
            },
            texture(1),
            texture(2),
        ],
    });
    let masks = layout::mask(device);
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original managed filter capture layout"),
        bind_group_layouts: &[Some(&frame), Some(&params), Some(&textures), Some(&masks)],
        immediate_size: 0,
    });
    let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2];
    let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("ProGPU original managed matrix/luma capture"),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs_main"),
            compilation_options: Default::default(),
            buffers: &[Some(wgpu::VertexBufferLayout {
                array_stride: 56,
                step_mode: wgpu::VertexStepMode::Vertex,
                attributes: &attributes,
            })],
        },
        primitive: Default::default(),
        depth_stencil: None,
        multisample: Default::default(),
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some("fs_main"),
            compilation_options: Default::default(),
            targets: &[Some(wgpu::ColorTargetState {
                format: wgpu::TextureFormat::Rgba8Unorm,
                blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        multiview_mask: None,
        cache: None,
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(pipeline)
}
pub(super) fn prepare_matrix(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    input: &GpuImage,
    matrix: crate::image::ImageColorMatrix,
    frame: Frame,
    metrics: &mut ColorFilterMetrics,
) -> Result<Capture, Error> {
    prepare_matrix_target(device, pipeline, input, matrix, frame.into(), metrics)
}
pub(super) fn prepare_matrix_target(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    input: &GpuImage,
    matrix: crate::image::ImageColorMatrix,
    frame: RenderFrame,
    metrics: &mut ColorFilterMetrics,
) -> Result<Capture, Error> {
    let draw = ImageEffectDraw::new(
        input.clone(),
        [0.0, 0.0, frame.width as f32, frame.height as f32],
    );
    // Retain the managed neutral effect arithmetic and alpha threshold in its
    // original shader. Texture's direct matrix-then-luma path is independent.
    let mut params = ImageEffectUniforms {
        texture0: [frame.width as f32, frame.height as f32, 1.0, 0.0],
        ..Default::default()
    };
    if matrix.luminance_to_alpha {
        params.flags0[3] = 1.0;
    } else {
        params.flags0[2] = 1.0;
        params.color_matrix_red = matrix.red;
        params.color_matrix_green = matrix.green;
        params.color_matrix_blue = matrix.blue;
        params.color_matrix_alpha = matrix.alpha;
        params.color_matrix_offset = matrix.offset;
    }
    params.validate()?;
    let vertices = buffer(
        device,
        "ProGPU original matrix capture vertices",
        bytemuck::cast_slice(&draw.vertices),
        wgpu::BufferUsages::VERTEX,
    );
    let indices = buffer(
        device,
        "ProGPU original matrix capture indices",
        bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
        wgpu::BufferUsages::INDEX,
    );
    let frame_binding = advanced::frame_binding(
        device,
        pipeline,
        frame.uniforms(device.limits().max_texture_dimension_2d)?,
    );
    let params = buffer(
        device,
        "ProGPU immutable managed matrix parameters",
        bytemuck::bytes_of(&params),
        wgpu::BufferUsages::UNIFORM,
    );
    let params = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU original matrix capture params"),
        layout: &pipeline.get_bind_group_layout(1),
        entries: &[wgpu::BindGroupEntry {
            binding: 0,
            resource: params.as_entire_binding(),
        }],
    });
    let view = input.texture().create_view(&wgpu::TextureViewDescriptor {
        base_mip_level: 0,
        mip_level_count: Some(1),
        ..Default::default()
    });
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("ProGPU original linear filter capture sampler"),
        mag_filter: wgpu::FilterMode::Linear,
        min_filter: wgpu::FilterMode::Linear,
        ..Default::default()
    });
    let textures = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU retained original matrix input"),
        layout: &pipeline.get_bind_group_layout(2),
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(&sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(&view),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::TextureView(&view),
            },
        ],
    });
    let primary = buffer(
        device,
        "ProGPU unused capture mask params",
        &[0; 96],
        wgpu::BufferUsages::UNIFORM,
    );
    let chain = buffer(
        device,
        "ProGPU unused capture mask chain",
        &[0; 288],
        wgpu::BufferUsages::UNIFORM,
    );
    let masks = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU disabled original capture masks"),
        layout: &pipeline.get_bind_group_layout(3),
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Sampler(&sampler),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(&view),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: primary.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: chain.as_entire_binding(),
            },
        ],
    });
    let (output, view) = output(device, [frame.width, frame.height], 1)?;
    metrics.render_passes += 1;
    metrics.geometry_bytes += vertices.size() + indices.size();
    metrics.uniform_bytes += 224 + 288 + 96 + 288;
    metrics.texture_bytes += u64::from(frame.width) * u64::from(frame.height) * 4;
    Ok(Capture {
        draw: DrawResources {
            pipeline: pipeline.clone(),
            groups: vec![frame_binding, params, textures, masks],
            vertices,
            indices,
            index_count: 6,
        },
        output,
        view,
    })
}
pub(super) fn prepare_mips(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    input: GpuImage,
    metrics: &mut ColorFilterMetrics,
) -> Result<Mips, Error> {
    prepare_mips_format(
        device,
        pipeline,
        input,
        wgpu::TextureFormat::Rgba8Unorm,
        metrics,
    )
}
pub(super) fn prepare_mips_format(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    input: GpuImage,
    format: wgpu::TextureFormat,
    metrics: &mut ColorFilterMetrics,
) -> Result<Mips, Error> {
    let (bytes, levels) = mip_storage(input.size());
    let bytes = bytes
        .checked_mul(if format == wgpu::TextureFormat::Rgba16Float {
            2
        } else {
            1
        })
        .ok_or(Error::Capacity)?;
    let (output, _) = output_format(device, input.size(), levels, format)?;
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("ProGPU original filtered-image linear mips"),
        min_filter: wgpu::FilterMode::Linear,
        mag_filter: wgpu::FilterMode::Linear,
        ..Default::default()
    });
    let mut passes = Vec::new();
    passes
        .try_reserve_exact((levels - 1) as usize)
        .map_err(|_| Error::Capacity)?;
    for level in 1..levels {
        let source = output.texture().create_view(&wgpu::TextureViewDescriptor {
            base_mip_level: level - 1,
            mip_level_count: Some(1),
            ..Default::default()
        });
        let target = output.texture().create_view(&wgpu::TextureViewDescriptor {
            base_mip_level: level,
            mip_level_count: Some(1),
            ..Default::default()
        });
        let source = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU owned filtered-image mip source"),
            layout: &pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&source),
                },
            ],
        });
        passes.push(MipPass { source, target });
    }
    metrics.mip_passes += levels - 1;
    metrics.texture_bytes += bytes;
    Ok(Mips {
        input,
        output,
        pipeline: pipeline.clone(),
        passes,
    })
}

impl PreparedColorFilteredImages {
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        for draw in &self.draws {
            draw._input.retain_submitted(queue);
            for pass in &draw.passes {
                if let FilterPass::Compute(p) = pass {
                    p.retain_submitted(queue);
                }
            }
        }
    }
}
