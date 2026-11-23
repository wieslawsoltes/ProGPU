//! Original owned StyledTextLayout: strict styled source, inline objects,
//! paragraph geometry, contextual shaping and ordered visual placement.
//! This writer retains its own source contracts, distinct from basic TextLayout.
use super::*;
mod input;
mod writer;

/// Original caller-authored font and layout state for one source range.
#[derive(Clone, Debug)]
pub struct StyledTextStyle {
    /// Actual immutable requested font.
    pub font: ManagedFont,
    /// Original positive font size.
    pub font_size: f32,
    /// Horizontal glyph-offset and advance scale.
    pub width_scale: f32,
    /// Additional advance-only tracking multiplier.
    pub tracking_factor: f32,
    /// Original baseline shift, incorporated separately into line metrics.
    pub baseline_shift: f32,
    /// Default alignment when no exact paragraph record exists.
    pub alignment: TextAlignment,
    /// Uninterpreted caller tag.
    pub tag: i32,
}
impl StyledTextStyle {
    /// Original defaults; complete admission occurs before layout publication.
    pub fn new(font: ManagedFont, font_size: f32) -> Self {
        Self {
            font,
            font_size,
            width_scale: 1.0,
            tracking_factor: 1.0,
            baseline_shift: 0.0,
            alignment: TextAlignment::Left,
            tag: 0,
        }
    }
}
/// Original half-open UTF-16 style range; ranges form one complete partition.
#[derive(Clone, Debug)]
pub struct StyledTextRange {
    /// Original UTF-16 start.
    pub start: i32,
    /// Original positive length, except the empty source's default range.
    pub length: i32,
    /// Actual selected caller style.
    pub style: StyledTextStyle,
}
/// One original inline object occupying exactly one U+FFFC source unit.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct StyledTextInlineBox {
    /// Original UTF-16 U+FFFC position.
    pub text_position: i32,
    /// Nonnegative width.
    pub width: f32,
    /// Nonnegative distance above baseline.
    pub ascent: f32,
    /// Nonnegative distance below baseline.
    pub descent: f32,
    /// Uninterpreted caller tag.
    pub tag: i32,
}
/// Original positioned styled glyph, retaining the selected fallback font.
#[derive(Clone, Debug)]
pub struct StyledTextGlyph {
    /// Actual selected glyph slot.
    pub glyph_index: u16,
    /// Original screen-space glyph placement.
    pub position: [f32; 2],
    /// Actual advance used for this physical line.
    pub advance: f32,
    /// Actual font owning the glyph.
    pub font: ManagedFont,
    /// Index in the retained original style-range inventory.
    pub style_index: usize,
    /// Original UTF-16 cluster.
    pub cluster: i32,
    /// Original resolved source level.
    pub bidi_level: i8,
    /// Actual shaped code point.
    pub code_point: u32,
    /// Original dependency flags.
    pub shaping_flags: u32,
}
/// Original positioned inline box, independent of glyph output slots.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct StyledTextPositionedBox {
    /// Actual top-left placement.
    pub position: [f32; 2],
    /// Original object width.
    pub width: f32,
    /// Original ascent plus descent.
    pub height: f32,
    /// Baseline distance within the box (original ascent).
    pub baseline: f32,
    /// Original U+FFFC source position.
    pub text_position: i32,
    /// Original style-range slot.
    pub style_index: usize,
    /// Original object tag.
    pub tag: i32,
}
/// Original physical line with independent glyph and box ranges.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct StyledTextLine {
    /// First emitted glyph.
    pub glyph_offset: usize,
    /// Emitted glyph count; tabs are not glyph output.
    pub glyph_count: usize,
    /// First emitted inline object.
    pub box_offset: usize,
    /// Emitted inline-object count.
    pub box_count: usize,
    /// Original line top before paragraph space-before.
    pub top: f32,
    /// Actual shared baseline.
    pub baseline: f32,
    /// Original recorded width, including indent/alignment/justification policy.
    pub width: f32,
    /// Original line height including applicable paragraph spacing.
    pub height: f32,
    /// Final physical line of this source paragraph.
    pub is_paragraph_final: bool,
    /// Original LF-delimited paragraph start.
    pub paragraph_start: i32,
}
/// Original tab-field alignment.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum StyledTextTabAlignment {
    /// Align field start.
    #[default]
    Left = 0,
    /// Align field midpoint.
    Center = 1,
    /// Align field end.
    Right = 2,
    /// Align first period, comma or space, or field end if absent.
    Decimal = 3,
}
/// An original absolute tab position measured from the layout's left edge.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct StyledTextTabStop {
    /// Finite positive position, strictly increasing within a paragraph record.
    pub position: f32,
    /// Original field-alignment mode.
    pub alignment: StyledTextTabAlignment,
}
/// Original paragraph geometry, selected only by its exact source start.
#[derive(Clone, Debug)]
pub struct StyledTextParagraphStyle {
    /// Zero or the original position immediately following an LF.
    pub text_start: i32,
    /// First-line indent relative to left indent; may be negative.
    pub first_line_indent: f32,
    /// Nonnegative indent from layout's left edge.
    pub left_indent: f32,
    /// Nonnegative indent from the finite right constraint.
    pub right_indent: f32,
    /// Original line alignment/justification.
    pub alignment: TextAlignment,
    /// Immutable strictly increasing absolute tab positions.
    pub tab_stops: Arc<[StyledTextTabStop]>,
    /// Positive continuing interval beyond custom stops.
    pub default_tab_interval: f32,
    /// Nonnegative space before the first line.
    pub space_before: f32,
    /// Nonnegative space after the final line.
    pub space_after: f32,
    /// Nonnegative line spacing constraint.
    pub minimum_line_spacing: f32,
    /// Use the positive spacing constraint exactly rather than as a minimum.
    pub exact_line_spacing: bool,
}
impl StyledTextParagraphStyle {
    /// Construct an unindented paragraph with explicit default tab interval.
    pub fn new(text_start: i32, default_tab_interval: f32) -> Self {
        Self {
            text_start,
            first_line_indent: 0.0,
            left_indent: 0.0,
            right_indent: 0.0,
            alignment: TextAlignment::Left,
            tab_stops: Arc::from([]),
            default_tab_interval,
            space_before: 0.0,
            space_after: 0.0,
            minimum_line_spacing: 0.0,
            exact_line_spacing: false,
        }
    }
}
/// Original complete styled-layout request.
#[derive(Clone, Debug)]
pub struct StyledTextLayoutOptions {
    /// Positive finite width or positive infinity.
    pub max_width: f32,
    /// Default nonnegative line-spacing constraint.
    pub minimum_line_spacing: f32,
    /// Default exact-spacing policy.
    pub exact_line_spacing: bool,
    /// Paragraph bidi direction, independent of per-run shaping direction.
    pub base_direction: Option<Direction>,
    /// Original script, language and OpenType feature selection.
    pub shaping: ManagedShapingOptions,
    /// Immutable ordered exact paragraph-start records.
    pub paragraph_styles: Arc<[StyledTextParagraphStyle]>,
}
impl Default for StyledTextLayoutOptions {
    fn default() -> Self {
        Self {
            max_width: f32::INFINITY,
            minimum_line_spacing: 0.0,
            exact_line_spacing: false,
            base_direction: None,
            shaping: ManagedShapingOptions::default(),
            paragraph_styles: Arc::from([]),
        }
    }
}
/// Completed immutable original styled layout. All input validation precedes
/// fallback/shaping callbacks; partial candidates are never published.
#[derive(Clone, Debug)]
pub struct StyledTextLayout {
    text: Arc<[u16]>,
    ranges: Arc<[StyledTextRange]>,
    inline_boxes: Arc<[StyledTextInlineBox]>,
    options: StyledTextLayoutOptions,
    preparation: ManagedPreparationOptions,
    glyphs: Vec<StyledTextGlyph>,
    boxes: Vec<StyledTextPositionedBox>,
    lines: Vec<StyledTextLine>,
    content_size: [f32; 2],
}
impl StyledTextLayout {
    /// Port of the original complete styled writer. Fallback and Unicode facts
    /// are explicit portable inputs, not inferred system-provider qualification.
    pub fn new<F: ManagedFontFallback + ?Sized>(
        text: impl Into<Arc<[u16]>>,
        ranges: impl Into<Arc<[StyledTextRange]>>,
        inline_boxes: impl Into<Arc<[StyledTextInlineBox]>>,
        options: StyledTextLayoutOptions,
        preparation: ManagedPreparationOptions,
        fallback: &F,
    ) -> Result<Self, TextLayoutError> {
        input::validate_options(&options)?;
        let text = text.into();
        if text.len() > i32::MAX as usize {
            return Err(TextLayoutError::Capacity);
        }
        if char::decode_utf16(text.iter().copied()).any(|c| c.is_err()) {
            return Err(TextLayoutError::Unicode(UnicodeError::InvalidEncoding));
        }
        let ranges = ranges.into();
        let styles = input::style_map(&text, &ranges)?;
        let inline_boxes = inline_boxes.into();
        let boxes = input::box_map(&text, &inline_boxes)?;
        input::validate_paragraph_styles(&text, &options.paragraph_styles)?;
        let mut result = Self {
            text,
            ranges,
            inline_boxes,
            options,
            preparation,
            glyphs: Vec::new(),
            boxes: Vec::new(),
            lines: Vec::new(),
            content_size: [0.0; 2],
        };
        result.generate(&styles, &boxes, fallback)?;
        Ok(result)
    }
    /// Original well-formed UTF-16 source.
    pub fn text(&self) -> &[u16] {
        &self.text
    }
    /// Frozen complete style partition, preserving font identities and tags.
    pub fn ranges(&self) -> &[StyledTextRange] {
        &self.ranges
    }
    /// Original inline object inventory, in caller order.
    pub fn inline_boxes(&self) -> &[StyledTextInlineBox] {
        &self.inline_boxes
    }
    /// Frozen request including exact paragraph records.
    pub fn options(&self) -> &StyledTextLayoutOptions {
        &self.options
    }
    /// Actual physical glyph output, excluding tabs and inline objects.
    pub fn glyphs(&self) -> &[StyledTextGlyph] {
        &self.glyphs
    }
    /// Actual physical inline-object output.
    pub fn boxes(&self) -> &[StyledTextPositionedBox] {
        &self.boxes
    }
    /// Original physical line frames and output membership.
    pub fn lines(&self) -> &[StyledTextLine] {
        &self.lines
    }
    /// Original constrained/unconstrained width and maximum line/ink bottom.
    pub fn content_size(&self) -> [f32; 2] {
        self.content_size
    }
    /// Build a complete width-reflow candidate while retaining this generation.
    pub fn reflow<F: ManagedFontFallback + ?Sized>(
        &self,
        max_width: f32,
        fallback: &F,
    ) -> Result<Self, TextLayoutError> {
        let mut options = self.options.clone();
        options.max_width = max_width;
        Self::new(
            self.text.clone(),
            self.ranges.clone(),
            self.inline_boxes.clone(),
            options,
            self.preparation.clone(),
            fallback,
        )
    }
}
