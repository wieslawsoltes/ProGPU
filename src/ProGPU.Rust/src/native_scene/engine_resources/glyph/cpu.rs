//! Explicit original CPU coverage policies; never an automatic GPU substitute.
use super::*;
use crate::text::glyph::GpuGlyphRecord;
mod crossings;
mod intrinsics;
mod roots;
mod scan;
use crossings::{Crossing, CurveMetadata, visit};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeGlyphCpuPolicy {
    ScalarReference,
    IntrinsicSimd,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeGlyphCpuImplementation {
    ScalarReference,
    Sse2,
    Neon,
    /// Original lane construction through safe portable lanes on an ISA for
    /// which the current `wide` dependency has no native implementation.
    PortableLanes,
    /// Original non-NEON/non-SSE2 cached-crossing branch, including wasm.
    CachedScalar,
}
impl NativeGlyphCpuPolicy {
    pub fn from_engine_flags(flags: u32) -> Result<Option<Self>, Error> {
        let selected = flags
            & (ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK
                | ENGINE_GLYPH_SCALAR_CPU_FALLBACK
                | ENGINE_GLYPH_RASTER_SHADER_FALLBACK);
        if selected.count_ones() > 1 {
            return Err(Error::InvalidInput);
        }
        Ok(if selected == ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK {
            Some(Self::IntrinsicSimd)
        } else if selected == ENGINE_GLYPH_SCALAR_CPU_FALLBACK {
            Some(Self::ScalarReference)
        } else {
            None
        })
    }
    pub fn implementation(self) -> NativeGlyphCpuImplementation {
        if self == Self::ScalarReference {
            NativeGlyphCpuImplementation::ScalarReference
        } else if cfg!(all(target_arch = "aarch64", target_feature = "neon")) {
            NativeGlyphCpuImplementation::Neon
        } else if cfg!(any(target_feature = "sse2", target_arch = "x86_64")) {
            NativeGlyphCpuImplementation::Sse2
        } else if cfg!(any(
            target_feature = "neon",
            all(target_os = "windows", target_arch = "aarch64")
        )) {
            NativeGlyphCpuImplementation::PortableLanes
        } else {
            NativeGlyphCpuImplementation::CachedScalar
        }
    }
}

/// Exactly one original queue-write source range per unique raster work item.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeGlyphCpuUpload {
    pub raster_index: u32,
    pub atlas_origin: [u32; 2],
    pub size: [u32; 2],
    pub offset: u32,
    pub bytes_per_row: u32,
    pub source_bytes: u64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeGlyphCpuMetrics {
    pub raster_count: u64,
    /// Complete original zero-initialized arena, including row/record padding.
    pub coverage_staging_bytes: u64,
    /// Sum of the actual queue-write lengths, excluding each last row's padding.
    pub texture_write_bytes: u64,
}
/// An immutable generation containing only coverage, never recreated placement.
/// Source aliases, no-ink slots and original coordinates stay in `page`.
#[derive(Clone, Debug)]
pub struct NativeGlyphCpuCoverage {
    page: Arc<NativeGlyphRasterPage>,
    policy: NativeGlyphCpuPolicy,
    bytes: Arc<[u8]>,
    uploads: Arc<[NativeGlyphCpuUpload]>,
    metrics: NativeGlyphCpuMetrics,
}
impl NativeGlyphCpuCoverage {
    pub fn compile(page: Arc<NativeGlyphRasterPage>) -> Result<Self, Error> {
        let policy = NativeGlyphCpuPolicy::from_engine_flags(page.source().engine_flags())?
            .ok_or(Error::UnsupportedExecution)?;
        if page.has_binary_glyphs() {
            return Err(Error::UnsupportedExecution);
        }
        let size = usize::try_from(page.coverage_staging_bytes()).map_err(|_| Error::Capacity)?;
        let mut uploads = allocate(page.work_indices().len())?;
        let mut metrics = NativeGlyphCpuMetrics {
            raster_count: page.work_indices().len() as u64,
            coverage_staging_bytes: page.coverage_staging_bytes(),
            texture_write_bytes: 0,
        };
        for &index in page.work_indices() {
            let raster = page
                .rasters()
                .get(index as usize)
                .ok_or(Error::InvalidInput)?;
            let uniform = page
                .uniforms()
                .get(index as usize)
                .ok_or(Error::InvalidInput)?;
            let record = page
                .records()
                .get(uniform.glyph_index as usize)
                .ok_or(Error::InvalidInput)?;
            let end = u64::from(record.start_segment) + u64::from(record.segment_count);
            let source_bytes = u64::from(raster.output_bytes_per_row)
                * u64::from(raster.height.checked_sub(1).ok_or(Error::InvalidInput)?)
                + u64::from(raster.width);
            if raster.width == 0
                || raster.output_bytes_per_row < raster.width
                || u64::from(raster.output_offset) + source_bytes > size as u64
                || end > page.source().segments().len() as u64
                || record.padding[0] != 0
            {
                return Err(Error::InvalidInput);
            }
            metrics.texture_write_bytes = metrics
                .texture_write_bytes
                .checked_add(source_bytes)
                .ok_or(Error::Capacity)?;
            uploads.push(NativeGlyphCpuUpload {
                raster_index: index,
                atlas_origin: [raster.atlas_x, raster.atlas_y],
                size: [raster.width, raster.height],
                offset: raster.output_offset,
                bytes_per_row: raster.output_bytes_per_row,
                source_bytes,
            });
        }
        let mut bytes = allocate(size)?;
        bytes.resize(size, 0);
        scan::rasterize(&page, policy, &mut bytes)?;
        Ok(Self {
            page,
            policy,
            bytes: bytes.into(),
            uploads: uploads.into(),
            metrics,
        })
    }
    pub fn page(&self) -> &Arc<NativeGlyphRasterPage> {
        &self.page
    }
    pub fn policy(&self) -> NativeGlyphCpuPolicy {
        self.policy
    }
    pub fn implementation(&self) -> NativeGlyphCpuImplementation {
        self.policy.implementation()
    }
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }
    pub fn uploads(&self) -> &[NativeGlyphCpuUpload] {
        &self.uploads
    }
    pub fn metrics(&self) -> NativeGlyphCpuMetrics {
        self.metrics
    }
}
fn minimum(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn coverage(samples: u32) -> u8 {
    ((samples * 255 + 32) / 64) as u8
}
