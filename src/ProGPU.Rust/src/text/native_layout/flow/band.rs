//! Native shaping-safe interval fitting and complete resolved-height band writer.
use super::*;
fn valid_scales(glyphs: &[ShapingGlyph], scales: &[f32]) -> bool {
    scales.is_empty()
        || scales.len() == glyphs.len()
            && glyphs.iter().zip(scales).all(|(&g, &s)| {
                s.is_finite() && s > 0.0 && scale_metrics(g, s).into_iter().all(f32::is_finite)
            })
}
pub(super) fn fit(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    start: usize,
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
    band: NativeExclusionRectangle,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    intervals: &mut [NativeLineInterval],
    fragments: &mut [NativeLineFragment],
    validated: bool,
) -> Result<(usize, usize, f32), FontError> {
    validate_options(options)?;
    if glyphs.len() > u32::MAX as usize
        || start > glyphs.len()
        || breaks.len() != glyphs.len()
        || options.trimming != NativeTextTrimming::None
        || !validated && !valid_scales(glyphs, scales)
        || !tabs.interval.is_finite()
        || tabs.interval < 0.0
        || !tabs.origin.is_finite()
        || !safe_break(glyphs, start)
    {
        return Err(FontError::InvalidArgument);
    }
    if exclusions.len() >= fragments.len() {
        return Err(FontError::Capacity);
    }
    let (count, retry) = native_resolve_text_intervals_into(band, exclusions, scratch, intervals)?;
    let rtl = options.direction == GposDirection::RightToLeft;
    let mut next = start;
    let mut written = 0;
    for i in 0..count {
        if next >= glyphs.len() {
            break;
        }
        let interval = intervals[if rtl { count - 1 - i } else { i }];
        let mut local = options;
        local.maximum_width = interval.right - interval.left;
        let mut local_tabs = tabs;
        local_tabs.origin += if rtl {
            band.right - interval.right
        } else {
            interval.left - band.left
        };
        if !local.maximum_width.is_finite() || !local_tabs.origin.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let fitted = scan::line(glyphs, breaks, scales, local, local_tabs, next, false);
        if !fitted.width.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let full_width = interval.left == band.left && interval.right == band.right;
        if fitted.width > local.maximum_width && !full_width {
            continue;
        }
        fragments[written] = NativeLineFragment {
            glyph_start: next as u32,
            glyph_count: (fitted.end - next) as u32,
            left: interval.left,
            width: local.maximum_width,
            content_width: fitted.width,
        };
        written += 1;
        next = fitted.end;
        if next != 0 && breaks[next - 1] == LineBreakKind::Mandatory {
            break;
        }
    }
    Ok((written, next, retry))
}
/// Fit only: caller must resolve the actual metric envelope before accepting a
/// band. Returned retry Y is an obstacle bottom, never a replacement row height.
pub fn native_fit_exclusion_band_into(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    start: u32,
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
    band: NativeExclusionRectangle,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut [NativeLineInterval],
    intervals: &mut [NativeLineInterval],
    fragments: &mut [NativeLineFragment],
) -> Result<(usize, u32, f32), FontError> {
    fit(
        glyphs,
        breaks,
        scales,
        start as usize,
        options,
        tabs,
        band,
        exclusions,
        scratch,
        intervals,
        fragments,
        false,
    )
    .map(|(n, g, y)| (n, g as u32, y))
}
pub(super) fn layout(
    input: NativeMeasuredInput<'_>,
    start: usize,
    band: NativeExclusionRectangle,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut NativeExclusionScratch<'_>,
    output: &mut [NativePositionedTextGlyph],
    lines: &mut [NativePositionedTextLine],
    validated: bool,
) -> Result<NativeExclusionBandResult, FontError> {
    let NativeMeasuredInput {
        glyphs,
        breaks,
        levels,
        scales,
        paragraph_level,
        options,
        tabs,
        justification: classes,
        item_metrics: metrics,
        ..
    } = input;
    if start > glyphs.len()
        || levels.len() != glyphs.len()
        || metrics.len() != glyphs.len()
        || !classes.is_empty() && classes.len() != glyphs.len()
        || !matches!(paragraph_level, 0 | 1)
    {
        return Err(FontError::InvalidArgument);
    }
    if !validated {
        if levels.iter().any(|&l| !(0..=125).contains(&l)) {
            return Err(FontError::InvalidArgument);
        }
        super::super::writer::envelope(metrics)?;
    }
    let remaining = glyphs.len() - start;
    if output.len() < remaining
        || lines.len() < remaining.min(exclusions.len() + 1)
        || scratch.layout.visual_groups.len() < remaining
        || scratch.layout.visual_indices.len() < remaining
        || tabs.interval > 0.0 && scratch.layout.tab_advances.len() < remaining
    {
        return Err(FontError::Capacity);
    }
    let mut fitting = options;
    fitting.direction = if paragraph_level == 0 {
        GposDirection::LeftToRight
    } else {
        GposDirection::RightToLeft
    };
    let (count, next, retry) = fit(
        glyphs,
        breaks,
        scales,
        start,
        fitting,
        tabs,
        band,
        exclusions,
        scratch.obstacles,
        scratch.intervals,
        scratch.fragments,
        validated,
    )?;
    let mut result = NativeExclusionBandResult {
        next_glyph: start as u32,
        top: band.top,
        next_y: retry,
        ..NativeExclusionBandResult::default()
    };
    if start == glyphs.len() {
        result.status = NativeExclusionBandStatus::Complete;
        return Ok(result);
    }
    if count == 0 {
        return Ok(result);
    }
    let envelope = super::super::writer::envelope(&metrics[start..next])?;
    result.height = max(options.line_height, envelope.ascent + envelope.descent);
    result.baseline = band.top + envelope.ascent;
    let bottom = band.top + result.height;
    if !bottom.is_finite() || !result.baseline.is_finite() || bottom <= band.top {
        return Err(FontError::InvalidArgument);
    }
    if bottom != band.bottom {
        result.status = NativeExclusionBandStatus::RefitHeight;
        return Ok(result);
    }
    let mut written = 0;
    for i in 0..count {
        let fragment = scratch.fragments[i];
        let begin = fragment.glyph_start as usize;
        let length = fragment.glyph_count as usize;
        let end = begin + length;
        let mut local = fitting;
        local.maximum_width = fragment.width;
        local.maximum_lines = 0;
        let mut local_tabs = tabs;
        local_tabs.origin += if paragraph_level == 0 {
            fragment.left - band.left
        } else {
            band.right - (fragment.left + fragment.width)
        };
        let continues = end < glyphs.len() && breaks[end - 1] != LineBreakKind::Mandatory;
        let emitted = native_layout_measured_into(
            NativeMeasuredInput {
                glyphs: &glyphs[begin..end],
                breaks: &breaks[begin..end],
                levels: &levels[begin..end],
                scales: if scales.is_empty() {
                    scales
                } else {
                    &scales[begin..end]
                },
                paragraph_level,
                options: local,
                tabs: local_tabs,
                justification: if classes.is_empty() {
                    classes
                } else {
                    &classes[begin..end]
                },
                item_metrics: &metrics[begin..end],
                continues,
            },
            NativeLayoutScratch {
                visual_groups: &mut *scratch.layout.visual_groups,
                visual_indices: &mut *scratch.layout.visual_indices,
                tab_advances: &mut *scratch.layout.tab_advances,
            },
            NativeMeasuredOutput {
                glyphs: &mut output[written..],
                lines: &mut lines[i..i + 1],
                retained: None,
            },
        )?;
        if emitted != [length, 1] {
            return Err(FontError::VerificationFailed);
        }
        let shift = result.baseline - lines[i].baseline_y;
        for g in &mut output[written..written + length] {
            g.glyph_index += begin as u32;
            let moved = [g.position[0] + fragment.left, g.position[1] + shift];
            if !moved.into_iter().all(f32::is_finite) {
                return Err(FontError::InvalidArgument);
            }
            g.position = moved;
        }
        lines[i].glyph_start = written as u32;
        lines[i].baseline_y = result.baseline;
        lines[i].height = result.height;
        if end < glyphs.len() {
            lines[i].input_end = glyphs[end].cluster;
        }
        written += length;
    }
    result.status = NativeExclusionBandStatus::Placed;
    result.next_glyph = next as u32;
    result.glyph_count = written as u32;
    result.fragment_count = count as u32;
    Ok(result)
}
/// Complete original single-band producer, including a typed refit/blocked result.
pub fn native_layout_exclusion_band_into(
    input: NativeMeasuredInput<'_>,
    start: u32,
    band: NativeExclusionRectangle,
    exclusions: &[NativeExclusionRectangle],
    scratch: &mut NativeExclusionScratch<'_>,
    output: &mut [NativePositionedTextGlyph],
    lines: &mut [NativePositionedTextLine],
) -> Result<NativeExclusionBandResult, FontError> {
    layout(
        input,
        start as usize,
        band,
        exclusions,
        scratch,
        output,
        lines,
        false,
    )
}
