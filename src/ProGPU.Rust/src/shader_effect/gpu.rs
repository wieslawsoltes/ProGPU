//! Original lazy program cache, immutable source bindings and ordered GPU draws.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    mask::{ClipPlan, MaskComposer},
    path::{ExecutionPath, ExecutionPreference},
    shaders,
};
use wgpu::util::DeviceExt;

#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct EffectUniforms {
    constants: [[f32; 4]; 32],
    extent: [f32; 4],
    sample_extent: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct SampleUniforms {
    source: EffectUniforms,
    quad_scale_offset: [f32; 4],
    output_lattice: [f32; 4],
    homogeneous: [f32; 4],
    physical_clip: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct AffineUniforms {
    sample: SampleUniforms,
    quad_cross: [f32; 4],
}
const _: () = {
    assert!(size_of::<EffectUniforms>() == 544);
    assert!(size_of::<SampleUniforms>() == 608);
    assert!(size_of::<AffineUniforms>() == 624);
};

/// Original complete positive-axis implicit capture and optional independent sampler.
pub struct LegacyShaderRequest<'a> {
    /// Actual validated program and current constants.
    pub effect: &'a ShaderEffect,
    /// Complete premultiplied implicit-input capture, including allocation extent.
    pub input: &'a GpuImage,
    /// Independently captured full-RGBA sampler; None samples the implicit input.
    pub sampler: Option<&'a GpuImage>,
    /// Actual valid physical implicit-input width/height, excluding spare allocation.
    pub extent: [u32; 2],
    /// Original optional UV derivative register.
    pub derivative: Option<u32>,
}
/// Original final-device shader draw retaining separate input and sampler pictures.
pub struct SampleShaderRequest<'a> {
    /// Actual validated program and current constants.
    pub effect: &'a ShaderEffect,
    /// Original v5 or v6 wire frame, with source provenance owned by its producer.
    pub frame: ShaderFrame,
    /// Actual current parent target lattice.
    pub target: SampleLattice,
    /// Complete implicit input at the frame's scale-space capture size.
    pub input: &'a GpuImage,
    /// Independently captured full-RGBA sampler, or the actual implicit input.
    pub sampler: Option<&'a GpuImage>,
    /// Original optional UV derivative register.
    pub derivative: Option<u32>,
    /// Exact path-produced coverage at this target's physical lattice.
    pub vector_mask: Option<&'a ShaderVectorMask>,
}
/// Typed coverage produced only by the original path clip factory. Arbitrary
/// sampled images cannot be relabeled as original vector-source clipping.
#[derive(Debug)]
pub struct ShaderVectorMask {
    owner: Arc<()>,
    image: GpuImage,
    target: SampleLattice,
}
impl ShaderVectorMask {
    /// Actual full-target red coverage and retained GPU ownership.
    pub fn image(&self) -> &GpuImage {
        &self.image
    }
    /// Exact physical target identity used by the producer.
    pub fn target(&self) -> SampleLattice {
        self.target
    }
    /// Produce original vector clipping through the engine's path-only clip factory.
    /// Path transforms in the plan already use this actual target-local frame.
    pub async fn from_clip(
        engine: &mut ShaderEffectEngine,
        plan: &ClipPlan,
        origin: [i32; 2],
    ) -> Result<Self, Error> {
        engine.prepare_vector_mask(plan, origin).await
    }
}
/// Actual prepared-resource inventory; these are counts, not timing measurements.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ShaderEffectMetrics {
    /// Original bytecode retained by the selected shared program.
    pub bytecode_bytes: u64,
    /// Original 544/608/624-byte immutable target-specific uniform block.
    pub uniform_bytes: u64,
    /// Actual owned legacy RGBA8 output storage; final-device draws allocate none.
    pub output_texture_bytes: u64,
    /// Original one effect draw on each encode.
    pub draws: u32,
}
struct GpuProgram {
    source: Arc<ShaderProgram>,
    final_sample: bool,
    affine: bool,
    masked: bool,
    format: wgpu::TextureFormat,
    layout: wgpu::BindGroupLayout,
    mask_layout: Option<wgpu::BindGroupLayout>,
    pipeline: wgpu::RenderPipeline,
}
/// A prepared effect owns exact programs, constants, source captures and masks.
/// It never shares mutable parameters with another frame or target.
pub struct PreparedShaderEffect {
    owner: Arc<()>,
    program: Arc<GpuProgram>,
    binding: wgpu::BindGroup,
    mask: Option<wgpu::BindGroup>,
    input: GpuImage,
    sampler: GpuImage,
    vector_mask: Option<GpuImage>,
    output: Option<GpuImage>,
    size: [u32; 2],
    metrics: ShaderEffectMetrics,
}
impl PreparedShaderEffect {
    /// Legacy prepared output, populated when the engine encodes this effect.
    /// Final-device variants draw directly and therefore have no output texture.
    pub fn output(&self) -> Option<&GpuImage> {
        self.output.as_ref()
    }
    /// Actual preparation inventory, separate from shader or frame timing.
    pub fn metrics(&self) -> ShaderEffectMetrics {
        self.metrics
    }
    /// Actual independently retained source and sampler images.
    pub fn sources(&self) -> (&GpuImage, &GpuImage) {
        (&self.input, &self.sampler)
    }
    /// Original path coverage owner for the masked final-device variant.
    pub fn vector_mask(&self) -> Option<&GpuImage> {
        self.vector_mask.as_ref()
    }
    /// Whether this effect evaluates directly at final parent-device samples.
    pub fn is_final_sample(&self) -> bool {
        self.program.final_sample
    }
    /// Replay retained final-device resources independently of current engine cache state.
    pub(crate) fn draw_owned(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scissor: Option<[u32; 4]>,
    ) -> Result<ShaderEffectMetrics, Error> {
        if !self.program.final_sample {
            return Err(Error::ResourceDomain);
        }
        let [x, y, w, h] = scissor.unwrap_or([0, 0, self.size[0], self.size[1]]);
        if w == 0
            || h == 0
            || x.checked_add(w).is_none_or(|v| v > self.size[0])
            || y.checked_add(h).is_none_or(|v| v > self.size[1])
        {
            return Err(Error::InvalidInput);
        }
        pass.set_pipeline(&self.program.pipeline);
        pass.set_bind_group(0, &self.binding, &[]);
        if let Some(mask) = &self.mask {
            pass.set_bind_group(1, mask, &[]);
        }
        pass.set_viewport(0.0, 0.0, self.size[0] as f32, self.size[1] as f32, 0.0, 1.0);
        pass.set_scissor_rect(x, y, w, h);
        pass.draw(0..6, 0..1);
        Ok(self.metrics)
    }
}
/// Native bytecode-effect GPU engine. Programs are lazy, exact-byte keyed and
/// bounded to 64 cache entries; in-flight/prepared owners prevent eviction.
pub struct ShaderEffectEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: wgpu::TextureFormat,
    programs: Vec<Arc<GpuProgram>>,
    nearest: Option<wgpu::Sampler>,
    linear: Option<wgpu::Sampler>,
    clips: Option<MaskComposer>,
}
impl ShaderEffectEngine {
    /// Retain actual device resources without compiling programs. Unsupported
    /// forced execution routes reject; no CPU shader fallback is selected.
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) || !matches!(
            format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::Rgba16Float
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            format,
            programs: Vec::new(),
            nearest: None,
            linear: None,
            clips: None,
        })
    }
    /// Original fragment-stage execution route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    /// Actual cached program variants, including distinct final/affine/masked forms.
    pub fn cached_program_count(&self) -> usize {
        self.programs.len()
    }
    fn source(&self, image: &GpuImage) -> Result<(), Error> {
        if image.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        if image.alpha() != ImageAlpha::Premultiplied
            || image.texture().format() == wgpu::TextureFormat::R8Unorm
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    fn size(&self, size: [u32; 2]) -> Result<(), Error> {
        if size.contains(&0)
            || size
                .iter()
                .any(|v| *v > self.device.limits().max_texture_dimension_2d)
            || u64::from(size[0]) * u64::from(size[1]) * 4 > 512 * 1024 * 1024
        {
            return Err(Error::Capacity);
        }
        Ok(())
    }
    fn sampler(&mut self, mode: ShaderSampling) -> wgpu::Sampler {
        let slot = if mode == ShaderSampling::Nearest {
            &mut self.nearest
        } else {
            &mut self.linear
        };
        slot.get_or_insert_with(|| {
            let filter = if mode == ShaderSampling::Nearest {
                wgpu::FilterMode::Nearest
            } else {
                wgpu::FilterMode::Linear
            };
            self.device.create_sampler(&wgpu::SamplerDescriptor {
                label: Some("ProGPU original bytecode source sampler"),
                mag_filter: filter,
                min_filter: filter,
                mipmap_filter: wgpu::MipmapFilterMode::Nearest,
                ..Default::default()
            })
        })
        .clone()
    }
    async fn program(
        &mut self,
        source: &Arc<ShaderProgram>,
        final_sample: bool,
        affine: bool,
        masked: bool,
    ) -> Result<Arc<GpuProgram>, Error> {
        let format = if final_sample {
            self.format
        } else {
            wgpu::TextureFormat::Rgba8Unorm
        };
        if let Some(program) = self.programs.iter().find(|p| {
            p.source.source_sampler() == source.source_sampler()
                && p.source.bytecode() == source.bytecode()
                && p.final_sample == final_sample
                && p.affine == affine
                && p.masked == masked
                && p.format == format
        }) {
            return Ok(program.clone());
        }
        let eviction = if self.programs.len() == 64 {
            Some(
                self.programs
                    .iter()
                    .position(|p| Arc::strong_count(p) == 1)
                    .ok_or(Error::Capacity)?,
            )
        } else {
            None
        };
        let template = if affine {
            shaders::WPF_BYTECODE_AFFINE_EFFECT
        } else if final_sample {
            shaders::WPF_BYTECODE_SAMPLE_EFFECT
        } else {
            shaders::WPF_BYTECODE_EFFECT
        };
        let marker = "// PROGPU_VALIDATED_BYTECODE_BODY";
        if template.matches(marker).count() != 1 {
            return Err(Error::InvalidInput);
        }
        let text = template.replacen(marker, source.body(), 1);
        let size = if affine {
            624
        } else if final_sample {
            608
        } else {
            544
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = self
            .device
            .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("ProGPU original bytecode effect layout"),
                entries: &[
                    wgpu::BindGroupLayoutEntry {
                        binding: 0,
                        visibility: if final_sample {
                            wgpu::ShaderStages::VERTEX_FRAGMENT
                        } else {
                            wgpu::ShaderStages::FRAGMENT
                        },
                        ty: wgpu::BindingType::Buffer {
                            ty: wgpu::BufferBindingType::Uniform,
                            has_dynamic_offset: false,
                            min_binding_size: std::num::NonZeroU64::new(size),
                        },
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 1,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                        count: None,
                    },
                    wgpu::BindGroupLayoutEntry {
                        binding: 2,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Texture {
                            sample_type: wgpu::TextureSampleType::Float { filterable: true },
                            view_dimension: wgpu::TextureViewDimension::D2,
                            multisampled: false,
                        },
                        count: None,
                    },
                ],
            });
        let mask_layout = masked.then(|| {
            self.device
                .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                    label: Some("ProGPU final source vector coverage layout"),
                    entries: &[wgpu::BindGroupLayoutEntry {
                        binding: 1,
                        visibility: wgpu::ShaderStages::FRAGMENT,
                        ty: wgpu::BindingType::Texture {
                            sample_type: wgpu::TextureSampleType::Float { filterable: true },
                            view_dimension: wgpu::TextureViewDimension::D2,
                            multisampled: false,
                        },
                        count: None,
                    }],
                })
        });
        let mut layouts = vec![Some(&layout)];
        if let Some(mask) = &mask_layout {
            layouts.push(Some(mask));
        }
        let pipeline_layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU bytecode effect pipeline layout"),
                bind_group_layouts: &layouts,
                immediate_size: 0,
            });
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU validated original pixel-shader program"),
                source: wgpu::ShaderSource::Wgsl(text.into()),
            });
        let pipeline = self
            .device
            .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("ProGPU owned programmable effect"),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &module,
                    entry_point: Some("vs_main"),
                    compilation_options: Default::default(),
                    buffers: &[],
                },
                fragment: Some(wgpu::FragmentState {
                    module: &module,
                    entry_point: Some(if masked { "fs_source_mask" } else { "fs_main" }),
                    compilation_options: Default::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format,
                        blend: final_sample
                            .then_some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                primitive: Default::default(),
                depth_stencil: None,
                multisample: Default::default(),
                multiview_mask: None,
                cache: None,
            });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let program = Arc::new(GpuProgram {
            source: source.clone(),
            final_sample,
            affine,
            masked,
            format,
            layout,
            mask_layout,
            pipeline,
        });
        if let Some(index) = eviction {
            self.programs.remove(index);
        }
        self.programs.push(program.clone());
        Ok(program)
    }
    fn binding(
        &mut self,
        program: &GpuProgram,
        mode: ShaderSampling,
        image: &GpuImage,
        bytes: &[u8],
    ) -> wgpu::BindGroup {
        let uniforms = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU immutable target-specific shader constants"),
                contents: bytes,
                usage: wgpu::BufferUsages::UNIFORM,
            });
        let sampler = self.sampler(mode);
        let view = image.texture().create_view(&Default::default());
        self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU retained shader sources"),
            layout: &program.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniforms.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(&view),
                },
            ],
        })
    }
    /// Prepare a legacy output with a private RGBA8 target and immutable uniforms.
    /// Repeated encode evaluates current source pixels without changing parameters.
    pub async fn prepare_legacy(
        &mut self,
        request: LegacyShaderRequest<'_>,
    ) -> Result<PreparedShaderEffect, Error> {
        let LegacyShaderRequest {
            effect,
            input,
            sampler,
            extent,
            derivative,
        } = request;
        effect.validate()?;
        self.source(input)?;
        self.size(extent)?;
        if (0..2).any(|i| extent[i] > input.size()[i]) {
            return Err(Error::InvalidInput);
        }
        let source = sampler.unwrap_or(input);
        self.source(source)?;
        let mut uniforms = EffectUniforms {
            constants: effect.capture_constants(derivative, extent)?,
            ..Zeroable::zeroed()
        };
        uniforms.extent = [
            extent[0] as f32,
            extent[1] as f32,
            source.size()[0] as f32,
            source.size()[1] as f32,
        ];
        let valid = if sampler.is_some() {
            source.size()
        } else {
            extent
        };
        uniforms.sample_extent = [valid[0] as f32, valid[1] as f32, 0.0, 0.0];
        let program = self.program(&effect.program, false, false, false).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let binding = self.binding(
            &program,
            effect.sampling,
            source,
            bytemuck::bytes_of(&uniforms),
        );
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU original bytecode effect output"),
            size: wgpu::Extent3d {
                width: extent[0],
                height: extent[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let output = GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied)?;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedShaderEffect {
            owner: self.owner.clone(),
            program,
            binding,
            mask: None,
            input: input.clone(),
            sampler: source.clone(),
            vector_mask: None,
            output: Some(output),
            size: extent,
            metrics: ShaderEffectMetrics {
                bytecode_bytes: effect.program.bytecode().len() as u64,
                uniform_bytes: 544,
                output_texture_bytes: u64::from(extent[0]) * u64::from(extent[1]) * 4,
                draws: 1,
            },
        })
    }
    /// Prepare actual final-device evaluation from an exact v5/v6 source frame.
    /// Source pictures and per-target parameters are retained independently.
    pub async fn prepare_sample(
        &mut self,
        request: SampleShaderRequest<'_>,
    ) -> Result<PreparedShaderEffect, Error> {
        let SampleShaderRequest {
            effect,
            frame,
            target,
            input,
            sampler,
            derivative,
            vector_mask,
        } = request;
        effect.validate()?;
        self.source(input)?;
        self.size(target.size)?;
        let (frame, clip) = match frame {
            ShaderFrame::Samples(record) => (record.resolve()?, record.clip),
            ShaderFrame::Affine(record) => (record.resolve()?, record.placement.clip),
            _ => return Err(Error::InvalidInput),
        };
        if input.size() != frame.capture.size || derivative.is_some_and(|v| v >= 32) {
            return Err(Error::InvalidInput);
        }
        let source = sampler.unwrap_or(input);
        self.source(source)?;
        if let Some(mask) = vector_mask {
            if !Arc::ptr_eq(&self.owner, &mask.owner) || mask.target != target {
                return Err(Error::ResourceDomain);
            }
        }
        let (projection, reciprocal) = frame.project(target)?;
        let mut uniforms = AffineUniforms::zeroed();
        uniforms.sample.source.constants = effect.constants;
        if let Some(register) = derivative {
            uniforms.sample.source.constants[register as usize] = [
                frame.device_to_unit.diagonal[0],
                if frame.affine {
                    frame.device_to_unit.cross[0]
                } else {
                    0.0
                },
                if frame.affine {
                    frame.device_to_unit.cross[1]
                } else {
                    0.0
                },
                frame.device_to_unit.diagonal[1],
            ];
        }
        uniforms.sample.source.extent = [
            frame.capture.size[0] as f32,
            frame.capture.size[1] as f32,
            source.size()[0] as f32,
            source.size()[1] as f32,
        ];
        uniforms.sample.source.sample_extent =
            [source.size()[0] as f32, source.size()[1] as f32, 0.0, 0.0];
        uniforms.sample.quad_scale_offset = [
            projection.diagonal[0],
            projection.diagonal[1],
            projection.translation[0],
            projection.translation[1],
        ];
        uniforms.sample.output_lattice = [
            target.origin[0] as f32,
            target.origin[1] as f32,
            target.size[0] as f32,
            target.size[1] as f32,
        ];
        uniforms.sample.homogeneous = [projection.diagonal[3], reciprocal[0], reciprocal[1], 0.0];
        uniforms.sample.physical_clip = clip;
        uniforms.quad_cross = [projection.cross[0], projection.cross[1], 0.0, 0.0];
        let bytes = if frame.affine {
            bytemuck::bytes_of(&uniforms)
        } else {
            bytemuck::bytes_of(&uniforms.sample)
        };
        let program = self
            .program(&effect.program, true, frame.affine, vector_mask.is_some())
            .await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let binding = self.binding(&program, effect.sampling, source, bytes);
        let mask = if let Some(mask) = vector_mask {
            let view = mask.image.texture().create_view(&Default::default());
            Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU actual target vector coverage"),
                layout: program.mask_layout.as_ref().ok_or(Error::ResourceDomain)?,
                entries: &[wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&view),
                }],
            }))
        } else {
            None
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(PreparedShaderEffect {
            owner: self.owner.clone(),
            program,
            binding,
            mask,
            input: input.clone(),
            sampler: source.clone(),
            vector_mask: vector_mask.map(|m| m.image.clone()),
            output: None,
            size: target.size,
            metrics: ShaderEffectMetrics {
                bytecode_bytes: effect.program.bytecode().len() as u64,
                uniform_bytes: bytes.len() as u64,
                output_texture_bytes: 0,
                draws: 1,
            },
        })
    }
    /// Produce typed original path clipping for an exact target lattice.
    pub async fn prepare_vector_mask(
        &mut self,
        plan: &ClipPlan,
        origin: [i32; 2],
    ) -> Result<ShaderVectorMask, Error> {
        self.size(plan.size())?;
        let mut composer = match self.clips.take() {
            Some(v) => v,
            None => MaskComposer::new(&self.device, &self.queue, ExecutionPreference::Automatic)?,
        };
        let prepared = composer.prepare_clip(plan).await?;
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU final source vector mask"),
            });
        composer.encode(&mut encoder, &prepared)?;
        self.queue.submit([encoder.finish()]);
        self.clips = Some(composer);
        Ok(ShaderVectorMask {
            owner: self.owner.clone(),
            image: prepared.into_image(),
            target: SampleLattice {
                origin,
                size: plan.size(),
            },
        })
    }
    /// Encode a legacy full-capture effect into its own target. No queue submission
    /// or polling occurs; callers place this before the output's consumer draw.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedShaderEffect,
    ) -> Result<ShaderEffectMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) || prepared.program.final_sample {
            return Err(Error::ResourceDomain);
        }
        let output = prepared.output.as_ref().ok_or(Error::ResourceDomain)?;
        let view = output.texture().create_view(&Default::default());
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ProGPU original bytecode capture pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &view,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            ..Default::default()
        });
        pass.set_pipeline(&prepared.program.pipeline);
        pass.set_bind_group(0, &prepared.binding, &[]);
        pass.set_viewport(
            0.0,
            0.0,
            prepared.size[0] as f32,
            prepared.size[1] as f32,
            0.0,
            1.0,
        );
        pass.set_scissor_rect(0, 0, prepared.size[0], prepared.size[1]);
        pass.draw(0..3, 0..1);
        Ok(prepared.metrics)
    }
    /// Draw original final-device evaluation in the caller's actual parent pass.
    /// The caller owns painter order, load policy and subsequent viewport restoration.
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        prepared: &PreparedShaderEffect,
    ) -> Result<ShaderEffectMetrics, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) || !prepared.program.final_sample {
            return Err(Error::ResourceDomain);
        }
        prepared.draw_owned(pass, None)
    }
}
