//! Original fragment frame measurement, with full row ordering admission.
use super::*;
pub fn native_measure_fragment_lines(
    lines: &[NativePositionedTextLine],
    placements: &[NativeFragmentPlacement],
    maximum_width: f32,
) -> Result<NativeTextLayoutMetrics, FontError> {
    if !maximum_width.is_finite() || maximum_width < 0.0 || placements.len() != lines.len() {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeTextLayoutMetrics::default();
    let mut direction = 0;
    for (i, (&line, &p)) in lines.iter().zip(placements).enumerate() {
        let right = f64::from(p.left) + f64::from(line.width);
        let bottom = p.top + f64::from(line.height);
        if !p.left.is_finite()
            || p.left < 0.0
            || !p.top.is_finite()
            || p.top < 0.0
            || !p.width.is_finite()
            || p.width <= 0.0
            || !(p.left + p.width).is_finite()
            || !line.width.is_finite()
            || line.width < 0.0
            || !line.height.is_finite()
            || line.height < 0.0
            || !line.baseline_y.is_finite()
            || !right.is_finite()
            || right > f64::from(f32::MAX)
            || !bottom.is_finite()
            || bottom > f64::from(f32::MAX)
            || line.baseline_y < p.top as f32
            || line.baseline_y > bottom as f32
        {
            return Err(FontError::InvalidArgument);
        }
        if i == 0 {
            if p.row_index != 0 {
                return Err(FontError::InvalidArgument);
            }
        } else {
            let previous = placements[i - 1];
            if p.row_index == previous.row_index {
                if p.top != previous.top
                    || line.height != lines[i - 1].height
                    || line.baseline_y != lines[i - 1].baseline_y
                {
                    return Err(FontError::InvalidArgument);
                }
                let order = if p.left >= previous.left + previous.width {
                    1
                } else if p.left + p.width <= previous.left {
                    -1
                } else {
                    0
                };
                if order == 0 || direction != 0 && order != direction {
                    return Err(FontError::InvalidArgument);
                }
                direction = order;
            } else {
                if p.row_index != previous.row_index + 1
                    || p.top < previous.top + f64::from(lines[i - 1].height)
                {
                    return Err(FontError::InvalidArgument);
                }
                direction = 0;
            }
        }
        result.content_width = max(result.content_width, right as f32);
        result.content_height = max(result.content_height, bottom as f32);
    }
    result.measured_width = if maximum_width > 0.0 {
        maximum_width
    } else {
        result.content_width
    };
    result.measured_height = result.content_height;
    Ok(result)
}
