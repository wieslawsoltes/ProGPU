//! Original fused image drawing, source Gaussian preparation and exact bindings.
use super::*;
use crate::{
    image::{GpuImage, ImageSource},
    image_effect::{
        BlurInput, BlurStorage, ImageBlurEngine, ImageBlurPolicy, ImageEffectSource,
        PreparedImageBlur,
    },
    mask::MaskUniforms,
};
use std::collections::{HashMap, HashSet};

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
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::RenderPipeline,
}
pub(super) struct ImageEffectState {
    pipelines: HashMap<(bool, bool), Pipeline>,
    blur: ImageBlurEngine,
}
pub(super) struct EffectDraw {
    pipeline: wgpu::RenderPipeline,
    params: wgpu::BindGroup,
    textures: wgpu::BindGroup,
    mask: wgpu::BindGroup,
    blur: Option<PreparedImageBlur>,
    first_vertex: i32,
}
pub(super) struct PreparedImageEffects {
    draws: Vec<Option<EffectDraw>>,
    frame: wgpu::BindGroup,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    metrics: ImageEffectMetrics,
}
impl PreparedImageEffects {
    pub fn encode_sources(&self, encoder: &mut wgpu::CommandEncoder) {
        for draw in self.draws.iter().flatten() {
            if let Some(blur) = &draw.blur {
                blur.encode_owned(encoder);
            }
        }
    }
    pub fn draw(&self, pass: &mut wgpu::RenderPass<'_>, index: usize) -> Result<(), Error> {
        let Some(draw) = self.draws.get(index).ok_or(Error::ResourceDomain)? else {
            return Ok(());
        };
        pass.set_pipeline(&draw.pipeline);
        pass.set_bind_group(0, &self.frame, &[]);
        pass.set_bind_group(1, &draw.params, &[]);
        pass.set_bind_group(2, &draw.textures, &[]);
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
            blur: ImageBlurEngine::new(device, crate::path::ExecutionPreference::Automatic)?,
        })
    }
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        chain: bool,
        premultiplied: bool,
    ) -> Result<Pipeline, Error> {
        if let Some(p) = self.pipelines.get(&(chain, premultiplied)) {
            return Ok(p.clone());
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original fused ImageEffect"),
            source: wgpu::ShaderSource::Wgsl(shaders::IMAGE_EFFECT.into()),
        });
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
                    blend: Some(if premultiplied {
                        wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING
                    } else {
                        wgpu::BlendState::ALPHA_BLENDING
                    }),
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
        self.pipelines
            .insert((chain, premultiplied), result.clone());
        Ok(result)
    }
}
impl Renderer {
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
        let (unfilterable, managed_bpp) = match &draw.source {
            ImageEffectSource::Rgb(source) => {
                let bpp = match source {
                    ImageSource::Pixels(i) => u64::from(i.format().bytes_per_pixel()),
                    ImageSource::Gpu(i) => {
                        if i.device() != &self.device {
                            return Err(Error::ResourceDomain);
                        }
                        if i.texture().format() == wgpu::TextureFormat::R8Unorm {
                            1
                        } else {
                            4
                        }
                    }
                };
                (false, bpp)
            }
            ImageEffectSource::Gpu(source) => {
                if source.device() != &self.device {
                    return Err(Error::ResourceDomain);
                }
                (source.is_unfilterable(), source.managed_bytes_per_pixel())
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
        if draw.effect.effects1[2] > 0.01 || unfilterable {
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
        for draw in scene.image_effects() {
            let (storage, work) = self.image_effect_cost(draw)?;
            bytes = bytes.checked_add(storage).ok_or(Error::Capacity)?;
            passes = passes.checked_add(work).ok_or(Error::Capacity)?;
            if bytes > 512 * 1024 * 1024 || passes > 16 * 1024 {
                return Err(Error::Capacity);
            }
        }
        Ok((bytes, passes))
    }
    pub(super) fn validate_image_effects(&self, compiled: &CompiledScene) -> Result<(), Error> {
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
        let mut state = 0;
        for draw in &compiled.draws {
            match draw {
                DrawRange::State(i) => state = *i,
                DrawRange::ImageEffect(i) => {
                    let draw = &compiled.image_effects[*i];
                    let (bytes, work) = self.image_effect_cost(draw)?;
                    texture_bytes = texture_bytes.checked_add(bytes).ok_or(Error::Capacity)?;
                    passes = passes.checked_add(work).ok_or(Error::Capacity)?;
                    if texture_bytes > 512 * 1024 * 1024 || passes > 16 * 1024 {
                        return Err(Error::Capacity);
                    }
                    let masks = &compiled.states[state];
                    if draw.mask.is_none()
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
        Ok(())
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
            let source_state = &compiled.states[states[index]];
            if !frame.clip_intersects(source_state.clip) {
                draws.push(None);
                continue;
            }
            let source = match &draw.source {
                ImageEffectSource::Rgb(source) => BlurInput::image(
                    &realize(
                        &self.device,
                        &self.queue,
                        &mut images,
                        source,
                        draw.sampling.uses_mipmaps(),
                        &mut prepared.metrics,
                    )
                    .await?,
                ),
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
            let blur = if params.effects1[2] > 0.01 || source.is_unfilterable() {
                Some(
                    state
                        .blur
                        .prepare(
                            &source,
                            params.effects1[2],
                            match draw.blur_storage {
                                ImageBlurPolicy::Native => BlurStorage::NativeRgba8,
                                ImageBlurPolicy::Managed => BlurStorage::ManagedSource,
                            },
                            None,
                        )
                        .await?,
                )
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
            let pipeline = state
                .pipeline(&self.device, self.format, chain, params.texture0[3] > 0.5)
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
            let views = actual.views();
            let sampler = images.sampler(
                &self.device,
                image::SamplerKey {
                    mode: draw.sampling,
                    anisotropy: 1,
                    address: draw.address,
                },
            );
            let textures = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU frozen image-effect source pair"),
                layout: &pipeline.pipeline.get_bind_group_layout(2),
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Sampler(sampler),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(&views[0]),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(&views[1]),
                    },
                ],
            });
            let buffer = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU original 288-byte image effect"),
                    contents: bytemuck::bytes_of(&params),
                    usage: wgpu::BufferUsages::UNIFORM,
                });
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable fused-effect parameters"),
                layout: &pipeline.pipeline.get_bind_group_layout(1),
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: buffer.as_entire_binding(),
                }],
            });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            metrics.draws += 1;
            metrics.parameter_bytes += 288;
            if let Some(blur) = &blur {
                let m = blur.metrics();
                metrics.blur_passes += m.render_passes;
                metrics.parameter_bytes += m.uniform_bytes;
                metrics.blur_texture_bytes += m.texture_bytes;
            }
            let first_vertex = i32::try_from(vertices.len()).map_err(|_| Error::Capacity)?;
            vertices.extend_from_slice(&draw.vertices);
            frame_pipeline = Some(pipeline.pipeline.clone());
            draws.push(Some(EffectDraw {
                pipeline: pipeline.pipeline,
                params: binding,
                textures,
                mask,
                blur,
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
            prepared.image_effects = Some(PreparedImageEffects {
                draws,
                frame: frame_uniform,
                vertices,
                indices,
                metrics,
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
        add!(blur_passes);
        add!(blur_texture_bytes);
        add!(parameter_bytes);
        add!(geometry_bytes);
        Ok(())
    }
}
