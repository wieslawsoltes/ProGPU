//! Original styled paragraph-to-fragment/floating producer, retaining complete
//! native runs, scalar events and actual resolved placement ownership.
use super::*;
const LIMIT: usize = 1 << 20;
/// Original source event between scalar positions, independent of inline objects.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeFloatingSourceItem {
    pub scalar_index: u32,
    pub width: f32,
    pub height: f32,
    pub alignment: NativeAnchorAlignment,
}
#[derive(Clone, Debug, Default)]
pub struct NativeFloatingSourceFlow {
    pub empty_metrics: NativeTextItemMetrics,
    pub items: Vec<NativeFloatingSourceItem>,
}
#[derive(Clone, Debug)]
pub struct NativeParagraphFlow {
    pub origin_y: f64,
    pub maximum_attempts: u32,
    pub exclusions: Vec<NativeExclusionRectangle>,
    pub floating: Option<NativeFloatingSourceFlow>,
}
impl Default for NativeParagraphFlow {
    fn default() -> Self {
        Self {
            origin_y: 0.0,
            maximum_attempts: LIMIT as u32,
            exclusions: Vec::new(),
            floating: None,
        }
    }
}
/// Original paragraph source and every positioned fragment/float frame. This is
/// the ordinary design-unit flow; device/source-ideal-unit policies stay separate.
#[derive(Debug)]
pub struct NativeFragmentParagraph {
    source: Arc<NativeDesignSource>,
    options: NativeDesignOptions,
    flow: NativeParagraphFlow,
    float_items: Vec<NativeFloatingItem>,
    glyphs: Vec<NativeParagraphPositionedRecord>,
    owners: Vec<NativeDesignOccurrence>,
    lines: Vec<NativePositionedTextLine>,
    placements: Vec<NativeFragmentPlacement>,
    floats: Vec<NativeFloatingPlacement>,
    result: NativeExclusionFlowResult,
    floating_result: Option<NativeFloatingFlowResult>,
    metrics: NativeTextLayoutMetrics,
    intrinsic: Option<NativeTextIntrinsicWidths>,
}
fn validate_flow(
    input_count: usize,
    options: NativeDesignOptions,
    flow: &NativeParagraphFlow,
) -> Result<(), NativeParagraphError> {
    let stage = NativeParagraphStage::Shaping;
    if options.continuation_start.is_some()
        || options.layout.trimming != NativeTextTrimming::None
        || options.layout.collapse_width != -1.0
        || options.layout.maximum_width <= 0.0
        || !flow.origin_y.is_finite()
        || flow.origin_y < 0.0
        || flow.origin_y > f64::from(f32::MAX)
        || flow.maximum_attempts == 0
        || flow.maximum_attempts > LIMIT as u32
        || flow.exclusions.len() > LIMIT
        || flow.exclusions.iter().any(|r| {
            ![r.left, r.top, r.right, r.bottom]
                .into_iter()
                .all(f32::is_finite)
                || r.right < r.left
                || r.bottom < r.top
        })
    {
        return Err(invalid(stage));
    }
    if let Some(f) = &flow.floating {
        if options.measure_intrinsic_widths
            || f.items.len() > LIMIT - flow.exclusions.len()
            || [f.empty_metrics.ascent, f.empty_metrics.descent]
                .into_iter()
                .any(|v| !v.is_finite() || v < 0.0)
        {
            return Err(invalid(stage));
        }
        let mut previous = 0;
        for item in &f.items {
            if item.scalar_index < previous
                || item.scalar_index as usize > input_count
                || !item.width.is_finite()
                || !item.height.is_finite()
                || item.width <= 0.0
                || item.height <= 0.0
            {
                return Err(invalid(stage));
            }
            previous = item.scalar_index;
        }
    }
    Ok(())
}
impl NativeFragmentParagraph {
    /// Shape the full original styled paragraph once, then execute the original
    /// measured exclusion/floating writer. Source events are admitted before fonts.
    pub fn shape(
        fonts: &[SfntFace],
        input: &[UnicodeScalar],
        styles: &[NativeDesignStyle],
        shaping: NativeShapeRequest<'_>,
        options: NativeDesignOptions,
        inline: &NativeInlineFlow,
        flow: &NativeParagraphFlow,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        prepare::validate(fonts, input, styles, shaping, options, Some(inline))?;
        validate_flow(input.len(), options, flow)?;
        let source = Arc::new(prepare::build(
            fonts,
            input,
            styles,
            shaping,
            options,
            Some(inline),
        )?);
        Self::from_source(source, options, flow)
    }
    /// Reuse an actual full generic paragraph source with caller-resolved metrics.
    /// Original source-event boundaries are mapped before placement publication.
    pub fn from_source(
        source: Arc<NativeDesignSource>,
        mut options: NativeDesignOptions,
        flow: &NativeParagraphFlow,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Layout;
        prepare::options(options, source.source.len(), true)?;
        validate_flow(source.source.len(), options, flow)?;
        if source.inline.is_none() || source.tab_flow != (options.tabs.interval > 0.0) {
            return Err(invalid(stage));
        }
        options.layout.direction = if source.paragraph_level == 0 {
            GposDirection::LeftToRight
        } else {
            GposDirection::RightToLeft
        };
        let flow = NativeParagraphFlow {
            origin_y: flow.origin_y,
            maximum_attempts: flow.maximum_attempts,
            exclusions: copied(&flow.exclusions).map_err(|e| error(stage, e))?,
            floating: flow
                .floating
                .as_ref()
                .map(|f| {
                    Ok::<_, FontError>(NativeFloatingSourceFlow {
                        empty_metrics: f.empty_metrics,
                        items: copied(&f.items)?,
                    })
                })
                .transpose()
                .map_err(|e| error(stage, e))?,
        };
        let mut float_items = reserved(flow.floating.as_ref().map_or(0, |f| f.items.len()))
            .map_err(|e| error(stage, e))?;
        if let Some(f) = &flow.floating {
            let mut glyph = 0;
            for item in &f.items {
                let scalar = item.scalar_index as usize;
                if scalar == source.input.len() {
                    glyph = source.logical.len();
                } else {
                    let input = source.input[scalar].input_index as i32;
                    while glyph < source.logical.len() && source.logical[glyph].cluster < input {
                        glyph += 1;
                    }
                    if glyph == source.logical.len() || source.logical[glyph].cluster != input {
                        return Err(invalid(NativeParagraphStage::ClusterMap));
                    }
                }
                float_items.push(NativeFloatingItem {
                    glyph_index: glyph as u32,
                    width: item.width,
                    height: item.height,
                    alignment: item.alignment,
                });
            }
        }
        if source.input.is_empty() && float_items.is_empty() {
            let result = NativeExclusionFlowResult {
                height: flow.origin_y,
                ..NativeExclusionFlowResult::default()
            };
            let floating_result = flow.floating.as_ref().map(|_| NativeFloatingFlowResult {
                text: result,
                height: flow.origin_y,
                ..NativeFloatingFlowResult::default()
            });
            let metrics = NativeTextLayoutMetrics {
                content_height: flow.origin_y as f32,
                ..NativeTextLayoutMetrics::default()
            };
            let intrinsic = options
                .measure_intrinsic_widths
                .then(NativeTextIntrinsicWidths::default);
            return Ok(Arc::new(Self {
                source,
                options,
                flow,
                float_items,
                glyphs: Vec::new(),
                owners: Vec::new(),
                lines: Vec::new(),
                placements: Vec::new(),
                floats: Vec::new(),
                result,
                floating_result,
                metrics,
                intrinsic,
            }));
        }
        let scales = if source.styles.is_empty() {
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
                    scales,
                    options.layout,
                    options.tabs,
                )
                .map_err(|e| error(stage, e))?,
            )
        } else {
            None
        };
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
        let n = source.logical.len();
        let line_capacity = if n == 0 && !float_items.is_empty() {
            1
        } else {
            n
        };
        let e = flow.exclusions.len() + float_items.len();
        let mut positions =
            filled(n, NativePositionedTextGlyph::default()).map_err(|e| error(stage, e))?;
        let mut lines = filled(line_capacity, NativePositionedTextLine::default())
            .map_err(|e| error(stage, e))?;
        let mut placements = filled(line_capacity, NativeFragmentPlacement::default())
            .map_err(|e| error(stage, e))?;
        let mut floats = filled(float_items.len(), NativeFloatingPlacement::default())
            .map_err(|e| error(stage, e))?;
        let mut collisions =
            filled(e, NativeExclusionRectangle::default()).map_err(|e| error(stage, e))?;
        let mut obstacles =
            filled(e, NativeLineInterval::default()).map_err(|e| error(stage, e))?;
        let mut intervals =
            filled(e + 1, NativeLineInterval::default()).map_err(|e| error(stage, e))?;
        let mut fragments =
            filled(e + 1, NativeLineFragment::default()).map_err(|e| error(stage, e))?;
        let mut groups =
            filled(n, NativeVisualClusterGroup::default()).map_err(|e| error(stage, e))?;
        let mut indices = filled(n, 0u32).map_err(|e| error(stage, e))?;
        let mut tabs = filled(if options.tabs.interval > 0.0 { n } else { 0 }, 0f32)
            .map_err(|e| error(stage, e))?;
        let input = NativeMeasuredInput {
            glyphs: &source.logical,
            breaks: &source.breaks,
            levels: &source.levels,
            scales,
            paragraph_level: source.paragraph_level,
            options: options.layout,
            tabs: options.tabs,
            justification: &justification,
            item_metrics: &source.metrics,
            continues: false,
        };
        let mut scratch = NativeExclusionScratch {
            obstacles: &mut obstacles,
            intervals: &mut intervals,
            fragments: &mut fragments,
            layout: NativeLayoutScratch {
                visual_groups: &mut groups,
                visual_indices: &mut indices,
                tab_advances: &mut tabs,
            },
        };
        let output = NativeFragmentOutput {
            glyphs: &mut positions,
            lines: &mut lines,
            placements: &mut placements,
        };
        let (result, floating_result) = if let Some(f) = &flow.floating {
            let result = native_layout_floating_into(
                input,
                flow.origin_y,
                f.empty_metrics,
                &float_items,
                &flow.exclusions,
                &mut collisions,
                &mut scratch,
                output,
                &mut floats,
                flow.maximum_attempts,
            )
            .map_err(|e| error(stage, e))?;
            (result.text, Some(result))
        } else {
            (
                native_layout_excluded_into(
                    input,
                    flow.origin_y,
                    &flow.exclusions,
                    &mut scratch,
                    output,
                    flow.maximum_attempts,
                )
                .map_err(|e| error(stage, e))?,
                None,
            )
        };
        positions.truncate(result.glyph_count as usize);
        lines.truncate(result.fragment_count as usize);
        placements.truncate(result.fragment_count as usize);
        floats.truncate(floating_result.map_or(0, |r| r.float_count as usize));
        let metrics =
            native_measure_fragment_lines(&lines, &placements, options.layout.maximum_width)
                .map_err(|e| error(stage, e))?;
        let mut glyphs = reserved(positions.len()).map_err(|e| error(stage, e))?;
        let mut owners = reserved(positions.len()).map_err(|e| error(stage, e))?;
        for g in positions {
            let i = g.glyph_index as usize;
            let owner = *source.owners.get(i).ok_or_else(|| invalid(stage))?;
            let font_index = source.font_indices[i];
            glyphs.push(NativeParagraphPositionedRecord {
                glyph_index: g.glyph_index,
                glyph_id: g.glyph_id,
                font_index,
                cluster: g.cluster,
                x: g.position[0],
                y: g.position[1],
                advance_x: g.advance[0],
                advance_y: g.advance[1],
            });
            owners.push(owner);
        }
        Ok(Arc::new(Self {
            source,
            options,
            flow,
            float_items,
            glyphs,
            owners,
            lines,
            placements,
            floats,
            result,
            floating_result,
            metrics,
            intrinsic,
        }))
    }
    pub fn reflow(
        &self,
        options: NativeDesignOptions,
        flow: &NativeParagraphFlow,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        Self::from_source(self.source.clone(), options, flow)
    }
    pub fn source(&self) -> &Arc<NativeDesignSource> {
        &self.source
    }
    pub fn options(&self) -> NativeDesignOptions {
        self.options
    }
    pub fn flow(&self) -> &NativeParagraphFlow {
        &self.flow
    }
    pub fn floating_items(&self) -> &[NativeFloatingItem] {
        &self.float_items
    }
    pub fn glyphs(&self) -> &[NativeParagraphPositionedRecord] {
        &self.glyphs
    }
    pub fn owners(&self) -> &[NativeDesignOccurrence] {
        &self.owners
    }
    pub fn lines(&self) -> &[NativePositionedTextLine] {
        &self.lines
    }
    pub fn placements(&self) -> &[NativeFragmentPlacement] {
        &self.placements
    }
    pub fn floating_placements(&self) -> &[NativeFloatingPlacement] {
        &self.floats
    }
    pub fn result(&self) -> NativeExclusionFlowResult {
        self.result
    }
    pub fn floating_result(&self) -> Option<NativeFloatingFlowResult> {
        self.floating_result
    }
    pub fn metrics(&self) -> NativeTextLayoutMetrics {
        self.metrics
    }
    pub fn intrinsic_widths(&self) -> Option<NativeTextIntrinsicWidths> {
        self.intrinsic
    }
    pub fn copy_into(
        &self,
        glyphs: &mut [NativeParagraphPositionedRecord],
        owners: &mut [NativeDesignOccurrence],
        lines: &mut [NativePositionedTextLine],
        placements: &mut [NativeFragmentPlacement],
        floats: &mut [NativeFloatingPlacement],
    ) -> Result<[usize; 3], FontError> {
        if glyphs.len() < self.glyphs.len()
            || owners.len() < self.owners.len()
            || lines.len() < self.lines.len()
            || placements.len() < self.placements.len()
            || floats.len() < self.floats.len()
        {
            return Err(FontError::Capacity);
        }
        glyphs[..self.glyphs.len()].copy_from_slice(&self.glyphs);
        owners[..self.owners.len()].copy_from_slice(&self.owners);
        lines[..self.lines.len()].copy_from_slice(&self.lines);
        placements[..self.placements.len()].copy_from_slice(&self.placements);
        floats[..self.floats.len()].copy_from_slice(&self.floats);
        Ok([self.glyphs.len(), self.lines.len(), self.floats.len()])
    }
}
