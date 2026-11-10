use super::*;
use crate::native_scene::{
    engine_gpu::{
        NativeGroupPipeline, NativeMaskBinding, NativeMaskMode, NativePipelineCache,
        NativeRenderPipeline, NativeWriteChannels, PreparedNativeMaterials,
    },
    engine_resources::layers::{
        NativeLayerPoolOptions, NativeLayerResourceMetrics, NativeLayerTarget, NativeLayerTargetId,
        NativeLayerTargetPool, PreparedNativeLayerComposite, PreparedNativeLayerTargets,
    },
    execution::{
        NativeSceneFrame,
        budget::{
            NativeCacheBudget, NativeLayerBudget, NativeResidencyBudget, NativeResidencyReservation,
        },
    },
};
use std::num::NonZeroU64;

#[derive(Clone, Copy, Debug)]
pub struct NativeAdvancedScratchRequest {
    pub frame: NativeSceneFrame,
    pub source_size: [u32; 2],
    pub needs_layer_coverage: bool,
}
impl NativeAdvancedScratchRequest {
    pub fn from_layer_plan(
        plan: &crate::native_scene::execution::NativeLayerExecutionPlan,
    ) -> Self {
        let m = plan.metrics();
        Self {
            frame: plan.frame(),
            source_size: [m.advanced_source_width, m.advanced_source_height],
            needs_layer_coverage: m.needs_layer_coverage,
        }
    }
}
/// Actual source/parent owners selected by the ordered replay program. The
/// separate sampling descriptor retains original semantic (possibly empty) bounds.
#[derive(Clone)]
pub struct NativeAdvancedBlendRequest {
    pub source: Arc<NativeLayerTarget>,
    pub parent: Arc<NativeLayerTarget>,
    pub sampling: NativeAdvancedBlendSampling,
    pub final_effect_texture: Option<u8>,
    pub mask: Option<Arc<NativeMaskBinding>>,
    pub first_resolve_vertex: u32,
    pub first_copy_vertex: u32,
    pub target_ignores_alpha: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeAdvancedBlendMetrics {
    pub render_passes: u64,
    pub draws: u64,
    pub uniform_upload_bytes: u64,
    pub allocated_buffer_bytes: u64,
    pub buffer_allocations: u64,
    pub bind_groups: u64,
    pub pipelines_created: u64,
    pub coverage_texture_bytes: u64,
    pub coverage_texture_allocations: u64,
    pub layers: NativeLayerResourceMetrics,
}
struct Coverage {
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    size: [u32; 2],
    _reservation: NativeResidencyReservation,
}
#[derive(Clone)]
struct Resources {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::RenderPipeline,
}
#[derive(Clone)]
struct Operation {
    request: NativeAdvancedBlendRequest,
    binding: wgpu::BindGroup,
    resolve: wgpu::RenderPipeline,
    copy: wgpu::RenderPipeline,
}
/// Dedicated scratch uses the same native physical texture/frame/retirement
/// owners as materialized layers. Scratch slots are not source layer history.
pub struct NativeAdvancedBlendEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: wgpu::TextureFormat,
    pool: NativeLayerTargetPool,
    coverage: Option<Arc<Coverage>>,
    resources: Option<Resources>,
    capacity: u64,
}
#[derive(Clone)]
pub struct PreparedNativeAdvancedBlends {
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    scratch: Option<PreparedNativeLayerTargets>,
    coverage: Option<Arc<Coverage>>,
    resources: Option<Resources>,
    uniforms: Option<wgpu::Buffer>,
    composite: Arc<PreparedNativeLayerComposite>,
    operations: Arc<[Operation]>,
    metrics: NativeAdvancedBlendMetrics,
}
impl NativeAdvancedBlendEngine {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        residency: Arc<NativeResidencyBudget>,
    ) -> Result<Self, Error> {
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            format,
            pool: NativeLayerTargetPool::new(device, format, residency)?,
            coverage: None,
            resources: None,
            capacity: 0,
        })
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: wgpu::TextureFormat,
        residency: Arc<NativeResidencyBudget>,
        preference: crate::path::ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            crate::path::ExecutionPreference::Automatic
                | crate::path::ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Self::new(device, queue, format, residency)
    }
    pub fn execution_path(&self) -> crate::path::ExecutionPath {
        crate::path::ExecutionPath::GpuShader
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn target_format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn preflight(
        &self,
        pipelines: &NativePipelineCache,
        material: &PreparedNativeMaterials,
        scratch: NativeAdvancedScratchRequest,
        requests: &[NativeAdvancedBlendRequest],
        composite: &PreparedNativeLayerComposite,
    ) -> Result<NativeAdvancedBlendMetrics, Error> {
        if pipelines.device() != &self.device
            || pipelines.target_format() != self.format
            || material.device() != &self.device
            || composite.device() != &self.device
        {
            return Err(Error::ResourceDomain);
        }
        let mut metrics = NativeAdvancedBlendMetrics::default();
        if requests.is_empty() {
            return Ok(metrics);
        }
        scratch
            .frame
            .resolve_presentation()
            .ok_or(Error::InvalidInput)?;
        let limits = self.device.limits();
        if scratch.source_size.contains(&0)
            || scratch
                .source_size
                .into_iter()
                .any(|v| v > limits.max_texture_dimension_2d)
            || limits.max_sampled_textures_per_shader_stage < 3
            || limits.max_uniform_buffer_binding_size < 96
            || limits.min_uniform_buffer_offset_alignment == 0
            || 256 % limits.min_uniform_buffer_offset_alignment != 0
        {
            return Err(Error::Capacity);
        }
        if requests.len() > crate::native_scene::execution::budget::MAX_DRAW_PASSES as usize {
            return Err(Error::Capacity);
        }
        for request in requests {
            let source = request.source.color().ok_or(Error::ResourceDomain)?;
            let parent = request.parent.color().ok_or(Error::ResourceDomain)?;
            if source.device() != &self.device
                || parent.device() != &self.device
                || source.texture().format() != self.format
                || parent.texture().format() != self.format
                || request.parent.frame().is_none()
                || request.source.frame().is_none()
            {
                return Err(Error::ResourceDomain);
            }
            let [width, height] = parent.size();
            if width > scratch.frame.width
                || height > scratch.frame.height
                || request.sampling.source_size()[0] > scratch.source_size[0]
                || request.sampling.source_size()[1] > scratch.source_size[1]
            {
                return Err(Error::Capacity);
            }
            let source_extent = request.sampling.source();
            if source_extent.width > source.size()[0]
                || source_extent.height > source.size()[1]
                || request
                    .parent
                    .frame()
                    .expect("frame admitted")
                    .uniforms()
                    .dpi_scale
                    != scratch.frame.dpi_scale
                || request
                    .source
                    .frame()
                    .expect("frame admitted")
                    .uniforms()
                    .dpi_scale
                    != scratch.frame.dpi_scale
            {
                return Err(Error::ResourceDomain);
            }
            if source.texture() == parent.texture() {
                return Err(Error::ResourceDomain);
            }
            if let Some(index) = request.final_effect_texture {
                if request
                    .source
                    .effects()
                    .and_then(|v| v.textures().get(index as usize))
                    .is_none()
                {
                    return Err(Error::ResourceDomain);
                }
            }
            if let Some(mask) = &request.mask {
                if mask.device() != &self.device {
                    return Err(Error::ResourceDomain);
                }
                // Native group resolves use the single mask layout. Analytic
                // chains must be realized by the original mask producer first.
                if mask.mode() != NativeMaskMode::Sampled {
                    return Err(Error::UnsupportedExecution);
                }
            }
            if request.sampling.mode().coverage() && !scratch.needs_layer_coverage {
                return Err(Error::InvalidInput);
            }
            if let NativeAdvancedBlendMode::ReplaceAxisBackground { linear_byte } =
                request.sampling.mode()
            {
                if linear_byte
                    != matches!(
                        self.format,
                        wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Bgra8Unorm
                    )
                {
                    return Err(Error::InvalidInput);
                }
            }
            for first in [request.first_resolve_vertex, request.first_copy_vertex] {
                admit_quad(composite, first)?;
            }
        }
        let layers = scratch_budget(scratch);
        let plan = self.pool.plan(
            scratch.frame,
            &layers,
            &NativeCacheBudget::default(),
            NativeLayerPoolOptions::default(),
        )?;
        self.pool.preflight(pipelines, material, &plan)?;
        let coverage_bytes = if scratch.needs_layer_coverage {
            texture_bytes(scratch.source_size)?
        } else {
            0
        };
        if plan
            .source_texture_bytes()
            .checked_add(coverage_bytes)
            .ok_or(Error::Capacity)?
            > u64::from(SCENE_MAX_LAYER_BYTES)
        {
            return Err(Error::Capacity);
        }
        buffer_capacity(
            self.capacity,
            requests.len() as u64 * 256,
            limits.max_buffer_size,
        )?;
        metrics.render_passes = requests.len() as u64 * 3;
        metrics.draws = metrics.render_passes;
        metrics.uniform_upload_bytes = requests.len() as u64 * 96;
        Ok(metrics)
    }
    pub async fn prepare(
        &mut self,
        pipelines: &mut NativePipelineCache,
        material: Arc<PreparedNativeMaterials>,
        scratch_request: NativeAdvancedScratchRequest,
        requests: &[NativeAdvancedBlendRequest],
        composite: Arc<PreparedNativeLayerComposite>,
    ) -> Result<PreparedNativeAdvancedBlends, Error> {
        let mut metrics =
            self.preflight(pipelines, &material, scratch_request, requests, &composite)?;
        if requests.is_empty() {
            return Ok(PreparedNativeAdvancedBlends {
                device: self.device.clone(),
                format: self.format,
                scratch: None,
                coverage: None,
                resources: None,
                uniforms: None,
                composite,
                operations: Arc::from([]),
                metrics,
            });
        }
        let capacity = buffer_capacity(
            self.capacity,
            requests.len() as u64 * 256,
            self.device.limits().max_buffer_size,
        )?;
        // Reserve the independent coverage attachment before any GPU mutation.
        let new_coverage = scratch_request.needs_layer_coverage
            && self
                .coverage
                .as_ref()
                .is_none_or(|v| v.size != scratch_request.source_size);
        let reservation = if new_coverage {
            Some(
                self.pool
                    .residency()
                    .try_acquire(texture_bytes(scratch_request.source_size)?)
                    .ok_or(Error::Capacity)?,
            )
        } else {
            None
        };
        let mut candidate_pool = self.pool.fork_candidate();
        let scratch = candidate_pool
            .prepare(
                pipelines,
                material,
                scratch_request.frame,
                &scratch_budget(scratch_request),
                &NativeCacheBudget::default(),
                NativeLayerPoolOptions::default(),
            )
            .await?;
        let source = scratch
            .target(NativeLayerTargetId::Slot(0))
            .ok_or(Error::ResourceDomain)?;
        let output = scratch
            .target(NativeLayerTargetId::Slot(1))
            .ok_or(Error::ResourceDomain)?;
        let mut selected = allocate(requests.len())?;
        for request in requests {
            let resolve = group_key(
                request.mask.is_some(),
                request.sampling.mode().coverage(),
                false,
            );
            let copy = group_key(false, false, request.target_ignores_alpha);
            let (resolve, hit) = pipelines.prepare(resolve).await?;
            metrics.pipelines_created += u64::from(!hit);
            let (copy, hit) = pipelines.prepare(copy).await?;
            metrics.pipelines_created += u64::from(!hit);
            selected.push((resolve, copy));
        }
        let mut operations = allocate(requests.len())?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let coverage = if let Some(reservation) = reservation {
            let texture = self.device.create_texture(&wgpu::TextureDescriptor {
                label: Some("Native independent floating layer coverage"),
                size: wgpu::Extent3d {
                    width: scratch_request.source_size[0],
                    height: scratch_request.source_size[1],
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::R32Float,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                    | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats: &[],
            });
            let view = texture.create_view(&Default::default());
            metrics.coverage_texture_allocations = 1;
            metrics.coverage_texture_bytes = reservation.bytes();
            Some(Arc::new(Coverage {
                texture,
                view,
                size: scratch_request.source_size,
                _reservation: reservation,
            }))
        } else {
            self.coverage.clone()
        };
        let resources = self
            .resources
            .clone()
            .unwrap_or_else(|| Resources::new(&self.device, self.format));
        if self.resources.is_none() {
            metrics.pipelines_created += 1;
        }
        let uniforms = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Native immutable advanced blend uniform slots"),
            size: capacity,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        for (index, (request, (resolve, copy))) in requests.iter().zip(selected).enumerate() {
            let offset = index as u64 * 256;
            self.queue.write_buffer(
                &uniforms,
                offset,
                bytemuck::bytes_of(request.sampling.uniforms()),
            );
            let color = request.parent.color().expect("whole owner preflight");
            let source_view = if request.sampling.mode().coverage() {
                request.source.color().expect("source").view()
            } else {
                source.color().expect("scratch source").view()
            };
            let pattern_view = if request.sampling.mode().coverage() {
                &coverage.as_ref().expect("coverage requested").view
            } else {
                source_view
            };
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("Native advanced blend actual destination/source/coverage"),
                layout: &resources.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureView(color.view()),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::TextureView(source_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: wgpu::BindingResource::TextureView(pattern_view),
                    },
                    wgpu::BindGroupEntry {
                        binding: 3,
                        resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                            buffer: &uniforms,
                            offset,
                            size: NonZeroU64::new(96),
                        }),
                    },
                ],
            });
            operations.push(Operation {
                request: request.clone(),
                binding,
                resolve,
                copy,
            });
        }
        // Scratch output is bound by its retained canonical linear texture group.
        output
            .color()
            .expect("scratch color admitted before GPU preparation");
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        metrics.layers = scratch.metrics();
        metrics.buffer_allocations = 1;
        metrics.allocated_buffer_bytes = capacity;
        metrics.bind_groups = requests.len() as u64;
        self.pool = candidate_pool;
        self.coverage = coverage.clone();
        self.resources = Some(resources.clone());
        self.capacity = capacity;
        Ok(PreparedNativeAdvancedBlends {
            device: self.device.clone(),
            format: self.format,
            scratch: Some(scratch),
            coverage,
            resources: Some(resources),
            uniforms: Some(uniforms),
            composite,
            operations: operations.into(),
            metrics,
        })
    }
}
impl PreparedNativeAdvancedBlends {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn target_format(&self) -> wgpu::TextureFormat {
        self.format
    }
    pub fn metrics(&self) -> NativeAdvancedBlendMetrics {
        self.metrics
    }
    pub fn len(&self) -> usize {
        self.operations.len()
    }
    pub fn is_empty(&self) -> bool {
        self.operations.is_empty()
    }
    pub fn request(&self, index: usize) -> Option<&NativeAdvancedBlendRequest> {
        self.operations.get(index).map(|v| &v.request)
    }
    pub fn uniform_buffer(&self) -> Option<&wgpu::Buffer> {
        self.uniforms.as_ref()
    }
    pub fn scratch(&self) -> Option<&PreparedNativeLayerTargets> {
        self.scratch.as_ref()
    }
    pub fn coverage_texture(&self) -> Option<&wgpu::Texture> {
        self.coverage.as_ref().map(|v| &v.texture)
    }
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder, index: usize) -> Result<(), Error> {
        let op = self.operations.get(index).ok_or(Error::InvalidInput)?;
        let scratch = self.scratch.as_ref().ok_or(Error::ResourceDomain)?;
        let source = scratch
            .target(NativeLayerTargetId::Slot(0))
            .ok_or(Error::ResourceDomain)?;
        let output = scratch
            .target(NativeLayerTargetId::Slot(1))
            .ok_or(Error::ResourceDomain)?;
        let source_view = if op.request.sampling.mode().coverage() {
            &self.coverage.as_ref().ok_or(Error::ResourceDomain)?.view
        } else {
            source.color().ok_or(Error::ResourceDomain)?.view()
        };
        let source_binding = match op.request.final_effect_texture {
            Some(i) => op
                .request
                .source
                .effects()
                .and_then(|e| e.textures().get(i as usize))
                .ok_or(Error::ResourceDomain)?
                .linear_binding(),
            None => op
                .request
                .source
                .color()
                .ok_or(Error::ResourceDomain)?
                .linear_binding(),
        };
        let parent = op.request.parent.color().ok_or(Error::ResourceDomain)?;
        let parent_frame = op.request.parent.frame().ok_or(Error::ResourceDomain)?;
        let scratch_frame = source.frame().ok_or(Error::ResourceDomain)?;
        let output_color = output.color().ok_or(Error::ResourceDomain)?;
        let pipeline = &self
            .resources
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .pipeline;
        {
            let mut pass = begin_pass(
                encoder,
                source_view,
                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                "Native advanced source resolve",
            );
            let [w, h] = op.request.sampling.source_size();
            pass.set_scissor_rect(0, 0, w, h);
            pass.set_pipeline(&op.resolve);
            pass.set_bind_group(0, &scratch_frame.frame, &[]);
            pass.set_bind_group(1, source_binding, &[]);
            if let Some(mask) = &op.request.mask {
                pass.set_bind_group(2, mask.bind_group(), &[]);
            }
            self.composite
                .encode_quad(&mut pass, op.request.first_resolve_vertex)?;
        }
        {
            let mut pass = begin_pass(
                encoder,
                output_color.view(),
                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                "Native destination-aware blend",
            );
            let [w, h] = parent.size();
            pass.set_scissor_rect(0, 0, w, h);
            pass.set_pipeline(pipeline);
            pass.set_bind_group(0, &op.binding, &[]);
            pass.draw(0..3, 0..1);
        }
        {
            let mut pass = begin_pass(
                encoder,
                parent.view(),
                wgpu::LoadOp::Load,
                "Native advanced parent replace",
            );
            let [w, h] = parent.size();
            pass.set_scissor_rect(0, 0, w, h);
            pass.set_pipeline(&op.copy);
            pass.set_bind_group(0, &parent_frame.frame, &[]);
            pass.set_bind_group(1, output_color.linear_binding(), &[]);
            self.composite
                .encode_quad(&mut pass, op.request.first_copy_vertex)?;
        }
        Ok(())
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
pub(super) fn admit_quad(
    composite: &PreparedNativeLayerComposite,
    first: u32,
) -> Result<(), Error> {
    if first % 4 != 0 || first > i32::MAX as u32 || first as usize + 4 > composite.vertices().len()
    {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
fn scratch_budget(request: NativeAdvancedScratchRequest) -> NativeLayerBudget {
    let mut budget = NativeLayerBudget::default();
    budget.peak_materialized_depth = 2;
    budget.slot_widths[0] = request.source_size[0];
    budget.slot_heights[0] = request.source_size[1];
    budget.slot_widths[1] = request.frame.width;
    budget.slot_heights[1] = request.frame.height;
    budget
}
fn texture_bytes(size: [u32; 2]) -> Result<u64, Error> {
    u64::from(size[0])
        .checked_mul(u64::from(size[1]))
        .and_then(|n| n.checked_mul(4))
        .ok_or(Error::Capacity)
}
fn buffer_capacity(previous: u64, required: u64, maximum: u64) -> Result<u64, Error> {
    if required == 0 || required > maximum {
        return Err(Error::Capacity);
    }
    let mut size = previous.max(256).min(maximum);
    while size < required {
        size = size.saturating_mul(2).min(maximum);
    }
    Ok(size)
}
pub(super) fn group_key(masked: bool, coverage_only: bool, rgb: bool) -> NativeRenderPipeline {
    NativeRenderPipeline::Group(NativeGroupPipeline {
        blend_mode: BLEND_SRC,
        masked,
        coverage_only,
        channels: if rgb {
            NativeWriteChannels::Rgb
        } else {
            NativeWriteChannels::All
        },
        linear_byte_opacity: false,
    })
}
pub(super) fn begin_pass<'a>(
    encoder: &'a mut wgpu::CommandEncoder,
    view: &'a wgpu::TextureView,
    load: wgpu::LoadOp<wgpu::Color>,
    label: &'static str,
) -> wgpu::RenderPass<'a> {
    encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some(label),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view,
            depth_slice: None,
            resolve_target: None,
            ops: wgpu::Operations {
                load,
                store: wgpu::StoreOp::Store,
            },
        })],
        ..Default::default()
    })
}
impl Resources {
    fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Self {
        let texture = |binding, filterable| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Native advanced blend layout"),
            entries: &[
                texture(0, true),
                texture(1, true),
                texture(2, false),
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: NonZeroU64::new(96),
                    },
                    count: None,
                },
            ],
        });
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Native shared AdvancedBlend WGSL"),
            source: wgpu::ShaderSource::Wgsl(crate::shaders::ADVANCED_BLEND.into()),
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Native advanced blend pipeline layout"),
            bind_group_layouts: &[&layout],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Native destination-aware blend"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        Self { layout, pipeline }
    }
}
