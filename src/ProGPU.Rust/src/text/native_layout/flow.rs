//! Original native exclusion bands, anchors and floating paragraph flow. Double
//! row prefixes and actual fragment frames stay separate from source-width policy.
use super::*;
mod band;
mod intervals;
mod metrics;
mod writer;
pub use band::{native_fit_exclusion_band_into, native_layout_exclusion_band_into};
pub use intervals::{
    native_place_text_anchor, native_place_text_floater, native_resolve_anchor_width,
    native_resolve_text_intervals_into,
};
pub use metrics::native_measure_fragment_lines;
pub use writer::{native_layout_excluded_into, native_layout_floating_into};
const LIMIT: usize = 1 << 20;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeExclusionRectangle {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeLineInterval {
    pub left: f32,
    pub right: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeLineFragment {
    pub glyph_start: u32,
    pub glyph_count: u32,
    pub left: f32,
    pub width: f32,
    pub content_width: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeAnchorAlignment {
    #[default]
    Left,
    Center,
    Right,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeAnchorWidthMode {
    #[default]
    Fixed,
    Fill,
    FitContent,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeAnchorWidth {
    pub content_width: f32,
    pub outer_width: f32,
    pub requires_remeasure: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeExclusionBandStatus {
    Placed,
    RefitHeight,
    #[default]
    Blocked,
    Complete,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeExclusionBandResult {
    pub status: NativeExclusionBandStatus,
    pub next_glyph: u32,
    pub glyph_count: u32,
    pub fragment_count: u32,
    pub top: f32,
    pub baseline: f32,
    pub height: f32,
    pub next_y: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeFragmentPlacement {
    pub row_index: u32,
    pub left: f32,
    pub top: f64,
    pub width: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeExclusionFlowResult {
    pub glyph_count: u32,
    pub fragment_count: u32,
    pub row_count: u32,
    pub next_glyph: u32,
    pub attempts: u32,
    pub height: f64,
    pub content_width: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeFloatingItem {
    pub glyph_index: u32,
    pub width: f32,
    pub height: f32,
    pub alignment: NativeAnchorAlignment,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeFloatingPlacement {
    pub source_row: u32,
    pub bounds: NativeExclusionRectangle,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeFloatingFlowResult {
    pub text: NativeExclusionFlowResult,
    pub float_count: u32,
    pub height: f64,
    pub content_width: f32,
}
/// Original caller-owned E / E+1 / G scratch. Safe mutable borrows establish
/// write disjointness; source arrays remain independent immutable inputs.
pub struct NativeExclusionScratch<'a> {
    pub obstacles: &'a mut [NativeLineInterval],
    pub intervals: &'a mut [NativeLineInterval],
    pub fragments: &'a mut [NativeLineFragment],
    pub layout: NativeLayoutScratch<'a>,
}
pub struct NativeFragmentOutput<'a> {
    pub glyphs: &'a mut [NativePositionedTextGlyph],
    pub lines: &'a mut [NativePositionedTextLine],
    pub placements: &'a mut [NativeFragmentPlacement],
}
fn min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn valid_rectangle(r: NativeExclusionRectangle) -> bool {
    [r.left, r.top, r.right, r.bottom]
        .into_iter()
        .all(f32::is_finite)
        && r.left <= r.right
        && r.top <= r.bottom
}
