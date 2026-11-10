//! Original Line3D/ACIS shaders and immutable source generation bindings.
use super::*;
use crate::picture_archive::ArchiveThreeDDraw;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ThreeDMetrics {
    pub line_draws: u32,
    pub acis_draws: u32,
    pub acis_records: u32,
    pub acis_edges: u32,
    pub storage_bytes: u64,
    pub uniform_bytes: u64,
}
pub(super) struct ThreeDPipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
pub(super) struct ThreeDState {
    line: Option<ThreeDPipeline>,
    acis: Option<ThreeDPipeline>,
}
impl Default for ThreeDState {
    fn default() -> Self {
        Self {
            line: None,
            acis: None,
        }
    }
}
struct Binding {
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
}
pub(super) struct PreparedThreeD {
    line: Option<Binding>,
    acis: Option<Binding>,
    _uniform: Option<wgpu::Buffer>,
    _storage: Option<[wgpu::Buffer; 2]>,
    _sources: Vec<Arc<ArchiveThreeDDraw>>,
    metrics: ThreeDMetrics,
}
impl PreparedThreeD {
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scene: &PreparedScene,
        range: std::ops::Range<u32>,
        acis: bool,
    ) -> Result<(), Error> {
        let draw = if acis {
            self.acis.as_ref()
        } else {
            self.line.as_ref()
        }
        .ok_or(Error::ResourceDomain)?;
        pass.set_pipeline(&draw.pipeline);
        pass.set_bind_group(0, &draw.binding, &[]);
        pass.set_vertex_buffer(0, scene.vertices.slice(..));
        pass.set_index_buffer(scene.indices.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(range, 0, 0..1);
        Ok(())
    }
}
impl PreparedScene {
    pub fn three_d_metrics(&self) -> ThreeDMetrics {
        self.three_d
            .as_ref()
            .map_or_else(Default::default, |v| v.metrics)
    }
}
impl ThreeDPipeline {
    async fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        acis: bool,
    ) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original 3D extension"),
            source: wgpu::ShaderSource::Wgsl(
                if acis {
                    shaders::ACIS_SOLID
                } else {
                    shaders::LINE_3D
                }
                .into(),
            ),
        });
        let entry = |binding, storage, size| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::VERTEX,
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
        let entries = if acis {
            vec![entry(0, false, 192), entry(1, true, 96), entry(2, true, 32)]
        } else {
            vec![entry(0, false, 192)]
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original 3D bindings"),
            entries: &entries,
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU original 3D pipeline"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original straight SrcOver 3D"),
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
    pub(super) fn validate_three_d(&self, compiled: &CompiledScene) -> Result<(), Error> {
        let limits = self.device.limits();
        for size in [
            std::mem::size_of_val(compiled.three_d.records.as_slice()),
            std::mem::size_of_val(compiled.three_d.edges.as_slice()),
        ] {
            let size = size as u64;
            if size > limits.max_buffer_size
                || size > u64::from(limits.max_storage_buffer_binding_size)
            {
                return Err(Error::Capacity);
            }
        }
        if compiled.three_d.records.iter().any(|v| {
            bytemuck::cast_slice::<_, f32>(std::slice::from_ref(v))
                .iter()
                .take(20)
                .any(|v| !v.is_finite())
                || !v.pen_thickness.is_finite()
                || !v.opacity.is_finite()
        }) || compiled
            .three_d
            .edges
            .iter()
            .any(|v| v.p0.iter().chain(v.p1.iter()).any(|v| !v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    pub(super) async fn prepare_three_d(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.three_d.sources.is_empty() {
            return Ok(());
        }
        self.validate_three_d(compiled)?;
        let mut sources = Vec::new();
        sources
            .try_reserve_exact(compiled.three_d.sources.len())
            .map_err(|_| Error::Capacity)?;
        sources.extend(compiled.three_d.sources.iter().cloned());
        let line_count = compiled
            .draws
            .iter()
            .filter(|v| matches!(v, DrawRange::ThreeDLine(_)))
            .count();
        let acis_count = compiled
            .draws
            .iter()
            .filter(|v| matches!(v, DrawRange::ThreeDAcis(_)))
            .count();
        let mut result = PreparedThreeD {
            line: None,
            acis: None,
            _uniform: None,
            _storage: None,
            _sources: sources,
            metrics: ThreeDMetrics {
                line_draws: u32::try_from(line_count).map_err(|_| Error::Capacity)?,
                acis_draws: u32::try_from(acis_count).map_err(|_| Error::Capacity)?,
                acis_records: u32::try_from(compiled.three_d.records.len())
                    .map_err(|_| Error::Capacity)?,
                acis_edges: u32::try_from(compiled.three_d.edges.len())
                    .map_err(|_| Error::Capacity)?,
                ..Default::default()
            },
        };
        if line_count == 0 && acis_count == 0 {
            prepared.three_d = Some(result);
            return Ok(());
        }
        if line_count != 0 && self.three_d.line.is_none() {
            self.three_d.line = Some(ThreeDPipeline::new(&self.device, self.format, false).await?);
        }
        if acis_count != 0 && self.three_d.acis.is_none() {
            self.three_d.acis = Some(ThreeDPipeline::new(&self.device, self.format, true).await?);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut uniforms = frame.uniforms(self.device.limits().max_texture_dimension_2d)?;
        // Original managed extension header uses projection in Mvp unless any
        // source command selected the frame's shared late camera generation.
        if compiled.camera.is_none() {
            uniforms.mvp = uniforms.projection;
        }
        let uniform = self
            .device
            .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("ProGPU original 192-byte 3D frame"),
                contents: &bytemuck::bytes_of(&uniforms)[..192],
                usage: wgpu::BufferUsages::UNIFORM,
            });
        if line_count != 0 {
            let state = self.three_d.line.as_ref().ok_or(Error::ResourceDomain)?;
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained 3D line frame"),
                layout: &state.layout,
                entries: &[wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform.as_entire_binding(),
                }],
            });
            result.line = Some(Binding {
                pipeline: state.pipeline.clone(),
                binding,
            });
        }
        if acis_count != 0 {
            let records = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU immutable ACIS records"),
                    contents: bytemuck::cast_slice(&compiled.three_d.records),
                    usage: wgpu::BufferUsages::STORAGE,
                });
            let edges = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU immutable ACIS edges"),
                    contents: bytemuck::cast_slice(&compiled.three_d.edges),
                    usage: wgpu::BufferUsages::STORAGE,
                });
            let state = self.three_d.acis.as_ref().ok_or(Error::ResourceDomain)?;
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU retained ACIS generation"),
                layout: &state.layout,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: uniform.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: records.as_entire_binding(),
                    },
                    wgpu::BindGroupEntry {
                        binding: 2,
                        resource: edges.as_entire_binding(),
                    },
                ],
            });
            result.metrics.storage_bytes = records.size() + edges.size();
            result._storage = Some([records, edges]);
            result.acis = Some(Binding {
                pipeline: state.pipeline.clone(),
                binding,
            });
        }
        result._uniform = Some(uniform);
        result.metrics.uniform_bytes = 192;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        prepared.metrics.geometry_bytes = prepared
            .metrics
            .geometry_bytes
            .checked_add(result.metrics.storage_bytes)
            .ok_or(Error::Capacity)?;
        prepared.metrics.uniform_bytes = prepared
            .metrics
            .uniform_bytes
            .checked_add(result.metrics.uniform_bytes)
            .ok_or(Error::Capacity)?;
        prepared.three_d = Some(result);
        Ok(())
    }
}
