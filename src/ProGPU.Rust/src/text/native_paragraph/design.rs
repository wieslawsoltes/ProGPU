//! Original generic unhinted paragraph core, separate from device/source paragraphs.
use super::*;
mod flow;
mod prepare;
pub use flow::*;
/// Original explicit styled selection; empty inventory selects native fallback.
#[derive(Clone, Debug)]
pub struct NativeDesignStyle {
    pub scalar_start: u32,
    pub scalar_count: u32,
    pub font_index: u32,
    pub scale: f32,
    pub features: Vec<NativeFeature>,
    pub language: Tag,
    pub number_substitution: NativeNumberSubstitution,
}
/// Original inline-object slot. Objects are retained items, never shaped glyphs.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeInlineObject {
    pub scalar_index: u32,
    pub width: f32,
    pub ascent: f32,
    pub descent: f32,
}
#[derive(Clone, Debug, Default)]
pub struct NativeInlineFlow {
    pub style_metrics: Vec<NativeTextItemMetrics>,
    pub objects: Vec<NativeInlineObject>,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeDesignOptions {
    pub layout: NativeTextLayoutOptions,
    pub tabs: NativeTextTabs,
    pub continuation_start: Option<i32>,
    pub measure_intrinsic_widths: bool,
}
impl Default for NativeDesignOptions {
    fn default() -> Self {
        Self {
            layout: NativeTextLayoutOptions::default(),
            tabs: NativeTextTabs::default(),
            continuation_start: None,
            measure_intrinsic_widths: false,
        }
    }
}
/// Actual design-run ownership or the original explicit nonfont item.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDesignOccurrence {
    Glyph {
        run_index: u32,
        run_glyph_index: u32,
    },
    Tab {
        scalar_index: u32,
        font_index: u32,
    },
    InlineObject {
        object_index: u32,
    },
}
#[derive(Debug)]
pub struct NativeDesignRun {
    pub generation: Arc<NativeRun>,
    pub scalar_range: Range<u32>,
    pub logical_range: Range<u32>,
    pub font_index: u32,
    pub style_index: Option<u32>,
    pub bidi_level: i8,
    pub scale: f32,
}
/// Complete original paragraph generation. Reflow does not discard hidden runs,
/// resolve fonts again or fabricate a shaping owner for tabs, objects or ellipsis.
#[derive(Debug)]
pub struct NativeDesignSource {
    source: Vec<UnicodeScalar>,
    input: Vec<UnicodeScalar>,
    fonts: Vec<SfntFace>,
    styles: Vec<NativeDesignStyle>,
    shaping: NativeParagraphShaping,
    inline: Option<NativeInlineFlow>,
    tab_flow: bool,
    paragraph_level: i8,
    source_digit_bidi: bool,
    scalar_levels: Vec<BidiLevel>,
    scripts: Vec<ScriptRun>,
    graphemes: Vec<GraphemeCluster>,
    fallback: Vec<NativeFallbackRun>,
    line_classes: Vec<LineBreakClass>,
    scalar_breaks: Vec<LineBreakKind>,
    runs: Vec<NativeDesignRun>,
    logical: Vec<ShapingGlyph>,
    levels: Vec<i8>,
    font_indices: Vec<u32>,
    scales: Vec<f32>,
    owners: Vec<NativeDesignOccurrence>,
    breaks: Vec<LineBreakKind>,
    metrics: Vec<NativeTextItemMetrics>,
}
impl NativeDesignSource {
    pub fn source(&self) -> &[UnicodeScalar] {
        &self.source
    }
    pub fn admitted_source(&self) -> &[UnicodeScalar] {
        &self.input
    }
    pub fn fonts(&self) -> &[SfntFace] {
        &self.fonts
    }
    pub fn styles(&self) -> &[NativeDesignStyle] {
        &self.styles
    }
    pub fn shaping(&self) -> &NativeParagraphShaping {
        &self.shaping
    }
    pub fn inline_flow(&self) -> Option<&NativeInlineFlow> {
        self.inline.as_ref()
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
    pub fn runs(&self) -> &[NativeDesignRun] {
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
    pub fn glyph_scales(&self) -> &[f32] {
        &self.scales
    }
    pub fn logical_owners(&self) -> &[NativeDesignOccurrence] {
        &self.owners
    }
    pub fn breaks_after(&self) -> &[LineBreakKind] {
        &self.breaks
    }
    pub fn item_metrics(&self) -> &[NativeTextItemMetrics] {
        &self.metrics
    }
    pub fn uses_incremental_tabs(&self) -> bool {
        self.tab_flow
    }
}
#[derive(Debug)]
pub struct NativeDesignParagraph {
    source: Arc<NativeDesignSource>,
    options: NativeDesignOptions,
    glyphs: Vec<NativeParagraphPositionedRecord>,
    lines: Vec<NativePositionedTextLine>,
    owners: Vec<Option<NativeDesignOccurrence>>,
    metrics: NativeTextLayoutMetrics,
    intrinsic: Option<NativeTextIntrinsicWidths>,
}
impl NativeDesignParagraph {
    /// Native font-to-positioned paragraph orchestration with original styled or
    /// fallback selection, full context, tabs, inline objects and trimming policy.
    pub fn shape(
        fonts: &[SfntFace],
        input: &[UnicodeScalar],
        styles: &[NativeDesignStyle],
        shaping: NativeShapeRequest<'_>,
        options: NativeDesignOptions,
        inline: Option<&NativeInlineFlow>,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        prepare::validate(fonts, input, styles, shaping, options, inline)?;
        let source = Arc::new(prepare::build(
            fonts, input, styles, shaping, options, inline,
        )?);
        Self::place(source, options)
    }
    fn place(
        source: Arc<NativeDesignSource>,
        mut options: NativeDesignOptions,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Layout;
        prepare::options(options, source.source.len(), source.inline.is_some())?;
        if (options.tabs.interval > 0.0) != source.tab_flow {
            return Err(invalid(stage));
        }
        if source.input.is_empty() {
            let intrinsic = options
                .measure_intrinsic_widths
                .then(NativeTextIntrinsicWidths::default);
            return Ok(Arc::new(Self {
                source,
                options,
                glyphs: Vec::new(),
                lines: Vec::new(),
                owners: Vec::new(),
                metrics: NativeTextLayoutMetrics::default(),
                intrinsic,
            }));
        }
        options.layout.direction = if source.paragraph_level == 1 {
            GposDirection::RightToLeft
        } else {
            GposDirection::LeftToRight
        };
        let start = if let Some(input) = options.continuation_start {
            let i = source.logical.partition_point(|g| g.cluster < input);
            if source.logical.get(i).is_none_or(|g| g.cluster != input) {
                return Err(invalid(NativeParagraphStage::ClusterMap));
            }
            i
        } else {
            0
        };
        let all_scales = if source.styles.is_empty() {
            &[][..]
        } else {
            &source.scales[..]
        };
        let intrinsic = if options.measure_intrinsic_widths {
            Some(
                native_measure_intrinsic_widths(
                    &source.input,
                    &source.logical,
                    &source.breaks,
                    all_scales,
                    options.layout,
                    options.tabs,
                )
                .map_err(|e| error(stage, e))?,
            )
        } else {
            None
        };
        let scales = if source.styles.is_empty() {
            &[][..]
        } else {
            &source.scales[start..]
        };
        let glyphs = &source.logical[start..];
        let needed = native_layout_requirements(
            glyphs,
            &source.breaks[start..],
            scales,
            options.layout,
            options.tabs,
        )
        .map_err(|e| error(stage, e))?;
        let mut positions = filled(needed.glyph_capacity, NativePositionedTextGlyph::default())
            .map_err(|e| error(stage, e))?;
        let mut lines = filled(needed.line_capacity, NativePositionedTextLine::default())
            .map_err(|e| error(stage, e))?;
        let mut groups = filled(needed.glyph_capacity, NativeVisualClusterGroup::default())
            .map_err(|e| error(stage, e))?;
        let mut indices = filled(needed.glyph_capacity, 0u32).map_err(|e| error(stage, e))?;
        let mut advances = filled(
            if options.tabs.interval > 0.0 {
                needed.glyph_capacity
            } else {
                0
            },
            0f32,
        )
        .map_err(|e| error(stage, e))?;
        let mut justification = if options.layout.alignment == NativeTextAlignment::Justify {
            filled(source.logical.len(), NativeTextJustification::Content)
                .map_err(|e| error(stage, e))?
        } else {
            Vec::new()
        };
        if options.layout.alignment == NativeTextAlignment::Justify {
            native_classify_justification_into(&source.input, &source.logical, &mut justification)
                .map_err(|e| error(NativeParagraphStage::ClusterMap, e))?;
        }
        let count = native_layout_measured_into(
            NativeMeasuredInput {
                glyphs,
                breaks: &source.breaks[start..],
                levels: &source.levels[start..],
                scales,
                paragraph_level: source.paragraph_level,
                options: options.layout,
                tabs: options.tabs,
                justification: if options.layout.alignment == NativeTextAlignment::Justify {
                    &justification[start..]
                } else {
                    &[]
                },
                item_metrics: if source.inline.is_some() {
                    &source.metrics[start..]
                } else {
                    &[]
                },
                continues: false,
            },
            NativeLayoutScratch {
                visual_groups: &mut groups,
                visual_indices: &mut indices,
                tab_advances: &mut advances,
            },
            NativeMeasuredOutput {
                glyphs: &mut positions,
                lines: &mut lines,
                retained: None,
            },
        )
        .map_err(|e| error(stage, e))?;
        positions.truncate(count[0]);
        lines.truncate(count[1]);
        let metrics = native_measure_text_lines(
            &lines,
            options.layout.maximum_width,
            source.inline.is_some(),
        )
        .map_err(|e| error(stage, e))?;
        let mut output = reserved(positions.len()).map_err(|e| error(stage, e))?;
        let mut owners = reserved(positions.len()).map_err(|e| error(stage, e))?;
        for mut g in positions {
            let font = if g.glyph_index == u32::MAX {
                owners.push(None);
                0
            } else {
                g.glyph_index += start as u32;
                let i = g.glyph_index as usize;
                owners.push(Some(source.owners[i]));
                source.font_indices[i]
            };
            output.push(NativeParagraphPositionedRecord {
                glyph_index: g.glyph_index,
                glyph_id: g.glyph_id,
                font_index: font,
                cluster: g.cluster,
                x: g.position[0],
                y: g.position[1],
                advance_x: g.advance[0],
                advance_y: g.advance[1],
            });
        }
        Ok(Arc::new(Self {
            source,
            options,
            glyphs: output,
            lines,
            owners,
            metrics,
            intrinsic,
        }))
    }
    /// Place another actual full-paragraph cluster suffix using retained native
    /// source/run owners. Original ordinary layout and collapse admission apply.
    /// Switching incremental tabs on/off requires a new shape generation because
    /// it changes native run partitioning. Positive tab grids may change freely.
    pub fn reflow(&self, options: NativeDesignOptions) -> Result<Arc<Self>, NativeParagraphError> {
        Self::place(self.source.clone(), options)
    }
    pub fn source(&self) -> &Arc<NativeDesignSource> {
        &self.source
    }
    pub fn options(&self) -> NativeDesignOptions {
        self.options
    }
    pub fn glyphs(&self) -> &[NativeParagraphPositionedRecord] {
        &self.glyphs
    }
    pub fn lines(&self) -> &[NativePositionedTextLine] {
        &self.lines
    }
    pub fn owners(&self) -> &[Option<NativeDesignOccurrence>] {
        &self.owners
    }
    pub fn metrics(&self) -> NativeTextLayoutMetrics {
        self.metrics
    }
    pub fn intrinsic_widths(&self) -> Option<NativeTextIntrinsicWidths> {
        self.intrinsic
    }
    /// Whole-capacity preflight preserves atomic outputs and unused tails.
    pub fn copy_into(
        &self,
        glyphs: &mut [NativeParagraphPositionedRecord],
        lines: &mut [NativePositionedTextLine],
        owners: &mut [Option<NativeDesignOccurrence>],
    ) -> Result<[usize; 2], FontError> {
        if glyphs.len() < self.glyphs.len()
            || lines.len() < self.lines.len()
            || owners.len() < self.owners.len()
        {
            return Err(FontError::Capacity);
        }
        glyphs[..self.glyphs.len()].copy_from_slice(&self.glyphs);
        lines[..self.lines.len()].copy_from_slice(&self.lines);
        owners[..self.owners.len()].copy_from_slice(&self.owners);
        Ok([self.glyphs.len(), self.lines.len()])
    }
}
