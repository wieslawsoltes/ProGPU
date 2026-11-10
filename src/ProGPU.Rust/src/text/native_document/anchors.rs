//! Original all-or-nothing ordered anchor sizing and collision placement batches.
use super::*;
#[derive(Clone, Copy, Debug)]
pub struct NativeDocumentAnchorWidthRequest {
    pub available_width: f32,
    pub horizontal_insets: f32,
    pub specified_width: f32,
    pub measured_width: f32,
    pub mode: NativeAnchorWidthMode,
    pub has_measurement: bool,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeDocumentAnchorRequest {
    pub reference: NativeExclusionRectangle,
    pub width: f32,
    pub height: f32,
    pub alignment: NativeAnchorAlignment,
    pub allow_delay: bool,
    pub maximum_attempts: u32,
}
/// Original two-pass allocation-free sizing. No later bad request can publish
/// an earlier result; actual child measurement remains the caller's policy.
pub fn native_document_anchor_widths_into(
    requests: &[NativeDocumentAnchorWidthRequest],
    output: &mut [NativeAnchorWidth],
) -> Result<()> {
    if requests.len() > LIMIT {
        return invalid();
    }
    if output.len() < requests.len() {
        return Err(NativeDocumentError::InsufficientCapacity);
    }
    for pass in 0..2 {
        for (i, r) in requests.iter().enumerate() {
            let value = native_resolve_anchor_width(
                r.available_width,
                r.horizontal_insets,
                r.mode,
                r.specified_width,
                r.has_measurement,
                r.measured_width,
            )
            .map_err(|_| NativeDocumentError::InvalidArgument)?;
            if pass != 0 {
                output[i] = value;
            }
        }
    }
    Ok(())
}
/// Original source-order anchor collision batch. Each accepted box joins the
/// complete exclusions of the next request. Failure never publishes a prefix.
pub fn native_document_place_anchors_into(
    requests: &[NativeDocumentAnchorRequest],
    exclusions: &[NativeExclusionRectangle],
    output: &mut [NativeExclusionRectangle],
) -> Result<()> {
    if exclusions.len() > LIMIT || requests.len() > LIMIT - exclusions.len() {
        return invalid();
    }
    if output.len() < requests.len() {
        return Err(NativeDocumentError::InsufficientCapacity);
    }
    let total = exclusions.len() + requests.len();
    let mut retained = filled(total, NativeExclusionRectangle::default())?;
    let mut scratch = filled(total, NativeLineInterval::default())?;
    let mut intervals = filled(total + 1, NativeLineInterval::default())?;
    retained[..exclusions.len()].copy_from_slice(exclusions);
    native_resolve_text_intervals_into(
        NativeExclusionRectangle {
            left: 0.0,
            top: 0.0,
            right: 1.0,
            bottom: 1.0,
        },
        exclusions,
        &mut scratch,
        &mut intervals,
    )
    .map_err(|_| NativeDocumentError::InvalidArgument)?;
    for (i, r) in requests.iter().enumerate() {
        let value = native_place_text_anchor(
            r.reference,
            r.width,
            r.height,
            r.alignment,
            r.allow_delay,
            &retained[..exclusions.len() + i],
            &mut scratch,
            &mut intervals,
            r.maximum_attempts,
        )
        .map_err(|e| {
            if e == crate::font::FontError::VerificationFailed {
                NativeDocumentError::Unsupported
            } else {
                NativeDocumentError::InvalidArgument
            }
        })?;
        retained[exclusions.len() + i] = value;
    }
    output[..requests.len()].copy_from_slice(&retained[exclusions.len()..]);
    Ok(())
}
