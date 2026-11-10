//! Original chart and extension instancing, with immutable GPU generations.
use super::*;
use crate::series::{
    SeriesDraw, SeriesKind, SeriesLayout, SeriesPackingPath, SeriesShader, SeriesSource,
};
use std::{
    collections::HashMap,
    sync::{Arc, Weak},
};

/// Actual same-device uploaded generation. Cloning never aliases writable data;
/// source replacement creates another generation retained by prior prepared draws.
#[derive(Clone, Debug)]
pub struct GpuSeriesBuffer {
    device: wgpu::Device,
    buffer: wgpu::Buffer,
    count: u32,
    layout: SeriesLayout,
}
impl GpuSeriesBuffer {
    pub fn point_count(&self) -> u32 {
        self.count
    }
    pub fn layout(&self) -> SeriesLayout {
        self.layout
    }
    pub fn allocated_bytes(&self) -> u64 {
        self.buffer.size()
    }
    pub fn is_owned_by(&self, device: &wgpu::Device) -> bool {
        self.device == *device
    }
}
impl From<GpuSeriesBuffer> for SeriesSource {
    fn from(buffer: GpuSeriesBuffer) -> Self {
        Self::Gpu(buffer)
    }
}
/// Actual resources produced during one preparation, never inferred cache hits.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SeriesMetrics {
    pub draws: u32,
    pub point_upload_bytes: u64,
    pub point_allocations: u32,
    pub point_reuses: u32,
    pub uniform_bytes: u64,
    pub simd_packed_points: u64,
    pub scalar_packed_points: u64,
    pub cached_buffers: u32,
    pub cached_bytes: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
struct DataKey {
    source: usize,
    length: usize,
    offset: usize,
    count: u32,
    layout: SeriesLayout,
    kind: SeriesKind,
    radius: u32,
}
struct Cached {
    source: Weak<[f32]>,
    buffer: GpuSeriesBuffer,
    frame: u64,
}
#[derive(Clone)]
struct Pipeline {
    pipeline: wgpu::RenderPipeline,
    layout: wgpu::BindGroupLayout,
}
#[derive(Default)]
pub(super) struct SeriesState {
    pipelines: HashMap<(SeriesKind, SeriesShader), Pipeline>,
    buffers: HashMap<DataKey, Cached>,
    frame: u64,
    bytes: u64,
}
pub(super) struct PreparedSeries {
    draws: Vec<Option<PreparedDraw>>,
    metrics: SeriesMetrics,
}
struct PreparedDraw {
    pipeline: wgpu::RenderPipeline,
    binding: wgpu::BindGroup,
    source: GpuSeriesBuffer,
    kind: SeriesKind,
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct VertexUniforms {
    transform: [[f32; 4]; 4],
    viewport: [f32; 2],
    parameters: [f32; 2],
    scale: [f32; 2],
    translate: [f32; 2],
}
const _: () = assert!(std::mem::size_of::<VertexUniforms>() == 96);

impl PreparedSeries {
    pub(super) fn draw_count(&self, index: usize) -> Result<u32, Error> {
        Ok(u32::from(
            self.draws
                .get(index)
                .ok_or(Error::ResourceDomain)?
                .is_some(),
        ))
    }

    pub(super) fn draw(&self, pass: &mut wgpu::RenderPass<'_>, index: usize) -> Result<(), Error> {
        let Some(draw) = self.draws.get(index).ok_or(Error::ResourceDomain)? else {
            return Ok(());
        };
        pass.set_pipeline(&draw.pipeline);
        pass.set_bind_group(0, &draw.binding, &[]);
        let instances = if draw.kind == SeriesKind::Line {
            draw.source.count - 1
        } else {
            pass.set_vertex_buffer(0, draw.source.buffer.slice(..));
            draw.source.count
        };
        pass.draw(0..6, 0..instances);
        Ok(())
    }
}
impl PreparedScene {
    pub fn series_metrics(&self) -> SeriesMetrics {
        self.series
            .as_ref()
            .map_or_else(Default::default, |s| s.metrics)
    }
}
impl SeriesState {
    fn upload(
        &mut self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        draw: &SeriesDraw,
        metrics: &mut SeriesMetrics,
    ) -> Result<GpuSeriesBuffer, Error> {
        draw.validate()?;
        let SeriesSource::Data(data) = &draw.source else {
            let SeriesSource::Gpu(buffer) = &draw.source else {
                unreachable!()
            };
            if !buffer.is_owned_by(device) {
                return Err(Error::ResourceDomain);
            }
            metrics.point_reuses += 1;
            return Ok(buffer.clone());
        };
        let pack = draw.packing_path()?;
        let expands = pack != SeriesPackingPath::DirectUpload;
        let key = DataKey {
            source: data.owner().as_ptr() as usize,
            length: data.owner().len(),
            offset: data.offset(),
            count: data.point_count(),
            layout: data.layout(),
            kind: draw.kind,
            radius: if expands {
                draw.radius_or_width.to_bits()
            } else {
                0
            },
        };
        if let Some(entry) = self.buffers.get_mut(&key) {
            if entry
                .source
                .upgrade()
                .is_some_and(|owner| Arc::ptr_eq(&owner, data.owner()))
            {
                entry.frame = self.frame;
                metrics.point_reuses += 1;
                return Ok(entry.buffer.clone());
            }
        }
        let required = u64::from(data.point_count())
            .checked_mul(if expands {
                12
            } else {
                (data.layout().stride() * 4) as u64
            })
            .ok_or(Error::Capacity)?;
        let size = required.max(64);
        if size > device.limits().max_buffer_size
            || (draw.kind == SeriesKind::Line
                && size > u64::from(device.limits().max_storage_buffer_binding_size))
        {
            return Err(Error::Capacity);
        }
        let packed = if expands {
            Some(crate::series::pack_scatter(
                data,
                draw.radius_or_width,
                pack,
            )?)
        } else {
            None
        };
        let values = packed.as_deref().unwrap_or_else(|| data.values());
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU immutable series points"),
            size,
            usage: wgpu::BufferUsages::VERTEX
                | wgpu::BufferUsages::STORAGE
                | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        if data.point_count() > 0 {
            queue.write_buffer(&buffer, 0, bytemuck::cast_slice(values));
            metrics.point_upload_bytes += required;
        }
        let result = GpuSeriesBuffer {
            device: device.clone(),
            buffer,
            count: data.point_count(),
            layout: if expands {
                SeriesLayout::XyRadius
            } else {
                data.layout()
            },
        };
        metrics.point_allocations += 1;
        match pack {
            SeriesPackingPath::CpuSimd => {
                metrics.simd_packed_points += u64::from(data.point_count())
            }
            SeriesPackingPath::ScalarReference => {
                metrics.scalar_packed_points += u64::from(data.point_count())
            }
            _ => {}
        }
        if let Some(old) = self.buffers.remove(&key) {
            self.bytes -= old.buffer.allocated_bytes();
        }
        self.buffers.retain(|_, entry| {
            if entry.source.strong_count() == 0 {
                self.bytes -= entry.buffer.allocated_bytes();
                false
            } else {
                true
            }
        });
        const BUDGET: u64 = 512 * 1024 * 1024;
        while !self.buffers.is_empty()
            && (self.buffers.len() >= 4096 || self.bytes.saturating_add(size) > BUDGET)
        {
            let Some(oldest) = self
                .buffers
                .iter()
                .min_by_key(|(_, v)| v.frame)
                .map(|(k, _)| *k)
            else {
                break;
            };
            if let Some(old) = self.buffers.remove(&oldest) {
                self.bytes -= old.buffer.allocated_bytes();
            }
        }
        if size <= BUDGET {
            self.bytes += size;
            self.buffers.insert(
                key,
                Cached {
                    source: Arc::downgrade(data.owner()),
                    buffer: result.clone(),
                    frame: self.frame,
                },
            );
        }
        Ok(result)
    }
    async fn pipeline(
        &mut self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        kind: SeriesKind,
        shader: SeriesShader,
    ) -> Result<Pipeline, Error> {
        let key = (kind, shader);
        if let Some(pipeline) = self.pipelines.get(&key) {
            return Ok(pipeline.clone());
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let code = match key {
            (SeriesKind::Line, SeriesShader::BuiltIn) => shaders::CHART_LINE,
            (SeriesKind::Line, SeriesShader::Extension) => shaders::GPU_LINE_SERIES,
            (SeriesKind::Scatter, SeriesShader::BuiltIn) => shaders::CHART_SCATTER,
            (SeriesKind::Scatter, SeriesShader::Extension) => shaders::GPU_SCATTER_SERIES,
        };
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original series shader"),
            source: wgpu::ShaderSource::Wgsl(code.into()),
        });
        let entry = |binding, size, visibility, ty| wgpu::BindGroupLayoutEntry {
            binding,
            visibility,
            ty: wgpu::BindingType::Buffer {
                ty,
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(size),
            },
            count: None,
        };
        let mut entries = vec![
            entry(
                0,
                96,
                wgpu::ShaderStages::VERTEX,
                wgpu::BufferBindingType::Uniform,
            ),
            entry(
                1,
                16,
                wgpu::ShaderStages::FRAGMENT,
                wgpu::BufferBindingType::Uniform,
            ),
        ];
        if kind == SeriesKind::Line {
            entries.push(entry(
                2,
                8,
                wgpu::ShaderStages::VERTEX,
                wgpu::BufferBindingType::Storage { read_only: true },
            ));
        }
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU series bindings"),
            entries: &entries,
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU series pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32];
        let vertex_buffers = [Some(wgpu::VertexBufferLayout {
            array_stride: 12,
            step_mode: wgpu::VertexStepMode::Instance,
            attributes: &attributes,
        })];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original series pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: if kind == SeriesKind::Scatter {
                    &vertex_buffers
                } else {
                    &[]
                },
            },
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(crate::scene::LayerBlend::SrcOver.straight_draw_state()?),
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
        let result = Pipeline { pipeline, layout };
        self.pipelines.insert(key, result.clone());
        Ok(result)
    }
}
impl Renderer {
    /// Upload an explicitly owned static series generation on this actual device.
    /// Later uploads create independent storage; already prepared frames retain it.
    pub fn realize_series(&mut self, draw: &SeriesDraw) -> Result<GpuSeriesBuffer, Error> {
        let state = self.series.get_or_insert_with(Default::default);
        state.frame = state.frame.wrapping_add(1);
        state.upload(
            &self.device,
            &self.queue,
            draw,
            &mut SeriesMetrics::default(),
        )
    }
    pub(super) async fn prepare_series(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.series.is_empty() {
            return Ok(());
        }
        for draw in &compiled.series {
            draw.validate()?;
        }
        let mut state = self.series.take().unwrap_or_default();
        state.frame = state.frame.wrapping_add(1);
        let result = self.prepare_series_inner(&mut state, compiled, frame).await;
        self.series = Some(state);
        let result = result?;
        prepared.metrics.geometry_bytes = prepared
            .metrics
            .geometry_bytes
            .checked_add(result.metrics.point_upload_bytes)
            .ok_or(Error::Capacity)?;
        prepared.metrics.uniform_bytes = prepared
            .metrics
            .uniform_bytes
            .checked_add(result.metrics.uniform_bytes)
            .ok_or(Error::Capacity)?;
        prepared.series = Some(result);
        Ok(())
    }
    async fn prepare_series_inner(
        &self,
        state: &mut SeriesState,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<PreparedSeries, Error> {
        let target = frame.uniforms(self.device.limits().max_texture_dimension_2d)?;
        let projection = crate::vector::Matrix4(std::array::from_fn(|r| {
            std::array::from_fn(|c| target.projection[r * 4 + c])
        }));
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(compiled.series.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = SeriesMetrics::default();
        for draw in &compiled.series {
            let source = state.upload(&self.device, &self.queue, draw, &mut metrics)?;
            if !draw.is_renderable() {
                draws.push(None);
                continue;
            }
            let pipeline = state
                .pipeline(&self.device, self.format, draw.kind, draw.shader)
                .await?;
            let uniforms = VertexUniforms {
                transform: (draw.transform * projection).0,
                viewport: [frame.width as f32, frame.height as f32],
                parameters: if draw.kind == SeriesKind::Line {
                    [target.dpi_scale, draw.radius_or_width]
                } else {
                    [0.0; 2]
                },
                scale: draw.scale,
                translate: draw.translate,
            };
            let vertex = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU immutable series vertex parameters"),
                    contents: bytemuck::bytes_of(&uniforms),
                    usage: wgpu::BufferUsages::UNIFORM,
                });
            let fragment = self
                .device
                .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                    label: Some("ProGPU immutable series color"),
                    contents: bytemuck::cast_slice(&draw.color()),
                    usage: wgpu::BufferUsages::UNIFORM,
                });
            let mut entries = vec![
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: vertex.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: fragment.as_entire_binding(),
                },
            ];
            if draw.kind == SeriesKind::Line {
                entries.push(wgpu::BindGroupEntry {
                    binding: 2,
                    resource: source.buffer.as_entire_binding(),
                });
            }
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU immutable series draw"),
                layout: &pipeline.layout,
                entries: &entries,
            });
            metrics.uniform_bytes += 112;
            metrics.draws += 1;
            draws.push(Some(PreparedDraw {
                pipeline: pipeline.pipeline,
                binding,
                source,
                kind: draw.kind,
            }));
        }
        metrics.cached_buffers = state.buffers.len() as u32;
        metrics.cached_bytes = state.bytes;
        Ok(PreparedSeries { draws, metrics })
    }
}
