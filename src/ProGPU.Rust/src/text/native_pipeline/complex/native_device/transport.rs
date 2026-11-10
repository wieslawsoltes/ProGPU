//! Immutable original hinted-font transport. This module never executes a font.
use super::*;
use std::sync::Arc;

/// Original interpreter identity; neither value denotes a source raster policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeHintPolicy {
    /// Original TrueType interpreter 35.
    TrueType35 = 35,
    /// Original TrueType interpreter 40.
    #[default]
    TrueType40 = 40,
}
/// Exact independent outline-load target; all targets remain outline-only.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeOutlineLoadPolicy {
    /// Original normal outline loading.
    #[default]
    Normal = 0,
    /// Original monochrome scan metadata and outline loading.
    Monochrome = 1,
    /// Original horizontal LCD outline loading.
    HorizontalLcd = 2,
    /// Explicit original unhinted outline loading, retaining the selected request.
    Unhinted = 3,
}
/// Frozen original request. Raw axis coordinates remain in fvar order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeHintedRequest {
    /// Actual requested independent pixel-em sizes, in 26.6 units.
    pub pixels_per_em_26_6: [u32; 2],
    /// Selected native interpreter version.
    pub hint_policy: NativeHintPolicy,
    /// Actual independent physical phases, each in 0..64.
    pub phase_26_6: [u32; 2],
    /// Complete raw 16.16 user-coordinate inventory, including default axes.
    pub variation_coordinates_16_16: Vec<i32>,
    /// Original independent outline loading policy.
    pub outline_policy: NativeOutlineLoadPolicy,
}
impl NativeHintedRequest {
    pub(super) fn validate(&self) -> Result<(), FontError> {
        if self
            .pixels_per_em_26_6
            .iter()
            .any(|&v| v == 0 || v > i32::MAX as u32)
            || self.phase_26_6.iter().any(|&v| v >= 64)
        {
            return Err(FontError::InvalidArgument);
        }
        Ok(())
    }
    pub(super) fn try_copy(&self) -> Result<Self, FontError> {
        Ok(Self {
            pixels_per_em_26_6: self.pixels_per_em_26_6,
            hint_policy: self.hint_policy,
            phase_26_6: self.phase_26_6,
            variation_coordinates_16_16: copy_vec(&self.variation_coordinates_16_16)?,
            outline_policy: self.outline_policy,
        })
    }
}
/// Actual driver and design metrics captured from the same selected size/face.
/// Native `long` fields use i64 so LP64 input is rejected before narrowing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeHintedMetrics {
    /// Original selected head units-per-em.
    pub units_per_em: u16,
    /// Actual driver size metrics, independent of the raw requested 26.6 size.
    pub pixels_per_em: [u32; 2],
    /// Actual driver design-to-device scale, in 16.16.
    pub scale_16_16: [i64; 2],
    /// Actual driver ascender, descender, height and maximum advance, in 26.6.
    pub driver_line_metrics_26_6: [i64; 4],
    /// Original design ascender, descender, height and maximum advance.
    pub design_line_metrics: [i32; 4],
}
/// One actual captured descriptor, including repeated IDs and no-ink slots.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct NativeHintedGlyph {
    /// Exact original requested glyph ID.
    pub glyph_id: u32,
    /// Actual slot advance vector, including the original vertical component.
    pub advance_26_6: [i64; 2],
    /// Original horizontal X/Y bearings.
    pub horizontal_bearing_26_6: [i64; 2],
    /// Original metric width and height; no outline-envelope replacement.
    pub size_26_6: [i64; 2],
    /// Original horizontal metric advance.
    pub horizontal_advance_26_6: i64,
    /// Original vertical X/Y bearings.
    pub vertical_bearing_26_6: [i64; 2],
    /// Original vertical metric advance.
    pub vertical_advance_26_6: i64,
    /// Original linear horizontal and vertical advances in 16.16.
    pub linear_advances_16_16: [i64; 2],
    /// Original left and right side-bearing deltas.
    pub side_bearing_deltas_26_6: [i64; 2],
    /// Original outline precision/scan flags, unchanged.
    pub outline_flags: i32,
    /// Exact original captured coordinates, including the physical phase.
    pub points_26_6: Vec<[i64; 2]>,
    /// Exact original curve tags, one per point.
    pub tags: Vec<u8>,
    /// Exact original inclusive contour-end indices.
    pub contour_ends: Vec<i16>,
}
/// Immutable whole capture generation, owning its exact selected source bytes,
/// raw request, metrics and every original descriptor slot.
#[derive(Debug)]
pub struct NativeHintedBatch {
    font: SfntFace,
    request: NativeHintedRequest,
    metrics: NativeHintedMetrics,
    glyphs: Vec<NativeHintedGlyph>,
}
impl NativeHintedBatch {
    /// Move an actual producer's complete capture into immutable owned transport.
    /// This establishes transport structure only; the provider remains responsible
    /// for actual outline execution, dependency ownership and capture provenance.
    pub fn from_capture(
        font: SfntFace,
        request: NativeHintedRequest,
        metrics: NativeHintedMetrics,
        glyphs: Vec<NativeHintedGlyph>,
    ) -> Result<Self, FontError> {
        request.validate()?;
        if font.face_index() > u16::MAX as u32 {
            return Err(FontError::InvalidArgument);
        }
        let limit = u32::from(font.glyph_count().ok_or(FontError::InvalidFace)?);
        for g in &glyphs {
            if g.glyph_id >= limit
                || g.points_26_6.len() != g.tags.len()
                || g.points_26_6.len() > i16::MAX as usize
                || g.contour_ends.len() > i16::MAX as usize
            {
                return Err(FontError::InvalidArgument);
            }
            let mut previous = -1i16;
            for &end in &g.contour_ends {
                if end <= previous || end as usize >= g.points_26_6.len() {
                    return Err(FontError::InvalidArgument);
                }
                previous = end;
            }
            if g.points_26_6.is_empty() != g.contour_ends.is_empty()
                || !g.points_26_6.is_empty() && previous as usize + 1 != g.points_26_6.len()
            {
                return Err(FontError::InvalidArgument);
            }
        }
        Ok(Self {
            font,
            request,
            metrics,
            glyphs,
        })
    }
    /// Actual immutable selected font owner.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Exact raw request, independent of normalized shaping coordinates.
    pub fn request(&self) -> &NativeHintedRequest {
        &self.request
    }
    /// Actual original driver metrics from this capture generation.
    pub fn metrics(&self) -> NativeHintedMetrics {
        self.metrics
    }
    /// Complete source-order descriptors, including auxiliary spaces and no-ink.
    pub fn glyphs(&self) -> &[NativeHintedGlyph] {
        &self.glyphs
    }
    pub(super) fn matches(
        &self,
        font: &SfntFace,
        request: &NativeHintedRequest,
        ids: &[u32],
    ) -> bool {
        std::ptr::eq(self.font.data().as_ptr(), font.data().as_ptr())
            && self.font.data().len() == font.data().len()
            && self.font.face_index() == font.face_index()
            && &self.request == request
            && self.glyphs.len() == ids.len()
            && self.glyphs.iter().zip(ids).all(|(g, &id)| g.glyph_id == id)
    }
}
/// Original native capture failures, independent of shaping/positioning errors.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedCaptureError {
    /// Invalid request or glyph inventory.
    InvalidArgument,
    /// The actual source could not be opened.
    InvalidFont,
    /// The producer cannot execute this original outline format.
    UnsupportedFont,
    /// Actual loaded dependency ownership/version did not match the provider.
    DependencyMismatch,
    /// Original native glyph execution or outline capture failed.
    HintingFailed,
    /// Actual producer allocation failed.
    ResourceExhausted,
    /// No actual supported hinted producer is installed.
    DependencyUnavailable,
}
/// Required actual producer boundary. A capture is one exclusive, atomic whole
/// batch from the supplied owned face/request, in exact ID order. Implementations
/// must execute the original selected outline load target and retain raw points,
/// tags, precision/scan flags and no-ink descriptors. Unhinted reconstruction,
/// metric inference and glyph-ID deduplication do not satisfy this contract.
///
/// This crate supplies transport and shaping consumers, not the native dependency
/// loader/interpreter. Capturing successfully does not qualify source raster modes.
pub trait NativeHintedProvider {
    /// Capture one immutable actual generation. Failures publish no partial batch.
    fn capture(
        &mut self,
        font: &SfntFace,
        request: &NativeHintedRequest,
        glyph_ids: &[u32],
    ) -> Result<Arc<NativeHintedBatch>, NativeHintedCaptureError>;
}
/// Original design-vector projection selection. Forced GPU routes fail closed
/// because native positioning requires CPU-visible displacements before GPOS.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeHintedProjection {
    /// Original supported native SIMD route; no implicit scalar fallback.
    #[default]
    Automatic = 0,
    /// Retained unsupported CPU-visible native-compute request.
    NativeCompute = 1,
    /// Retained unsupported CPU-visible shader request.
    GpuShader = 2,
    /// Explicit original SIMD arithmetic path.
    IntrinsicSimd = 3,
    /// Explicit checked scalar arithmetic; not an independent dependency oracle.
    ScalarReference = 4,
}
/// Independent original projection admission failure.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedProjectionError {
    /// Complete output inventory is smaller than the input vector inventory.
    InsufficientCapacity,
    /// Source/request identity is not the exact retained generation.
    InvalidArgument,
    /// Exact frame, axis coordinates or signed32 result is unsupported.
    UnsupportedFrame,
    /// Forced CPU-visible projection policy has no supported implementation.
    UnsupportedPolicy,
}
/// Original error domains remain independent; no capture or projection failure
/// is converted into a successful unhinted/device approximation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeDeviceError {
    /// Original shaping result, including Capacity and VerificationFailed.
    pub shaping: FontError,
    /// Actual producer failure when capture was attempted.
    pub capture: Option<NativeHintedCaptureError>,
    /// Original projection/frame error when applicable.
    pub projection: Option<NativeHintedProjectionError>,
}
impl From<FontError> for NativeDeviceError {
    fn from(shaping: FontError) -> Self {
        Self {
            shaping,
            capture: None,
            projection: None,
        }
    }
}
impl std::fmt::Display for NativeDeviceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native device shaping failed: {:?}", self.shaping)?;
        if let Some(capture) = self.capture {
            write!(f, " (capture: {capture:?})")?;
        }
        if let Some(projection) = self.projection {
            write!(f, " (projection: {projection:?})")?;
        }
        Ok(())
    }
}
impl std::error::Error for NativeDeviceError {}
