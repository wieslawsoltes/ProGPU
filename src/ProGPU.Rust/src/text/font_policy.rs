//! Original source font policies, raw native metadata and explicit retained replay.
//! VDMX, managed gasp and horizontal source line metrics reuse their existing
//! original ports. Paired unfitted replay is an explicit independent policy;
//! ordinary font targets, source raster modes and layout remain unchanged.
use crate::font::{FontError, SfntFace, Tag};
use crate::path::Segment;
use crate::text::glyph::GpuGlyphRecord;
use crate::text::hinting::*;
use crate::text::native_pipeline::{
    NativeHintedBatch, NativeHintedCaptureError, NativeHintedProjection, NativeHintedProvider,
    NativeHintedRequest, NativeOutlineLoadPolicy,
};
use std::sync::Arc;
mod metadata;
mod replay;
mod scan;
pub use crate::font::{
    FontRasterizationRange, FontRasterizationRecommendations, FontVerticalDeviceMetrics,
    read_rasterization_recommendations, read_vertical_device_metrics,
};
pub use crate::text::layout::SourceDeviceLineMetrics;
pub use metadata::*;
pub use replay::*;
pub use scan::capture_unfitted_font_replay;

/// Independent source-policy errors; actual capture/geometry failures remain
/// typed and never select a substitute font target or fabricated generation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontPolicyError {
    /// Invalid explicit DPI, position, descriptor count or source request.
    InvalidArgument,
    /// Actual retained record/topology/owner inventories are incompatible.
    InvalidGeneration,
    /// Actual capacity allocation failed before output publication.
    ResourceExhausted,
    /// Original actual producer capture failure.
    Capture(NativeHintedCaptureError),
    /// Original owned geometry preparation failure.
    Geometry(NativeHintedGeometryError),
    /// Original fixed-width transport failure.
    Transport(NativeHintedTransportError),
}
impl std::fmt::Display for FontPolicyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "source font policy: {self:?}")
    }
}
impl std::error::Error for FontPolicyError {}
impl From<NativeHintedCaptureError> for FontPolicyError {
    fn from(error: NativeHintedCaptureError) -> Self {
        Self::Capture(error)
    }
}
impl From<NativeHintedGeometryError> for FontPolicyError {
    fn from(error: NativeHintedGeometryError) -> Self {
        Self::Geometry(error)
    }
}
impl From<NativeHintedTransportError> for FontPolicyError {
    fn from(error: NativeHintedTransportError) -> Self {
        Self::Transport(error)
    }
}
fn require(value: bool) -> Result<(), FontPolicyError> {
    if value {
        Ok(())
    } else {
        Err(FontPolicyError::InvalidGeneration)
    }
}
fn copy<T: Copy>(input: &[T]) -> Result<Vec<T>, FontPolicyError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(input.len())
        .map_err(|_| FontPolicyError::ResourceExhausted)?;
    output.extend_from_slice(input);
    Ok(output)
}
fn filled<T: Clone>(count: usize, value: T) -> Result<Vec<T>, FontPolicyError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| FontPolicyError::ResourceExhausted)?;
    output.resize(count, value);
    Ok(output)
}
