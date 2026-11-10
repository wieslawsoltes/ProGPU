//! Original native horizontal fitting and measured logical-to-visual writing.
//! Source glyph units, break classification and resolved line metrics are inputs;
//! this module neither shapes nor substitutes the managed paragraph executor.
use super::{
    ShapingGlyph,
    unicode::{UnicodeScalar, line_break::LineBreakKind},
};
use crate::font::{FontError, layout::GposDirection};
mod scan;
mod visual;
mod writer;
pub use scan::{
    NativeTextIntrinsicWidths, is_native_trailing_space, native_classify_justification_into,
    native_layout_requirements, native_measure_intrinsic_widths, native_soft_wrap_space_start,
};
pub use visual::{
    native_reorder_visual_into, native_visual_indices_into, native_visual_order_requirements,
};
pub use writer::{
    NativeLayoutRetained, NativeLayoutScratch, NativeMeasuredInput, NativeMeasuredOutput,
    native_layout_measured_into,
};

/// Original independent ellipsis policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeTextTrimming {
    /// Preserve all fitted source glyphs.
    #[default]
    None,
    /// Retain the last complete safe shaping boundary before the sign.
    CharacterEllipsis,
    /// Prefer the last complete legal word boundary before the sign.
    WordEllipsis,
}
/// Original horizontal line alignment.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeTextAlignment {
    /// Original left content frame.
    #[default]
    Left,
    /// Half the remaining measure.
    Center,
    /// The remaining measure.
    Right,
    /// Distribute over admitted interior word-space boundaries.
    Justify,
}
/// Complete native raw horizontal fitting policy. Zero maximum width is unbounded.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeTextLayoutOptions {
    /// Glyph design/device-unit to logical-unit conversion.
    pub scale: f32,
    /// Original logical measure; zero is unbounded.
    pub maximum_width: f32,
    /// Original minimum line height.
    pub line_height: f32,
    /// Zero admits every fitted line.
    pub maximum_lines: u32,
    /// Original horizontal direction; vertical directions reject.
    pub direction: GposDirection,
    /// Original optional synthetic sign policy.
    pub trimming: NativeTextTrimming,
    /// Original content alignment.
    pub alignment: NativeTextAlignment,
    /// Original selected sign glyph ID.
    pub ellipsis_glyph_id: u32,
    /// Original sign advance before scale.
    pub ellipsis_advance: f32,
    /// Final-line collapse measure; -1 retains legacy policy, zero is real.
    pub collapse_width: f32,
}
impl Default for NativeTextLayoutOptions {
    fn default() -> Self {
        Self {
            scale: 1.0,
            maximum_width: 0.0,
            line_height: 0.0,
            maximum_lines: 0,
            direction: GposDirection::LeftToRight,
            trimming: NativeTextTrimming::None,
            alignment: NativeTextAlignment::Left,
            ellipsis_glyph_id: 0,
            ellipsis_advance: 0.0,
            collapse_width: -1.0,
        }
    }
}
/// Original paragraph tab grid and emergency-break policy.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeTextTabs {
    /// Zero disables tab reinterpretation.
    pub interval: f32,
    /// Original line start relative to the paragraph tab grid.
    pub origin: f32,
    /// Permit complete safe clusters when no word boundary fits.
    pub allow_emergency_break: bool,
}
impl Default for NativeTextTabs {
    fn default() -> Self {
        Self {
            interval: 0.0,
            origin: 0.0,
            allow_emergency_break: true,
        }
    }
}
/// Caller-resolved logical distances from the baseline; never driver line metrics.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextItemMetrics {
    /// Nonnegative original ascent.
    pub ascent: f32,
    /// Nonnegative original descent.
    pub descent: f32,
}
/// Original logical cluster justification classification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeTextJustification {
    /// Actual content; bounds the expandable interior.
    #[default]
    Content,
    /// Other whitespace, retained but not expanded.
    Whitespace,
    /// Complete U+0020 source cluster.
    WordSpace,
}
/// Original source-indexed positioned glyph, in logical Y-down coordinates.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativePositionedTextGlyph {
    /// Exact source glyph index; u32::MAX only for an explicit synthetic sign.
    pub glyph_index: u32,
    /// Actual source glyph ID.
    pub glyph_id: u32,
    /// Original source cluster.
    pub cluster: i32,
    /// Logical position X/Y.
    pub position: [f32; 2],
    /// Original scaled horizontal/vertical advance.
    pub advance: [f32; 2],
}
/// Original horizontal fitted line, including its independent clipped flag.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativePositionedTextLine {
    /// First visual output glyph.
    pub glyph_start: u32,
    /// Complete visual output count.
    pub glyph_count: u32,
    /// Original source cluster start.
    pub input_start: i32,
    /// Original writer end-cluster convention.
    pub input_end: i32,
    /// Original content width, including retained trailing advances.
    pub width: f32,
    /// Original logical baseline.
    pub baseline_y: f32,
    /// Original minimum/resolved logical height.
    pub height: f32,
    /// Original wrapping/trimming/maximum-lines clipping flag.
    pub clipped: bool,
    /// Bit zero is the original right-to-left justified frame.
    pub flags: u8,
}
/// Original output/scratch requirements; complete source glyph capacity is retained.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeTextLayoutRequirements {
    /// Required source-sized glyph output/scratch capacity.
    pub glyph_capacity: usize,
    /// Required fitted line capacity.
    pub line_capacity: usize,
}
/// Exact original UAX #9 L1/L2 cluster group, preserving internal shaper order.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVisualClusterGroup {
    /// First logical glyph in this cluster.
    pub glyph_start: u32,
    /// Complete cluster glyph count.
    pub glyph_count: u32,
    /// Resolved line level after the original L1 policy.
    pub bidi_level: i8,
}
/// Original visual-order output and group capacities.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVisualOrderRequirements {
    /// Full logical glyph count.
    pub glyph_capacity: usize,
    /// Original number of contiguous source clusters.
    pub group_capacity: usize,
}
/// Original retained line frame uses the writer's double accumulated top.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextLineFrame {
    /// Original accumulated top, independent of rounded baseline/height.
    pub top: f64,
    /// Original ascent selected from the actual line item envelope.
    pub ascent: f32,
    /// True when caller-resolved measured item metrics determine this frame.
    pub measured: bool,
}
/// Original content/measured-size publication.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextLayoutMetrics {
    /// Maximum original line width.
    pub content_width: f32,
    /// Original maximum line bottom.
    pub content_height: f32,
    /// Explicit positive measure or original content width.
    pub measured_width: f32,
    /// Original content height.
    pub measured_height: f32,
}
/// Measure the original line outputs; measured rows accumulate height in f64.
pub fn native_measure_text_lines(
    lines: &[NativePositionedTextLine],
    maximum_width: f32,
    measured: bool,
) -> Result<NativeTextLayoutMetrics, FontError> {
    if !maximum_width.is_finite() || maximum_width < 0.0 {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeTextLayoutMetrics::default();
    let mut top = 0.0f64;
    for line in lines {
        let bottom = if measured {
            (top + f64::from(line.height)) as f32
        } else {
            line.baseline_y + line.height
        };
        if !line.width.is_finite()
            || line.width < 0.0
            || !line.baseline_y.is_finite()
            || !line.height.is_finite()
            || line.height < 0.0
            || !bottom.is_finite()
        {
            return Err(FontError::InvalidArgument);
        }
        result.content_width = max(result.content_width, line.width);
        result.content_height = max(result.content_height, bottom);
        top += f64::from(line.height);
    }
    result.measured_width = if maximum_width > 0.0 {
        maximum_width
    } else {
        result.content_width
    };
    result.measured_height = result.content_height;
    Ok(result)
}
fn max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn validate_options(o: NativeTextLayoutOptions) -> Result<(), FontError> {
    if !o.scale.is_finite()
        || o.scale <= 0.0
        || !o.maximum_width.is_finite()
        || o.maximum_width < 0.0
        || !o.line_height.is_finite()
        || o.line_height < 0.0
        || !matches!(
            o.direction,
            GposDirection::LeftToRight | GposDirection::RightToLeft
        )
        || !o.ellipsis_advance.is_finite()
        || o.ellipsis_advance < 0.0
        || !(o.ellipsis_advance * o.scale).is_finite()
        || !o.collapse_width.is_finite()
        || (o.collapse_width != -1.0 && o.collapse_width < 0.0)
        || (o.collapse_width >= 0.0
            && (o.maximum_lines == 0 || o.trimming == NativeTextTrimming::None))
    {
        return Err(FontError::InvalidArgument);
    }
    Ok(())
}
fn scale_metrics(glyph: ShapingGlyph, scale: f32) -> [f32; 4] {
    (wide::i32x4::from([
        glyph.advance_x,
        glyph.advance_y,
        glyph.offset_x,
        glyph.offset_y,
    ])
    .round_float()
        * wide::f32x4::splat(scale))
    .to_array()
}
fn scale_at(scales: &[f32], index: usize, options: NativeTextLayoutOptions) -> f32 {
    if scales.is_empty() {
        options.scale
    } else {
        scales[index]
    }
}
fn safe_break(glyphs: &[ShapingGlyph], index: usize) -> bool {
    index == 0
        || index >= glyphs.len()
        || (glyphs[index - 1].cluster != glyphs[index].cluster && glyphs[index].flags & 1 == 0)
}
pub(crate) fn alignment_shift(options: NativeTextLayoutOptions, width: f32) -> f32 {
    if options.maximum_width <= width {
        return 0.0;
    }
    match options.alignment {
        NativeTextAlignment::Center => (options.maximum_width - width) * 0.5,
        NativeTextAlignment::Right => options.maximum_width - width,
        _ => 0.0,
    }
}
