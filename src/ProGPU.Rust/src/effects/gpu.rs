//! Canonical GPU-only effect execution with actual same-device source ownership.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    shaders,
};
use wgpu::util::DeviceExt;

const UNIFORM_STRIDE: usize = 256;

/// Actual resources and dispatches for one prepared effect chain.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct EffectMetrics {
    /// Number of separate canonical compute passes encoded per execution.
    pub compute_passes: u32,
    /// Number of original logical effect nodes.
    pub effects: u32,
    /// Actual allocated uniform bytes, including original 256-byte slots.
    pub uniform_bytes: u64,
    /// Original 16/32-byte parameter payload bytes within those slots.
    pub parameter_bytes: u64,
    /// Actual distinct intermediate RGBA8 allocations.
    pub texture_allocations: u32,
    /// Actual retained intermediate storage, including the final output.
    pub texture_bytes: u64,
    /// Total 16x16 workgroups encoded across all passes.
    pub workgroups: u64,
}

#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::ComputePipeline,
    layout: wgpu::BindGroupLayout,
}
struct Dispatch {
    pipeline: wgpu::ComputePipeline,
    binding: wgpu::BindGroup,
    offset: u32,
}

/// Prepared GPU program and its actual resources. Re-encoding evaluates current
/// input contents and rewrites this program's output. It is not an immutable
/// content cache or a claim that externally owned textures cannot be mutated.
pub struct PreparedEffects {
    owner: Arc<()>,
    dispatches: Vec<Dispatch>,
    output: GpuImage,
    workgroups: [u32; 2],
    metrics: EffectMetrics,
    plan: EffectPlan,
}
impl PreparedEffects {
    /// Actual immutable physical plan and original texture dependency order.
    pub fn plan(&self) -> &EffectPlan {
        &self.plan
    }
    /// Actual preparation storage and per-execution dispatch counts.
    pub fn metrics(&self) -> EffectMetrics {
        self.metrics
    }
}

/// Encoded output. Submit the encoder before consuming its pixels; `apply`
/// already submits the complete chain. No CPU completion wait is introduced.
pub struct EffectOutput {
    image: GpuImage,
    metrics: EffectMetrics,
}
impl EffectOutput {
    /// Actual premultiplied RGBA8 output resource.
    pub fn image(&self) -> &GpuImage {
        &self.image
    }
    /// Transfer the output's owned texture handle to a retained image draw.
    pub fn into_image(self) -> GpuImage {
        self.image
    }
    /// Actual resources and passes, independently of renderer counters.
    pub fn metrics(&self) -> EffectMetrics {
        self.metrics
    }
    /// Actual kernel execution route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
}

/// Single-device original effect engine. Pipelines are lazy and no texture pool
/// overwrites an earlier prepared result. Pixel contents are never read by CPU.
pub struct EffectEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    blur: Option<[Pipeline; 2]>,
    shadow: Option<Pipeline>,
}
impl EffectEngine {
    /// Retain the actual device/queue and typed route. The original native
    /// compute kernels are currently implemented; other forced routes reject.
    /// Device and queue must come from the same actual device request.
    /// This capability check is not adapter performance or source qualification.
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        let limits = device.limits();
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) || limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_textures_per_shader_stage < 1
            || limits.max_sampled_textures_per_shader_stage < 2
            || limits.min_uniform_buffer_offset_alignment == 0
            || 256 % limits.min_uniform_buffer_offset_alignment != 0
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            blur: None,
            shadow: None,
        })
    }
    /// Actual selected kernel route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
    /// Whether the original blur kernels have been lazily initialized.
    pub fn blur_is_initialized(&self) -> bool {
        self.blur.is_some()
    }
    /// Whether a shadow has required its separate composition pipeline.
    pub fn shadow_is_initialized(&self) -> bool {
        self.shadow.is_some()
    }

    async fn initialize(&mut self, needs_shadow: bool) -> Result<(), Error> {
        if self.blur.is_none() {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let layout = binding_layout(&self.device, false);
            let candidates = [shaders::BLUR_HORIZONTAL, shaders::BLUR_VERTICAL]
                .map(|source| create_pipeline(&self.device, source, &layout));
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.blur = Some(candidates);
        }
        if needs_shadow && self.shadow.is_none() {
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let layout = binding_layout(&self.device, true);
            let candidate = create_pipeline(&self.device, shaders::DROP_SHADOW, &layout);
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.shadow = Some(candidate);
        }
        Ok(())
    }

    /// Validate the complete original chain and allocate a private candidate.
    /// Input must be a same-device, single-sample premultiplied RGBA/BGRA capture.
    /// The caller provides padding through its actual capture extent; no hidden
    /// bounds inflation, source conversion or transparent readback is performed.
    pub async fn prepare(
        &mut self,
        input: &GpuImage,
        chain: &EffectChain,
        dpi: [f32; 2],
    ) -> Result<PreparedEffects, Error> {
        let plan = chain.plan(dpi)?;
        if input.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        if input.alpha() != ImageAlpha::Premultiplied
            || !matches!(
                input.texture().format(),
                wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
                    | wgpu::TextureFormat::Rgba16Float
            )
        {
            return Err(Error::InvalidInput);
        }
        let size = input.size();
        plan.required_texture_bytes(size)?;
        let limits = self.device.limits();
        let workgroups = size.map(|v| v.div_ceil(16));
        let uniform_bytes = u64::from(plan.pass_count()) * UNIFORM_STRIDE as u64;
        if size
            .iter()
            .any(|v| *v == 0 || *v > limits.max_texture_dimension_2d)
            || workgroups
                .iter()
                .any(|v| *v > limits.max_compute_workgroups_per_dimension)
            || uniform_bytes > limits.max_buffer_size
            || limits.max_uniform_buffer_binding_size < 32
        {
            return Err(Error::Capacity);
        }
        self.initialize(plan.steps.iter().any(|s| s.shadow.is_some()))
            .await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let result = self.allocate(input, plan, workgroups);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        result
    }

    fn allocate(
        &self,
        input: &GpuImage,
        plan: EffectPlan,
        workgroups: [u32; 2],
    ) -> Result<PreparedEffects, Error> {
        let device = &self.device;
        let texture_count = plan.texture_count();
        let size = input.size();
        let texture_bytes = plan.required_texture_bytes(size)?;
        let mut textures = Vec::new();
        let mut views = Vec::new();
        textures
            .try_reserve_exact(texture_count)
            .map_err(|_| Error::Capacity)?;
        views
            .try_reserve_exact(texture_count)
            .map_err(|_| Error::Capacity)?;
        for _ in 0..texture_count {
            let texture = device.create_texture(&wgpu::TextureDescriptor {
                label: Some("ProGPU owned effect intermediate"),
                size: wgpu::Extent3d {
                    width: size[0],
                    height: size[1],
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba8Unorm,
                usage: wgpu::TextureUsages::TEXTURE_BINDING
                    | wgpu::TextureUsages::STORAGE_BINDING
                    | wgpu::TextureUsages::RENDER_ATTACHMENT,
                view_formats: &[],
            });
            views.push(texture.create_view(&Default::default()));
            textures.push(texture);
        }
        let source_view = input.texture().create_view(&wgpu::TextureViewDescriptor {
            base_mip_level: 0,
            mip_level_count: Some(1),
            ..Default::default()
        });
        let mut uniform_data = vec![0u8; plan.pass_count() as usize * UNIFORM_STRIDE];
        let mut offset = 0;
        let mut parameter_bytes = 0;
        for step in &plan.steps {
            for blur in &step.blur {
                let bytes = bytemuck::bytes_of(blur);
                uniform_data[offset..offset + bytes.len()].copy_from_slice(bytes);
                offset += UNIFORM_STRIDE;
                parameter_bytes += bytes.len() as u64;
            }
            if let Some(shadow) = &step.shadow {
                let bytes = bytemuck::bytes_of(shadow);
                uniform_data[offset..offset + bytes.len()].copy_from_slice(bytes);
                offset += UNIFORM_STRIDE;
                parameter_bytes += bytes.len() as u64;
            }
        }
        let uniforms = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("ProGPU immutable aligned effect parameters"),
            contents: &uniform_data,
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let mut dispatches = Vec::new();
        dispatches
            .try_reserve_exact(plan.pass_count() as usize)
            .map_err(|_| Error::Capacity)?;
        let blur = self.blur.as_ref().ok_or(Error::UnsupportedExecution)?;
        for step in &plan.steps {
            let source = match step.source {
                EffectSource::Input => &source_view,
                EffectSource::Intermediate(slot) => &views[usize::from(slot)],
            };
            for (pipeline, input, output) in [
                (&blur[0], source, &views[usize::from(step.horizontal)]),
                (
                    &blur[1],
                    &views[usize::from(step.horizontal)],
                    &views[usize::from(step.vertical)],
                ),
            ] {
                let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU original blur source/destination"),
                    layout: &pipeline.layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::TextureView(input),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: wgpu::BindingResource::TextureView(output),
                        },
                        uniform_binding(2, &uniforms, 16),
                    ],
                });
                dispatches.push(Dispatch {
                    pipeline: pipeline.pipeline.clone(),
                    binding,
                    offset: (dispatches.len() * UNIFORM_STRIDE) as u32,
                });
            }
            if step.shadow.is_some() {
                let pipeline = self.shadow.as_ref().ok_or(Error::UnsupportedExecution)?;
                let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU original source-over-shadow"),
                    layout: &pipeline.layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::TextureView(source),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: wgpu::BindingResource::TextureView(
                                &views[usize::from(step.vertical)],
                            ),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: wgpu::BindingResource::TextureView(
                                &views[usize::from(step.output)],
                            ),
                        },
                        uniform_binding(3, &uniforms, 32),
                    ],
                });
                dispatches.push(Dispatch {
                    pipeline: pipeline.pipeline.clone(),
                    binding,
                    offset: (dispatches.len() * UNIFORM_STRIDE) as u32,
                });
            }
        }
        let output_slot = usize::from(plan.steps.last().ok_or(Error::InvalidInput)?.output);
        let output =
            GpuImage::from_texture(device, &textures[output_slot], ImageAlpha::Premultiplied)?;
        let metrics = EffectMetrics {
            compute_passes: plan.pass_count(),
            effects: plan.steps.len() as u32,
            uniform_bytes: uniforms.size(),
            parameter_bytes,
            texture_allocations: texture_count as u32,
            texture_bytes,
            workgroups: u64::from(workgroups[0])
                * u64::from(workgroups[1])
                * u64::from(plan.pass_count()),
        };
        Ok(PreparedEffects {
            owner: self.owner.clone(),
            dispatches,
            output,
            workgroups,
            metrics,
            plan,
        })
    }

    /// Encode every original pass into the caller's same-device encoder. Input
    /// and all intermediates remain submission-owned through their bind groups.
    /// Reuse uploads nothing; each invocation recomputes the original program.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        prepared: &PreparedEffects,
    ) -> Result<EffectOutput, Error> {
        if !Arc::ptr_eq(&self.owner, &prepared.owner) {
            return Err(Error::ResourceDomain);
        }
        for dispatch in &prepared.dispatches {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU original ordered group effect"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&dispatch.pipeline);
            pass.set_bind_group(0, &dispatch.binding, &[dispatch.offset]);
            pass.dispatch_workgroups(prepared.workgroups[0], prepared.workgroups[1], 1);
        }
        Ok(EffectOutput {
            image: prepared.output.clone(),
            metrics: prepared.metrics,
        })
    }

    /// Prepare and submit one complete chain with no CPU readback or GPU wait.
    /// Earlier results own independent textures and cannot be overwritten by a
    /// later call. No cache infers content immutability from a texture handle.
    pub async fn apply(
        &mut self,
        input: &GpuImage,
        chain: &EffectChain,
        dpi: [f32; 2],
    ) -> Result<EffectOutput, Error> {
        let prepared = self.prepare(input, chain, dpi).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU complete group effect chain"),
            });
        let output = self.encode(&mut encoder, &prepared);
        let commands = encoder.finish();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let output = output?;
        self.queue.submit([commands]);
        Ok(output)
    }
}

fn uniform_binding(binding: u32, buffer: &wgpu::Buffer, size: u64) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
            buffer,
            offset: 0,
            size: std::num::NonZeroU64::new(size),
        }),
    }
}
fn binding_layout(device: &wgpu::Device, shadow: bool) -> wgpu::BindGroupLayout {
    let texture = |binding| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    };
    let mut entries = vec![texture(0)];
    if shadow {
        entries.push(texture(1));
    }
    entries.push(wgpu::BindGroupLayoutEntry {
        binding: entries.len() as u32,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::StorageTexture {
            access: wgpu::StorageTextureAccess::WriteOnly,
            format: wgpu::TextureFormat::Rgba8Unorm,
            view_dimension: wgpu::TextureViewDimension::D2,
        },
        count: None,
    });
    entries.push(wgpu::BindGroupLayoutEntry {
        binding: entries.len() as u32,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: true,
            min_binding_size: std::num::NonZeroU64::new(if shadow { 32 } else { 16 }),
        },
        count: None,
    });
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU canonical group-effect bindings"),
        entries: &entries,
    })
}
fn create_pipeline(
    device: &wgpu::Device,
    source: &'static str,
    layout: &wgpu::BindGroupLayout,
) -> Pipeline {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("ProGPU canonical group-effect WGSL"),
        source: wgpu::ShaderSource::Wgsl(source.into()),
    });
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("ProGPU original aligned effect layout"),
        bind_group_layouts: &[Some(layout)],
        immediate_size: 0,
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some("ProGPU original group-effect kernel"),
        layout: Some(&pipeline_layout),
        module: &shader,
        entry_point: Some("main"),
        compilation_options: Default::default(),
        cache: None,
    });
    Pipeline {
        pipeline,
        layout: layout.clone(),
    }
}
