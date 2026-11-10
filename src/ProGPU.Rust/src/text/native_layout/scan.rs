//! Original native shaping-safe forward fitter, tabs and ellipsis prefixes.
use super::*;

/// Original layout whitespace inventory, independent of Unicode break policy.
pub fn is_native_trailing_space(cp: u32) -> bool {
    matches!(cp, 0x09..=0x0d | 0x20 | 0x85 | 0xa0 | 0x1680 | 0x2000..=0x200b
        | 0x2028 | 0x2029 | 0x202f | 0x205f | 0x3000)
}
/// First complete trailing U+0020 cluster. Mixed-source clusters remain whole;
/// this helper alone does not select the separate source soft-wrap policy.
pub fn native_soft_wrap_space_start(glyphs: &[ShapingGlyph]) -> usize {
    let mut end = glyphs.len();
    while end != 0 {
        let mut start = end - 1;
        while start != 0 && glyphs[start - 1].cluster == glyphs[end - 1].cluster {
            start -= 1;
        }
        if !glyphs[start..end].iter().all(|g| g.code_point == 0x20) {
            break;
        }
        end = start;
    }
    end
}
/// Original intrinsic widths, excluding complete source whitespace clusters.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextIntrinsicWidths {
    /// Width bounded only by legal shaping-safe breaks, without emergency splits.
    pub minimum: f32,
    /// Width bounded only by mandatory source breaks.
    pub maximum: f32,
}
/// Measure original legal/mandatory break widths in one ordered source scan.
/// Candidate tab grids restart independently; mixed whitespace ligatures remain
/// indivisible. No completed floating sum is rounded or repaired.
pub fn native_measure_intrinsic_widths(
    input: &[UnicodeScalar],
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
) -> Result<NativeTextIntrinsicWidths, FontError> {
    validate_options(options)?;
    if breaks.len() != glyphs.len()
        || !scales.is_empty() && scales.len() != glyphs.len()
        || !tabs.interval.is_finite()
        || tabs.interval < 0.0
        || !tabs.origin.is_finite()
        || input.is_empty() && !glyphs.is_empty()
    {
        return Err(FontError::InvalidArgument);
    }
    for (&glyph, &scale) in glyphs.iter().zip(scales) {
        if !scale.is_finite()
            || scale <= 0.0
            || scale_metrics(glyph, scale).iter().any(|v| !v.is_finite())
        {
            return Err(FontError::InvalidArgument);
        }
    }
    for (index, scalar) in input.iter().enumerate() {
        if scalar.input_length == 0
            || index != 0
                && u64::from(scalar.input_index)
                    < u64::from(input[index - 1].input_index)
                        + u64::from(input[index - 1].input_length)
        {
            return Err(FontError::InvalidArgument);
        }
    }
    let mut result = NativeTextIntrinsicWidths::default();
    let (mut word, mut paragraph, mut word_visible, mut paragraph_visible) = (0.0, 0.0, 0.0, 0.0);
    let mut scalar = 0;
    let mut start = 0;
    while start < glyphs.len() {
        let cluster = glyphs[start].cluster;
        if cluster < 0 || scalar >= input.len() || input[scalar].input_index != cluster as u32 {
            return Err(FontError::InvalidArgument);
        }
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].cluster == cluster {
            end += 1;
        }
        if end < glyphs.len() && glyphs[end].cluster <= cluster {
            return Err(FontError::InvalidArgument);
        }
        let next = if end < glyphs.len() {
            glyphs[end].cluster as u64
        } else {
            u64::from(input[input.len() - 1].input_index)
                + u64::from(input[input.len() - 1].input_length)
        };
        let mut whitespace = true;
        while scalar < input.len() && u64::from(input[scalar].input_index) < next {
            whitespace &= is_native_trailing_space(input[scalar].code_point);
            scalar += 1;
        }
        for index in start..end {
            let scale = scale_at(scales, index, options);
            word += advance(glyphs[index], scale, word, tabs);
            paragraph += advance(glyphs[index], scale, paragraph, tabs);
            if !word.is_finite() || !paragraph.is_finite() || word < 0.0 || paragraph < 0.0 {
                return Err(FontError::InvalidArgument);
            }
        }
        if !whitespace {
            word_visible = word;
            paragraph_visible = paragraph;
        }
        if can_break(glyphs, breaks, end - 1) {
            result.minimum = max(result.minimum, word_visible);
            word = 0.0;
            word_visible = 0.0;
            if breaks[end - 1] == LineBreakKind::Mandatory {
                result.maximum = max(result.maximum, paragraph_visible);
                paragraph = 0.0;
                paragraph_visible = 0.0;
            }
        }
        start = end;
    }
    result.minimum = max(result.minimum, word_visible);
    result.maximum = max(result.maximum, paragraph_visible);
    Ok(result)
}
pub(super) fn can_break(glyphs: &[ShapingGlyph], breaks: &[LineBreakKind], index: usize) -> bool {
    breaks[index] != LineBreakKind::Prohibited
        && (safe_break(glyphs, index + 1)
            || is_native_trailing_space(glyphs[index].code_point)
                && (index + 1 >= glyphs.len()
                    || glyphs[index].cluster != glyphs[index + 1].cluster))
}
pub(super) fn advance(glyph: ShapingGlyph, scale: f32, width: f32, tabs: NativeTextTabs) -> f32 {
    if tabs.interval <= 0.0 || glyph.glyph_id != u32::MAX {
        return glyph.advance_x as f32 * scale;
    }
    let mut remainder = (f64::from(width) + f64::from(tabs.origin)) % f64::from(tabs.interval);
    if remainder < 0.0 {
        remainder += f64::from(tabs.interval);
    }
    let value = (f64::from(tabs.interval) - remainder) as f32;
    if (width + value).is_finite() && width + value > width {
        value
    } else {
        f32::INFINITY
    }
}
#[derive(Clone, Copy)]
pub(super) struct Scan {
    pub end: usize,
    pub width: f32,
    pub clipped: bool,
}
pub(super) fn line(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
    start: usize,
    final_allowed: bool,
) -> Scan {
    let mut width = 0.0;
    let mut visible_width = 0.0;
    let mut break_width = 0.0;
    let mut last_break = start;
    let mut cluster_width = 0.0;
    let mut last_cluster = start;
    for index in start..glyphs.len() {
        if index > start && safe_break(glyphs, index) {
            last_cluster = index;
            cluster_width = width;
        }
        let next = width + advance(glyphs[index], scale_at(scales, index, options), width, tabs);
        if !next.is_finite() {
            return Scan {
                end: index + 1,
                width: next,
                clipped: false,
            };
        }
        let boundary = can_break(glyphs, breaks, index);
        let mandatory = boundary && breaks[index] == LineBreakKind::Mandatory;
        if !is_native_trailing_space(glyphs[index].code_point) {
            visible_width = next;
        }
        let fitting_spaces = boundary
            && is_native_trailing_space(glyphs[index].code_point)
            && (options.maximum_width <= 0.0 || visible_width <= options.maximum_width);
        if options.maximum_width > 0.0
            && next > options.maximum_width
            && index > start
            && !fitting_spaces
        {
            if last_break > start {
                return Scan {
                    end: last_break,
                    width: break_width,
                    clipped: final_allowed,
                };
            }
            if !tabs.allow_emergency_break {
                if boundary {
                    return Scan {
                        end: index + 1,
                        width: next,
                        clipped: final_allowed,
                    };
                }
                width = next;
                continue;
            }
            if last_cluster > start {
                return Scan {
                    end: last_cluster,
                    width: cluster_width,
                    clipped: final_allowed,
                };
            }
            let mut end = index + 1;
            let mut hard_width = next;
            while end < glyphs.len() && !safe_break(glyphs, end) {
                hard_width += advance(
                    glyphs[end],
                    scale_at(scales, end, options),
                    hard_width,
                    tabs,
                );
                end += 1;
            }
            return Scan {
                end,
                width: hard_width,
                clipped: final_allowed,
            };
        }
        if mandatory {
            return Scan {
                end: index + 1,
                width: next,
                clipped: false,
            };
        }
        if boundary {
            last_break = index + 1;
            break_width = next;
        }
        width = next;
    }
    Scan {
        end: glyphs.len(),
        width,
        clipped: false,
    }
}
pub(super) fn trim(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
    start: usize,
    end: usize,
    width: f32,
) -> (usize, f32) {
    let sign = options.ellipsis_advance * options.scale;
    let limit = if options.collapse_width >= 0.0 {
        options.collapse_width
    } else {
        options.maximum_width
    };
    if limit <= 0.0 && options.collapse_width < 0.0 || width + sign <= limit {
        return (end, width);
    }
    let mut character = (start, 0.0);
    let mut word = (start, 0.0);
    let mut width = 0.0;
    for index in start..end {
        width += advance(glyphs[index], scale_at(scales, index, options), width, tabs);
        if width + sign <= limit && safe_break(glyphs, index + 1) {
            character = (index + 1, width);
            if can_break(glyphs, breaks, index) {
                word = character;
            }
        }
    }
    if options.trimming == NativeTextTrimming::WordEllipsis && word.0 > start {
        word
    } else {
        character
    }
}
/// Complete scaled/tabbed line scan. All four scaled metric lanes are checked
/// before publication; empty scales retain the original raw-options route.
pub fn native_layout_requirements(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    scales: &[f32],
    options: NativeTextLayoutOptions,
    tabs: NativeTextTabs,
) -> Result<NativeTextLayoutRequirements, FontError> {
    validate_options(options)?;
    if glyphs.len() > u32::MAX as usize
        || breaks.len() != glyphs.len()
        || !tabs.interval.is_finite()
        || tabs.interval < 0.0
        || !tabs.origin.is_finite()
        || !scales.is_empty() && scales.len() != glyphs.len()
    {
        return Err(FontError::InvalidArgument);
    }
    for (&glyph, &scale) in glyphs.iter().zip(scales) {
        if !scale.is_finite()
            || scale <= 0.0
            || scale_metrics(glyph, scale).iter().any(|v| !v.is_finite())
        {
            return Err(FontError::InvalidArgument);
        }
    }
    let mut count = 0;
    let mut start = 0;
    while start < glyphs.len() {
        let final_allowed =
            options.maximum_lines != 0 && count + 1 >= options.maximum_lines as usize;
        let scan = line(glyphs, breaks, scales, options, tabs, start, final_allowed);
        if scan.end <= start || scan.end > glyphs.len() || !scan.width.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        count += 1;
        start = scan.end;
        if final_allowed {
            break;
        }
    }
    Ok(NativeTextLayoutRequirements {
        glyph_capacity: glyphs.len(),
        line_capacity: count,
    })
}
/// Classify complete original source clusters in O(S+G), without another Unicode
/// version or inferred break opportunities. Output tails remain untouched.
pub fn native_classify_justification_into(
    input: &[UnicodeScalar],
    glyphs: &[ShapingGlyph],
    classes: &mut [NativeTextJustification],
) -> Result<(), FontError> {
    if classes.len() < glyphs.len() || input.is_empty() && !glyphs.is_empty() {
        return Err(FontError::InvalidArgument);
    }
    for (i, scalar) in input.iter().enumerate() {
        if scalar.input_length == 0
            || i != 0
                && u64::from(scalar.input_index)
                    < u64::from(input[i - 1].input_index) + u64::from(input[i - 1].input_length)
        {
            return Err(FontError::InvalidArgument);
        }
    }
    let mut scalar = 0;
    let mut start = 0;
    while start < glyphs.len() {
        let cluster = glyphs[start].cluster;
        if cluster < 0 || scalar >= input.len() || input[scalar].input_index != cluster as u32 {
            return Err(FontError::InvalidArgument);
        }
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].cluster == cluster {
            end += 1;
        }
        if end < glyphs.len() && glyphs[end].cluster <= cluster {
            return Err(FontError::InvalidArgument);
        }
        let next = if end < glyphs.len() {
            glyphs[end].cluster as u64
        } else {
            u64::from(input[input.len() - 1].input_index)
                + u64::from(input[input.len() - 1].input_length)
        };
        let mut whitespace = true;
        let mut word = true;
        while scalar < input.len() && u64::from(input[scalar].input_index) < next {
            let cp = input[scalar].code_point;
            scalar += 1;
            whitespace &= is_native_trailing_space(cp);
            word &= cp == 0x20;
        }
        classes[start..end].fill(if word {
            NativeTextJustification::WordSpace
        } else if whitespace {
            NativeTextJustification::Whitespace
        } else {
            NativeTextJustification::Content
        });
        start = end;
    }
    Ok(())
}
