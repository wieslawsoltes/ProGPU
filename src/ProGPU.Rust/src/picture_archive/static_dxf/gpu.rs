//! Shared static geometry/coverage uploads and original ordered target execution.
use super::*;
use crate::{
    image::ImageSampling,
    path::{CoverageAtlas, ExecutionPath, ExecutionPreference, PathRasterizer},
    scene::LayerBlend,
    shader_effect::ManagedShaderMask,
    vector::{GpuBrush, GpuGradientStop},
};
use bytemuck::{Pod, Zeroable};
use std::{collections::HashMap, sync::Weak};
use wgpu::util::DeviceExt;
mod pipeline;
mod text;
use pipeline::Pipeline;

/// Actual static target. The mask is an already-resolved original target-local
/// record/texture owner; this boundary performs no mask-source callbacks.
#[derive(Clone, Debug)]
pub struct StaticDxfTarget {
    pub frame: super::super::StaticHatchTarget,
    pub blend: LayerBlend,
    pub mask: Option<Arc<ManagedShaderMask>>,
}
impl StaticDxfTarget {
    pub fn validate(&self, device: &wgpu::Device) -> Result<(), Error> {
        self.frame.validate(device)?;
        if let Some(mask) = &self.mask {
            if mask.target().size != self.frame.size || mask.target().origin != [0; 2] {
                return Err(Error::ResourceDomain);
            }
            if let Some(image) = mask.image() {
                if image.device() != device {
                    return Err(Error::ResourceDomain);
                }
            }
        }
        Ok(())
    }
    fn uniforms(&self) -> Uniforms {
        let model = self.frame.model_to_screen();
        Uniforms {
            projection: self.frame.projection.0,
            mvp: model.0,
            view: Matrix4::IDENTITY.0,
            canvas_size: self.frame.canvas_size,
            dpi: self.frame.dpi_scale,
            padding: 0.0,
            origin: [0.0; 2],
            scales: model.stroke_scales().unwrap_or([0.0; 2]),
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
    dpi: f32,
    padding: f32,
    origin: [f32; 2],
    scales: [f32; 2],
}
const _: () = assert!(size_of::<Uniforms>() == 224);

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StaticDxfMetrics {
    pub source_commands: u32,
    pub vector_draws: u32,
    pub hatch_draws: u32,
    pub text_draws: u32,
    pub retained_glyph_draws: u32,
    pub text_upload_bytes: u64,
    pub glyph_texture_bytes: u64,
    pub path_rasters: u32,
    pub geometry_upload_bytes: u64,
    pub material_upload_bytes: u64,
    pub boundary_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub mask_uniform_upload_bytes: u64,
    pub static_resident_bytes: u64,
    pub coverage_texture_bytes: u64,
    pub generation_cache_hits: u32,
}
impl StaticDxfMetrics {
    pub fn draw_count(self) -> u64 {
        u64::from(self.vector_draws)
            + u64::from(self.hatch_draws)
            + u64::from(self.text_draws)
            + u64::from(self.retained_glyph_draws)
    }
    fn source(source: &StaticDxfBuffer) -> Self {
        Self {
            source_commands: source.source().commands.len() as u32,
            vector_draws: source
                .batches()
                .iter()
                .filter(|b| b.kind == StaticDxfBatchKind::Vector && !b.indices.is_empty())
                .count() as u32,
            hatch_draws: source
                .batches()
                .iter()
                .filter(|b| b.kind == StaticDxfBatchKind::Hatch && !b.indices.is_empty())
                .count() as u32,
            path_rasters: source.path_raster_count() as u32,
            text_draws: source
                .batches()
                .iter()
                .filter(|b| b.kind == StaticDxfBatchKind::Text && !b.indices.is_empty())
                .count() as u32,
            retained_glyph_draws: u32::from(!source.retained_glyphs().is_empty()),
            glyph_texture_bytes: text::texture_bytes(source),
            ..Default::default()
        }
    }
}
struct Uploaded {
    device: wgpu::Device,
    source: StaticDxfBuffer,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    brushes: wgpu::Buffer,
    stops: wgpu::Buffer,
    hatch_records: Option<wgpu::Buffer>,
    hatch_segments: Option<wgpu::Buffer>,
    coverage: Option<CoverageAtlas>,
    resident: u64,
    text: Option<text::UploadedText>,
}
/// Retained static vector/material/boundary/coverage generation shared by targets.
#[derive(Clone)]
pub struct UploadedStaticDxf {
    resources: Arc<Uploaded>,
    metrics: StaticDxfMetrics,
}
impl UploadedStaticDxf {
    pub fn source(&self) -> &StaticDxfBuffer {
        &self.resources.source
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.resources.device
    }
    pub fn metrics(&self) -> StaticDxfMetrics {
        self.metrics
    }
}
struct VectorBindings {
    frame: wgpu::BindGroup,
    coverage: wgpu::BindGroup,
    mask: Option<wgpu::BindGroup>,
}
struct Prepared {
    uploaded: Arc<Uploaded>,
    target: StaticDxfTarget,
    _uniform: wgpu::Buffer,
    _mask_uniform: Option<wgpu::Buffer>,
    vector: Option<(Arc<Pipeline>, VectorBindings)>,
    hatch: Option<(Arc<Pipeline>, wgpu::BindGroup)>,
    metrics: StaticDxfMetrics,
    text: Option<text::PreparedText>,
}
/// Frozen target camera, blend, mask and pipeline ownership. No mutation or
/// callbacks occur while the caller's actual render pass is being encoded.
#[derive(Clone)]
pub struct PreparedStaticDxf(Arc<Prepared>);
impl PreparedStaticDxf {
    pub fn source(&self) -> &StaticDxfBuffer {
        &self.0.uploaded.source
    }
    pub fn target(&self) -> &StaticDxfTarget {
        &self.0.target
    }
    pub fn metrics(&self) -> StaticDxfMetrics {
        self.0.metrics
    }
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scissor: Option<[u32; 4]>,
    ) -> Result<u32, Error> {
        if let Some([x, y, w, h]) = scissor {
            if x.checked_add(w)
                .is_none_or(|v| v > self.0.target.frame.size[0])
                || y.checked_add(h)
                    .is_none_or(|v| v > self.0.target.frame.size[1])
            {
                return Err(Error::InvalidInput);
            }
            if w == 0 || h == 0 {
                return Ok(0);
            }
            pass.set_scissor_rect(x, y, w, h);
        }
        let resources = &self.0.uploaded;
        pass.set_vertex_buffer(0, resources.vertices.slice(..));
        pass.set_index_buffer(resources.indices.slice(..), wgpu::IndexFormat::Uint32);
        let mut family = None;
        let mut draws = 0;
        for batch in self.source().batches() {
            if batch.indices.is_empty() {
                continue;
            }
            if batch.kind == StaticDxfBatchKind::Text {
                self.0
                    .text
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .draw_atlas(
                        pass,
                        resources.text.as_ref().ok_or(Error::ResourceDomain)?,
                        batch.indices.clone(),
                    )?;
                family = Some(batch.kind);
                draws += 1;
                continue;
            }
            if family != Some(batch.kind) {
                pass.set_vertex_buffer(0, resources.vertices.slice(..));
                pass.set_index_buffer(resources.indices.slice(..), wgpu::IndexFormat::Uint32);
                match batch.kind {
                    StaticDxfBatchKind::Vector => {
                        let (pipeline, bindings) =
                            self.0.vector.as_ref().ok_or(Error::ResourceDomain)?;
                        pass.set_pipeline(&pipeline.pipeline);
                        pass.set_bind_group(0, &bindings.frame, &[]);
                        pass.set_bind_group(1, &bindings.coverage, &[]);
                        if let Some(mask) = &bindings.mask {
                            pass.set_bind_group(2, mask, &[]);
                        }
                    }
                    StaticDxfBatchKind::Hatch => {
                        let (pipeline, binding) =
                            self.0.hatch.as_ref().ok_or(Error::ResourceDomain)?;
                        pass.set_pipeline(&pipeline.pipeline);
                        pass.set_bind_group(0, binding, &[]);
                    }
                    StaticDxfBatchKind::Text => return Err(Error::ResourceDomain),
                }
                family = Some(batch.kind);
            }
            pass.draw_indexed(batch.indices.clone(), 0, 0..1);
            draws += 1;
        }
        let count = u32::try_from(self.source().retained_glyphs().instances().len())
            .map_err(|_| Error::Capacity)?;
        if count != 0
            && self
                .0
                .text
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .draw_retained(pass, count)?
        {
            draws += 1;
        }
        Ok(draws)
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
struct PipelineKey {
    format: wgpu::TextureFormat,
    samples: u32,
    hatch: bool,
    blend: LayerBlend,
    masked: bool,
}
/// Actual device-domain static preparation. Coverage is computed once per
/// uploaded generation by the existing original path/Boolean GPU evaluator.
pub struct StaticDxfEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    uploads: HashMap<usize, Weak<Uploaded>>,
    pipelines: HashMap<PipelineKey, Arc<Pipeline>>,
    rasterizer: Option<PathRasterizer>,
    white: wgpu::Texture,
    sampler: wgpu::Sampler,
    text: text::TextEngine,
}
impl StaticDxfEngine {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Result<Self, Error> {
        Self::new_with_preference(device, queue, ExecutionPreference::Automatic)
    }
    pub fn new_with_preference(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
        ) {
            return Err(Error::UnsupportedExecution);
        }
        let white = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU static DXF empty atlas"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        queue.write_texture(
            white.as_image_copy(),
            &[255],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(1),
                rows_per_image: Some(1),
            },
            white.size(),
        );
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("ProGPU original static coverage sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            uploads: HashMap::new(),
            pipelines: HashMap::new(),
            rasterizer: None,
            white,
            sampler,
            text: text::TextEngine::default(),
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::GpuShader
    }
    pub fn coverage_execution_path(&self) -> Option<ExecutionPath> {
        self.rasterizer
            .as_ref()
            .map(|_| ExecutionPath::NativeCompute)
    }
    pub fn retire_unused_generations(&mut self) {
        self.uploads.retain(|_, v| v.strong_count() > 0);
    }
    pub fn preflight_upload(device: &wgpu::Device, source: &StaticDxfBuffer) -> Result<u64, Error> {
        let text_bytes = text::preflight(device, source)?;
        let compiled = &source.0.compiled;
        let storage = [
            bytes::<GpuBrush>(source.materials().brushes().len().max(1))?,
            bytes::<GpuGradientStop>(source.materials().records().len().max(1))?,
            bytes::<super::super::GpuHatchRecord>(compiled.hatches.records.len())?,
            bytes::<super::super::GpuHatchSegment>(compiled.hatches.segments.len())?,
        ];
        let geometry = [
            bytes::<crate::geometry::VectorVertex>(source.mesh().vertices().len())?.max(4),
            bytes::<u32>(source.mesh().indices().len())?.max(4),
        ];
        let limits = device.limits();
        if storage
            .iter()
            .any(|v| *v > u64::from(limits.max_storage_buffer_binding_size))
            || storage
                .iter()
                .chain(&geometry)
                .any(|v| *v > limits.max_buffer_size)
            || source.atlas_size() > limits.max_texture_dimension_2d
        {
            return Err(Error::Capacity);
        }
        let atlas = u64::from(source.atlas_size()).pow(2);
        let total = storage.iter().chain(&geometry).try_fold(
            atlas.checked_add(text_bytes).ok_or(Error::Capacity)?,
            |sum, v| sum.checked_add(*v).ok_or(Error::Capacity),
        )?;
        if total > 512 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        Ok(total)
    }
    /// Upload and, when needed, submit original coverage compute before return.
    /// No target draw is submitted. Cancellation never publishes partial owners.
    pub async fn upload(&mut self, source: &StaticDxfBuffer) -> Result<UploadedStaticDxf, Error> {
        let resident = Self::preflight_upload(&self.device, source)?;
        let identity = Arc::as_ptr(&source.0) as usize;
        if let Some(resources) = self.uploads.get(&identity).and_then(Weak::upgrade) {
            return Ok(UploadedStaticDxf {
                resources,
                metrics: StaticDxfMetrics {
                    static_resident_bytes: resident,
                    coverage_texture_bytes: u64::from(source.atlas_size()).pow(2),
                    generation_cache_hits: 1,
                    ..StaticDxfMetrics::source(source)
                },
            });
        }
        self.retire_unused_generations();
        self.uploads.try_reserve(1).map_err(|_| Error::Capacity)?;
        let compiled = &source.0.compiled;
        let coverage = if compiled.rasters.is_empty() {
            None
        } else {
            if self.rasterizer.is_none() {
                self.rasterizer = Some(
                    PathRasterizer::new(&self.device, &self.queue, ExecutionPreference::Automatic)
                        .await?,
                );
            }
            let mut requests = Vec::new();
            requests
                .try_reserve_exact(compiled.rasters.len())
                .map_err(|_| Error::Capacity)?;
            requests.extend(compiled.rasters.iter().map(|(p, r)| (p, *r)));
            let rasterizer = self.rasterizer.as_mut().ok_or(Error::ResourceDomain)?;
            rasterizer.prepare_programs(&requests).await?;
            Some(rasterizer.rasterize_atlas(&requests)?)
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let vertices = buffer_bytes(
            &self.device,
            "ProGPU static DXF vertices",
            bytemuck::cast_slice(source.mesh().vertices()),
            wgpu::BufferUsages::VERTEX,
        );
        let indices = buffer_bytes(
            &self.device,
            "ProGPU static DXF indices",
            bytemuck::cast_slice(source.mesh().indices()),
            wgpu::BufferUsages::INDEX,
        );
        let brushes = buffer(
            &self.device,
            "ProGPU static DXF brushes",
            source.materials().brushes(),
            wgpu::BufferUsages::STORAGE,
        );
        let stops = buffer(
            &self.device,
            "ProGPU static DXF auxiliary records",
            source.materials().records(),
            wgpu::BufferUsages::STORAGE,
        );
        let hatch_records = (!compiled.hatches.records.is_empty()).then(|| {
            buffer(
                &self.device,
                "ProGPU mixed static Hatch records",
                &compiled.hatches.records,
                wgpu::BufferUsages::STORAGE,
            )
        });
        let hatch_segments = (!compiled.hatches.records.is_empty()).then(|| {
            buffer(
                &self.device,
                "ProGPU mixed static Hatch boundaries",
                &compiled.hatches.segments,
                wgpu::BufferUsages::STORAGE,
            )
        });
        let text = text::upload(&self.device, source);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let metrics = StaticDxfMetrics {
            geometry_upload_bytes: vertices.size() + indices.size(),
            material_upload_bytes: brushes.size() + stops.size(),
            boundary_upload_bytes: hatch_records.as_ref().map_or(0, |v| v.size())
                + hatch_segments.as_ref().map_or(0, |v| v.size()),
            static_resident_bytes: resident,
            coverage_texture_bytes: u64::from(source.atlas_size()).pow(2),
            text_upload_bytes: text.as_ref().map_or(0, |v| v.bytes()),
            ..StaticDxfMetrics::source(source)
        };
        let resources = Arc::new(Uploaded {
            device: self.device.clone(),
            source: source.clone(),
            vertices,
            indices,
            brushes,
            stops,
            hatch_records,
            hatch_segments,
            coverage,
            resident,
            text,
        });
        self.uploads.insert(identity, Arc::downgrade(&resources));
        Ok(UploadedStaticDxf { resources, metrics })
    }
    async fn pipeline(
        &mut self,
        target: &StaticDxfTarget,
        hatch: bool,
    ) -> Result<Arc<Pipeline>, Error> {
        let key = PipelineKey {
            format: target.frame.format,
            samples: target.frame.sample_count,
            hatch,
            blend: if hatch {
                LayerBlend::SrcOver
            } else {
                target.blend.managed_vector_effective_mode()
            },
            masked: !hatch && target.mask.is_some(),
        };
        if let Some(value) = self.pipelines.get(&key) {
            return Ok(value.clone());
        }
        self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
        let value = Arc::new(Pipeline::new(&self.device, key).await?);
        self.pipelines.insert(key, value.clone());
        Ok(value)
    }
    pub async fn prepare(
        &mut self,
        uploaded: &UploadedStaticDxf,
        target: StaticDxfTarget,
    ) -> Result<PreparedStaticDxf, Error> {
        if uploaded.device() != &self.device {
            return Err(Error::ResourceDomain);
        }
        target.validate(&self.device)?;
        let has_vector = uploaded
            .source()
            .batches()
            .iter()
            .any(|b| b.kind == StaticDxfBatchKind::Vector && !b.indices.is_empty());
        let has_hatch = uploaded
            .source()
            .batches()
            .iter()
            .any(|b| b.kind == StaticDxfBatchKind::Hatch && !b.indices.is_empty());
        let vector = if has_vector {
            Some(self.pipeline(&target, false).await?)
        } else {
            None
        };
        let hatch = if has_hatch {
            Some(self.pipeline(&target, true).await?)
        } else {
            None
        };
        let resources = &uploaded.resources;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let uniform = buffer(
            &self.device,
            "ProGPU frozen static DXF viewport",
            std::slice::from_ref(&target.uniforms()),
            wgpu::BufferUsages::UNIFORM,
        );
        let mask_uniform = target.mask.as_ref().filter(|_| has_vector).map(|m| {
            buffer(
                &self.device,
                "ProGPU static outer coverage mapping",
                std::slice::from_ref(m.uniforms()),
                wgpu::BufferUsages::UNIFORM,
            )
        });
        let vector = vector
            .map(|pipeline| -> Result<_, Error> {
                let frame = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU static vector frame"),
                    layout: &pipeline.frame,
                    entries: &[
                        entry(0, &uniform),
                        entry(1, &resources.brushes),
                        entry(2, &resources.stops),
                    ],
                });
                let view = resources
                    .coverage
                    .as_ref()
                    .map_or(&self.white, |v| v.texture())
                    .create_view(&Default::default());
                let coverage = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU static vector coverage"),
                    layout: pipeline.coverage.as_ref().ok_or(Error::ResourceDomain)?,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: wgpu::BindingResource::Sampler(&self.sampler),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: wgpu::BindingResource::TextureView(&view),
                        },
                    ],
                });
                let mask = target
                    .mask
                    .as_ref()
                    .zip(mask_uniform.as_ref())
                    .map(|(mask, mapping)| -> Result<_, Error> {
                        let view = mask
                            .image()
                            .map_or(&self.white, |v| v.texture())
                            .create_view(&Default::default());
                        let filter = if mask.sampling() == ImageSampling::Nearest {
                            wgpu::FilterMode::Nearest
                        } else {
                            wgpu::FilterMode::Linear
                        };
                        let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
                            label: Some("ProGPU static outer mask sampler"),
                            mag_filter: filter,
                            min_filter: filter,
                            ..Default::default()
                        });
                        Ok(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                            label: Some("ProGPU static outer mask"),
                            layout: pipeline.mask.as_ref().ok_or(Error::ResourceDomain)?,
                            entries: &[
                                wgpu::BindGroupEntry {
                                    binding: 0,
                                    resource: wgpu::BindingResource::Sampler(&sampler),
                                },
                                wgpu::BindGroupEntry {
                                    binding: 1,
                                    resource: wgpu::BindingResource::TextureView(&view),
                                },
                                entry(2, mapping),
                            ],
                        }))
                    })
                    .transpose()?;
                Ok((
                    pipeline,
                    VectorBindings {
                        frame,
                        coverage,
                        mask,
                    },
                ))
            })
            .transpose()?;
        let hatch = if let Some(pipeline) = hatch {
            let records = resources
                .hatch_records
                .as_ref()
                .ok_or(Error::ResourceDomain)?;
            let segments = resources
                .hatch_segments
                .as_ref()
                .ok_or(Error::ResourceDomain)?;
            let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU static Hatch frame"),
                layout: &pipeline.frame,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                            buffer: &uniform,
                            offset: 0,
                            size: std::num::NonZeroU64::new(192),
                        }),
                    },
                    entry(1, &resources.brushes),
                    entry(2, records),
                    entry(3, segments),
                    entry(4, &resources.stops),
                ],
            });
            Some((pipeline, binding))
        } else {
            None
        };
        let text = if let Some(text) = &resources.text {
            Some(
                self.text
                    .prepare(
                        &self.device,
                        uploaded.source(),
                        text,
                        &target,
                        &uniform,
                        &self.white,
                        &self.sampler,
                    )
                    .await?,
            )
        } else {
            None
        };
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let metrics = StaticDxfMetrics {
            uniform_upload_bytes: uniform.size(),
            mask_uniform_upload_bytes: mask_uniform.as_ref().map_or(0, |v| v.size())
                + text.as_ref().map_or(0, |v| v.mask_uniform_bytes()),
            static_resident_bytes: resources.resident,
            coverage_texture_bytes: u64::from(uploaded.source().atlas_size()).pow(2),
            ..StaticDxfMetrics::source(uploaded.source())
        };
        Ok(PreparedStaticDxf(Arc::new(Prepared {
            uploaded: resources.clone(),
            target,
            _uniform: uniform,
            _mask_uniform: mask_uniform,
            vector,
            hatch,
            metrics,
            text,
        })))
    }
}
fn bytes<T>(count: usize) -> Result<u64, Error> {
    count
        .checked_mul(size_of::<T>())
        .and_then(|v| u64::try_from(v).ok())
        .ok_or(Error::Capacity)
}
fn buffer_bytes(
    device: &wgpu::Device,
    label: &str,
    data: &[u8],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: if data.is_empty() { &[0; 4] } else { data },
        usage: usage | wgpu::BufferUsages::COPY_DST,
    })
}
fn buffer<T: Pod + Zeroable>(
    device: &wgpu::Device,
    label: &str,
    data: &[T],
    usage: wgpu::BufferUsages,
) -> wgpu::Buffer {
    let zero = T::zeroed();
    let data = if data.is_empty() {
        std::slice::from_ref(&zero)
    } else {
        data
    };
    buffer_bytes(device, label, bytemuck::cast_slice(data), usage)
}
fn entry(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
