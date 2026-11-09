//! Original managed paragraph writer and immutable retained interaction.
//! Ordered font runs, shaping dependencies, sequential pen accumulation and
//! bidi cluster reordering retain the source algorithms. Platform font discovery
//! is an explicit resolver boundary, independent of this portable layout writer.
use super::{
    managed_pipeline::{ManagedShapingError, ManagedShapingOptions},
    preparation::ManagedPreparationOptions,
    script_actions::ManagedShapingDirection as Direction,
    unicode::{UnicodeError, bidi::ManagedBidiParagraph, grapheme::managed_next_grapheme_length},
};
use crate::font::{FontError, FontVerticalDeviceMetrics, ManagedFont};
use std::sync::Arc;
mod edit;
mod interaction;
mod shaping;
pub use edit::TextEditInteractionSnapshot;
pub use interaction::{
    TextBounds, TextCaretStop, TextClusterHitTestResult, TextHitTestResult, TextInteractionSnapshot,
};

/// Layout publication fails atomically; failed candidates retain no public glyphs.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum TextLayoutError {
    /// Font metadata or actual fallback resolver failure.
    #[error(transparent)]
    Font(#[from] FontError),
    /// Original shaping failure.
    #[error(transparent)]
    Shaping(#[from] ManagedShapingError),
    /// Bidi or source encoding failure.
    #[error(transparent)]
    Unicode(#[from] UnicodeError),
    /// An explicit source frame or interaction argument does not match.
    #[error("invalid text layout argument")]
    InvalidArgument,
    /// The generation did not retain the requested interaction ownership.
    #[error("text generation lacks the requested retained ownership")]
    UnsupportedInteraction,
    /// Candidate output does not fit representable/allocatable storage.
    #[error("text layout capacity exceeded")]
    Capacity,
}
/// Original paragraph alignment. Basic TextLayout keeps Justify's original
/// left placement; the independent styled writer supplies justification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum TextAlignment {
    /// Original left placement.
    #[default]
    Left,
    /// Center inside the finite maximum width.
    Center,
    /// Right edge inside the finite maximum width.
    Right,
    /// Original basic-writer behavior; styled justification is separate.
    Justify,
}
/// Exact immutable font-owned horizontal source line frame.
#[derive(Clone, Debug)]
pub struct SourceDeviceLineMetrics {
    font: ManagedFont,
    metrics: FontVerticalDeviceMetrics,
}
impl SourceDeviceLineMetrics {
    /// Capture the original exact static VDMX record, without nearest-size or
    /// caller-authored extrema substitution. Missing coverage stays separate.
    pub fn capture(font: ManagedFont, pixels_per_em: u16) -> Result<Option<Self>, FontError> {
        Ok(font
            .vertical_device_metrics(pixels_per_em)?
            .filter(|m| m.height() > 0)
            .map(|metrics| Self { font, metrics }))
    }
    /// Actual selected immutable font generation.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
    /// Exact source record.
    pub fn metrics(&self) -> FontVerticalDeviceMetrics {
        self.metrics
    }
    fn validate(
        &self,
        font: &ManagedFont,
        size: f32,
        direction: Option<Direction>,
    ) -> Result<(), TextLayoutError> {
        if !self.font.same_instance(font)
            || size != f32::from(self.metrics.pixels_per_em)
            || matches!(
                direction,
                Some(Direction::TopToBottom | Direction::BottomToTop)
            )
        {
            return Err(TextLayoutError::InvalidArgument);
        }
        Ok(())
    }
}
/// Immutable formatting independent of OpenType feature selection.
#[derive(Clone, Debug)]
pub struct TextLayoutFormattingOptions {
    /// Resolve missing non-space/tab glyphs through the explicit resolver.
    pub enable_font_fallback: bool,
    /// Include trailing space/tab advances in measured width.
    pub measure_trailing_whitespace: bool,
    /// Capture original source graphemes before fallback partitions text.
    pub retain_original_grapheme_ownership: bool,
    /// Optional exact font-owned VDMX line frame.
    pub source_device_line_metrics: Option<SourceDeviceLineMetrics>,
    /// Original tab sequence offset.
    pub first_tab_offset: f32,
    /// Original tab intervals, with invalid/nonpositive intervals skipped.
    pub tab_stops: Arc<[f32]>,
}
impl Default for TextLayoutFormattingOptions {
    fn default() -> Self {
        Self {
            enable_font_fallback: true,
            measure_trailing_whitespace: true,
            retain_original_grapheme_ownership: false,
            source_device_line_metrics: None,
            first_tab_offset: 0.0,
            tab_stops: Arc::from([]),
        }
    }
}
/// Complete original basic layout request, excluding selected font and source.
#[derive(Clone, Debug)]
pub struct ManagedTextLayoutOptions {
    /// Original maximum width; infinity means unconstrained.
    pub max_width: f32,
    /// Original paragraph alignment.
    pub alignment: TextAlignment,
    /// Original font-dependent shaping feature/script/direction policy.
    pub shaping: ManagedShapingOptions,
    /// Original line measurement and fallback policy.
    pub formatting: TextLayoutFormattingOptions,
}
impl Default for ManagedTextLayoutOptions {
    fn default() -> Self {
        Self {
            max_width: f32::INFINITY,
            alignment: TextAlignment::Left,
            shaping: ManagedShapingOptions::default(),
            formatting: TextLayoutFormattingOptions::default(),
        }
    }
}
/// Typed host fallback boundary. Implementations return the actual immutable
/// font selected for this source scalar; no family-name or guessed-face alias.
/// System discovery and glyph-resident file loading are separate providers.
pub trait ManagedFontFallback {
    /// Resolve a missing scalar, or report genuine absence with `None`.
    fn resolve(
        &self,
        requested: &ManagedFont,
        code_point: u32,
    ) -> Result<Option<ManagedFont>, FontError>;
}
/// An explicitly supplied ordered font list can serve portable/browser hosts.
/// This does not stand in for the original platform discovery provider.
impl ManagedFontFallback for [ManagedFont] {
    fn resolve(
        &self,
        _requested: &ManagedFont,
        code_point: u32,
    ) -> Result<Option<ManagedFont>, FontError> {
        for font in self {
            if font.glyph_index(code_point)? != 0 {
                return Ok(Some(font.clone()));
            }
        }
        Ok(None)
    }
}
/// Actual shaped glyph with original source placement and selected font owner.
#[derive(Clone, Debug)]
pub struct TextRunGlyph {
    /// Raw original UTF-16 unit at the clamped cluster.
    pub character: u16,
    /// Actual shaped source code point.
    pub code_point: u32,
    /// Actual selected font glyph.
    pub glyph_index: u16,
    /// Original screen-space placement, including shaping offsets.
    pub position: [f32; 2],
    /// Original layout box width (not ink or atlas extent).
    pub width: u32,
    /// Original layout box height.
    pub height: u32,
    /// Original horizontal or vertical pen advance.
    pub advance: f32,
    /// Actual immutable font owning this glyph, retained beyond the resolver.
    pub font: ManagedFont,
    /// Original signed UTF-16 cluster.
    pub cluster: i32,
    /// Original source embedding level.
    pub bidi_level: i8,
    /// Complete shaping dependency flags.
    pub shaping_flags: u32,
}
#[derive(Clone, Copy, Debug)]
struct LineRange {
    start: usize,
    count: usize,
    left: f32,
    top: f32,
    height: f32,
    source_start: i32,
    source_end: i32,
    paragraph_level: i8,
}
/// Completed immutable basic paragraph generation. Private glyph membership
/// prevents mutation from invalidating retained row/cluster ownership.
#[derive(Clone, Debug)]
pub struct ManagedTextLayout {
    text: Arc<[u16]>,
    font: ManagedFont,
    font_size: f32,
    options: ManagedTextLayoutOptions,
    preparation: ManagedPreparationOptions,
    glyphs: Vec<TextRunGlyph>,
    lines: Vec<LineRange>,
    content_size: [f32; 2],
    measured_size: [f32; 2],
    original_grapheme_boundaries: Option<Arc<[i32]>>,
}
impl ManagedTextLayout {
    /// Shape and place the complete candidate before returning a generation.
    /// Unicode profiles and fallback resolution are explicit host inputs.
    pub fn new<F: ManagedFontFallback + ?Sized>(
        text: impl Into<Arc<[u16]>>,
        font: ManagedFont,
        font_size: f32,
        options: ManagedTextLayoutOptions,
        preparation: ManagedPreparationOptions,
        fallback: &F,
    ) -> Result<Self, TextLayoutError> {
        if let Some(metrics) = &options.formatting.source_device_line_metrics {
            metrics.validate(&font, font_size, options.shaping.direction)?;
        }
        let text = text.into();
        if text.len() > i32::MAX as usize {
            return Err(TextLayoutError::Capacity);
        }
        let original_grapheme_boundaries = if options.formatting.retain_original_grapheme_ownership
        {
            capture_graphemes(&text, preparation.graphemes)?
        } else {
            None
        };
        let mut layout = Self {
            text,
            font,
            font_size,
            options,
            preparation,
            glyphs: Vec::new(),
            lines: Vec::new(),
            content_size: [0.0; 2],
            measured_size: [0.0; 2],
            original_grapheme_boundaries,
        };
        layout.generate(fallback)?;
        Ok(layout)
    }
    /// Original source, including malformed UTF-16 units.
    pub fn text(&self) -> &[u16] {
        &self.text
    }
    /// Selected primary font.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
    /// Original font size.
    pub fn font_size(&self) -> f32 {
        self.font_size
    }
    /// Frozen request.
    pub fn options(&self) -> &ManagedTextLayoutOptions {
        &self.options
    }
    /// Complete immutable glyph sequence in physical writer order.
    pub fn glyphs(&self) -> &[TextRunGlyph] {
        &self.glyphs
    }
    /// Original maximum measured line width and accumulated height.
    pub fn content_size(&self) -> [f32; 2] {
        self.content_size
    }
    /// Original constrained width and accumulated height.
    pub fn measured_size(&self) -> [f32; 2] {
        self.measured_size
    }
    /// Build another complete generation; failure leaves this one unchanged.
    pub fn reflow<F: ManagedFontFallback + ?Sized>(
        &self,
        max_width: f32,
        alignment: TextAlignment,
        fallback: &F,
    ) -> Result<Self, TextLayoutError> {
        let mut options = self.options.clone();
        options.max_width = max_width;
        options.alignment = alignment;
        Self::new(
            self.text.clone(),
            self.font.clone(),
            self.font_size,
            options,
            self.preparation.clone(),
            fallback,
        )
    }
    fn vertical(&self) -> bool {
        matches!(
            self.options.shaping.direction,
            Some(Direction::TopToBottom | Direction::BottomToTop)
        )
    }
    fn line_metrics(&self) -> (f32, f32) {
        if let Some(source) = &self.options.formatting.source_device_line_metrics {
            return (
                source.metrics.height() as f32,
                f32::from(source.metrics.maximum_y),
            );
        }
        let m = self.font.metadata();
        let scale = self.font_size / f32::from(m.units_per_em);
        (
            (i32::from(m.ascender) - i32::from(m.descender) + i32::from(m.line_gap)) as f32 * scale,
            f32::from(m.ascender) * scale,
        )
    }
    fn alignment_shift(&self, width: f32) -> f32 {
        match self.options.alignment {
            TextAlignment::Center => (self.options.max_width - width) * 0.5,
            TextAlignment::Right => self.options.max_width - width,
            _ => 0.0,
        }
    }
}
fn reserve<T>(values: &mut Vec<T>, extra: usize) -> Result<(), TextLayoutError> {
    if values
        .len()
        .checked_add(extra)
        .is_none_or(|count| count > i32::MAX as usize)
    {
        return Err(TextLayoutError::Capacity);
    }
    values
        .try_reserve(extra)
        .map_err(|_| TextLayoutError::Capacity)
}
// Original Math.Min/Max propagate NaNs and retain IEEE signed-zero ordering.
fn max(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() & b.to_bits())
    } else {
        a.max(b)
    }
}
fn min(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() | b.to_bits())
    } else {
        a.min(b)
    }
}
fn capture_graphemes(
    text: &[u16],
    profile: super::unicode::grapheme::ManagedGraphemeProfile,
) -> Result<Option<Arc<[i32]>>, TextLayoutError> {
    if char::decode_utf16(text.iter().copied()).any(|c| c.is_err()) {
        return Ok(None);
    }
    let mut boundaries = Vec::new();
    reserve(&mut boundaries, 1)?;
    boundaries.push(0);
    let mut at = 0;
    while at < text.len() {
        at += managed_next_grapheme_length(&text[at..], profile);
        reserve(&mut boundaries, 1)?;
        boundaries.push(at as i32);
    }
    Ok(Some(boundaries.into()))
}
