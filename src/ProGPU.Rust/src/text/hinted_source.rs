//! Original owned native hinted-run fitting and captured source geometry.
//! Actual device runs retain their complete producer batch and source metadata;
//! neither the managed shaper nor an inferred hinted provider is substituted.
use super::unicode::line_break::LineBreakKind;
use super::{
    ShapingGlyph,
    native_layout::*,
    native_pipeline::{NativeDeviceRun, NativeHintedProjection},
};
use crate::{
    font::{FontError, layout::GposDirection},
    path::Segment,
};
use std::sync::Arc;
mod geometry;
pub use geometry::*;

/// Caller-resolved metadata in the original retained run's glyph order. Only
/// the used prefix is copied, preserving all unused caller tails unchanged.
#[derive(Clone, Copy)]
pub struct NativeHintedLayoutMetadata<'a> {
    /// Original break classification, including final/mandatory boundaries.
    pub breaks: &'a [LineBreakKind],
    /// Original full source bidi levels, including parity of this native run.
    pub bidi_levels: &'a [i8],
    /// Exact exclusive source cluster ends.
    pub cluster_ends: &'a [i32],
    /// Mandatory caller-resolved logical ascent/descent, never driver rounding.
    pub item_metrics: &'a [NativeTextItemMetrics],
    /// Original optional complete source justification classes.
    pub justification: &'a [NativeTextJustification],
    /// Original paragraph direction, exactly zero or one.
    pub paragraph_level: i8,
}
/// Complete immutable original hinted fitting snapshot. Maximum-lines output
/// retains the original whole run, including every unpositioned descriptor.
#[derive(Debug)]
pub struct NativeHintedTextLayout {
    run: Arc<NativeDeviceRun>,
    units_per_pixel: f32,
    paragraph_level: i8,
    options: NativeTextLayoutOptions,
    logical: Vec<ShapingGlyph>,
    logical_run_indices: Vec<u32>,
    breaks: Vec<LineBreakKind>,
    logical_levels: Vec<i8>,
    logical_cluster_ends: Vec<i32>,
    justification: Vec<NativeTextJustification>,
    item_metrics: Vec<NativeTextItemMetrics>,
    glyphs: Vec<NativePositionedTextGlyph>,
    descriptors: Vec<u32>,
    levels: Vec<i8>,
    cluster_ends: Vec<i32>,
    lines: Vec<NativePositionedTextLine>,
    line_origins: Vec<f32>,
    metrics: NativeTextLayoutMetrics,
}
fn filled<T: Clone>(count: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    output.resize(count, value);
    Ok(output)
}
fn reserved<T>(count: usize) -> Result<Vec<T>, FontError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    Ok(output)
}
impl NativeHintedTextLayout {
    /// Apply the original native measured writer to one actual device run.
    /// `options.scale` must be one: the sole conversion is units/pixel / 64.
    /// Logical cluster restoration changes only original Y-up metric signs.
    pub fn new(
        run: Arc<NativeDeviceRun>,
        metadata: NativeHintedLayoutMetadata<'_>,
        logical_units_per_physical_pixel: f32,
        options: NativeTextLayoutOptions,
    ) -> Result<Self, FontError> {
        let count = run.glyphs().len();
        if run.batch().font().data().is_empty()
            || !matches!(
                run.direction(),
                GposDirection::LeftToRight | GposDirection::RightToLeft
            )
            || count > u32::MAX as usize
            || run.descriptor_indices().len() != count
            || run.source_descriptor_count() > run.batch().glyphs().len()
            || !matches!(metadata.paragraph_level, 0 | 1)
            || options.direction != run.direction()
            || options.scale != 1.0
            || options.trimming != NativeTextTrimming::None
            || !logical_units_per_physical_pixel.is_finite()
            || logical_units_per_physical_pixel <= 0.0
        {
            return Err(FontError::InvalidArgument);
        }
        if metadata.breaks.len() < count
            || metadata.bidi_levels.len() < count
            || metadata.cluster_ends.len() < count
            || metadata.item_metrics.len() < count
            || !metadata.justification.is_empty() && metadata.justification.len() < count
        {
            return Err(FontError::Capacity);
        }
        let scale = logical_units_per_physical_pixel / 64.0;
        if !scale.is_finite() || scale <= 0.0 {
            return Err(FontError::InvalidArgument);
        }
        let parity = i8::from(run.direction() == GposDirection::RightToLeft);
        for (index, glyph) in run.glyphs().iter().enumerate() {
            let descriptor = run.descriptor_indices()[index] as usize;
            if descriptor >= run.source_descriptor_count()
                || run.batch().glyphs()[descriptor].glyph_id != glyph.glyph_id
                || glyph.cluster < 0
                || glyph.advance_y == i32::MIN
                || glyph.offset_y == i32::MIN
                || metadata.cluster_ends[index] <= glyph.cluster
                || !(0..=125).contains(&metadata.bidi_levels[index])
                || metadata.bidi_levels[index] & 1 != parity
            {
                return Err(FontError::InvalidArgument);
            }
        }
        let mut result = Self {
            run,
            units_per_pixel: logical_units_per_physical_pixel,
            paragraph_level: metadata.paragraph_level,
            options: NativeTextLayoutOptions { scale, ..options },
            logical: reserved(count)?,
            logical_run_indices: reserved(count)?,
            breaks: reserved(count)?,
            logical_levels: reserved(count)?,
            logical_cluster_ends: reserved(count)?,
            justification: reserved(if metadata.justification.is_empty() {
                0
            } else {
                count
            })?,
            item_metrics: reserved(count)?,
            glyphs: Vec::new(),
            descriptors: Vec::new(),
            levels: Vec::new(),
            cluster_ends: Vec::new(),
            lines: Vec::new(),
            line_origins: Vec::new(),
            metrics: Default::default(),
        };
        let mut append = |first: usize, last: usize| {
            for index in first..last {
                let mut glyph = result.run.glyphs()[index];
                glyph.advance_y = -glyph.advance_y;
                glyph.offset_y = -glyph.offset_y;
                result.logical.push(glyph);
                result.logical_run_indices.push(index as u32);
                result.breaks.push(metadata.breaks[index]);
                result.logical_levels.push(metadata.bidi_levels[index]);
                result
                    .logical_cluster_ends
                    .push(metadata.cluster_ends[index]);
                result.item_metrics.push(metadata.item_metrics[index]);
                if !metadata.justification.is_empty() {
                    result.justification.push(metadata.justification[index]);
                }
            }
        };
        if parity == 0 {
            append(0, count);
        } else {
            let mut last = count;
            while last != 0 {
                let mut first = last - 1;
                // Use the retained source reference: cluster groups reverse,
                // while each group's original internal shaper order survives.
                while first != 0
                    && result.run.glyphs()[first - 1].cluster
                        == result.run.glyphs()[last - 1].cluster
                {
                    first -= 1;
                }
                append(first, last);
                last = first;
            }
        }
        for index in 1..count {
            let previous = result.logical[index - 1].cluster;
            let current = result.logical[index].cluster;
            if current < previous
                || current > previous && result.logical_cluster_ends[index - 1] > current
                || current == previous
                    && (result.logical_cluster_ends[index]
                        != result.logical_cluster_ends[index - 1]
                        || result.logical_levels[index] != result.logical_levels[index - 1])
            {
                return Err(FontError::InvalidArgument);
            }
        }
        let scales = filled(count, scale)?;
        let needed = native_layout_requirements(
            &result.logical,
            &result.breaks,
            &scales,
            result.options,
            NativeTextTabs::default(),
        )?;
        result.glyphs = filled(needed.glyph_capacity, NativePositionedTextGlyph::default())?;
        result.lines = filled(needed.line_capacity, NativePositionedTextLine::default())?;
        let mut groups = filled(needed.glyph_capacity, NativeVisualClusterGroup::default())?;
        let mut indices = filled(needed.glyph_capacity, 0)?;
        let [glyph_count, line_count] = native_layout_measured_into(
            NativeMeasuredInput {
                glyphs: &result.logical,
                breaks: &result.breaks,
                levels: &result.logical_levels,
                scales: &scales,
                paragraph_level: metadata.paragraph_level,
                options: result.options,
                tabs: NativeTextTabs::default(),
                justification: &result.justification,
                item_metrics: &result.item_metrics,
                continues: false,
            },
            NativeLayoutScratch {
                visual_groups: &mut groups,
                visual_indices: &mut indices,
                tab_advances: &mut [],
            },
            NativeMeasuredOutput {
                glyphs: &mut result.glyphs,
                lines: &mut result.lines,
                retained: None,
            },
        )?;
        result.glyphs.truncate(glyph_count);
        result.lines.truncate(line_count);
        result.retain_line_origins()?;
        result.descriptors = reserved(glyph_count)?;
        result.levels = reserved(glyph_count)?;
        result.cluster_ends = reserved(glyph_count)?;
        for glyph in &mut result.glyphs {
            if glyph.glyph_index as usize >= result.logical_run_indices.len()
                || !glyph
                    .position
                    .iter()
                    .chain(&glyph.advance)
                    .all(|v| v.is_finite())
                || !(glyph.position[0] + glyph.advance[0]).is_finite()
                || !(glyph.position[1] + glyph.advance[1]).is_finite()
            {
                return Err(FontError::InvalidArgument);
            }
            let logical = glyph.glyph_index as usize;
            let original = result.logical_run_indices[logical] as usize;
            result
                .descriptors
                .push(result.run.descriptor_indices()[original]);
            result.levels.push(result.logical_levels[logical]);
            result
                .cluster_ends
                .push(result.logical_cluster_ends[logical]);
            glyph.glyph_index = original as u32;
        }
        result.metrics = native_measure_text_lines(&result.lines, options.maximum_width, true)?;
        Ok(result)
    }
    fn retain_line_origins(&mut self) -> Result<(), FontError> {
        self.line_origins = filled(self.lines.len(), 0.0)?;
        for (index, line) in self.lines.iter().enumerate() {
            let justified = line.flags & 1 != 0;
            let mut trailing = 0.0;
            if justified {
                if line.glyph_count == 0 {
                    return Err(FontError::InvalidArgument);
                }
                let mut first = self.logical.len();
                let mut last = 0;
                for glyph in &self.glyphs
                    [line.glyph_start as usize..(line.glyph_start + line.glyph_count) as usize]
                {
                    first = first.min(glyph.glyph_index as usize);
                    last = last.max(glyph.glyph_index as usize + 1);
                }
                if last > self.logical.len() || last - first != line.glyph_count as usize {
                    return Err(FontError::InvalidArgument);
                }
                let mut content_end = last;
                while content_end > first
                    && self.justification[content_end - 1] != NativeTextJustification::Content
                {
                    content_end -= 1;
                }
                for glyph in &self.logical[content_end..last] {
                    trailing += glyph.advance_x as f32 * self.options.scale;
                }
            }
            let mut origin = if justified { -trailing } else { 0.0 };
            let shift = super::native_layout::alignment_shift(self.options, line.width);
            if shift > 0.0 {
                origin += shift;
            }
            if !origin.is_finite() {
                return Err(FontError::InvalidArgument);
            }
            self.line_origins[index] = origin;
        }
        Ok(())
    }
    /// Exact original whole native run retained across maximum-lines truncation.
    pub fn run(&self) -> &Arc<NativeDeviceRun> {
        &self.run
    }
    /// Sole original physical-to-logical conversion.
    pub fn logical_units_per_physical_pixel(&self) -> f32 {
        self.units_per_pixel
    }
    /// Original paragraph direction level.
    pub fn paragraph_level(&self) -> i8 {
        self.paragraph_level
    }
    /// Original fitting options with scale converted to logical/device-26.6 units.
    pub fn options(&self) -> NativeTextLayoutOptions {
        self.options
    }
    /// Original logical restored shaping records, preserving all flags/IDs.
    pub fn logical_glyphs(&self) -> &[ShapingGlyph] {
        &self.logical
    }
    /// Original logical-to-run mapping, never a glyph-ID search.
    pub fn logical_run_indices(&self) -> &[u32] {
        &self.logical_run_indices
    }
    /// Original logical breaks retained for source-aware reflow.
    pub fn breaks(&self) -> &[LineBreakKind] {
        &self.breaks
    }
    /// Original full logical source levels, independent of per-line L1 resets.
    pub fn logical_bidi_levels(&self) -> &[i8] {
        &self.logical_levels
    }
    /// Original logical source cluster ends.
    pub fn logical_cluster_ends(&self) -> &[i32] {
        &self.logical_cluster_ends
    }
    /// Original caller-resolved line item metrics in restored order.
    pub fn item_metrics(&self) -> &[NativeTextItemMetrics] {
        &self.item_metrics
    }
    /// Original source justification inventory; empty retains absent classification.
    pub fn justification_classes(&self) -> &[NativeTextJustification] {
        &self.justification
    }
    /// Actual positioned visual occurrences, with original run indices.
    pub fn glyphs(&self) -> &[NativePositionedTextGlyph] {
        &self.glyphs
    }
    /// Exact capture descriptor for every positioned occurrence, including no-ink.
    pub fn descriptor_indices(&self) -> &[u32] {
        &self.descriptors
    }
    /// Original source bidi level per positioned occurrence.
    pub fn bidi_levels(&self) -> &[i8] {
        &self.levels
    }
    /// Exact source end per positioned occurrence.
    pub fn cluster_ends(&self) -> &[i32] {
        &self.cluster_ends
    }
    /// Original fitted lines.
    pub fn lines(&self) -> &[NativePositionedTextLine] {
        &self.lines
    }
    /// Original writer content origins, independent of glyph ink.
    pub fn line_origins(&self) -> &[f32] {
        &self.line_origins
    }
    /// Original content/measured dimensions.
    pub fn metrics(&self) -> NativeTextLayoutMetrics {
        self.metrics
    }
}
