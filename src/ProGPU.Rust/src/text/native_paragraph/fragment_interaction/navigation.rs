//! Original physical caret navigation across excluded intervals and rows.
use super::*;

/// Move from an actual emitted caret index. Empty rows remain empty; crossing
/// chooses the closest existing row, and vertical ties retain source affinity.
pub fn native_move_fragment_caret(
    carets: &[NativeCaretStop],
    placements: &[NativeFragmentPlacement],
    current_index: u32,
    direction: NativeFragmentCaretDirection,
    paragraph_level: i8,
    preferred_x: f32,
) -> Result<u32, FontError> {
    if carets.len() > u32::MAX as usize
        || current_index as usize >= carets.len()
        || !matches!(paragraph_level, 0 | 1)
        || !preferred_x.is_finite()
    {
        return Err(FontError::InvalidArgument);
    }
    validate_placements(placements, None)?;
    for (i, &c) in carets.iter().enumerate() {
        if !valid_caret(c)
            || c.line_index as usize >= placements.len()
            || c.position[1] != placements[c.line_index as usize].top as f32
            || i != 0 && c.line_index < carets[i - 1].line_index
        {
            return Err(FontError::InvalidArgument);
        }
    }
    let current = current_index as usize;
    let row = placements[carets[current].line_index as usize].row_index;
    let horizontal = matches!(
        direction,
        NativeFragmentCaretDirection::Left | NativeFragmentCaretDirection::Right
    );
    let right = direction == NativeFragmentCaretDirection::Right;
    let less = |a: usize, b: usize| {
        if carets[a].position[0] != carets[b].position[0] {
            return carets[a].position[0] < carets[b].position[0];
        }
        let ax = placements[carets[a].line_index as usize].left;
        let bx = placements[carets[b].line_index as usize].left;
        if ax != bx { ax < bx } else { a < b }
    };
    let mut found: Option<usize> = None;
    if horizontal {
        for i in 0..carets.len() {
            if placements[carets[i].line_index as usize].row_index != row
                || if right {
                    !less(current, i)
                } else {
                    !less(i, current)
                }
            {
                continue;
            }
            if found.is_none_or(|f| if right { less(i, f) } else { less(f, i) }) {
                found = Some(i);
            }
        }
    }
    if found.is_none() {
        let forward = if horizontal {
            right != (paragraph_level != 0)
        } else {
            direction == NativeFragmentCaretDirection::Down
        };
        let mut target_row: Option<u32> = None;
        let mut distance = f64::INFINITY;
        for i in 0..carets.len() {
            let candidate_row = placements[carets[i].line_index as usize].row_index;
            if if forward {
                candidate_row <= row
            } else {
                candidate_row >= row
            } {
                continue;
            }
            let nearer = target_row.is_none_or(|r| {
                if forward {
                    candidate_row < r
                } else {
                    candidate_row > r
                }
            });
            if nearer {
                target_row = Some(candidate_row);
                found = None;
                distance = f64::INFINITY;
            }
            if Some(candidate_row) != target_row {
                continue;
            }
            if horizontal {
                if found.is_none_or(|f| if right { less(i, f) } else { less(f, i) }) {
                    found = Some(i);
                }
            } else {
                let delta = (f64::from(carets[i].position[0]) - f64::from(preferred_x)).abs();
                if delta < distance
                    || delta == distance
                        && found.is_some_and(|f| {
                            carets[i].trailing == carets[current].trailing
                                && carets[f].trailing != carets[current].trailing
                        })
                {
                    distance = delta;
                    found = Some(i);
                }
            }
        }
    }
    Ok(found.map_or(current_index, |v| v as u32))
}
