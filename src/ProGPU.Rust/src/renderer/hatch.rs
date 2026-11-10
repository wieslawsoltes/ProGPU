//! Original analytic Hatch.wgsl pipeline with immutable frame/static ownership.
use super::*;
use crate::picture_archive::ArchiveHatchDraw;

/// Actual uploaded original hatch records for one prepared generation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct HatchMetrics {
    pub records: u32,
    pub segments: u32,
    /// Original 32-byte record and 48-byte boundary payloads, uploaded once.
    pub boundary_bytes: u64,
    /// Original four vertices and six indices for each emitted record. Already
    /// included in the ordinary scene geometry counter; not a second upload.
    pub quad_bytes: u64,
}
pub(super) struct HatchPipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
pub(super) struct PreparedHatches {
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    _records: wgpu::Buffer,
    _segments: wgpu::Buffer,
    _sources: Vec<Arc<ArchiveHatchDraw>>,
    metrics: HatchMetrics,
}
impl PreparedHatches {
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scene: &PreparedScene,
        range: std::ops::Range<u32>,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &self.binding, &[]);
        pass.set_vertex_buffer(0, scene.vertices.slice(..));
        pass.set_index_buffer(scene.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(range, 0, 0..1);
    }
}
impl PreparedScene {
    /// Actual retained hatch payload; replay performs no boundary re-upload.
    pub fn hatch_metrics(&self) -> HatchMetrics {
        self.hatches
            .as_ref()
            .map_or_else(Default::default, |h| h.metrics)
    }
}
impl HatchPipeline {
    async fn new(device: &wgpu::Device, format: wgpu::TextureFormat) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original Hatch.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::HATCH.into()),
        });
        let entry = |binding, storage, size| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: if storage {
                wgpu::ShaderStages::FRAGMENT
            } else {
                wgpu::ShaderStages::VERTEX
            },
            ty: wgpu::BindingType::Buffer {
                ty: if storage {
                    wgpu::BufferBindingType::Storage { read_only: true }
                } else {
                    wgpu::BufferBindingType::Uniform
                },
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(size),
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original five hatch bindings"),
            entries: &[
                entry(0, false, 192),
                entry(1, true, 256),
                entry(2, true, 32),
                entry(3, true, 48),
                entry(4, true, 32),
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU original hatch pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,
            3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original straight-alpha hatch"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<VectorVertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &attributes,
                })],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            // This renderer's public target contract is single-sample, matching
            // the original offscreen branch. No MSAA qualification is inferred.
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
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
        Ok(Self { pipeline, layout })
    }
}
impl Renderer {
    pub(super) fn validate_hatches(&self, compiled: &CompiledScene) -> Result<(), Error> {
        // Original Hatch does not consume generic alpha-mask or per-draw
        // blend records. Its constant straight SrcOver pipeline retains only
        // the render pass's actual scissor, exactly as the extension source.
        let limits = self.device.limits();
        for size in [
            std::mem::size_of_val(compiled.hatches.records.as_slice()),
            std::mem::size_of_val(compiled.hatches.segments.as_slice()),
        ] {
            let size = u64::try_from(size).map_err(|_| Error::Capacity)?;
            if size > limits.max_buffer_size
                || size > u64::from(limits.max_storage_buffer_binding_size)
            {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    pub(super) async fn prepare_hatches(
        &mut self,
        compiled: &CompiledScene,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if !compiled
            .draws
            .iter()
            .any(|draw| matches!(draw, DrawRange::Hatch(_)))
        {
            return Ok(());
        }
        self.validate_hatches(compiled)?;
        let mut sources = Vec::new();
        sources
            .try_reserve_exact(compiled.hatches.sources.len())
            .map_err(|_| Error::Capacity)?;
        sources.extend(compiled.hatches.sources.iter().cloned());
        if self.hatch.is_none() {
            self.hatch = Some(HatchPipeline::new(&self.device, self.format).await?);
        }
        let state = self.hatch.as_ref().ok_or(Error::ResourceDomain)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let records = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU immutable hatch records"),
                contents: bytemuck::cast_slice(&compiled.hatches.records),
                usage: wgpu::BufferUsages::STORAGE,
            });
        let segments = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU immutable hatch boundaries"),
                contents: bytemuck::cast_slice(&compiled.hatches.segments),
                usage: wgpu::BufferUsages::STORAGE,
            });
        let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU retained hatch generation"),
            layout: &state.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &prepared.frame_buffer,
                        offset: 0,
                        size: std::num::NonZeroU64::new(192),
                    }),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: prepared.material_buffers.0.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: records.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: segments.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: prepared.material_buffers.1.as_entire_binding(),
                },
            ],
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let metrics = HatchMetrics {
            records: u32::try_from(compiled.hatches.records.len()).map_err(|_| Error::Capacity)?,
            segments: u32::try_from(compiled.hatches.segments.len())
                .map_err(|_| Error::Capacity)?,
            boundary_bytes: records.size() + segments.size(),
            quad_bytes: compiled.hatches.records.len() as u64
                * (4 * size_of::<VectorVertex>() + 6 * size_of::<u32>()) as u64,
        };
        prepared.metrics.geometry_bytes = prepared
            .metrics
            .geometry_bytes
            .checked_add(metrics.boundary_bytes)
            .ok_or(Error::Capacity)?;
        prepared.hatches = Some(PreparedHatches {
            pipeline: state.pipeline.clone(),
            binding,
            _records: records,
            _segments: segments,
            _sources: sources,
            metrics,
        });
        Ok(())
    }
}
