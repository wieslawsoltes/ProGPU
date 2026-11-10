//! Original native float measured writer with ordered double line-top accumulation.
use super::*;

/// Complete borrowed native layout inputs; no source storage survives the call.
#[derive(Clone, Copy)]
pub struct NativeMeasuredInput<'a> {
    /// Original logical-order shaping records, with Y already converted down.
    pub glyphs: &'a [ShapingGlyph],
    /// Original per-glyph break classification.
    pub breaks: &'a [LineBreakKind],
    /// Full resolved source bidi levels, one per glyph.
    pub levels: &'a [i8],
    /// Complete independent scale inventory, or empty for options.scale.
    pub scales: &'a [f32],
    /// Original paragraph direction level, exactly zero or one.
    pub paragraph_level: i8,
    /// Original raw float fitting policy.
    pub options: NativeTextLayoutOptions,
    /// Original tab-grid and emergency-break policy.
    pub tabs: NativeTextTabs,
    /// Complete original classification, or empty when justification is unavailable.
    pub justification: &'a [NativeTextJustification],
    /// Complete caller-resolved line metrics, or empty for fixed-height writing.
    pub item_metrics: &'a [NativeTextItemMetrics],
    /// Original internal continuation flag for justification of a final fragment.
    pub continues: bool,
}
/// Original caller scratch, used only in bounded prefixes. Safe borrows establish
/// complete source/output/scratch disjointness without unsafe pointer admission.
pub struct NativeLayoutScratch<'a> {
    /// Original visual cluster groups, sized to the source glyph capacity.
    pub visual_groups: &'a mut [NativeVisualClusterGroup],
    /// Original visual-to-logical indices, sized to source glyph capacity.
    pub visual_indices: &'a mut [u32],
    /// Original logical tab advances; needed only when tabs.interval is positive.
    pub tab_advances: &'a mut [f32],
}
/// Optional original retained writer metadata. It observes the actual emission
/// loop and L1/L2 groups; it never reconstructs bidi or line origins afterward.
pub struct NativeLayoutRetained<'a> {
    /// Full actual resolved output levels.
    pub bidi_levels: &'a mut [i8],
    /// Actual original content pen origins, independent of glyph ink offsets.
    pub line_origins: &'a mut [f32],
    /// Optional complete double-top/ascent frames; empty omits frame storage.
    pub line_frames: &'a mut [NativeTextLineFrame],
}
/// Original caller output storage. Failed writing can change used prefixes, as
/// in the native low-level writer; only a successful returned count publishes them.
pub struct NativeMeasuredOutput<'a> {
    /// Capacity for complete original positioned glyphs.
    pub glyphs: &'a mut [NativePositionedTextGlyph],
    /// Capacity for original fitted lines.
    pub lines: &'a mut [NativePositionedTextLine],
    /// Optional retained emission metadata; synthetic trimming remains unadmitted.
    pub retained: Option<NativeLayoutRetained<'a>>,
}
pub(super) fn envelope(
    metrics: &[NativeTextItemMetrics],
) -> Result<NativeTextItemMetrics, FontError> {
    let mut maximum = wide::f32x4::splat(0.0);
    for metric in metrics {
        if !metric.ascent.is_finite()
            || metric.ascent < 0.0
            || !metric.descent.is_finite()
            || metric.descent < 0.0
        {
            return Err(FontError::InvalidArgument);
        }
        maximum = maximum.max(wide::f32x4::from([metric.ascent, metric.descent, 0.0, 0.0]));
    }
    let maximum = maximum.to_array();
    Ok(NativeTextItemMetrics {
        ascent: maximum[0],
        descent: maximum[1],
    })
}
fn opportunity(
    glyphs: &[ShapingGlyph],
    classes: &[NativeTextJustification],
    index: usize,
    first: usize,
    last: usize,
) -> bool {
    index >= first
        && index < last
        && classes[index] == NativeTextJustification::WordSpace
        && (index + 1 == glyphs.len() || glyphs[index].cluster != glyphs[index + 1].cluster)
        && safe_break(glyphs, index + 1)
}
/// Execute the complete original float measured/justified/tabbed writer. Fitting,
/// original L1/L2 groups, final residual expansion, minimum height, alignment and
/// source occurrence indices retain their native order. Source-double fitting,
/// exclusions and floating items have separate independent contracts.
pub fn native_layout_measured_into(
    input: NativeMeasuredInput<'_>,
    scratch: NativeLayoutScratch<'_>,
    mut output: NativeMeasuredOutput<'_>,
) -> Result<[usize; 2], FontError> {
    let NativeMeasuredInput {
        glyphs,
        breaks,
        levels,
        scales,
        paragraph_level: paragraph,
        options,
        tabs,
        justification: classes,
        item_metrics: metrics,
        continues,
    } = input;
    if output.retained.is_some() && options.trimming != NativeTextTrimming::None {
        return Err(FontError::InvalidArgument);
    }
    if !metrics.is_empty() {
        if metrics.len() != glyphs.len() || options.trimming != NativeTextTrimming::None {
            return Err(FontError::InvalidArgument);
        }
        let peak = envelope(metrics)?;
        let height =
            f64::from(options.line_height).max(f64::from(peak.ascent) + f64::from(peak.descent));
        if height * glyphs.len() as f64 > f64::from(f32::MAX) {
            return Err(FontError::InvalidArgument);
        }
    }
    if !classes.is_empty() && classes.len() != glyphs.len() {
        return Err(FontError::InvalidArgument);
    }
    let needed = native_layout_requirements(glyphs, breaks, scales, options, tabs)?;
    if levels.len() != glyphs.len()
        || !matches!(paragraph, 0 | 1)
        || levels.iter().any(|&v| !(0..=125).contains(&v))
    {
        return Err(FontError::InvalidArgument);
    }
    if (tabs.interval > 0.0 && scratch.tab_advances.len() < needed.glyph_capacity)
        || scratch.visual_groups.len() < needed.glyph_capacity
        || scratch.visual_indices.len() < needed.glyph_capacity
        || output.glyphs.len() < needed.glyph_capacity
        || output.lines.len() < needed.line_capacity
        || output.retained.as_ref().is_some_and(|retained| {
            retained.bidi_levels.len() < needed.glyph_capacity
                || retained.line_origins.len() < needed.line_capacity
                || !retained.line_frames.is_empty()
                    && retained.line_frames.len() < needed.line_capacity
        })
    {
        return Err(FontError::Capacity);
    }
    let mut start = 0;
    let mut cursor = 0;
    let mut line_count = 0;
    let mut measured_top = 0.0f64;
    while start < glyphs.len() && line_count < needed.line_capacity {
        let final_allowed =
            options.maximum_lines != 0 && line_count + 1 >= options.maximum_lines as usize;
        let line = scan::line(glyphs, breaks, scales, options, tabs, start, final_allowed);
        let should_trim = options.trimming != NativeTextTrimming::None
            && (line.clipped
                || final_allowed
                    && options.collapse_width >= 0.0
                    && line.width > options.collapse_width
                || final_allowed && line.end < glyphs.len());
        let (visible_end, content_width) = if should_trim {
            scan::trim(
                glyphs, breaks, scales, options, tabs, start, line.end, line.width,
            )
        } else {
            (line.end, line.width)
        };
        let logical = &glyphs[start..visible_end];
        let visual_count = native_visual_indices_into(
            logical,
            &levels[start..visible_end],
            paragraph,
            scratch.visual_groups,
            scratch.visual_indices,
            false,
        )?;
        let output_start = cursor;
        if tabs.interval > 0.0 {
            let mut logical_width = 0.0;
            for index in start..visible_end {
                scratch.tab_advances[index] = scan::advance(
                    glyphs[index],
                    scale_at(scales, index, options),
                    logical_width,
                    tabs,
                );
                logical_width += scratch.tab_advances[index];
            }
        }
        let mut justify_start = start;
        let mut justify_end = visible_end;
        let mut opportunities = 0;
        let mut expansion = 0.0;
        let mut trailing_width = 0.0;
        if options.alignment == NativeTextAlignment::Justify
            && !classes.is_empty()
            && !should_trim
            && !line.clipped
            && (line.end < glyphs.len() || continues)
            && breaks[line.end - 1] != LineBreakKind::Mandatory
            && options.maximum_width > 0.0
        {
            while justify_start < justify_end
                && classes[justify_start] != NativeTextJustification::Content
            {
                justify_start += 1;
            }
            while justify_end > justify_start
                && classes[justify_end - 1] != NativeTextJustification::Content
            {
                justify_end -= 1;
            }
            for index in start..visible_end {
                if glyphs[index].glyph_id == u32::MAX {
                    justify_start = index + 1;
                }
                if index >= justify_end {
                    trailing_width += if tabs.interval > 0.0 {
                        scratch.tab_advances[index]
                    } else {
                        glyphs[index].advance_x as f32 * scale_at(scales, index, options)
                    };
                }
            }
            while justify_start < justify_end
                && classes[justify_start] != NativeTextJustification::Content
            {
                justify_start += 1;
            }
            for index in justify_start..justify_end {
                opportunities += usize::from(opportunity(
                    glyphs,
                    classes,
                    index,
                    justify_start,
                    justify_end,
                ));
            }
            let available = options.maximum_width - (content_width - trailing_width);
            if opportunities != 0 && available > 0.0 && (content_width + available).is_finite() {
                expansion = available;
            }
        }
        let mut height = options.line_height;
        let mut baseline = line_count as f32 * options.line_height;
        let mut ascent = 0.0;
        if !metrics.is_empty() {
            let line_metrics = envelope(&metrics[start..visible_end])?;
            height = max(height, line_metrics.ascent + line_metrics.descent);
            baseline = (measured_top + f64::from(line_metrics.ascent)) as f32;
            ascent = line_metrics.ascent;
        }
        let sign_width = if should_trim {
            options.ellipsis_advance * options.scale
        } else {
            0.0
        };
        let leading_sign = should_trim && options.collapse_width >= 0.0 && paragraph & 1 != 0;
        let mut cursor_x = if leading_sign {
            sign_width
        } else if expansion > 0.0 && paragraph & 1 != 0 {
            -trailing_width
        } else {
            0.0
        };
        let initial_cursor_x = cursor_x;
        let mut cursor_y = baseline;
        let mut distributed = 0.0;
        let mut remaining = opportunities;
        let sign_cluster = if options.collapse_width >= 0.0 && visible_end < glyphs.len() {
            glyphs[visible_end].cluster
        } else if visible_end > start {
            glyphs[visible_end - 1].cluster
        } else {
            glyphs[start].cluster
        };
        if leading_sign {
            output.glyphs[cursor] = NativePositionedTextGlyph {
                glyph_index: u32::MAX,
                glyph_id: options.ellipsis_glyph_id,
                cluster: sign_cluster,
                position: [0.0, baseline],
                advance: [sign_width, 0.0],
            };
            cursor += 1;
        }
        let mut retained_group = 0;
        let mut retained_end = if output.retained.is_some() && visual_count != 0 {
            scratch.visual_groups[0].glyph_count as usize
        } else {
            0
        };
        for visual in 0..visual_count {
            let index = start + scratch.visual_indices[visual] as usize;
            let glyph = glyphs[index];
            let mut lane = scale_metrics(glyph, scale_at(scales, index, options));
            if tabs.interval > 0.0 {
                lane[0] = scratch.tab_advances[index];
            }
            if expansion > 0.0 && opportunity(glyphs, classes, index, justify_start, justify_end) {
                remaining -= 1;
                let extra = if remaining == 0 {
                    expansion - distributed
                } else {
                    expansion / opportunities as f32
                };
                lane[0] += extra;
                distributed += extra;
            }
            if let Some(retained) = output.retained.as_mut() {
                if visual == retained_end {
                    retained_group += 1;
                    retained_end += scratch.visual_groups[retained_group].glyph_count as usize;
                }
                retained.bidi_levels[cursor] = scratch.visual_groups[retained_group].bidi_level;
            }
            output.glyphs[cursor] = NativePositionedTextGlyph {
                glyph_index: index as u32,
                glyph_id: glyph.glyph_id,
                cluster: glyph.cluster,
                position: [cursor_x + lane[2], cursor_y + lane[3]],
                advance: [lane[0], lane[1]],
            };
            cursor += 1;
            cursor_x += lane[0];
            cursor_y += lane[1];
        }
        if should_trim && !leading_sign {
            output.glyphs[cursor] = NativePositionedTextGlyph {
                glyph_index: u32::MAX,
                glyph_id: options.ellipsis_glyph_id,
                cluster: sign_cluster,
                position: [cursor_x, cursor_y],
                advance: [options.ellipsis_advance * options.scale, 0.0],
            };
            cursor += 1;
        }
        let width = content_width
            + expansion
            + if should_trim {
                options.ellipsis_advance * options.scale
            } else {
                0.0
            };
        let shift = alignment_shift(options, width);
        if let Some(retained) = output.retained.as_mut() {
            retained.line_origins[line_count] = if shift > 0.0 {
                initial_cursor_x + shift
            } else {
                initial_cursor_x
            };
        }
        if shift > 0.0 {
            for glyph in &mut output.glyphs[output_start..cursor] {
                glyph.position[0] += shift;
            }
        }
        let input_end = if visible_end < glyphs.len() {
            glyphs[visible_end].cluster
        } else {
            glyphs[visible_end - 1]
                .cluster
                .checked_add(1)
                .ok_or(FontError::InvalidArgument)?
        };
        output.lines[line_count] = NativePositionedTextLine {
            glyph_start: output_start as u32,
            glyph_count: (cursor - output_start) as u32,
            input_start: glyphs[start].cluster,
            input_end,
            width,
            baseline_y: baseline,
            height,
            clipped: should_trim || line.clipped || final_allowed && line.end < glyphs.len(),
            flags: u8::from(expansion > 0.0 && paragraph & 1 != 0),
        };
        if let Some(retained) = output
            .retained
            .as_mut()
            .filter(|r| !r.line_frames.is_empty())
        {
            retained.line_frames[line_count] = NativeTextLineFrame {
                top: if metrics.is_empty() {
                    f64::from(baseline)
                } else {
                    measured_top
                },
                ascent,
                measured: !metrics.is_empty(),
            };
        }
        line_count += 1;
        measured_top += f64::from(height);
        start = line.end;
        if final_allowed {
            break;
        }
    }
    Ok([cursor, line_count])
}
