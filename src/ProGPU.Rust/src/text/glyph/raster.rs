//! Original incremental outline uploads and bounded GPU-only glyph raster batches.
use super::*;
use crate::{Error, path::ExecutionPreference, shaders};
use std::{collections::HashMap, num::NonZeroU64};
mod dispatch;
mod upload;

/// Connected font/raster failure without erasing the original font error.
#[derive(Debug, thiserror::Error)]
pub enum GlyphGpuError {
    /// Original font outline failure.
    #[error(transparent)]
    Font(#[from] FontError),
    /// Resource, capacity or GPU validation failure.
    #[error(transparent)]
    Render(#[from] Error),
}
/// Original selected GPU raster route. CPU implementations remain separate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GlyphExecutionPath {
    /// Canonical packed compute coverage followed by R8 copies.
    NativeCompute,
    /// Canonical identical grayscale coverage in a fragment shader.
    RasterShader,
}
/// Explicit raster mode, never inferred from ordinary font recommendation bits.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum GlyphRasterMode {
    /// Original 8-by-8 grayscale coverage.
    #[default]
    Grayscale,
    /// Original bounded physical binary scan, preserving both ordered passes.
    Binary,
}
/// Original bounded upload/raster staging capacities.
#[derive(Clone, Copy, Debug)]
pub struct GlyphRasterizerOptions {
    /// Requested route. Unsupported forced CPU routes fail explicitly.
    pub preference: ExecutionPreference,
    /// Original 256-byte-aligned uniform ring size (default 64 KiB).
    pub uniform_ring_bytes: u32,
    /// Original 256-byte-multiple coverage ring size (default 256 KiB).
    pub coverage_ring_bytes: u32,
}
impl Default for GlyphRasterizerOptions {
    fn default() -> Self {
        Self {
            preference: ExecutionPreference::Automatic,
            uniform_ring_bytes: 64 * 1024,
            coverage_ring_bytes: 256 * 1024,
        }
    }
}
/// Actual cumulative upload and submission counters, not estimated performance.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GlyphGpuMetrics {
    /// Actual tightly packed RGBA bitmap texture writes.
    pub bitmap_upload_writes: u64,
    /// Actual RGBA bytes uploaded, without row repacking.
    pub bitmap_upload_bytes: u64,
    /// Contiguous original record/segment upload writes.
    pub outline_upload_writes: u64,
    /// Actual record/segment payload bytes.
    pub outline_upload_bytes: u64,
    /// Uniform ring or oversized uniform writes.
    pub uniform_upload_writes: u64,
    /// Actual uploaded uniform slice bytes, including original ring padding.
    pub uniform_upload_bytes: u64,
    /// Submitted coverage batches, including oversized standalone requests.
    pub raster_submissions: u64,
    /// Actual compute passes, each containing ordered glyph dispatches.
    pub compute_passes: u64,
    /// Actual fragment passes.
    pub raster_passes: u64,
    /// Actual raster bind-group constructions.
    pub bind_groups: u64,
    /// Actual record/segment buffer copy submissions on growth.
    pub outline_copy_submissions: u64,
    /// Original over-budget cache resets.
    pub outline_cache_resets: u64,
    /// Actual glyph raster requests submitted.
    pub rasterized_glyphs: u64,
}
/// Opaque actual record slot tied to its creating rasterizer/cache generation.
/// Stale/foreign slots reject before encoding; submitted GPU owners remain alive.
#[derive(Clone, Debug)]
pub struct GlyphGpuSlot {
    owner: Arc<()>,
    generation: u64,
    index: u32,
    record: GpuGlyphRecord,
}
impl GlyphGpuSlot {
    /// Original rebased record uploaded for this slot.
    pub fn record(&self) -> GpuGlyphRecord {
        self.record
    }
    /// Actual uploaded record index, for diagnostics only.
    pub fn index(&self) -> u32 {
        self.index
    }
}
/// Original physical raster request into one caller-selected atlas region.
#[derive(Clone, Debug)]
pub struct GlyphRasterRequest {
    /// Actual immutable uploaded outline slot.
    pub slot: GlyphGpuSlot,
    /// Original X/Y starts, with screen Y downward.
    pub origin: [f32; 2],
    /// Original positive source-to-physical scalar scale.
    pub scale: f32,
    /// Original physical X phase.
    pub subpixel_x: f32,
    /// Actual destination atlas texel origin.
    pub atlas_origin: [u32; 2],
    /// Exact coverage dimensions; allocation-class padding remains separate.
    pub size: [u32; 2],
    /// Explicit original raster contract.
    pub mode: GlyphRasterMode,
}
/// Actual R8 target generation, retained by wgpu commands and consumer views.
/// Growth returns a new owner; never explicitly destroys the old GPU texture.
#[derive(Clone, Debug)]
pub struct GlyphCoverageTexture {
    owner: Arc<()>,
    texture: wgpu::Texture,
    size: [u32; 2],
}
/// Separate straight-alpha RGBA8 atlas generation. Its private typed wrapper
/// cannot be supplied to a grayscale/binary coverage dispatch.
#[derive(Clone, Debug)]
pub struct GlyphColorTexture(GlyphCoverageTexture);
impl GlyphColorTexture {
    /// Actual RGBA8 texture, retained independently through growth/submission.
    pub fn texture(&self) -> &wgpu::Texture {
        &self.0.texture
    }
    /// Actual physical width and height.
    pub fn size(&self) -> [u32; 2] {
        self.0.size
    }
}
impl GlyphCoverageTexture {
    /// Actual R8 texture. Consumers must not explicitly destroy it while retained.
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    /// Actual physical width and height.
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
}
struct FontGpuData {
    _font: ManagedFont,
    slots: HashMap<u16, GlyphGpuSlot>,
}
struct BinaryPipeline {
    layout: wgpu::BindGroupLayout,
    horizontal: wgpu::ComputePipeline,
    vertical: wgpu::ComputePipeline,
}
enum Pipeline {
    Compute(wgpu::ComputePipeline),
    Raster(wgpu::RenderPipeline),
}
/// Original shared glyph GPU engine, independently usable by design-font and
/// retained physical-outline atlas callers. Canonical shaders are unchanged.
/// Atlas packing/residency and final text drawing are separate consumers.
pub struct GlyphRasterizer {
    owner: Arc<()>,
    generation: u64,
    device: wgpu::Device,
    queue: wgpu::Queue,
    path: GlyphExecutionPath,
    options: GlyphRasterizerOptions,
    layout: wgpu::BindGroupLayout,
    pipeline: Pipeline,
    binary: Option<BinaryPipeline>,
    uniforms: wgpu::Buffer,
    uniform_upload: Vec<u8>,
    coverage: Option<wgpu::Buffer>,
    horizontal: Option<wgpu::Buffer>,
    records: wgpu::Buffer,
    segments: wgpu::Buffer,
    record_count: u32,
    segment_count: u32,
    record_capacity: u32,
    segment_capacity: u32,
    pending_records: Vec<GpuGlyphRecord>,
    pending_segments: Vec<Segment>,
    glyph_scratch: Vec<Segment>,
    pending_record_start: u32,
    pending_segment_start: u32,
    font_slots: HashMap<(usize, usize), FontGpuData>,
    ring_bindings: Option<wgpu::BindGroup>,
    binary_bindings: Option<wgpu::BindGroup>,
    metrics: GlyphGpuMetrics,
}
const INITIAL_RECORDS: u32 = 48;
const INITIAL_SEGMENTS: u32 = 896;
const OUTLINE_CHUNK: usize = 256 * 1024;
const OUTLINE_BUDGET: u64 = 4 * 1024 * 1024;
impl GlyphRasterizer {
    pub(super) fn device(&self) -> &wgpu::Device {
        &self.device
    }
    /// Create the selected canonical grayscale route with original compiler
    /// defaults. Automatic prefers admitted compute limits, then the equivalent
    /// fragment route. Binary is compute-only and compiled on first explicit use.
    /// Adapter/source/package qualification remains an independent gate.
    /// Device and queue must come from the same original device request.
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        options: GlyphRasterizerOptions,
    ) -> Result<Self, Error> {
        let limits = device.limits();
        if options.uniform_ring_bytes < 256
            || !options.uniform_ring_bytes.is_multiple_of(256)
            || options.coverage_ring_bytes < 256
            || !options.coverage_ring_bytes.is_multiple_of(256)
            || limits.min_uniform_buffer_offset_alignment > 256
            || u64::from(options.uniform_ring_bytes) > limits.max_buffer_size
        {
            return Err(Error::InvalidInput);
        }
        let compute = limits.max_compute_invocations_per_workgroup >= 256
            && limits.max_compute_workgroup_size_x >= 16
            && limits.max_compute_workgroup_size_y >= 16
            && limits.max_storage_buffers_per_shader_stage >= 3;
        let path = match options.preference {
            ExecutionPreference::Automatic if compute => GlyphExecutionPath::NativeCompute,
            ExecutionPreference::Automatic | ExecutionPreference::GpuShader
                if limits.max_storage_buffers_per_shader_stage >= 2 =>
            {
                GlyphExecutionPath::RasterShader
            }
            ExecutionPreference::NativeCompute if compute => GlyphExecutionPath::NativeCompute,
            _ => return Err(Error::UnsupportedExecution),
        };
        check_buffer(device, u64::from(INITIAL_RECORDS) * 32)?;
        check_buffer(device, u64::from(INITIAL_SEGMENTS) * 48)?;
        if path == GlyphExecutionPath::NativeCompute {
            check_buffer(device, u64::from(options.coverage_ring_bytes))?;
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = binding_layout(device, path, false);
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU glyph raster pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical GlyphRasterizer"),
            source: wgpu::ShaderSource::Wgsl(shaders::GLYPH.into()),
        });
        let pipeline = match path {
            GlyphExecutionPath::NativeCompute => Pipeline::Compute(compute_pipeline(
                device,
                &pipeline_layout,
                &module,
                "cs_main",
            )),
            GlyphExecutionPath::RasterShader => Pipeline::Raster(device.create_render_pipeline(
                &wgpu::RenderPipelineDescriptor {
                    label: Some("ProGPU original glyph fragment raster"),
                    layout: Some(&pipeline_layout),
                    vertex: wgpu::VertexState {
                        module: &module,
                        entry_point: Some("vs_raster_fallback"),
                        compilation_options: Default::default(),
                        buffers: &[],
                    },
                    primitive: Default::default(),
                    depth_stencil: None,
                    multisample: Default::default(),
                    fragment: Some(wgpu::FragmentState {
                        module: &module,
                        entry_point: Some("fs_raster_fallback"),
                        compilation_options: Default::default(),
                        targets: &[Some(wgpu::ColorTargetState {
                            format: wgpu::TextureFormat::R8Unorm,
                            blend: None,
                            write_mask: wgpu::ColorWrites::ALL,
                        })],
                    }),
                    multiview_mask: None,
                    cache: None,
                },
            )),
        };
        let records = outline_buffer(device, INITIAL_RECORDS, 32);
        let segments = outline_buffer(device, INITIAL_SEGMENTS, 48);
        let uniforms = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU glyph uniform ring"),
            size: u64::from(options.uniform_ring_bytes),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let coverage = (path == GlyphExecutionPath::NativeCompute)
            .then(|| coverage_buffer(device, u64::from(options.coverage_ring_bytes), true));
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let mut uniform_upload = Vec::new();
        uniform_upload
            .try_reserve_exact(options.uniform_ring_bytes as usize)
            .map_err(|_| Error::Capacity)?;
        uniform_upload.resize(options.uniform_ring_bytes as usize, 0);
        Ok(Self {
            owner: Arc::new(()),
            generation: 0,
            device: device.clone(),
            queue: queue.clone(),
            path,
            options,
            layout,
            pipeline,
            binary: None,
            uniforms,
            uniform_upload,
            coverage,
            horizontal: None,
            records,
            segments,
            record_count: 0,
            segment_count: 0,
            record_capacity: INITIAL_RECORDS,
            segment_capacity: INITIAL_SEGMENTS,
            pending_records: Vec::new(),
            pending_segments: Vec::new(),
            glyph_scratch: Vec::new(),
            pending_record_start: 0,
            pending_segment_start: 0,
            font_slots: HashMap::new(),
            ring_bindings: None,
            binary_bindings: None,
            metrics: GlyphGpuMetrics::default(),
        })
    }
    /// Actual selected GPU path.
    pub fn execution_path(&self) -> GlyphExecutionPath {
        self.path
    }
    /// Actual cumulative upload and pass counters.
    pub fn metrics(&self) -> GlyphGpuMetrics {
        self.metrics
    }
    pub(super) fn invalidate_font_slots(&mut self) {
        // A failed/cancelled atlas upload cannot certify newly appended slots.
        // Existing submitted resources remain owned; future requests re-upload.
        self.font_slots.clear();
    }
    /// Original current record/segment count.
    pub fn outline_counts(&self) -> [u32; 2] {
        [self.record_count, self.segment_count]
    }
    /// Original allocated persistent record/segment bytes.
    pub fn allocated_outline_bytes(&self) -> u64 {
        u64::from(self.record_capacity) * 32 + u64::from(self.segment_capacity) * 48
    }
    /// Original persistent outline cache budget.
    pub fn outline_cache_budget_bytes(&self) -> u64 {
        OUTLINE_BUDGET
    }
    async fn ensure_binary(&mut self) -> Result<(), Error> {
        if self.binary.is_some() {
            return Ok(());
        }
        let limits = self.device.limits();
        if self.path != GlyphExecutionPath::NativeCompute
            || limits.max_compute_workgroup_size_x < 256
            || limits.max_compute_workgroup_storage_size < 10240
            || limits.max_storage_buffers_per_shader_stage < 4
        {
            return Err(Error::UnsupportedExecution);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = binding_layout(&self.device, self.path, true);
        let pipeline_layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU binary glyph pipeline layout"),
                bind_group_layouts: &[Some(&layout)],
                immediate_size: 0,
            });
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU canonical BinaryGlyphRasterizer"),
                source: wgpu::ShaderSource::Wgsl(shaders::BINARY_GLYPH.into()),
            });
        let horizontal = compute_pipeline(
            &self.device,
            &pipeline_layout,
            &module,
            "cs_binary_horizontal",
        );
        let vertical = compute_pipeline(
            &self.device,
            &pipeline_layout,
            &module,
            "cs_binary_vertical",
        );
        let scratch = coverage_buffer(
            &self.device,
            u64::from(self.options.coverage_ring_bytes),
            false,
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.binary = Some(BinaryPipeline {
            layout,
            horizontal,
            vertical,
        });
        self.horizontal = Some(scratch);
        Ok(())
    }
}
fn check_buffer(device: &wgpu::Device, bytes: u64) -> Result<(), Error> {
    let limits = device.limits();
    if bytes > limits.max_buffer_size || bytes > limits.max_storage_buffer_binding_size {
        return Err(Error::Capacity);
    }
    Ok(())
}
fn outline_buffer(device: &wgpu::Device, capacity: u32, size: u64) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("ProGPU incremental glyph outlines"),
        size: u64::from(capacity) * size,
        usage: wgpu::BufferUsages::STORAGE
            | wgpu::BufferUsages::COPY_SRC
            | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    })
}
fn coverage_buffer(device: &wgpu::Device, size: u64, copy: bool) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("ProGPU glyph packed coverage"),
        size,
        usage: wgpu::BufferUsages::STORAGE
            | if copy {
                wgpu::BufferUsages::COPY_SRC
            } else {
                wgpu::BufferUsages::empty()
            },
        mapped_at_creation: false,
    })
}
fn compute_pipeline(
    device: &wgpu::Device,
    layout: &wgpu::PipelineLayout,
    module: &wgpu::ShaderModule,
    entry: &str,
) -> wgpu::ComputePipeline {
    device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some(entry),
        layout: Some(layout),
        module,
        entry_point: Some(entry),
        compilation_options: Default::default(),
        cache: None,
    })
}
fn binding_layout(
    device: &wgpu::Device,
    path: GlyphExecutionPath,
    binary: bool,
) -> wgpu::BindGroupLayout {
    let count = if binary {
        5
    } else if path == GlyphExecutionPath::NativeCompute {
        4
    } else {
        3
    };
    let entries: Vec<_> = (0..count)
        .map(|binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: if path == GlyphExecutionPath::NativeCompute {
                wgpu::ShaderStages::COMPUTE
            } else {
                wgpu::ShaderStages::FRAGMENT
            },
            ty: wgpu::BindingType::Buffer {
                ty: if binding == 0 {
                    wgpu::BufferBindingType::Uniform
                } else {
                    wgpu::BufferBindingType::Storage {
                        read_only: binding < 3,
                    }
                },
                has_dynamic_offset: binding == 0,
                min_binding_size: NonZeroU64::new(match binding {
                    0 => 48,
                    1 => 32,
                    2 => 48,
                    _ => 4,
                }),
            },
            count: None,
        })
        .collect();
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU glyph bindings"),
        entries: &entries,
    })
}
