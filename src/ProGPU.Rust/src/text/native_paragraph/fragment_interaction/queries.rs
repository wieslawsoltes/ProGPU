//! Original native nearest-hit, source-affinity and full-cluster selection.
use super::*;

pub fn native_fragment_hit_test(
    boxes: &[NativeClusterBox],
    x: f32,
    y: f32,
) -> Result<NativeTextHit, FontError> {
    if boxes.is_empty() || !x.is_finite() || !y.is_finite() {
        return Err(FontError::InvalidArgument);
    }
    let mut best = None;
    let mut best_distance = f32::INFINITY;
    let mut inside = false;
    for &b in boxes {
        if !valid_box(b) {
            return Err(FontError::InvalidArgument);
        }
        let r = b.bounds;
        let right = r.x + r.width;
        let bottom = r.y + r.height;
        let dx = if x < r.x {
            r.x - x
        } else if x > right {
            x - right
        } else {
            0.0
        };
        let dy = if y < r.y {
            r.y - y
        } else if y > bottom {
            y - bottom
        } else {
            0.0
        };
        let distance = dx * dx + dy * dy;
        if distance >= best_distance {
            continue;
        }
        best_distance = distance;
        best = Some(b);
        inside = dx == 0.0 && dy == 0.0;
    }
    let b = best.ok_or(FontError::InvalidArgument)?;
    let visual_right = x >= b.bounds.x + b.bounds.width * 0.5;
    let trailing = if b.bidi_level & 1 != 0 {
        !visual_right
    } else {
        visual_right
    };
    Ok(NativeTextHit {
        input_position: if trailing { b.input_end } else { b.input_start },
        line_index: b.line_index,
        bounds: b.bounds,
        bidi_level: b.bidi_level,
        trailing,
        inside,
    })
}

fn caret_index(
    carets: &[NativeCaretStop],
    input_position: i32,
    trailing: bool,
    weighted: bool,
) -> Result<usize, FontError> {
    if carets.is_empty() || carets.len() > u32::MAX as usize {
        return Err(FontError::InvalidArgument);
    }
    let mut best = 0;
    let mut best_distance = i64::MAX;
    for (i, &c) in carets.iter().enumerate() {
        if !valid_caret(c) {
            return Err(FontError::InvalidArgument);
        }
        let distance = (i64::from(c.input_position) - i64::from(input_position)).abs();
        let distance = if weighted {
            distance * 4 + i64::from(c.trailing != trailing)
        } else {
            distance
        };
        if distance < best_distance
            || !weighted
                && distance == best_distance
                && c.trailing == trailing
                && carets[best].trailing != trailing
        {
            best = i;
            best_distance = distance;
        }
    }
    Ok(best)
}

/// Index of the original nearest logical position and affinity. This index can
/// be passed directly to fragment navigation; duplicate positions stay distinct.
pub fn native_fragment_caret_index(
    carets: &[NativeCaretStop],
    input_position: i32,
    trailing_affinity: bool,
) -> Result<u32, FontError> {
    Ok(caret_index(carets, input_position, trailing_affinity, false)? as u32)
}

pub fn native_fragment_caret(
    carets: &[NativeCaretStop],
    input_position: i32,
    trailing_affinity: bool,
) -> Result<NativeCaretStop, FontError> {
    Ok(carets[caret_index(carets, input_position, trailing_affinity, false)?])
}

/// Original sequential visual-order movement, distinct from physical row-aware
/// navigation. Only the sign of direction affects the selected step.
pub fn native_fragment_move_visual_caret(
    carets: &[NativeCaretStop],
    input_position: i32,
    trailing_affinity: bool,
    direction: i32,
) -> Result<NativeCaretStop, FontError> {
    let mut i = caret_index(carets, input_position, trailing_affinity, true)?;
    if direction < 0 && i != 0 {
        i -= 1;
    } else if direction > 0 && i + 1 < carets.len() {
        i += 1;
    }
    Ok(carets[i])
}

pub fn native_fragment_selection_requirements(
    boxes: &[NativeClusterBox],
    start: i32,
    end: i32,
) -> Result<u32, FontError> {
    if end < start || boxes.len() > u32::MAX as usize {
        return Err(FontError::InvalidArgument);
    }
    let mut count = 0;
    let mut previous_line = u32::MAX;
    let mut previous_right = 0.0;
    for &b in boxes {
        if !valid_box(b) {
            return Err(FontError::InvalidArgument);
        }
        if b.input_end <= start || b.input_start >= end {
            continue;
        }
        let r = b.bounds;
        if count == 0 || b.line_index != previous_line || r.x > previous_right + 0.5 {
            count += 1;
            previous_right = r.x + r.width;
        } else {
            previous_right = max(previous_right, r.x + r.width);
        }
        previous_line = b.line_index;
    }
    Ok(count)
}

/// Complete admission and capacity precede the original same-fragment half-pixel
/// merge. Adjacent fragments on one physical row remain separate rectangles.
pub fn native_fragment_selection_into(
    boxes: &[NativeClusterBox],
    start: i32,
    end: i32,
    rectangles: &mut [NativeTextRectangle],
) -> Result<u32, FontError> {
    let required = native_fragment_selection_requirements(boxes, start, end)?;
    if rectangles.len() < required as usize {
        return Err(FontError::Capacity);
    }
    let mut written = 0usize;
    let mut previous_line = u32::MAX;
    for &b in boxes {
        if b.input_end <= start || b.input_start >= end {
            continue;
        }
        let r = b.bounds;
        if written != 0
            && b.line_index == previous_line
            && r.x <= rectangles[written - 1].x + rectangles[written - 1].width + 0.5
        {
            let p = &mut rectangles[written - 1];
            let right = max(p.x + p.width, r.x + r.width);
            p.x = min(p.x, r.x);
            p.width = right - p.x;
        } else {
            rectangles[written] = r;
            written += 1;
        }
        previous_line = b.line_index;
    }
    Ok(written as u32)
}
