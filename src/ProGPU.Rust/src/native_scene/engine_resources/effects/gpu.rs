use super::*;
use crate::{
    effects::EffectSource,
    native_scene::engine_resources::layers::{NativeLayerTarget, NativeLayerTexture},
    shaders,
};
use std::num::NonZeroU64;

#[derive(Clone)]
pub struct NativeEffectRequest {
    pub plan: Arc<NativeEffectPlan>,
    pub target: Arc<NativeLayerTarget>,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeEffectMetrics {
    pub compute_passes: u64,
    pub workgroups: u64,
    pub uniform_upload_bytes: u64,
    pub buffer_allocations: u64,
    pub allocated_buffer_bytes: u64,
    pub bind_groups: u64,
    pub pipelines_created: u64,
}
#[derive(Clone)]
struct Resources {
    blur: wgpu::BindGroupLayout,
    shadow: Option<wgpu::BindGroupLayout>,
    horizontal: wgpu::ComputePipeline,
    vertical: wgpu::ComputePipeline,
    drop_shadow: Option<wgpu::ComputePipeline>,
}
#[derive(Clone)]
struct Pass {
    binding_key: (i32, i32, u8),
    pipeline: wgpu::ComputePipeline,
    binding: wgpu::BindGroup,
    offset: u32,
}
#[derive(Clone)]
struct Program {
    request: NativeEffectRequest,
    passes: Vec<Pass>,
    steps: Vec<std::ops::Range<usize>>,
    workgroups: [u32; 2],
}
/// Native semantic chain executor borrowing the actual three layer scratch
/// textures. Preparation never submits and encoding never calls source owners.
pub struct NativeEffectEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    resources: Option<Resources>,
    capacity: u64,
}
#[derive(Clone)]
pub struct PreparedNativeEffects {
    device: wgpu::Device,
    programs: Arc<[Program]>,
    uniform_buffer: Option<wgpu::Buffer>,
    metrics: NativeEffectMetrics,
}
impl NativeEffectEngine {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        Self {
            device: device.clone(),
            queue: queue.clone(),
            resources: None,
            capacity: 0,
        }
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: crate::path::ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            crate::path::ExecutionPreference::Automatic
                | crate::path::ExecutionPreference::NativeCompute
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self::new(device, queue))
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn execution_path(&self) -> crate::path::ExecutionPath {
        crate::path::ExecutionPath::NativeCompute
    }
    pub fn initialized_pipeline_count(&self) -> usize {
        self.resources
            .as_ref()
            .map_or(0, |r| 2 + usize::from(r.drop_shadow.is_some()))
    }
    pub fn preflight(
        &self,
        requests: &[NativeEffectRequest],
    ) -> Result<NativeEffectMetrics, Error> {
        self.preflight_capacity(requests, None)
    }
    /// Original full-scene slab includes preflighted effect passes even when
    /// an actual retained layer-cache hit omits that program from this replay.
    pub fn preflight_for_plan(
        &self,
        plan: &crate::native_scene::execution::NativeLayerExecutionPlan,
        requests: &[NativeEffectRequest],
    ) -> Result<NativeEffectMetrics, Error> {
        let presentation = plan.presentation();
        if requests.iter().any(|r| {
            !Arc::ptr_eq(r.plan.source(), plan.scene())
                || r.plan.dpi() != [presentation.dpi_scale_x, presentation.dpi_scale_y]
        }) {
            return Err(Error::ResourceDomain);
        }
        self.preflight_capacity(requests, Some(plan.metrics().effect_pass_count))
    }
    fn preflight_capacity(
        &self,
        requests: &[NativeEffectRequest],
        pass_capacity: Option<u32>,
    ) -> Result<NativeEffectMetrics, Error> {
        let mut m = NativeEffectMetrics::default();
        let limits = self.device.limits();
        for r in requests {
            let color = r.target.color().ok_or(Error::ResourceDomain)?;
            let effects = r.target.effects().ok_or(Error::ResourceDomain)?;
            if color.device() != &self.device
                || effects.size() != color.size()
                || color.texture().sample_count() != 1
                || !color
                    .texture()
                    .usage()
                    .contains(wgpu::TextureUsages::TEXTURE_BINDING)
            {
                return Err(Error::ResourceDomain);
            }
            if !matches!(
                color.texture().format(),
                wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
                    | wgpu::TextureFormat::R8Unorm
            ) {
                return Err(Error::UnsupportedExecution);
            }
            for t in effects.textures() {
                if t.device() != &self.device
                    || t.size() != color.size()
                    || t.texture().format() != wgpu::TextureFormat::Rgba8Unorm
                    || t.texture().sample_count() != 1
                    || !t.texture().usage().contains(
                        wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::STORAGE_BINDING,
                    )
                {
                    return Err(Error::ResourceDomain);
                }
            }
            let groups = color.size().map(|n| n.div_ceil(16));
            if groups
                .into_iter()
                .any(|n| n > limits.max_compute_workgroups_per_dimension)
            {
                return Err(Error::Capacity);
            }
            m.compute_passes = m
                .compute_passes
                .checked_add(u64::from(r.plan.pass_count()))
                .ok_or(Error::Capacity)?;
            m.workgroups = m
                .workgroups
                .checked_add(
                    u64::from(groups[0]) * u64::from(groups[1]) * u64::from(r.plan.pass_count()),
                )
                .ok_or(Error::Capacity)?;
        }
        let pass_capacity = pass_capacity.map(u64::from).unwrap_or(m.compute_passes);
        if pass_capacity < m.compute_passes {
            return Err(Error::InvalidInput);
        }
        if pass_capacity == 0 {
            return Ok(m);
        }
        if pass_capacity > u64::from(crate::native_scene::execution::budget::MAX_EFFECT_PASSES)
            || limits.max_compute_invocations_per_workgroup < 256
            || limits.max_compute_workgroup_size_x < 16
            || limits.max_compute_workgroup_size_y < 16
            || limits.max_storage_textures_per_shader_stage < 1
            || limits.max_sampled_textures_per_shader_stage < 2
            || limits.max_uniform_buffer_binding_size < 32
            || limits.max_dynamic_uniform_buffers_per_pipeline_layout < 1
            || limits.max_bindings_per_bind_group < 4
            || limits.min_uniform_buffer_offset_alignment == 0
            || 256 % limits.min_uniform_buffer_offset_alignment != 0
        {
            return Err(Error::Capacity);
        }
        m.uniform_upload_bytes = pass_capacity * 256;
        buffer_capacity(
            self.capacity,
            m.uniform_upload_bytes,
            limits.max_buffer_size,
        )?;
        Ok(m)
    }
    pub async fn prepare(
        &mut self,
        requests: &[NativeEffectRequest],
    ) -> Result<PreparedNativeEffects, Error> {
        let metrics = self.preflight(requests)?;
        self.prepare_admitted(requests, metrics, false).await
    }
    pub async fn prepare_for_plan(
        &mut self,
        plan: &crate::native_scene::execution::NativeLayerExecutionPlan,
        requests: &[NativeEffectRequest],
    ) -> Result<PreparedNativeEffects, Error> {
        let metrics = self.preflight_for_plan(plan, requests)?;
        self.prepare_admitted(requests, metrics, plan.metrics().has_drop_shadows)
            .await
    }
    async fn prepare_admitted(
        &mut self,
        requests: &[NativeEffectRequest],
        mut metrics: NativeEffectMetrics,
        original_shadow_inventory: bool,
    ) -> Result<PreparedNativeEffects, Error> {
        if metrics.uniform_upload_bytes == 0 {
            return Ok(PreparedNativeEffects {
                device: self.device.clone(),
                programs: Arc::from([]),
                uniform_buffer: None,
                metrics,
            });
        }
        let capacity = buffer_capacity(
            self.capacity,
            metrics.uniform_upload_bytes,
            self.device.limits().max_buffer_size,
        )?;
        let shadow = original_shadow_inventory
            || requests
                .iter()
                .any(|r| r.plan.steps().iter().any(|s| s.shadow_uniforms().is_some()));
        let mut bytes = allocate(metrics.uniform_upload_bytes as usize)?;
        for r in requests {
            bytes.extend_from_slice(r.plan.uniform_bytes());
        }
        bytes.resize(metrics.uniform_upload_bytes as usize, 0);
        let mut programs = allocate(requests.len())?;
        for request in requests {
            programs.push(Program {
                request: request.clone(),
                passes: allocate(request.plan.pass_count() as usize)?,
                steps: allocate(request.plan.steps().len())?,
                workgroups: request
                    .target
                    .effects()
                    .expect("whole target preflight")
                    .size()
                    .map(|n| n.div_ceil(16)),
            });
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let resources = Resources::prepare(&self.device, self.resources.as_ref(), shadow);
        let buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Native effect immutable aligned uniform slab"),
            size: capacity,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        self.queue.write_buffer(&buffer, 0, &bytes);
        let mut offset = 0u32;
        for program in &mut programs {
            let request = &program.request;
            let passes = &mut program.passes;
            let steps = &mut program.steps;
            for step in request.plan.steps() {
                let begin = passes.len();
                let [h, v, out] = step.texture_slots();
                let source = match step.source() {
                    EffectSource::Input => -1,
                    EffectSource::Intermediate(n) => i32::from(n),
                };
                for (input, output, pipeline) in [
                    (source, h, &resources.horizontal),
                    (i32::from(h), v, &resources.vertical),
                ] {
                    let binding_key = (input, -1, output);
                    let binding = if let Some(old) =
                        passes.iter().find(|p| p.binding_key == binding_key)
                    {
                        old.binding.clone()
                    } else {
                        metrics.bind_groups += 1;
                        self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                            label: Some("Native effect blur source/output"),
                            layout: &resources.blur,
                            entries: &[
                                wgpu::BindGroupEntry {
                                    binding: 0,
                                    resource: wgpu::BindingResource::TextureView(
                                        request
                                            .target
                                            .effect_source(input)
                                            .expect("whole target preflight"),
                                    ),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 1,
                                    resource: wgpu::BindingResource::TextureView(
                                        request
                                            .target
                                            .effect_source(i32::from(output))
                                            .expect("three scratch textures"),
                                    ),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 2,
                                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                                        buffer: &buffer,
                                        offset: 0,
                                        size: NonZeroU64::new(16),
                                    }),
                                },
                            ],
                        })
                    };
                    passes.push(Pass {
                        binding_key,
                        pipeline: pipeline.clone(),
                        binding,
                        offset,
                    });
                    offset += 256;
                }
                if step.shadow_uniforms().is_some() {
                    let binding_key = (source, i32::from(v), out);
                    let binding = if let Some(old) =
                        passes.iter().find(|p| p.binding_key == binding_key)
                    {
                        old.binding.clone()
                    } else {
                        metrics.bind_groups += 1;
                        self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                            label: Some("Native effect source/blurred shadow"),
                            layout: resources.shadow.as_ref().expect("shadow demanded"),
                            entries: &[
                                wgpu::BindGroupEntry {
                                    binding: 0,
                                    resource: wgpu::BindingResource::TextureView(
                                        request.target.effect_source(source).expect("source"),
                                    ),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 1,
                                    resource: wgpu::BindingResource::TextureView(
                                        request
                                            .target
                                            .effect_source(i32::from(v))
                                            .expect("vertical"),
                                    ),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 2,
                                    resource: wgpu::BindingResource::TextureView(
                                        request
                                            .target
                                            .effect_source(i32::from(out))
                                            .expect("output"),
                                    ),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 3,
                                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                                        buffer: &buffer,
                                        offset: 0,
                                        size: NonZeroU64::new(32),
                                    }),
                                },
                            ],
                        })
                    };
                    passes.push(Pass {
                        binding_key,
                        pipeline: resources
                            .drop_shadow
                            .as_ref()
                            .expect("shadow demanded")
                            .clone(),
                        binding,
                        offset,
                    });
                    offset += 256;
                }
                steps.push(begin..passes.len());
            }
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        metrics.buffer_allocations = 1;
        metrics.allocated_buffer_bytes = capacity;
        metrics.pipelines_created = (2 + usize::from(resources.drop_shadow.is_some())
            - self.initialized_pipeline_count()) as u64;
        self.capacity = capacity;
        self.resources = Some(resources);
        Ok(PreparedNativeEffects {
            device: self.device.clone(),
            programs: programs.into(),
            uniform_buffer: Some(buffer),
            metrics,
        })
    }
}
impl PreparedNativeEffects {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn metrics(&self) -> NativeEffectMetrics {
        self.metrics
    }
    pub fn len(&self) -> usize {
        self.programs.len()
    }
    pub fn is_empty(&self) -> bool {
        self.programs.is_empty()
    }
    pub fn request(&self, index: usize) -> Option<&NativeEffectRequest> {
        self.programs.get(index).map(|p| &p.request)
    }
    pub fn output(&self, index: usize) -> Option<&NativeLayerTexture> {
        let p = self.programs.get(index)?;
        p.request
            .target
            .effects()?
            .textures()
            .get(p.request.plan.final_texture() as usize)
    }
    pub fn uniform_buffer(&self) -> Option<&wgpu::Buffer> {
        self.uniform_buffer.as_ref()
    }
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder, index: usize) -> Result<(), Error> {
        let p = self.programs.get(index).ok_or(Error::InvalidInput)?;
        encode_passes(encoder, &p.passes, p.workgroups);
        Ok(())
    }
    pub fn encode_step(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        index: usize,
        step: usize,
    ) -> Result<(), Error> {
        let p = self.programs.get(index).ok_or(Error::InvalidInput)?;
        let range = p.steps.get(step).ok_or(Error::InvalidInput)?.clone();
        encode_passes(encoder, &p.passes[range], p.workgroups);
        Ok(())
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn encode_passes(encoder: &mut wgpu::CommandEncoder, passes: &[Pass], groups: [u32; 2]) {
    for p in passes {
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("Native ordered built-in effect pass"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&p.pipeline);
        pass.set_bind_group(0, &p.binding, &[p.offset]);
        pass.dispatch_workgroups(groups[0], groups[1], 1);
    }
}
impl Resources {
    fn prepare(device: &wgpu::Device, previous: Option<&Self>, needs_shadow: bool) -> Self {
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
        let output = |binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::StorageTexture {
                access: wgpu::StorageTextureAccess::WriteOnly,
                format: wgpu::TextureFormat::Rgba8Unorm,
                view_dimension: wgpu::TextureViewDimension::D2,
            },
            count: None,
        };
        let uniform = |binding, bytes| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty: wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: true,
                min_binding_size: NonZeroU64::new(bytes),
            },
            count: None,
        };
        let pipeline =
            |layout: &wgpu::BindGroupLayout, source: &'static str, label: &'static str| {
                let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some(label),
                    source: wgpu::ShaderSource::Wgsl(source.into()),
                });
                let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some(label),
                    bind_group_layouts: &[layout],
                    immediate_size: 0,
                });
                device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                    label: Some(label),
                    layout: Some(&layout),
                    module: &shader,
                    entry_point: Some("main"),
                    compilation_options: Default::default(),
                    cache: None,
                })
            };
        let mut r = if let Some(p) = previous {
            p.clone()
        } else {
            let blur = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("Native dynamic blur layout"),
                entries: &[texture(0), output(1), uniform(2, 16)],
            });
            Self {
                horizontal: pipeline(&blur, shaders::BLUR_HORIZONTAL, "Native horizontal blur"),
                vertical: pipeline(&blur, shaders::BLUR_VERTICAL, "Native vertical blur"),
                blur,
                shadow: None,
                drop_shadow: None,
            }
        };
        if needs_shadow && r.shadow.is_none() {
            let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("Native dynamic shadow layout"),
                entries: &[texture(0), texture(1), output(2), uniform(3, 32)],
            });
            r.drop_shadow = Some(pipeline(
                &layout,
                shaders::DROP_SHADOW,
                "Native drop shadow",
            ));
            r.shadow = Some(layout);
        }
        r
    }
}
pub(super) fn buffer_capacity(previous: u64, required: u64, maximum: u64) -> Result<u64, Error> {
    if required == 0 || required > maximum {
        return Err(Error::Capacity);
    }
    let mut size = previous.max(256).min(maximum);
    while size < required {
        size = size.saturating_mul(2).min(maximum);
    }
    Ok(size)
}
