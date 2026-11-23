//! Original complete input preflight, before any source fallback callbacks.
use super::*;
pub(super) fn validate_options(options: &StyledTextLayoutOptions) -> Result<(), TextLayoutError> {
    if (options.max_width != f32::INFINITY
        && (!options.max_width.is_finite() || options.max_width <= 0.0))
        || !options.minimum_line_spacing.is_finite()
        || options.minimum_line_spacing < 0.0
    {
        return Err(TextLayoutError::InvalidArgument);
    }
    Ok(())
}
fn validate_style(style: &StyledTextStyle) -> Result<(), TextLayoutError> {
    if !style.font_size.is_finite()
        || style.font_size <= 0.0
        || !style.width_scale.is_finite()
        || style.width_scale <= 0.0
        || !style.tracking_factor.is_finite()
        || style.tracking_factor <= 0.0
        || !style.baseline_shift.is_finite()
    {
        return Err(TextLayoutError::InvalidArgument);
    }
    Ok(())
}
pub(super) fn style_map(
    text: &[u16],
    ranges: &[StyledTextRange],
) -> Result<Vec<usize>, TextLayoutError> {
    if text.is_empty() {
        if ranges.len() != 1 || ranges[0].start != 0 || ranges[0].length != 0 {
            return Err(TextLayoutError::InvalidArgument);
        }
        validate_style(&ranges[0].style)?;
        return Ok(Vec::new());
    }
    if ranges.is_empty() {
        return Err(TextLayoutError::InvalidArgument);
    }
    let mut result = Vec::new();
    reserve(&mut result, text.len())?;
    result.resize(text.len(), 0);
    let mut expected = 0;
    for (index, range) in ranges.iter().enumerate() {
        if range.start != expected
            || range.length <= 0
            || range.start > text.len() as i32 - range.length
        {
            return Err(TextLayoutError::InvalidArgument);
        }
        let end = (range.start + range.length) as usize;
        let start = range.start as usize;
        if (start > 0 && (0xdc00..=0xdfff).contains(&text[start]))
            || (end < text.len() && (0xdc00..=0xdfff).contains(&text[end]))
        {
            return Err(TextLayoutError::InvalidArgument);
        }
        validate_style(&range.style)?;
        result[start..end].fill(index);
        expected += range.length;
    }
    if expected != text.len() as i32 {
        return Err(TextLayoutError::InvalidArgument);
    }
    Ok(result)
}
pub(super) fn box_map(
    text: &[u16],
    boxes: &[StyledTextInlineBox],
) -> Result<Vec<i32>, TextLayoutError> {
    if boxes.len() > i32::MAX as usize {
        return Err(TextLayoutError::Capacity);
    }
    let mut result = Vec::new();
    reserve(&mut result, text.len())?;
    result.resize(text.len(), -1);
    for (index, b) in boxes.iter().enumerate() {
        if b.text_position < 0
            || b.text_position as usize >= text.len()
            || text[b.text_position as usize] != 0xfffc
            || result[b.text_position as usize] >= 0
            || !b.width.is_finite()
            || b.width < 0.0
            || !b.ascent.is_finite()
            || b.ascent < 0.0
            || !b.descent.is_finite()
            || b.descent < 0.0
        {
            return Err(TextLayoutError::InvalidArgument);
        }
        result[b.text_position as usize] = index as i32;
    }
    if text
        .iter()
        .zip(&result)
        .any(|(&c, &b)| c == 0xfffc && b < 0)
    {
        return Err(TextLayoutError::InvalidArgument);
    }
    Ok(result)
}
pub(super) fn validate_paragraph_styles(
    text: &[u16],
    styles: &[StyledTextParagraphStyle],
) -> Result<(), TextLayoutError> {
    let mut previous = -1;
    for style in styles {
        if style.text_start <= previous
            || style.text_start < 0
            || style.text_start as usize > text.len()
            || (style.text_start > 0 && text[style.text_start as usize - 1] != 10)
            || !style.first_line_indent.is_finite()
            || !style.left_indent.is_finite()
            || style.left_indent < 0.0
            || !style.right_indent.is_finite()
            || style.right_indent < 0.0
            || !style.default_tab_interval.is_finite()
            || style.default_tab_interval <= 0.0
            || !style.space_before.is_finite()
            || style.space_before < 0.0
            || !style.space_after.is_finite()
            || style.space_after < 0.0
            || !style.minimum_line_spacing.is_finite()
            || style.minimum_line_spacing < 0.0
        {
            return Err(TextLayoutError::InvalidArgument);
        }
        let mut previous_position = 0.0;
        for tab in style.tab_stops.iter() {
            if !tab.position.is_finite() || tab.position <= previous_position {
                return Err(TextLayoutError::InvalidArgument);
            }
            previous_position = tab.position;
        }
        previous = style.text_start;
    }
    Ok(())
}
