//! Original target-storage Clear with a fresh submission-owned uniform per replay.
use super::*;
use crate::scene::{ClearColor, TargetAlpha};

/// Work encoded by one retained scene replay, excluding preparation/capture work.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ReplayMetrics {
    /// Actual draw commands, including advanced blend copies and target clears.
    pub draws: u32,
    /// Nonempty target-storage Clear occurrences.
    pub target_clears: u32,
    /// Original 16-byte color upload per executed Clear, including warm replay.
    pub uniform_upload_bytes: u64,
    /// Original independent uniform buffer allocation per executed Clear.
    pub uniform_buffer_allocations: u32,
    /// Exact explicit texture-copy bytes used to load an existing destination
    /// into an owned advanced root. Shader-based copies remain draw commands.
    pub texture_copy_bytes: u64,
}

/// Actual queue submission and the work encoded for it; completion is independent.
pub struct RenderSubmission {
    /// Submission returned by the renderer's actual device queue.
    pub submission_index: wgpu::SubmissionIndex,
    /// Actual encoded work for this submission's scene replay.
    pub metrics: ReplayMetrics,
}

/// An explicit physical storage operation for an actual caller-owned texture.
#[derive(Clone, Copy, Debug)]
pub struct TargetClearRequest {
    /// Original validated straight source color.
    pub color: ClearColor,
    /// Nonempty physical x/y/width/height within the actual base-level texture.
    pub scissor: [u32; 4],
    /// Actual target contract, not an inference from clear color or ancestors.
    pub alpha: TargetAlpha,
}

#[derive(Clone)]
pub(super) struct ClearPipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
impl ClearPipeline {
    async fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            // Original nonempty source identity is required by the pinned FXC path.
            label: Some("ProGPU target-storage Clear shader"),
            source: wgpu::ShaderSource::Wgsl(shaders::TARGET_CLEAR.into()),
        });
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU target-storage Clear binding layout"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: std::num::NonZeroU64::new(16),
                },
                count: None,
            }],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU target-storage Clear pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU target-storage Clear replace pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_target_clear"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_target_clear"),
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
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self { pipeline, layout })
    }
    #[allow(clippy::too_many_arguments)] // Actual device/queue/encoder/attachment plus one physical request.
    fn encode(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        target: &wgpu::TextureView,
        extent: [u32; 2],
        request: TargetClearRequest,
    ) -> Result<ReplayMetrics, Error> {
        validate_scissor(extent, request.scissor)?;
        let color = request.color.premultiplied(request.alpha);
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU submission-owned target Clear color"),
            size: 16,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        // Original per-occurrence upload, even on warm replay. A distinct buffer
        // prevents later encodes from changing an earlier unsubmitted clear.
        queue.write_buffer(&buffer, 0, bytemuck::cast_slice(&color));
        let binding = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU submission-owned target Clear binding"),
            layout: &self.layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: buffer.as_entire_binding(),
            }],
        });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU target-storage Clear pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            let [x, y, w, h] = request.scissor;
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, &binding, &[]);
            pass.set_viewport(0.0, 0.0, extent[0] as f32, extent[1] as f32, 0.0, 1.0);
            pass.set_scissor_rect(x, y, w, h);
            pass.draw(0..3, 0..1);
        }
        // Wgpu owns the actual bind-group/buffer references through submission.
        // No explicit destroy, poll, unmap, or destructor queue work is added.
        Ok(ReplayMetrics {
            draws: 1,
            target_clears: 1,
            uniform_upload_bytes: 16,
            uniform_buffer_allocations: 1,
            texture_copy_bytes: 0,
        })
    }
}
fn validate_scissor([width, height]: [u32; 2], [x, y, w, h]: [u32; 4]) -> Result<(), Error> {
    if width == 0
        || height == 0
        || w == 0
        || h == 0
        || x >= width
        || y >= height
        || w > width - x
        || h > height - y
    {
        return Err(Error::InvalidInput);
    }
    Ok(())
}

struct Operation {
    draw_index: usize,
    state_index: Option<usize>,
    request: TargetClearRequest,
}
pub(super) struct PreparedClears {
    pipeline: ClearPipeline,
    operations: Vec<Operation>,
}
impl PreparedScene {
    /// Work performed by each replay of this exact frozen generation. Explicit
    /// Clear retains original per-occurrence uploads; ordinary draws upload none.
    pub fn replay_metrics(&self) -> ReplayMetrics {
        let clears = self
            .clears
            .as_ref()
            .map_or(0, |c| c.operations.len() as u32);
        ReplayMetrics {
            draws: self.metrics.draws,
            target_clears: clears,
            uniform_upload_bytes: u64::from(clears) * 16,
            uniform_buffer_allocations: clears,
            texture_copy_bytes: 0,
        }
    }
}
impl Renderer {
    /// Encode one original storage Clear into the actual base level of a
    /// same-device texture. No submission or CPU readback is performed.
    ///
    /// Texture size/format/usages are inspected directly. The explicit alpha
    /// policy must describe this actual target; no color proves opacity. Caller
    /// owns previous contents, source order and any enclosing source AA policy.
    pub async fn encode_target_clear(
        &mut self,
        encoder: &mut wgpu::CommandEncoder,
        texture: &wgpu::Texture,
        request: TargetClearRequest,
    ) -> Result<ReplayMetrics, Error> {
        let extent = [texture.width(), texture.height()];
        validate_scissor(extent, request.scissor)?;
        if texture.dimension() != wgpu::TextureDimension::D2
            || texture.depth_or_array_layers() != 1
            || texture.sample_count() != 1
            || texture.format() != self.format
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::RENDER_ATTACHMENT)
        {
            return Err(Error::InvalidInput);
        }
        if self.clear.is_none() {
            self.clear = Some(ClearPipeline::new(&self.device, self.format).await?);
        }
        let view = texture.create_view(&wgpu::TextureViewDescriptor {
            mip_level_count: Some(1),
            array_layer_count: Some(1),
            ..Default::default()
        });
        self.clear.as_ref().ok_or(Error::ResourceDomain)?.encode(
            &self.device,
            &self.queue,
            encoder,
            &view,
            extent,
            request,
        )
    }
    pub(super) async fn prepare_clears(
        &mut self,
        compiled: &CompiledScene,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        let mut operations = Vec::new();
        let mut state_index = None;
        let extent = [prepared.frame.width, prepared.frame.height];
        for (draw_index, draw) in compiled.draws.iter().enumerate() {
            match draw {
                DrawRange::State(index) => state_index = Some(*index),
                DrawRange::Clear(color) => {
                    let scissor = if let Some(index) = state_index {
                        let state = prepared
                            .masks
                            .as_ref()
                            .and_then(|m| m.states.get(index))
                            .ok_or(Error::ResourceDomain)?;
                        if state.binding.is_some() {
                            return Err(Error::UnsupportedExecution);
                        }
                        state.scissor
                    } else {
                        [0, 0, extent[0], extent[1]]
                    };
                    if scissor[2] == 0 || scissor[3] == 0 {
                        continue;
                    }
                    validate_scissor(extent, scissor)?;
                    if operations.len() == 16 * 1024 {
                        return Err(Error::Capacity);
                    }
                    operations.try_reserve(1).map_err(|_| Error::Capacity)?;
                    operations.push(Operation {
                        draw_index,
                        state_index,
                        request: TargetClearRequest {
                            color: *color,
                            scissor,
                            alpha: TargetAlpha::Preserve,
                        },
                    });
                }
                _ => {}
            }
        }
        if operations.is_empty() {
            return Ok(());
        }
        let pipeline = match &self.clear {
            Some(pipeline) => pipeline.clone(),
            None => ClearPipeline::new(&self.device, self.format).await?,
        };
        prepared.clears = Some(PreparedClears {
            pipeline: pipeline.clone(),
            operations,
        });
        self.clear = Some(pipeline);
        Ok(())
    }
    #[allow(clippy::too_many_arguments)] // Ordered span retains its exact target, load and scope state.
    pub(super) fn encode_span(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        scene: &PreparedScene,
        target: &wgpu::TextureView,
        mut load: wgpu::LoadOp<wgpu::Color>,
        range: std::ops::Range<usize>,
        mut initial_state: Option<usize>,
    ) -> Result<(), Error> {
        let Some(clears) = &scene.clears else {
            return self.encode_draw_span(encoder, scene, target, load, range, initial_state);
        };
        let mut first = range.start;
        let start = clears
            .operations
            .partition_point(|op| op.draw_index < first);
        for operation in &clears.operations[start..] {
            if operation.draw_index >= range.end {
                break;
            }
            self.encode_draw_span(
                encoder,
                scene,
                target,
                load,
                first..operation.draw_index,
                initial_state,
            )?;
            clears.pipeline.encode(
                &self.device,
                &self.queue,
                encoder,
                target,
                [scene.frame.width, scene.frame.height],
                operation.request,
            )?;
            first = operation.draw_index + 1;
            initial_state = operation.state_index;
            load = wgpu::LoadOp::Load;
        }
        if first < range.end || first == range.start {
            self.encode_draw_span(
                encoder,
                scene,
                target,
                load,
                first..range.end,
                initial_state,
            )?;
        }
        Ok(())
    }
}
