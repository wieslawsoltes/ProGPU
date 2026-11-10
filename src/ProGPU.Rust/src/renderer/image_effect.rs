//! Original fused image drawing, source Gaussian preparation and exact bindings.
use super::*;
use crate::{
    image::{GpuImage, ImageSource},
    image_effect::{
        BlurInput, BlurStorage, ImageBlurEngine, ImageBlurPolicy, ImageEffectDrawPolicy,
        ImageEffectSource, PreparedImageBlur,
    },
    mask::MaskUniforms,
};
use std::{
    collections::{HashMap, HashSet},
    sync::Arc,
};
mod cache;

/// Actual original image-effect resources and per-replay work.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ImageEffectMetrics {
    /// Nonempty fused image-effect draws.
    pub draws: u32,
    /// Original horizontal/vertical blur passes executed before consumers.
    pub blur_passes: u32,
    /// Actual two-target live-blur storage across prepared draws.
    pub blur_texture_bytes: u64,
    /// Original 288-byte effect records and 912-byte axis records.
    pub parameter_bytes: u64,
    /// Actual quad/index bytes, also included in scene geometry upload counters.
    /// The shared 224-byte frame is counted separately in scene uniform uploads.
    pub geometry_bytes: u64,
    /// Canonical bounded fragment blur draws outside original live eligibility.
    pub inline_blur_draws: u32,
    /// Managed commands selecting the original default SrcOver factor fallback.
    pub blend_fallback_draws: u32,
    /// Actual same-preparation generation-key live-blur reuse hits.
    pub blur_deduplicated_draws: u32,
    /// Actual recycled two-target storage sets; retained scenes prevent mutation.
    pub blur_storage_reuses: u32,
    /// Actual newly allocated live-blur textures, excluding reused storage.
    pub blur_texture_allocations: u32,
    /// Actual completed private parameter-slot reuse and view-binding cache hits.
    pub parameter_reuses: u32,
    pub texture_binding_hits: u32,
    /// Pool parameter, texture-binding and two-target storage counts at publication.
    /// Merged captures report each observed maximum, not a sum of shared caches.
    pub pool_entries: [u32; 3],
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::RenderPipeline,
}
pub(super) struct ImageEffectState {
    pipelines: HashMap<(wgpu::TextureFormat, bool, bool, crate::scene::LayerBlend), Pipeline>,
    samplers: HashMap<crate::image::ImageSampling, wgpu::Sampler>,
    blur: ImageBlurEngine,
    layout: Option<wgpu::PipelineLayout>,
    shader: Option<wgpu::ShaderModule>,
    resources: cache::Resources,
}
pub(super) struct EffectDraw {
    pipeline: wgpu::RenderPipeline,
    params: Arc<cache::Parameters>,
    textures: Arc<cache::TextureBinding>,
    mask: wgpu::BindGroup,
    first_vertex: i32,
}
pub(super) struct PreparedImageEffects {
    draws: Vec<Option<EffectDraw>>,
    frame: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    metrics: ImageEffectMetrics,
    sources: Vec<Arc<PreparedImageBlur>>,
    clock: Arc<std::sync::atomic::AtomicU64>,
}
impl PreparedImageEffects {
    pub fn encode_sources(&self, encoder: &mut wgpu::CommandEncoder) {
        let frame = self.clock.load(std::sync::atomic::Ordering::Acquire);
        for source in &self.sources {
            source.storage().touch(frame);
            source.encode_owned(encoder);
        }
    }
    pub fn draw(&self, pass: &mut wgpu::RenderPass<'_>, index: usize) -> Result<(), Error> {
        let Some(draw) = self.draws.get(index).ok_or(Error::ResourceDomain)? else {
            return Ok(());
        };
        draw.params.usage.encode();
        draw.textures
            .touch(self.clock.load(std::sync::atomic::Ordering::Acquire));
        pass.set_pipeline(&draw.pipeline);
        pass.set_bind_group(0, &self.frame, &[]);
        pass.set_bind_group(1, &draw.params.binding, &[]);
        pass.set_bind_group(2, &draw.textures.binding, &[]);
        pass.set_bind_group(3, &draw.mask, &[]);
        pass.set_vertex_buffer(0, self.vertices.slice(..));
        pass.set_index_buffer(self.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..6, draw.first_vertex, 0..1);
        Ok(())
    }
}
impl PreparedScene {
    /// Actual fused effect resources and per-replay source preparation.
    pub fn image_effect_metrics(&self) -> ImageEffectMetrics {
        self.image_effects
            .as_ref()
            .map_or_else(Default::default, |e| e.metrics)
    }
}
impl ImageEffectState {
    fn new(device: &wgpu::Device) -> Result<Self, Error> {
        Ok(Self {
            pipelines: HashMap::new(),
            samplers: HashMap::new(),
            layout: None,
            shader: None,
            resources: Default::default(),
            blur: ImageBlurEngine::new(device, crate::path::ExecutionPreference::Automatic)?,
        })
    }
    fn managed_sampler(
        &mut self,
        device: &wgpu::Device,
        mode: crate::image::ImageSampling,
    ) -> wgpu::Sampler {
        self.samplers
            .entry(mode)
            .or_insert_with(|| {
                let [mag, min, mip] = mode.filters();
                let filter = |linear| {
                    if linear {
                        wgpu::FilterMode::Linear
                    } else {
                        wgpu::FilterMode::Nearest
                    }
                };
                device.create_sampler(&wgpu::SamplerDescriptor {
                    label: Some("ProGPU managed image-effect original sampler"),
                    mag_filter: filter(mag),
                    min_filter: filter(min),
                    mipmap_filter: if mip || mode == crate::image::ImageSampling::Linear {
                        wgpu::MipmapFilterMode::Linear
                    } else {
                        wgpu::MipmapFilterMode::Nearest
                    },
                    lod_max_clamp: if mode.uses_mipmaps() { 32.0 } else { 1.0 },
                    ..Default::default()
                })
            })
            .clone()
    }
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        chain: bool,
        premultiplied: bool,
        blend: crate::scene::LayerBlend,
    ) -> Result<Pipeline, Error> {
        let key = (format, chain, premultiplied, blend);
        if let Some(p) = self.pipelines.get(&key) {
            return Ok(p.clone());
        }
        let blend_state = blend.managed_draw_state(premultiplied)?;
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = if let Some(module) = &self.shader {
            module.clone()
        } else {
            let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU original fused ImageEffect"),
                source: wgpu::ShaderSource::Wgsl(shaders::IMAGE_EFFECT.into()),
            });
            self.shader = Some(module.clone());
            module
        };
        let layout = if let Some(layout) = &self.layout {
            layout.clone()
        } else {
            let uniform = |size, visibility| {
                device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU original image-effect uniform layout"),
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
                label: Some("ProGPU actual image/chroma layout"),
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
            let mask = layout::mask(device);
            let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU fused image-effect layout"),
                bind_group_layouts: &[Some(&frame), Some(&params), Some(&textures), Some(&mask)],
                immediate_size: 0,
            });
            self.layout = Some(layout.clone());
            layout
        };
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU fused image-effect pipeline"),
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
                entry_point: Some(if chain { "fs_main_chain" } else { "fs_main" }),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(blend_state),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let result = Pipeline { pipeline };
        self.pipelines.insert(key, result.clone());
        Ok(result)
    }
}
impl Renderer {
    /// Actual retained cache entry counts on this renderer: effect parameter
    /// slots, source view bindings and two-target live-blur storage sets.
    pub fn image_effect_pool_entries(&self) -> [u32; 3] {
        self.image_effects
            .as_ref()
            .map_or([0; 3], |state| state.resources.counts())
    }
    /// Supply the source host's actual monotonically increasing frame number for
    /// original 120/240-frame cache aging. Call once before preparing that frame;
    /// nested captures must share the value, not increment it independently.
    /// Without this opt-in, aging counts preparation epochs rather than display
    /// frames. Existing prepared scenes retain their resources through eviction.
    pub fn begin_image_effect_frame(&mut self, frame: u64) -> Result<(), Error> {
        if self.image_effects.is_none() {
            self.image_effects = Some(ImageEffectState::new(&self.device)?);
        }
        self.image_effects
            .as_mut()
            .ok_or(Error::ResourceDomain)?
            .resources
            .host_frame(frame)
    }
    fn image_effect_uses_live_blur(
        draw: &crate::image_effect::ImageEffectDraw,
    ) -> Result<bool, Error> {
        let sigma = draw.effect.effects1[2];
        let unfilterable = matches!(&draw.source,
            ImageEffectSource::Gpu(source) if source.is_unfilterable());
        if draw.policy == ImageEffectDrawPolicy::ExplicitUniforms {
            return Ok(sigma > 0.01 || unfilterable);
        }
        let eligible = match &draw.source {
            ImageEffectSource::Rgb(ImageSource::Pixels(source)) => {
                source.format() != crate::image::ImageFormat::R8
            }
            ImageEffectSource::Rgb(ImageSource::Gpu(source)) => {
                BlurInput::image(source).managed_live_blur_eligible()
            }
            ImageEffectSource::Gpu(source) => source.managed_live_blur_eligible(),
        };
        let live = sigma > 0.01 && sigma <= 32.0 && eligible;
        if unfilterable && !live {
            // The managed fused shader requires a filterable binding. Its
            // original failed live-prepass gate does not authorize a decode
            // prepass with different sigma or a different resource generation.
            return Err(Error::UnsupportedExecution);
        }
        Ok(live)
    }
    fn image_effect_cost(
        &self,
        draw: &crate::image_effect::ImageEffectDraw,
    ) -> Result<(u64, u32), Error> {
        draw.validate()?;
        let limits = self.device.limits();
        if limits.max_bind_groups < 4
            || limits.max_sampled_textures_per_shader_stage < 3
            || limits.max_uniform_buffers_per_shader_stage < 4
            || limits.max_uniform_buffer_binding_size < 912
        {
            return Err(Error::UnsupportedExecution);
        }
        let size = draw.source.size();
        let managed_bpp = match &draw.source {
            ImageEffectSource::Rgb(source) => {
                let bpp = match source {
                    ImageSource::Pixels(i) => u64::from(i.format().bytes_per_pixel()),
                    ImageSource::Gpu(i) => {
                        if i.device() != &self.device {
                            return Err(Error::ResourceDomain);
                        }
                        BlurInput::image(i).managed_bytes_per_pixel()
                    }
                };
                bpp
            }
            ImageEffectSource::Gpu(source) => {
                if source.device() != &self.device {
                    return Err(Error::ResourceDomain);
                }
                source.managed_bytes_per_pixel()
            }
        };
        if size.iter().any(|v| *v > limits.max_texture_dimension_2d) {
            return Err(Error::Capacity);
        }
        if let Some(mask) = &draw.mask {
            if mask
                .size()
                .iter()
                .any(|v| *v > limits.max_texture_dimension_2d)
            {
                return Err(Error::Capacity);
            }
            if let ImageSource::Gpu(mask) = mask
                && mask.device() != &self.device
            {
                return Err(Error::ResourceDomain);
            }
        }
        if Self::image_effect_uses_live_blur(draw)? {
            let bpp = if draw.blur_storage == ImageBlurPolicy::Native {
                4
            } else {
                managed_bpp
            };
            let bytes = u64::from(size[0]) * u64::from(size[1]) * bpp * 2;
            if bytes > 512 * 1024 * 1024 {
                return Err(Error::Capacity);
            }
            Ok((bytes, 3))
        } else {
            Ok((0, 1))
        }
    }
    pub(super) fn preflight_image_effects(&self, scene: &Scene) -> Result<(u64, u32), Error> {
        let mut bytes = 0u64;
        let mut passes = 0u32;
        let mut keys = HashSet::new();
        for draw in scene.image_effects() {
            let (storage, work) =
                cache::distinct_cost(draw, self.image_effect_cost(draw)?, &mut keys)?;
            bytes = bytes.checked_add(storage).ok_or(Error::Capacity)?;
            passes = passes.checked_add(work).ok_or(Error::Capacity)?;
            if bytes > 512 * 1024 * 1024 || passes > 16 * 1024 {
                return Err(Error::Capacity);
            }
        }
        Ok((bytes, passes))
    }
    pub(super) fn validate_image_effects(
        &self,
        compiled: &CompiledScene,
    ) -> Result<(u64, u32), Error> {
        let limits = self.device.limits();
        if !compiled.image_effects.is_empty()
            && (limits.max_bind_groups < 4
                || limits.max_sampled_textures_per_shader_stage < 3
                || limits.max_uniform_buffers_per_shader_stage < 4
                || limits.max_uniform_buffer_binding_size < 912)
        {
            return Err(Error::UnsupportedExecution);
        }
        if compiled.image_effects.len() > 16 * 1024
            || compiled.image_effects.len() as u64 * 224 > limits.max_buffer_size
        {
            return Err(Error::Capacity);
        }
        let mut texture_bytes = 0u64;
        let mut passes = 0u32;
        let mut keys = HashSet::new();
        let mut state = 0;
        for draw in &compiled.draws {
            match draw {
                DrawRange::State(i) => state = *i,
                DrawRange::ImageEffect(i) => {
                    let draw = &compiled.image_effects[*i];
                    let (bytes, work) =
                        cache::distinct_cost(draw, self.image_effect_cost(draw)?, &mut keys)?;
                    texture_bytes = texture_bytes.checked_add(bytes).ok_or(Error::Capacity)?;
                    passes = passes.checked_add(work).ok_or(Error::Capacity)?;
                    if texture_bytes > 512 * 1024 * 1024 || passes > 16 * 1024 {
                        return Err(Error::Capacity);
                    }
                    let masks = &compiled.states[state];
                    draw.clipped_vertices(masks.logical_clip)?;
                    if draw.policy == ImageEffectDrawPolicy::ExplicitUniforms
                        && masks.blend != crate::scene::LayerBlend::SrcOver
                    {
                        return Err(Error::UnsupportedExecution);
                    }
                    if draw.mask.is_none()
                        && !masks.materialized
                        && (masks.count > 3
                            || (masks.coverage.is_some() && masks.count > 1)
                            || masks.masks[..masks.count]
                                .iter()
                                .any(|m| m.options[0] > 4.5))
                    {
                        // The original fused shader has one primary OR three
                        // analytic chain slots. Full composite-mask materialization
                        // is a separate port; never reinterpret area masks as rings.
                        return Err(Error::UnsupportedExecution);
                    }
                }
                _ => (),
            }
        }
        Ok((texture_bytes, passes))
    }
    pub(super) async fn prepare_image_effects(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.image_effects.is_empty() {
            return Ok(());
        }
        self.validate_image_effects(compiled)?;
        let mut state = match self.image_effects.take() {
            Some(s) => s,
            None => ImageEffectState::new(&self.device)?,
        };
        state.resources.begin()?;
        let mut images = self.images.take().unwrap_or_default();
        let mut masks = self
            .masks
            .take()
            .unwrap_or_else(|| mask::MaskState::new(&self.device));
        let mut states = vec![0; compiled.image_effects.len()];
        let mut current = 0;
        for draw in &compiled.draws {
            match draw {
                DrawRange::State(i) => current = *i,
                DrawRange::ImageEffect(i) => states[*i] = current,
                _ => (),
            }
        }
        let mut sources = Vec::new();
        let mut deduplicated = HashMap::<cache::BlurKey, Arc<PreparedImageBlur>>::new();
        let mut vertices = Vec::new();
        let mut draws = Vec::new();
        let mut metrics = ImageEffectMetrics::default();
        let mut mask_textures = HashSet::new();
        vertices
            .try_reserve_exact(compiled.image_effects.len() * 4)
            .map_err(|_| Error::Capacity)?;
        draws
            .try_reserve_exact(compiled.image_effects.len())
            .map_err(|_| Error::Capacity)?;
        let mut frame_pipeline = None;
        for (index, draw) in compiled.image_effects.iter().enumerate() {
            let source_state = &prepared
                .masks
                .as_ref()
                .map_or(&compiled.states, |m| &m.resolved)[states[index]];
            if !frame.clip_intersects(source_state.clip) {
                draws.push(None);
                continue;
            }
            let Some(quad) = draw.clipped_vertices(source_state.logical_clip)? else {
                draws.push(None);
                continue;
            };
            let sampling = draw.effective_sampling();
            let source = match &draw.source {
                ImageEffectSource::Rgb(source) => {
                    let image = realize(
                        &self.device,
                        &self.queue,
                        &mut images,
                        source,
                        sampling.uses_mipmaps(),
                        &mut prepared.metrics,
                    )
                    .await?;
                    let input = BlurInput::image(&image);
                    if matches!(source, ImageSource::Pixels(_)) {
                        input.immutable_upload()
                    } else {
                        input
                    }
                }
                ImageEffectSource::Gpu(source) => source.clone(),
            };
            if source.device() != &self.device {
                return Err(Error::ResourceDomain);
            }
            let mut params = draw.effect;
            let yuv = source.conversion();
            params.flags0[0] = if yuv.is_some() { 1.0 } else { 0.0 };
            if let Some(yuv) = yuv {
                params.yuv_range = yuv.range;
                params.yuv_red = yuv.red;
                params.yuv_green = yuv.green;
                params.yuv_blue = yuv.blue;
            }
            params.texture0[2] = if source.alpha() == crate::image::ImageAlpha::Premultiplied {
                1.0
            } else {
                0.0
            };
            let blur = if Self::image_effect_uses_live_blur(draw)? {
                let policy = match draw.blur_storage {
                    ImageBlurPolicy::Native => BlurStorage::NativeRgba8,
                    ImageBlurPolicy::Managed => BlurStorage::ManagedSource,
                };
                let managed = draw.policy == ImageEffectDrawPolicy::ManagedExtension;
                let key = managed
                    .then(|| cache::BlurKey::new(&source, params.effects1[2], policy))
                    .flatten();
                if let Some(cached) = key.as_ref().and_then(|key| deduplicated.get(key)) {
                    metrics.blur_deduplicated_draws += 1;
                    Some(cached.clone())
                } else {
                    let storage = if managed {
                        state.resources.acquire_storage(&source, policy)
                    } else {
                        None
                    };
                    let reused = storage.is_some();
                    let value = Arc::new(
                        state
                            .blur
                            .prepare_reusing(
                                &source,
                                params.effects1[2],
                                policy,
                                None,
                                storage,
                                Some(&self.queue),
                            )
                            .await?,
                    );
                    if managed {
                        state.resources.publish_storage(value.storage())?;
                    }
                    let work = value.metrics();
                    metrics.blur_passes += work.render_passes;
                    metrics.parameter_bytes += work.uniform_bytes;
                    metrics.blur_texture_bytes += work.texture_bytes;
                    metrics.blur_texture_allocations += work.texture_allocations;
                    metrics.blur_storage_reuses += u32::from(reused);
                    sources.try_reserve(1).map_err(|_| Error::Capacity)?;
                    sources.push(value.clone());
                    if let Some(key) = key {
                        deduplicated.try_reserve(1).map_err(|_| Error::Capacity)?;
                        deduplicated.insert(key, value.clone());
                    }
                    Some(value)
                }
            } else {
                None
            };
            let actual = match &blur {
                Some(blur) => {
                    params = params.after_live_blur();
                    blur.as_input()?
                }
                None => source,
            };
            let chain = draw.mask.is_none() && source_state.count > 1;
            let blend = source_state.blend;
            if draw.policy == ImageEffectDrawPolicy::ManagedExtension {
                params.texture0[2] = if actual.alpha() == crate::image::ImageAlpha::Premultiplied {
                    1.0
                } else {
                    0.0
                };
                params.texture0[3] = if crate::image_effect::ImageEffectDraw::managed_output_alpha(
                    actual.alpha(),
                    blend,
                ) == crate::image::ImageAlpha::Premultiplied
                {
                    1.0
                } else {
                    0.0
                };
            }
            let pipeline = state
                .pipeline(
                    &self.device,
                    self.format,
                    chain,
                    params.texture0[3] > 0.5,
                    blend,
                )
                .await?;
            let explicit_mask = if let Some(source) = &draw.mask {
                Some(
                    realize(
                        &self.device,
                        &self.queue,
                        &mut images,
                        source,
                        false,
                        &mut prepared.metrics,
                    )
                    .await?,
                )
            } else {
                None
            };
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let mask = if let Some(source) = &explicit_mask {
                params.effects1[3] = 1.0;
                params.texture0[0] = source.size()[0] as f32;
                params.texture0[1] = source.size()[1] as f32;
                Ok(own_mask(
                    &self.device,
                    &pipeline.pipeline,
                    source,
                    draw.policy == ImageEffectDrawPolicy::ManagedExtension,
                    &mut prepared.metrics,
                ))
            } else {
                params.effects1[3] = if source_state.count != 0 { 1.0 } else { 0.0 };
                params.texture0[0] = frame.width as f32;
                params.texture0[1] = frame.height as f32;
                let mut source = source_state.clone();
                if chain {
                    source.masks = [
                        MaskUniforms::zeroed(),
                        source_state.masks[0],
                        source_state.masks[1],
                        source_state.masks[2],
                    ];
                    source.count += 1;
                }
                masks.binding(
                    &self.device,
                    &self.queue,
                    &source,
                    &mut prepared.metrics,
                    &mut mask_textures,
                )
            };
            let mask = match mask {
                Ok(mask) => mask,
                Err(error) => {
                    let _ = scope.pop().await;
                    return Err(error);
                }
            };
            let sampler = if draw.policy == ImageEffectDrawPolicy::ManagedExtension {
                state.managed_sampler(&self.device, sampling)
            } else {
                images
                    .sampler(
                        &self.device,
                        image::SamplerKey {
                            mode: sampling,
                            anisotropy: 1,
                            address: draw.address,
                        },
                    )
                    .clone()
            };
            let resources = (|| -> Result<_, Error> {
                let (textures, texture_hit) = state.resources.textures(
                    &self.device,
                    &pipeline.pipeline.get_bind_group_layout(2),
                    &actual,
                    &sampler,
                    draw.sampling,
                    draw.address,
                    draw.policy == ImageEffectDrawPolicy::ManagedExtension,
                    self.format,
                )?;
                let (binding, parameter_reused) = state.resources.parameters(
                    &self.device,
                    &self.queue,
                    &pipeline.pipeline.get_bind_group_layout(1),
                    &params,
                )?;
                Ok((textures, binding, texture_hit, parameter_reused))
            })();
            let (textures, binding, texture_hit, parameter_reused) = match resources {
                Ok(value) => value,
                Err(error) => {
                    let _ = scope.pop().await;
                    return Err(error);
                }
            };
            metrics.texture_binding_hits += u32::from(texture_hit);
            metrics.parameter_reuses += u32::from(parameter_reused);
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            metrics.draws += 1;
            metrics.inline_blur_draws += u32::from(params.effects1[2] > 0.01);
            metrics.blend_fallback_draws += u32::from(
                draw.policy == ImageEffectDrawPolicy::ManagedExtension
                    && blend.managed_vector_effective_mode() != blend,
            );
            metrics.parameter_bytes += 288;
            let first_vertex = i32::try_from(vertices.len()).map_err(|_| Error::Capacity)?;
            vertices.extend_from_slice(&quad);
            frame_pipeline = Some(pipeline.pipeline.clone());
            draws.push(Some(EffectDraw {
                pipeline: pipeline.pipeline,
                params: binding,
                textures,
                mask,
                first_vertex,
            }));
        }
        if let Some(pipeline) = frame_pipeline {
            let frame_uniform = advanced::frame_binding(
                &self.device,
                &pipeline,
                frame.uniforms(self.device.limits().max_texture_dimension_2d)?,
            );
            let vertices = advanced::buffer(
                &self.device,
                "ProGPU fused-effect quads",
                bytemuck::cast_slice(&vertices),
                wgpu::BufferUsages::VERTEX,
            );
            let indices = advanced::buffer(
                &self.device,
                "ProGPU fused-effect quad indices",
                bytemuck::cast_slice(&[0u32, 1, 2, 0, 2, 3]),
                wgpu::BufferUsages::INDEX,
            );
            metrics.geometry_bytes = vertices.size() + indices.size();
            prepared.metrics.geometry_bytes += metrics.geometry_bytes;
            prepared.metrics.uniform_bytes += 224 + metrics.parameter_bytes;
            metrics.pool_entries = state.resources.counts();
            prepared.image_effects = Some(PreparedImageEffects {
                draws,
                frame: frame_uniform,
                vertices,
                indices,
                metrics,
                sources,
                clock: state.resources.clock(),
            });
        }
        self.image_effects = Some(state);
        self.images = Some(images);
        self.masks = Some(masks);
        Ok(())
    }
}
async fn realize(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    state: &mut image::ImageState,
    source: &ImageSource,
    mips: bool,
    metrics: &mut UploadMetrics,
) -> Result<GpuImage, Error> {
    match source {
        ImageSource::Pixels(source) => {
            let (image, bytes, passes) = state.upload(device, queue, source, mips).await?;
            metrics.image_upload_bytes += bytes;
            metrics.image_mip_passes += passes;
            Ok(image)
        }
        ImageSource::Gpu(source) => {
            if source.device() != device {
                return Err(Error::ResourceDomain);
            }
            Ok(source.clone())
        }
    }
}
fn own_mask(
    device: &wgpu::Device,
    pipeline: &wgpu::RenderPipeline,
    source: &GpuImage,
    managed: bool,
    metrics: &mut UploadMetrics,
) -> wgpu::BindGroup {
    let mut uniform = MaskUniforms::zeroed();
    uniform.coordinate1[0] = 1.0 / source.size()[0] as f32;
    uniform.coordinate1[1] = 1.0 / source.size()[1] as f32;
    uniform.options[0] = 1.0;
    let primary = advanced::buffer(
        device,
        "ProGPU original effect texture-mask mapping",
        bytemuck::bytes_of(&uniform),
        wgpu::BufferUsages::UNIFORM,
    );
    let chain = advanced::buffer(
        device,
        "ProGPU unused effect mask chain",
        &[0; 288],
        wgpu::BufferUsages::UNIFORM,
    );
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("ProGPU source effect mask sampler"),
        min_filter: wgpu::FilterMode::Linear,
        mag_filter: wgpu::FilterMode::Linear,
        mipmap_filter: if managed {
            wgpu::MipmapFilterMode::Linear
        } else {
            wgpu::MipmapFilterMode::Nearest
        },
        lod_max_clamp: if managed { 1.0 } else { 32.0 },
        ..Default::default()
    });
    let view = source.texture().create_view(&Default::default());
    metrics.uniform_bytes += 384;
    metrics.mask_bindings += 1;
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU actual source effect mask"),
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
    })
}

impl ImageEffectMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        macro_rules! add {
            ($field:ident) => {
                self.$field = self
                    .$field
                    .checked_add(other.$field)
                    .ok_or(Error::Capacity)?;
            };
        }
        add!(draws);
        add!(inline_blur_draws);
        add!(blend_fallback_draws);
        add!(blur_deduplicated_draws);
        add!(blur_storage_reuses);
        add!(blur_texture_allocations);
        add!(parameter_reuses);
        add!(texture_binding_hits);
        for index in 0..3 {
            self.pool_entries[index] = self.pool_entries[index].max(other.pool_entries[index]);
        }
        add!(blur_passes);
        add!(blur_texture_bytes);
        add!(parameter_bytes);
        add!(geometry_bytes);
        Ok(())
    }
}

impl PreparedImageEffects {
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        for draw in self.draws.iter().flatten() {
            crate::image_effect::ResourceUse::submitted(draw.params.usage.clone(), queue);
        }
        for source in &self.sources {
            source.retain_submitted(queue);
        }
    }
}
