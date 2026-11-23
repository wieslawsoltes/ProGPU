//! Native layer output and independent picture leases for original bytecode effects.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    native_scene::engine_resources::{
        layers::{NativeLayerTarget, NativeLayerTexture},
        masks::NativeMaskTarget,
    },
    path::ExecutionPreference,
    shader_effect::{
        LegacyShaderRequest, PreparedShaderEffect, SampleShaderRequest, ShaderEffectEngine,
        ShaderEffectMetrics,
    },
};
use std::{future::Future, pin::Pin};

/// The actual native nested-picture executor. The same producer/cache may serve
/// ordinary images and shader dependencies; no callback occurs during encoding.
pub trait NativeShaderPictureResolver {
    /// Additional child budget, excluding the required source-format output counted by
    /// the effect plan. This method must not acquire or render a source texture.
    fn preflight(
        &self,
        source: &NativePictureImageSource,
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<NativeImageResourceMetrics, Error>;
    fn prepare<'a>(
        &'a mut self,
        source: NativePictureImageSource,
        device: &'a wgpu::Device,
        queue: &'a wgpu::Queue,
        target_format: wgpu::TextureFormat,
    ) -> Pin<Box<dyn Future<Output = Result<NativeShaderPictureCapture, Error>> + 'a>>;
}
/// A completed exact original physical picture capture in the enclosing native target format. Actual metrics include
/// its output allocation when created; a retained cache hit reports no upload.
pub struct NativeShaderPictureCapture {
    pub texture: NativeImageTexture,
    pub metrics: NativeImageResourceMetrics,
}
/// Preparation inventory; preflight returns a budget, preparation returns actual
/// allocations/uploads. Source descriptor retention is reported independently.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeShaderEffectMetrics {
    pub shader: ShaderEffectMetrics,
    pub pictures: NativeImageResourceMetrics,
    pub picture_requests: u64,
    pub retained_picture_bytes: u64,
    pub legacy_passes: u64,
    pub final_draws: u64,
}
pub struct PreparedNativeShaderEffect {
    plan: Arc<NativeShaderEffectPlan>,
    prepared: Option<PreparedShaderEffect>,
    pictures: Vec<NativeImageTexture>,
    source: Option<NativeLayerTarget>,
    output: Option<NativeLayerTexture>,
    mask: Option<Arc<PreparedNativeMask>>,
    metrics: NativeShaderEffectMetrics,
}
impl PreparedNativeShaderEffect {
    pub fn plan(&self) -> &Arc<NativeShaderEffectPlan> {
        &self.plan
    }
    pub fn suppressed(&self) -> bool {
        self.plan.suppressed()
    }
    pub fn final_sample(&self) -> bool {
        self.plan.final_sample()
    }
    pub fn output(&self) -> Option<&NativeLayerTexture> {
        self.output.as_ref()
    }
    pub fn pictures(&self) -> &[NativeImageTexture] {
        &self.pictures
    }
    pub fn mask(&self) -> Option<&Arc<PreparedNativeMask>> {
        self.mask.as_ref()
    }
    pub fn metrics(&self) -> NativeShaderEffectMetrics {
        self.metrics
    }
    /// Replay adds draws/passes only; it does not repeat immutable uploads or
    /// invoke picture dependencies. Legacy source pixels may change in order.
    pub fn replay_metrics(&self) -> NativeShaderEffectMetrics {
        NativeShaderEffectMetrics {
            shader: ShaderEffectMetrics {
                draws: u32::from(!self.suppressed()),
                ..Default::default()
            },
            legacy_passes: self.metrics.legacy_passes,
            final_draws: self.metrics.final_draws,
            ..Default::default()
        }
    }
    /// Original legacy pass clears and writes the actual source slot effect[0].
    /// It must occur after source drawing and before its layer consumer.
    pub fn encode_legacy(
        &self,
        encoder: &mut wgpu::CommandEncoder,
    ) -> Result<ShaderEffectMetrics, Error> {
        if self.final_sample() {
            return Err(Error::ResourceDomain);
        }
        self.prepared
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .encode_owned(encoder)
    }
    /// Original final-device draw. None selects the original full-parent
    /// viewport/scissor; an explicit actual binary scissor remains caller-owned.
    /// A suppressed draw is an ordered no-op, retaining its source generation.
    pub fn draw_final(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scissor: Option<[u32; 4]>,
    ) -> Result<ShaderEffectMetrics, Error> {
        if !self.final_sample() {
            return Err(Error::ResourceDomain);
        }
        if self.suppressed() {
            return Ok(ShaderEffectMetrics::default());
        }
        self.prepared
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .draw_owned(pass, scissor)
    }
    /// Invoke after the actual queue submission. Source callbacks/leases and
    /// private target generations outlive cache eviction and prepared replay.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        if let Some(p) = &self.prepared {
            p.retain_submitted(queue);
        }
        for picture in &self.pictures {
            picture.retain_submitted(queue);
        }
        if let Some(source) = &self.source {
            if let Some(color) = source.color() {
                color.retain_submitted(queue);
            }
            if let Some(effects) = source.effects() {
                for texture in effects.textures() {
                    texture.retain_submitted(queue);
                }
            }
        }
        if let Some(mask) = &self.mask {
            mask.retain_submitted(queue);
        }
        let plan = self.plan.clone();
        queue.on_submitted_work_done(move || drop(plan));
    }
}
/// One actual native device's original shared program/sampler cache. Prepared
/// bindings are independent immutable owners, including during failed candidates.
pub struct NativeShaderEffectEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    shaders: ShaderEffectEngine,
    format: wgpu::TextureFormat,
}
impl NativeShaderEffectEngine {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_format: wgpu::TextureFormat,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            shaders: ShaderEffectEngine::new(device, queue, target_format, preference)?,
            format: target_format,
        })
    }
    pub fn cached_program_count(&self) -> usize {
        self.shaders.cached_program_count()
    }
    /// Admit all actual target/mask/source metadata before callbacks or GPU
    /// mutation. Layer output allocations are already charged to the layer pool.
    pub fn preflight(
        &self,
        plan: &NativeShaderEffectPlan,
        source: Option<&NativeLayerTarget>,
        mask: Option<&Arc<PreparedNativeMask>>,
        pictures: &impl NativeShaderPictureResolver,
    ) -> Result<NativeShaderEffectMetrics, Error> {
        self.local_preflight(plan, source, mask)?;
        self.preflight_sources(plan, pictures)
    }
    /// Whole-scene device and nested-source budget admission, independent of
    /// layer allocation and mask production. This never acquires a texture.
    pub fn preflight_sources(
        &self,
        plan: &NativeShaderEffectPlan,
        pictures: &impl NativeShaderPictureResolver,
    ) -> Result<NativeShaderEffectMetrics, Error> {
        self.metadata_preflight(plan)?;
        if plan.suppressed() {
            return Ok(Default::default());
        }
        let mut metrics = base_metrics(plan, self.format);
        for picture in plan.pictures() {
            let size = [picture.descriptor.width, picture.descriptor.height];
            self.size(size)?;
            let child = pictures.preflight(picture, &self.device, self.format)?;
            metrics.pictures =
                metrics
                    .pictures
                    .checked_add(child)?
                    .checked_add(NativeImageResourceMetrics {
                        texture_bytes: u64::from(size[0])
                            * u64::from(size[1])
                            * pixel_bytes(self.format),
                        texture_allocations: 1,
                        nested_pictures: 1,
                        ..Default::default()
                    })?;
        }
        admit_budget(metrics)?;
        Ok(metrics)
    }
    /// Atomic immutable binding candidate. Captures execute in original input,
    /// distinct-sampler order. A later failure drops only this candidate's leases.
    pub async fn prepare(
        &mut self,
        plan: Arc<NativeShaderEffectPlan>,
        source: Option<NativeLayerTarget>,
        mask: Option<Arc<PreparedNativeMask>>,
        pictures: &mut impl NativeShaderPictureResolver,
    ) -> Result<PreparedNativeShaderEffect, Error> {
        let budget = self.preflight(&plan, source.as_ref(), mask.as_ref(), pictures)?;
        if plan.suppressed() {
            return Ok(PreparedNativeShaderEffect {
                plan,
                prepared: None,
                pictures: Vec::new(),
                source,
                output: None,
                mask,
                metrics: Default::default(),
            });
        }
        // Freeze the proof before nested preparation; raw sampled images cannot
        // be substituted for this exact native vector-mask producer.
        let vector_mask = mask
            .as_ref()
            .map(|m| {
                self.shaders.native_vector_mask(
                    m.clone(),
                    &plan.replay.scene,
                    mask_target(&plan),
                    plan.target(),
                )
            })
            .transpose()?;
        let mut sources = allocate(plan.pictures.len())?;
        let mut images = allocate(plan.pictures.len())?;
        let mut metrics = base_metrics(&plan, self.format);
        for picture in plan.pictures() {
            let capture = pictures
                .prepare(picture.clone(), &self.device, &self.queue, self.format)
                .await?;
            self.picture(
                &capture.texture,
                [picture.descriptor.width, picture.descriptor.height],
            )?;
            metrics.pictures = metrics.pictures.checked_add(capture.metrics)?;
            admit_budget(metrics)?;
            // An actual producer must not exceed its earlier complete budget.
            if metrics.pictures.texture_bytes > budget.pictures.texture_bytes
                || metrics.pictures.vertex_bytes > budget.pictures.vertex_bytes
                || metrics.pictures.index_bytes > budget.pictures.index_bytes
                || metrics.pictures.uniform_upload_bytes > budget.pictures.uniform_upload_bytes
                || metrics.pictures.texture_upload_bytes > budget.pictures.texture_upload_bytes
            {
                return Err(Error::Capacity);
            }
            images.push(image(&capture.texture)?);
            sources.push(capture.texture);
        }
        let effect = plan.resource.effect();
        let derivative = plan.resource.derivative_register();
        let (prepared, output) = if plan.final_sample() {
            let input = plan.input.ok_or(Error::ResourceDomain)?;
            let sampler = plan.sampler.unwrap_or(input);
            let prepared = self
                .shaders
                .prepare_sample_view(
                    SampleShaderRequest {
                        effect,
                        frame: plan.resource.frame(),
                        target: plan.target(),
                        input: &images[input],
                        sampler: Some(&images[sampler]),
                        derivative,
                        vector_mask: vector_mask.as_ref(),
                    },
                    Some(sources[sampler].view()),
                )
                .await?;
            (prepared, None)
        } else {
            let layer = source.as_ref().ok_or(Error::ResourceDomain)?;
            let input = layer.color().ok_or(Error::ResourceDomain)?;
            let output = &layer.effects().ok_or(Error::ResourceDomain)?.textures()[0];
            let input_image = image(&input.as_image()?)?;
            let output_image = image(&output.as_image()?)?;
            let sampler = plan.sampler.map(|i| &images[i]);
            let source_view = plan.sampler.map_or(input.view(), |i| sources[i].view());
            let prepared = self
                .shaders
                .prepare_legacy_views(
                    LegacyShaderRequest {
                        effect,
                        input: &input_image,
                        sampler,
                        extent: [
                            plan.replay.source_extent.width,
                            plan.replay.source_extent.height,
                        ],
                        derivative,
                    },
                    Some((&output_image, output.view())),
                    Some(source_view),
                )
                .await?;
            (prepared, Some(output.clone()))
        };
        metrics.shader = prepared.metrics();
        Ok(PreparedNativeShaderEffect {
            plan,
            prepared: Some(prepared),
            pictures: sources,
            source,
            output,
            mask,
            metrics,
        })
    }
    fn local_preflight(
        &self,
        plan: &NativeShaderEffectPlan,
        source: Option<&NativeLayerTarget>,
        mask: Option<&Arc<PreparedNativeMask>>,
    ) -> Result<(), Error> {
        self.metadata_preflight(plan)?;
        if plan.suppressed() {
            return Ok(());
        }
        if plan.final_sample() {
            self.size(plan.target().size)?;
            if plan.replay.source_vector_mask != mask.is_some() {
                return Err(Error::ResourceDomain);
            }
            if let Some(mask) = mask {
                if mask.request().resource_index() != plan.replay.source_vector_mask_resource_index
                {
                    return Err(Error::ResourceDomain);
                }
                self.shaders.native_vector_mask(
                    mask.clone(),
                    &plan.replay.scene,
                    mask_target(plan),
                    plan.target(),
                )?;
            }
        } else {
            if mask.is_some() {
                return Err(Error::ResourceDomain);
            }
            let source = source.ok_or(Error::ResourceDomain)?;
            if source.request().target != plan.replay.source {
                return Err(Error::ResourceDomain);
            }
            let input = source.color().ok_or(Error::ResourceDomain)?;
            let output = &source.effects().ok_or(Error::ResourceDomain)?.textures()[0];
            let size = [
                plan.replay.source_extent.width,
                plan.replay.source_extent.height,
            ];
            self.size(size)?;
            for (texture, format) in [
                (input, self.format),
                (output, wgpu::TextureFormat::Rgba8Unorm),
            ] {
                if texture.device() != &self.device
                    || texture.texture().format() != format
                    || texture.texture().mip_level_count() != 1
                    || texture.texture().sample_count() != 1
                    || texture.texture().dimension() != wgpu::TextureDimension::D2
                    || (0..2).any(|i| size[i] > texture.size()[i])
                    || !texture.texture().usage().contains(
                        wgpu::TextureUsages::TEXTURE_BINDING
                            | wgpu::TextureUsages::RENDER_ATTACHMENT,
                    )
                {
                    return Err(Error::ResourceDomain);
                }
            }
            if input.texture() == output.texture() {
                return Err(Error::ResourceDomain);
            }
            plan.resource
                .effect()
                .capture_constants(plan.resource.derivative_register(), size)?;
        }
        Ok(())
    }
    fn metadata_preflight(&self, plan: &NativeShaderEffectPlan) -> Result<(), Error> {
        plan.resource.effect().validate()?;
        if plan.suppressed() {
            return Ok(());
        }
        let limits = self.device.limits();
        let groups = if plan.final_sample() && plan.replay.source_vector_mask {
            2
        } else {
            1
        };
        if plan.actual_uniform_bytes() > u64::from(limits.max_uniform_buffer_binding_size)
            || plan.actual_uniform_bytes() > limits.max_buffer_size
            || limits.max_bind_groups < groups
            || limits.max_sampled_textures_per_shader_stage < groups
            || limits.max_samplers_per_shader_stage < 1
        {
            return Err(Error::Capacity);
        }
        if plan.final_sample() {
            self.size(plan.target().size)?;
        } else {
            let size = [
                plan.replay.source_extent.width,
                plan.replay.source_extent.height,
            ];
            self.size(size)?;
            plan.resource
                .effect()
                .capture_constants(plan.resource.derivative_register(), size)?;
        }
        Ok(())
    }
    fn size(&self, size: [u32; 2]) -> Result<(), Error> {
        if size
            .into_iter()
            .any(|n| n == 0 || n > 16384 || n > self.device.limits().max_texture_dimension_2d)
        {
            Err(Error::Capacity)
        } else {
            Ok(())
        }
    }
    fn picture(&self, picture: &NativeImageTexture, size: [u32; 2]) -> Result<(), Error> {
        self.size(size)?;
        let texture = picture.texture();
        if picture.device() != &self.device
            || picture.size() != size
            || texture.width() != size[0]
            || texture.height() != size[1]
            || texture.depth_or_array_layers() != 1
            || texture.mip_level_count() != 1
            || texture.sample_count() != 1
            || texture.dimension() != wgpu::TextureDimension::D2
            || texture.format() != self.format
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::TEXTURE_BINDING)
        {
            Err(Error::ResourceDomain)
        } else {
            Ok(())
        }
    }
}
fn image(texture: &NativeImageTexture) -> Result<GpuImage, Error> {
    Ok(GpuImage::from_texture(
        texture.device(),
        texture.texture(),
        ImageAlpha::Premultiplied,
    )?
    .with_producer(Arc::new(texture.clone())))
}
fn mask_target(plan: &NativeShaderEffectPlan) -> NativeMaskTarget {
    NativeMaskTarget {
        extent: plan.replay.parent_extent,
        presentation: plan.replay.parent_presentation,
        dpi_scale: plan.replay.dpi_scale,
        source_state_dpi: plan.replay.source_state_dpi,
    }
}
fn base_metrics(
    plan: &NativeShaderEffectPlan,
    format: wgpu::TextureFormat,
) -> NativeShaderEffectMetrics {
    NativeShaderEffectMetrics {
        shader: ShaderEffectMetrics {
            bytecode_bytes: plan.resource.effect().program.bytecode().len() as u64,
            uniform_bytes: plan.actual_uniform_bytes(),
            output_texture_bytes: 0,
            draws: 1,
        },
        picture_requests: plan.pictures.len() as u64,
        retained_picture_bytes: plan.picture_bytes / 4 * pixel_bytes(format),
        legacy_passes: u64::from(!plan.final_sample()),
        final_draws: u64::from(plan.final_sample()),
        ..Default::default()
    }
}
fn admit_budget(metrics: NativeShaderEffectMetrics) -> Result<(), Error> {
    let p = metrics.pictures;
    [
        p.texture_bytes,
        p.vertex_bytes,
        p.index_bytes,
        p.uniform_upload_bytes,
        metrics.shader.uniform_bytes,
    ]
    .into_iter()
    .try_fold(0u64, |sum, value| {
        sum.checked_add(value)
            .filter(|v| *v <= MAX_TOTAL_COMPILED_BYTES)
            .ok_or(Error::Capacity)
    })?;
    Ok(())
}

fn pixel_bytes(format: wgpu::TextureFormat) -> u64 {
    if format == wgpu::TextureFormat::Rgba16Float {
        8
    } else {
        4
    }
}
