//! Original static retained-glyph interception, independent of atlas requests.
use super::*;

pub(crate) struct RetainedGlyphPaint<'a> {
    pub font: &'a ManagedFont,
    pub glyph: u16,
    pub position: [f32; 2],
    pub font_size: f32,
    pub italic_skew: f32,
    pub scale_x: f32,
    pub coverage_gamma: f32,
    pub color: [f32; 4],
    pub transform: Matrix4,
    pub pass_count: usize,
    pub bold_offset: f32,
    pub sample_grid: u32,
}
pub(crate) trait RetainedGlyphSink {
    /// False continues the unchanged original vector/bitmap/atlas selection.
    fn try_glyph(&mut self, paint: RetainedGlyphPaint<'_>) -> Result<bool>;
}
