//! Immutable static buffers and actual-target bindings for canonical Hatch.wgsl.
use super::*;
use crate::{
    path::{ExecutionPath, ExecutionPreference},
    vector::{GpuBrush, GpuGradientStop},
};
use bytemuck::{Pod, Zeroable};
use std::{collections::HashMap, sync::Weak};
use wgpu::util::DeviceExt;
mod pipeline;

/// The actual attachment and original static-DXF viewport inputs. No outer
/// opacity or mask parameter is accepted: the original Hatch extension ignores
/// those inputs and uses its fixed straight source-over pipeline.
#[derive(Clone, Copy, Debug)]
pub struct StaticHatchTarget {
    pub projection: Matrix4,
    pub placement: Matrix4,
    pub viewport: Option<StaticHatchViewport>,
    pub size: [u32; 2],
    pub canvas_size: [f32; 2],
    pub dpi_scale: f32,
    pub format: wgpu::TextureFormat,
    /// Original primary target admits1/4; original offscreen target uses1.
    pub sample_count: u32,
}
impl StaticHatchTarget {
    pub fn model_to_screen(&self) -> Matrix4 {
        self.viewport
            .map_or(self.placement, |v| v.matrix() * self.placement)
    }
    pub fn validate(&self, device: &wgpu::Device) -> Result<(), Error> {
        if self.size.contains(&0)
            || self
                .size
                .iter()
                .any(|v| *v > device.limits().max_texture_dimension_2d)
            || self.canvas_size.iter().any(|v| !v.is_finite() || *v <= 0.0)
            || !self.dpi_scale.is_finite()
            || self.dpi_scale <= 0.0
            || !matches!(self.sample_count, 1 | 4)
            || self.projection.0.iter().flatten().any(|v| !v.is_finite())
            || self.placement.0.iter().flatten().any(|v| !v.is_finite())
            || self
                .model_to_screen()
                .0
                .iter()
                .flatten()
                .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        match self.format {
            wgpu::TextureFormat::Rgba8Unorm
            | wgpu::TextureFormat::Rgba8UnormSrgb
            | wgpu::TextureFormat::Bgra8Unorm
            | wgpu::TextureFormat::Bgra8UnormSrgb
            | wgpu::TextureFormat::Rgba16Float => {}
            _ => return Err(Error::UnsupportedExecution),
        }
        if device.limits().max_uniform_buffer_binding_size < 224
            || device.limits().max_storage_buffers_per_shader_stage < 4
            || device.limits().max_vertex_attributes < 8
            || device.limits().max_vertex_buffer_array_stride < 56
        {
            return Err(Error::Capacity);
        }
        Ok(())
    }
    fn uniforms(&self) -> Uniforms {
        let model = self.model_to_screen();
        Uniforms {
            projection: self.projection.0,
            mvp: model.0,
            view: Matrix4::IDENTITY.0,
            canvas_size: self.canvas_size,
            dpi_scale: self.dpi_scale,
            padding: 0.0,
            render_origin: [0.0; 2],
            stroke_scales: model.stroke_scales().unwrap_or([0.0; 2]),
        }
    }
}
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable)]
struct Uniforms {
    projection: [[f32; 4]; 4],
    mvp: [[f32; 4]; 4],
    view: [[f32; 4]; 4],
    canvas_size: [f32; 2],
    dpi_scale: f32,
    padding: f32,
    render_origin: [f32; 2],
    stroke_scales: [f32; 2],
}
const _: () = assert!(size_of::<Uniforms>() == 224);

/// Actual work performed by this upload/preparation call. Static resident data
/// is reported independently so repeated target preparation does not claim a
/// second boundary/material upload.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StaticHatchMetrics {
    pub source_commands: u32,
    pub records: u32,
    pub segments: u32,
    pub batches: u32,
    pub draw_calls: u32,
    pub geometry_upload_bytes: u64,
    pub boundary_upload_bytes: u64,
    pub material_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub static_resident_bytes: u64,
    pub generation_cache_hits: u32,
}
impl StaticHatchMetrics {
    fn source(source: &StaticHatchBuffer) -> Self {
        Self {
            source_commands: source.commands().len() as u32,
            records: source.records().len() as u32,
            segments: source.segments().len() as u32,
            batches: source.batches().len() as u32,
            draw_calls: source
                .batches()
                .iter()
                .filter(|b| !b.indices.is_empty())
                .count() as u32,
            ..Default::default()
        }
    }
}

struct GeometryBuffers {
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    records: wgpu::Buffer,
    segments: wgpu::Buffer,
}
struct Uploaded {
    source: StaticHatchBuffer,
    device: wgpu::Device,
    geometry: Option<GeometryBuffers>,
    brushes: wgpu::Buffer,
    stops: wgpu::Buffer,
    resident_bytes: u64,
}
/// Explicit upload lease. Clone shares the same immutable original generation;
/// dropping the engine cannot retire buffers still owned by a prepared draw.
#[derive(Clone)]
pub struct UploadedStaticHatches {
    resources: Arc<Uploaded>,
    metrics: StaticHatchMetrics,
}
impl UploadedStaticHatches {
    pub fn source(&self) -> &StaticHatchBuffer {
        &self.resources.source
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.resources.device
    }
    pub fn metrics(&self) -> StaticHatchMetrics {
        self.metrics
    }
}

struct Prepared {
    uploaded: Arc<Uploaded>,
    _uniform: wgpu::Buffer,
    pipeline: Option<Arc<pipeline::Pipeline>>,
    binding: Option<wgpu::BindGroup>,
    target: StaticHatchTarget,
    metrics: StaticHatchMetrics,
}
/// Frozen target uniform/binding snapshot. Preparing another placement/camera
/// never overwrites this draw, including after cancellation or engine eviction.
#[derive(Clone)]
pub struct PreparedStaticHatches(Arc<Prepared>);
impl PreparedStaticHatches {
    pub fn source(&self) -> &StaticHatchBuffer {
        &self.0.uploaded.source
    }
    pub fn target(&self) -> StaticHatchTarget {
        self.0.target
    }
    pub fn metrics(&self) -> StaticHatchMetrics {
        self.0.metrics
    }
    /// Encode in original command order. None preserves the caller's current
    /// scissor exactly; Some supplies the actual already-localized binary clip.
    /// Empty clip skips draws without issuing an invalid zero-area WebGPU clip.
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scissor: Option<[u32; 4]>,
    ) -> Result<u32, Error> {
        if let Some([x, y, w, h]) = scissor {
            if x.checked_add(w).is_none_or(|v| v > self.0.target.size[0])
                || y.checked_add(h).is_none_or(|v| v > self.0.target.size[1])
            {
                return Err(Error::InvalidInput);
            }
            if w == 0 || h == 0 {
                return Ok(0);
            }
            pass.set_scissor_rect(x, y, w, h);
        }
        let Some(geometry) = &self.0.uploaded.geometry else {
            return Ok(0);
        };
        let pipeline = self.0.pipeline.as_ref().ok_or(Error::ResourceDomain)?;
        let binding = self.0.binding.as_ref().ok_or(Error::ResourceDomain)?;
        pass.set_pipeline(&pipeline.pipeline);
        pass.set_bind_group(0, binding, &[]);
        pass.set_vertex_buffer(0, geometry.vertices.slice(..));
        pass.set_index_buffer(geometry.indices.slice(..), wgpu::IndexFormat::Uint32);
        for batch in self.source().batches() {
            if !batch.indices.is_empty() {
                pass.draw_indexed(batch.indices.clone(), 0, 0..1);
            }
        }
        Ok(self.0.metrics.draw_calls)
    }
    /// Queue-completion lease for callers submitting their own render pass.
    /// No finalizer polling, explicit destroy, or producer callbacks occur.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}

/// Render-thread static upload/pipeline owner. The generation index holds weak
/// references only; actual callers own retirement and can share uploads across
/// targets. The shader and compiler flags are the unchanged original defaults.
pub struct StaticHatchEngine {
    device: wgpu::Device,
    uploaded: HashMap<usize, Weak<Uploaded>>,
    pipelines: HashMap<(wgpu::TextureFormat, u32), Arc<pipeline::Pipeline>>,
}
impl StaticHatchEngine {
    pub fn new(device: &wgpu::Device) -> Result<Self, Error> {
        Self::new_with_preference(device, ExecutionPreference::Automatic)
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            device: device.clone(),
            uploaded: HashMap::new(),
            pipelines: HashMap::new(),
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    pub fn pipeline_count(&self) -> usize {
        self.pipelines.len()
    }
    /// Remove expired lookup entries; live upload and draw owners stay valid.
    pub fn retire_unused_generations(&mut self) {
        self.uploaded.retain(|_, owner| owner.strong_count() > 0);
    }
    /// Admit all original storage/vertex/index extents before creating buffers.
    pub fn preflight_upload(
        device: &wgpu::Device,
        source: &StaticHatchBuffer,
    ) -> Result<u64, Error> {
        let storage = [
            bytes::<GpuBrush>(source.materials().brushes().len().max(1))?,
            bytes::<GpuGradientStop>(source.materials().records().len().max(1))?,
            if source.is_empty() {
                0
            } else {
                bytes::<GpuHatchRecord>(source.records().len())?
            },
            if source.is_empty() {
                0
            } else {
                bytes::<GpuHatchSegment>(source.segments().len())?
            },
        ];
        let geometry = [
            bytes::<VectorVertex>(source.vertices().len())?,
            bytes::<u32>(source.indices().len())?,
        ];
        let limits = device.limits();
        if storage
            .iter()
            .any(|v| *v > u64::from(limits.max_storage_buffer_binding_size))
            || storage
                .iter()
                .chain(&geometry)
                .any(|v| *v > limits.max_buffer_size)
        {
            return Err(Error::Capacity);
        }
        storage
            .iter()
            .chain(&geometry)
            .try_fold(0_u64, |sum, value| {
                sum.checked_add(*value).ok_or(Error::Capacity)
            })
    }
    pub async fn upload(
        &mut self,
        source: &StaticHatchBuffer,
    ) -> Result<UploadedStaticHatches, Error> {
        let resident_bytes = Self::preflight_upload(&self.device, source)?;
        let identity = Arc::as_ptr(&source.0) as usize;
        if let Some(resources) = self.uploaded.get(&identity).and_then(Weak::upgrade) {
            let metrics = StaticHatchMetrics {
                static_resident_bytes: resident_bytes,
                generation_cache_hits: 1,
                ..StaticHatchMetrics::source(source)
            };
            return Ok(UploadedStaticHatches { resources, metrics });
        }
        self.retire_unused_generations();
        self.uploaded.try_reserve(1).map_err(|_| Error::Capacity)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let brushes = buffer(
            &self.device,
            "ProGPU static Hatch brushes",
            source.materials().brushes(),
            wgpu::BufferUsages::STORAGE,
        );
        let stops = buffer(
            &self.device,
            "ProGPU static Hatch auxiliary records",
            source.materials().records(),
            wgpu::BufferUsages::STORAGE,
        );
        let geometry = (!source.is_empty()).then(|| GeometryBuffers {
            vertices: buffer(
                &self.device,
                "ProGPU static Hatch vertices",
                source.vertices(),
                wgpu::BufferUsages::VERTEX,
            ),
            indices: buffer(
                &self.device,
                "ProGPU static Hatch indices",
                source.indices(),
                wgpu::BufferUsages::INDEX,
            ),
            records: buffer(
                &self.device,
                "ProGPU static Hatch records",
                source.records(),
                wgpu::BufferUsages::STORAGE,
            ),
            segments: buffer(
                &self.device,
                "ProGPU static Hatch boundaries",
                source.segments(),
                wgpu::BufferUsages::STORAGE,
            ),
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let metrics = StaticHatchMetrics {
            geometry_upload_bytes: bytes::<VectorVertex>(source.vertices().len())?
                + bytes::<u32>(source.indices().len())?,
            boundary_upload_bytes: bytes::<GpuHatchRecord>(source.records().len())?
                + bytes::<GpuHatchSegment>(source.segments().len())?,
            material_upload_bytes: brushes.size() + stops.size(),
            static_resident_bytes: resident_bytes,
            ..StaticHatchMetrics::source(source)
        };
        let resources = Arc::new(Uploaded {
            source: source.clone(),
            device: self.device.clone(),
            geometry,
            brushes,
            stops,
            resident_bytes,
        });
        self.uploaded.insert(identity, Arc::downgrade(&resources));
        Ok(UploadedStaticHatches { resources, metrics })
    }
    pub async fn prepare(
        &mut self,
        uploaded: &UploadedStaticHatches,
        target: StaticHatchTarget,
    ) -> Result<PreparedStaticHatches, Error> {
        if uploaded.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        target.validate(&self.device)?;
        let pipeline = if uploaded.resources.geometry.is_some() {
            let key = (target.format, target.sample_count);
            if !self.pipelines.contains_key(&key) {
                self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
                let pipeline = Arc::new(
                    pipeline::Pipeline::new(&self.device, target.format, target.sample_count)
                        .await?,
                );
                self.pipelines.insert(key, pipeline);
            }
            Some(
                self.pipelines
                    .get(&key)
                    .ok_or(Error::ResourceDomain)?
                    .clone(),
            )
        } else {
            None
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let uniforms = target.uniforms();
        let uniform = buffer(
            &self.device,
            "ProGPU immutable static Hatch viewport",
            std::slice::from_ref(&uniforms),
            wgpu::BufferUsages::UNIFORM,
        );
        let binding = match (&pipeline, &uploaded.resources.geometry) {
            (Some(pipeline), Some(geometry)) => {
                Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU static Hatch owned target generation"),
                    layout: &pipeline.layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                                buffer: &uniform,
                                offset: 0,
                                size: std::num::NonZeroU64::new(192),
                            }),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: uploaded.resources.brushes.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: geometry.records.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: geometry.segments.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 4,
                            resource: uploaded.resources.stops.as_entire_binding(),
                        },
                    ],
                }))
            }
            _ => None,
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let metrics = StaticHatchMetrics {
            uniform_upload_bytes: uniform.size(),
            static_resident_bytes: uploaded.resources.resident_bytes,
            ..StaticHatchMetrics::source(uploaded.source())
        };
        Ok(PreparedStaticHatches(Arc::new(Prepared {
            uploaded: uploaded.resources.clone(),
            _uniform: uniform,
            pipeline,
            binding,
            target,
            metrics,
        })))
    }
}
fn bytes<T>(count: usize) -> Result<u64, Error> {
    count
        .checked_mul(size_of::<T>())
        .and_then(|v| u64::try_from(v).ok())
        .ok_or(Error::Capacity)
}
fn buffer<T: Pod + Zeroable>(
    device: &wgpu::Device,
    label: &str,
    contents: &[T],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    let zero = T::zeroed();
    let contents = if contents.is_empty() {
        std::slice::from_ref(&zero)
    } else {
        contents
    };
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: bytemuck::cast_slice(contents),
        usage: usage | wgpu::BufferUsages::COPY_DST,
    })
}
