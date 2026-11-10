//! Original native retained horizontal hinted paragraphs. Explicit style fonts,
//! grapheme fallback selection and source number substitution retain independent
//! policies; every run is executed by the native device shaper and actual provider.
//! This is the raw float measured route. Source-double/ideal-unit fitting is a
//! separate producer contract and is never silently narrowed into this API.
use super::{
    ShapingGlyph,
    native_layout::*,
    native_pipeline::*,
    unicode::{
        self, ScriptRun, UnicodeError, UnicodeScalar,
        bidi::{BidiLevel, BidiWorkspace},
        grapheme::{GraphemeCluster, GraphemePolicy},
        line_break::{LineBreakClass, LineBreakKind, LineBreakPolicy},
    },
};
use crate::font::{FontError, SfntFace, Tag, layout::GposDirection};
use std::{ops::Range, sync::Arc};
mod design;
pub use design::*;
mod fallback;
mod fragment_interaction;
pub use fragment_interaction::*;
mod geometry;
mod source;
pub use source::*;
mod interaction;
pub use geometry::*;
mod prepare;
pub use fallback::{NativeFallbackCandidate, NativeFallbackRun, native_itemize_font_fallback};
pub use interaction::{
    NativeCaretStop, NativeClusterBox, NativeParagraphInteraction, NativeTextHit,
    NativeTextRectangle,
};

/// Original stage attached to a failed private paragraph candidate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeParagraphStage {
    Shaping,
    Bidi,
    LineBreak,
    ClusterMap,
    Layout,
    Interaction,
}
/// Exact underlying native font, Unicode or actual-provider error.
#[derive(Debug, thiserror::Error)]
pub enum NativeParagraphCause {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Unicode(#[from] UnicodeError),
    #[error(transparent)]
    Device(#[from] NativeDeviceError),
}
/// Failure does not publish a paragraph or replace an earlier retained view.
#[derive(Debug, thiserror::Error)]
#[error("native paragraph {stage:?}: {cause}")]
pub struct NativeParagraphError {
    pub stage: NativeParagraphStage,
    pub cause: NativeParagraphCause,
}
fn error(
    stage: NativeParagraphStage,
    cause: impl Into<NativeParagraphCause>,
) -> NativeParagraphError {
    NativeParagraphError {
        stage,
        cause: cause.into(),
    }
}
fn invalid(stage: NativeParagraphStage) -> NativeParagraphError {
    error(stage, FontError::InvalidArgument)
}
fn reserved<T>(n: usize) -> Result<Vec<T>, FontError> {
    let mut v = Vec::new();
    v.try_reserve_exact(n).map_err(|_| FontError::Capacity)?;
    Ok(v)
}
fn filled<T: Clone>(n: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut v = reserved(n)?;
    v.resize(n, value);
    Ok(v)
}
fn copied<T: Clone>(v: &[T]) -> Result<Vec<T>, FontError> {
    let mut o = reserved(v.len())?;
    o.extend_from_slice(v);
    Ok(o)
}

/// Exact original number-symbol policy bits, independent of glyph/font metrics.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeNumberSubstitution {
    /// Low 21 bits identify the zero scalar; bit31 contextual, bit30 source bidi.
    pub digits: u32,
    /// Zero leaves the corresponding original ASCII symbol unchanged.
    pub percent: u32,
    pub group_separator: u32,
    pub decimal_separator: u32,
}
/// One complete native style, covering an exact contiguous scalar range.
#[derive(Clone, Debug)]
pub struct NativeParagraphStyle {
    pub scalar_start: u32,
    pub scalar_count: u32,
    /// Index in the retained immutable font inventory; no per-glyph OS discovery.
    pub font_index: u32,
    /// Original style identity. Device advances use units_per_pixel / 64 instead.
    pub source_scale: f32,
    pub features: Vec<NativeFeature>,
    pub language: Tag,
    pub number_substitution: NativeNumberSubstitution,
    /// Actual frozen requested size, interpreter, phase and user coordinates.
    pub hinting: NativeHintedRequest,
    pub logical_units_per_physical_pixel: f32,
    /// Original caller-resolved horizontal source line metrics.
    pub metrics: NativeTextItemMetrics,
}
/// Exact source run occurrence ownership, including repeated/no-ink descriptors.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeParagraphGlyphOwner {
    pub run_index: u32,
    pub run_glyph_index: u32,
    pub descriptor_index: u32,
}
/// Native run partition and its complete actual captured generation.
#[derive(Debug)]
pub struct NativeParagraphRun {
    pub generation: Arc<NativeDeviceRun>,
    pub scalar_range: Range<u32>,
    pub logical_range: Range<u32>,
    pub font_index: u32,
    pub style_index: u32,
    pub bidi_level: i8,
    pub source_scale: f32,
    pub logical_units_per_physical_pixel: f32,
}
/// Frozen shaping settings. Reflow shares this exact source, never a new request.
#[derive(Clone, Debug)]
pub struct NativeParagraphShaping {
    pub unicode_script: Tag,
    pub language: Tag,
    pub direction: GposDirection,
    pub features: Vec<NativeFeature>,
    pub normalized_coordinates: Vec<i16>,
    pub alternate_value: u32,
    pub zero_mark_advances: bool,
    pub fallback_bounds: NativeFallbackBounds,
    pub cluster_level: NativeClusterLevel,
    pub flags: NativeBufferFlags,
    pub normalization: Option<unicode::normalization::NormalizationPlan>,
    pub pre_context: Vec<UnicodeScalar>,
    pub post_context: Vec<UnicodeScalar>,
}
impl NativeParagraphShaping {
    fn capture(r: NativeShapeRequest<'_>) -> Result<Self, FontError> {
        Ok(Self {
            unicode_script: r.unicode_script,
            language: r.language,
            direction: r.direction,
            features: copied(r.features)?,
            normalized_coordinates: copied(r.normalized_coordinates)?,
            alternate_value: r.alternate_value,
            zero_mark_advances: r.zero_mark_advances,
            fallback_bounds: r.fallback_bounds,
            cluster_level: r.cluster_level,
            flags: r.flags,
            normalization: r.normalization.cloned(),
            pre_context: copied(r.pre_context)?,
            post_context: copied(r.post_context)?,
        })
    }
    fn request(&self) -> NativeShapeRequest<'_> {
        NativeShapeRequest {
            unicode_script: self.unicode_script,
            language: self.language,
            direction: self.direction,
            features: &self.features,
            normalized_coordinates: &self.normalized_coordinates,
            alternate_value: self.alternate_value,
            zero_mark_advances: self.zero_mark_advances,
            fallback_bounds: self.fallback_bounds,
            cluster_level: self.cluster_level,
            flags: self.flags,
            normalization: self.normalization.as_ref(),
            pre_context: &self.pre_context,
            post_context: &self.post_context,
        }
    }
}
/// Full source generation shared by every retained suffix. All arrays are private
/// and immutable after successful publication; accessors borrow exact records.
#[derive(Debug)]
pub struct NativeParagraphSource {
    source_policies: Option<Vec<NativeSourcePolicy>>,
    source: Vec<UnicodeScalar>,
    input: Vec<UnicodeScalar>,
    fonts: Vec<SfntFace>,
    styles: Vec<NativeParagraphStyle>,
    shaping: NativeParagraphShaping,
    paragraph_level: i8,
    source_digit_bidi: bool,
    scalar_levels: Vec<BidiLevel>,
    scripts: Vec<ScriptRun>,
    graphemes: Vec<GraphemeCluster>,
    fallback: Vec<NativeFallbackRun>,
    line_classes: Vec<LineBreakClass>,
    scalar_breaks: Vec<LineBreakKind>,
    runs: Vec<NativeParagraphRun>,
    logical: Vec<ShapingGlyph>,
    levels: Vec<i8>,
    font_indices: Vec<u32>,
    source_scales: Vec<f32>,
    scales: Vec<f32>,
    owners: Vec<NativeParagraphGlyphOwner>,
    breaks: Vec<LineBreakKind>,
    metrics: Vec<NativeTextItemMetrics>,
    ends: Vec<i32>,
    justification: Vec<NativeTextJustification>,
}
impl NativeParagraphSource {
    pub fn source_policies(&self) -> Option<&[NativeSourcePolicy]> {
        self.source_policies.as_deref()
    }
    pub fn source(&self) -> &[UnicodeScalar] {
        &self.source
    }
    pub fn shaping_input(&self) -> &[UnicodeScalar] {
        &self.input
    }
    pub fn fonts(&self) -> &[SfntFace] {
        &self.fonts
    }
    pub fn styles(&self) -> &[NativeParagraphStyle] {
        &self.styles
    }
    pub fn shaping(&self) -> &NativeParagraphShaping {
        &self.shaping
    }
    pub fn paragraph_level(&self) -> i8 {
        self.paragraph_level
    }
    pub fn preserves_source_digit_bidi(&self) -> bool {
        self.source_digit_bidi
    }
    pub fn scalar_levels(&self) -> &[BidiLevel] {
        &self.scalar_levels
    }
    pub fn script_runs(&self) -> &[ScriptRun] {
        &self.scripts
    }
    pub fn graphemes(&self) -> &[GraphemeCluster] {
        &self.graphemes
    }
    pub fn fallback_runs(&self) -> &[NativeFallbackRun] {
        &self.fallback
    }
    pub fn line_break_classes(&self) -> &[LineBreakClass] {
        &self.line_classes
    }
    pub fn scalar_breaks(&self) -> &[LineBreakKind] {
        &self.scalar_breaks
    }
    pub fn runs(&self) -> &[NativeParagraphRun] {
        &self.runs
    }
    pub fn logical_glyphs(&self) -> &[ShapingGlyph] {
        &self.logical
    }
    pub fn logical_bidi_levels(&self) -> &[i8] {
        &self.levels
    }
    pub fn logical_font_indices(&self) -> &[u32] {
        &self.font_indices
    }
    pub fn logical_source_scales(&self) -> &[f32] {
        &self.source_scales
    }
    pub fn glyph_scales(&self) -> &[f32] {
        &self.scales
    }
    pub fn logical_owners(&self) -> &[NativeParagraphGlyphOwner] {
        &self.owners
    }
    pub fn breaks_after(&self) -> &[LineBreakKind] {
        &self.breaks
    }
    pub fn item_metrics(&self) -> &[NativeTextItemMetrics] {
        &self.metrics
    }
    pub fn logical_cluster_ends(&self) -> &[i32] {
        &self.ends
    }
    pub fn justification_classes(&self) -> &[NativeTextJustification] {
        &self.justification
    }
}
/// Immutable positioned paragraph. A suffix shares the entire original source
/// and actual runs, including source occurrences omitted by maximum-lines fitting.
#[derive(Debug)]
pub struct NativeHintedParagraph {
    source: Arc<NativeParagraphSource>,
    options: NativeTextLayoutOptions,
    logical_start: usize,
    glyphs: Vec<NativePositionedTextGlyph>,
    lines: Vec<NativePositionedTextLine>,
    owners: Vec<NativeParagraphGlyphOwner>,
    levels: Vec<i8>,
    ends: Vec<i32>,
    origins: Vec<f32>,
    frames: Vec<NativeTextLineFrame>,
    metrics: NativeTextLayoutMetrics,
}
impl NativeHintedParagraph {
    /// Shape, capture, reorder and measure a complete styled native paragraph.
    /// All policy/source admission precedes actual font callbacks. The actual
    /// provider remains mandatory; failures retire the unpublished candidate.
    pub fn shape(
        fonts: &[SfntFace],
        input: &[UnicodeScalar],
        styles: &[NativeParagraphStyle],
        shaping: NativeShapeRequest<'_>,
        options: NativeTextLayoutOptions,
        projection: NativeHintedProjection,
        provider: &mut dyn NativeHintedProvider,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        prepare::validate(fonts, input, styles, shaping, options)?;
        let source = prepare::build(fonts, input, styles, shaping, options, projection, provider)?;
        Self::place(Arc::new(source), 0, options)
    }
    fn place(
        source: Arc<NativeParagraphSource>,
        start: usize,
        mut options: NativeTextLayoutOptions,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Layout;
        options.direction = if source.paragraph_level == 0 {
            GposDirection::LeftToRight
        } else {
            GposDirection::RightToLeft
        };
        let logical = &source.logical[start..];
        let required = native_layout_requirements(
            logical,
            &source.breaks[start..],
            &source.scales[start..],
            options,
            NativeTextTabs::default(),
        )
        .map_err(|e| error(stage, e))?;
        let mut glyphs = filled(
            required.glyph_capacity,
            NativePositionedTextGlyph::default(),
        )
        .map_err(|e| error(stage, e))?;
        let mut lines = filled(required.line_capacity, NativePositionedTextLine::default())
            .map_err(|e| error(stage, e))?;
        let mut levels = filled(required.glyph_capacity, 0i8).map_err(|e| error(stage, e))?;
        let mut origins = filled(required.line_capacity, 0f32).map_err(|e| error(stage, e))?;
        let mut frames = filled(required.line_capacity, NativeTextLineFrame::default())
            .map_err(|e| error(stage, e))?;
        let mut groups = filled(logical.len(), NativeVisualClusterGroup::default())
            .map_err(|e| error(stage, e))?;
        let mut indices = filled(logical.len(), 0u32).map_err(|e| error(stage, e))?;
        let [ng, nl] = native_layout_measured_into(
            NativeMeasuredInput {
                glyphs: logical,
                breaks: &source.breaks[start..],
                levels: &source.levels[start..],
                scales: &source.scales[start..],
                paragraph_level: source.paragraph_level,
                options,
                tabs: NativeTextTabs::default(),
                justification: if source.justification.is_empty() {
                    &[]
                } else {
                    &source.justification[start..]
                },
                item_metrics: &source.metrics[start..],
                continues: false,
            },
            NativeLayoutScratch {
                visual_groups: &mut groups,
                visual_indices: &mut indices,
                tab_advances: &mut [],
            },
            NativeMeasuredOutput {
                glyphs: &mut glyphs,
                lines: &mut lines,
                retained: Some(NativeLayoutRetained {
                    bidi_levels: &mut levels,
                    line_origins: &mut origins,
                    line_frames: &mut frames,
                }),
            },
        )
        .map_err(|e| error(stage, e))?;
        glyphs.truncate(ng);
        levels.truncate(ng);
        lines.truncate(nl);
        origins.truncate(nl);
        frames.truncate(nl);
        let mut owners = reserved(ng).map_err(|e| error(stage, e))?;
        let mut ends = reserved(ng).map_err(|e| error(stage, e))?;
        for g in &mut glyphs {
            if g.glyph_index as usize >= logical.len() {
                return Err(invalid(stage));
            }
            g.glyph_index += start as u32;
            let index = g.glyph_index as usize;
            owners.push(source.owners[index]);
            ends.push(source.ends[index]);
        }
        let metrics = native_measure_text_lines(&lines, options.maximum_width, true)
            .map_err(|e| error(stage, e))?;
        Ok(Arc::new(Self {
            source,
            options,
            logical_start: start,
            glyphs,
            lines,
            owners,
            levels,
            ends,
            origins,
            frames,
            metrics,
        }))
    }
    /// Reuse the exact full paragraph generation at a proven original cluster.
    /// Nested continuation cannot move before its current first input position.
    /// No font callbacks, bidi resolution or shaping occur during reflow.
    pub fn reflow(
        &self,
        input_start: i32,
        maximum_width: f32,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Layout;
        if self.source.source_policies.is_some()
            || !maximum_width.is_finite()
            || maximum_width < 0.0
            || input_start < 0
            || self
                .lines
                .first()
                .is_none_or(|line| input_start < line.input_start)
        {
            return Err(invalid(stage));
        }
        let start = self
            .source
            .logical
            .partition_point(|g| g.cluster < input_start);
        if self
            .source
            .logical
            .get(start)
            .is_none_or(|g| g.cluster != input_start)
        {
            return Err(invalid(stage));
        }
        let mut options = self.options;
        options.maximum_width = maximum_width;
        let result = Self::place(self.source.clone(), start, options)?;
        if result
            .lines
            .first()
            .is_none_or(|l| l.input_start != input_start)
        {
            return Err(invalid(stage));
        }
        Ok(result)
    }
    pub fn source(&self) -> &Arc<NativeParagraphSource> {
        &self.source
    }
    pub fn options(&self) -> NativeTextLayoutOptions {
        self.options
    }
    pub fn logical_start(&self) -> usize {
        self.logical_start
    }
    pub fn glyphs(&self) -> &[NativePositionedTextGlyph] {
        &self.glyphs
    }
    pub fn lines(&self) -> &[NativePositionedTextLine] {
        &self.lines
    }
    pub fn owners(&self) -> &[NativeParagraphGlyphOwner] {
        &self.owners
    }
    pub fn bidi_levels(&self) -> &[i8] {
        &self.levels
    }
    pub fn cluster_ends(&self) -> &[i32] {
        &self.ends
    }
    pub fn line_origins(&self) -> &[f32] {
        &self.origins
    }
    pub fn line_frames(&self) -> &[NativeTextLineFrame] {
        &self.frames
    }
    pub fn metrics(&self) -> NativeTextLayoutMetrics {
        self.metrics
    }
    /// Complete independent copies, after preflighting every output capacity.
    /// The immutable owner is retained by `self`; unused caller tails survive.
    pub fn copy_into(
        &self,
        glyphs: &mut [NativePositionedTextGlyph],
        lines: &mut [NativePositionedTextLine],
        owners: &mut [NativeParagraphGlyphOwner],
        levels: &mut [i8],
        ends: &mut [i32],
        origins: &mut [f32],
        frames: &mut [NativeTextLineFrame],
    ) -> Result<(), FontError> {
        let n = self.glyphs.len();
        let l = self.lines.len();
        if glyphs.len() < n
            || owners.len() < n
            || levels.len() < n
            || ends.len() < n
            || lines.len() < l
            || origins.len() < l
            || frames.len() < l
        {
            return Err(FontError::Capacity);
        }
        glyphs[..n].copy_from_slice(&self.glyphs);
        lines[..l].copy_from_slice(&self.lines);
        owners[..n].copy_from_slice(&self.owners);
        levels[..n].copy_from_slice(&self.levels);
        ends[..n].copy_from_slice(&self.ends);
        origins[..l].copy_from_slice(&self.origins);
        frames[..l].copy_from_slice(&self.frames);
        Ok(())
    }
}
