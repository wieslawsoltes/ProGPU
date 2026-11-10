//! Original native hinted-face lifecycle, exact cache and captured-outline writer.
//! Actual pinned dependency execution is supplied through an explicit safe backend;
//! this module neither invents an interpreter nor substitutes unhinted geometry.
use super::native_pipeline::{
    NativeDeviceRun, NativeHintPolicy, NativeHintedBatch, NativeHintedCaptureError,
    NativeHintedGlyph, NativeHintedMetrics, NativeHintedProjection, NativeHintedProvider,
    NativeHintedRequest, NativeOutlineLoadPolicy,
};
use crate::font::SfntFace;
use std::sync::Arc;
mod cache;
mod geometry;
mod outline;
mod producer;
mod transport;
pub use cache::*;
pub use geometry::*;
pub use outline::*;
pub use producer::*;
pub use transport::*;

fn copy<T: Copy>(input: &[T]) -> Result<Vec<T>, NativeHintedCaptureError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(input.len())
        .map_err(|_| NativeHintedCaptureError::ResourceExhausted)?;
    output.extend_from_slice(input);
    Ok(output)
}
fn copy_request(
    request: &NativeHintedRequest,
) -> Result<NativeHintedRequest, NativeHintedCaptureError> {
    Ok(NativeHintedRequest {
        pixels_per_em_26_6: request.pixels_per_em_26_6,
        hint_policy: request.hint_policy,
        phase_26_6: request.phase_26_6,
        variation_coordinates_16_16: copy(&request.variation_coordinates_16_16)?,
        outline_policy: request.outline_policy,
    })
}
fn same_source(left: &SfntFace, right: &SfntFace) -> bool {
    left.data().as_ptr() == right.data().as_ptr()
        && left.data().len() == right.data().len()
        && left.face_index() == right.face_index()
}
