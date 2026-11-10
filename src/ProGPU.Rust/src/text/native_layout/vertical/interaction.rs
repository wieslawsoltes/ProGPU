//! Original vertical advance-box, caret, hit and selection algorithms. No outline
//! ink, horizontal bidi heuristics or invented empty-column carets are used.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeVerticalRectangle {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeVerticalClusterBox {
    pub input_start: i32,
    pub input_end: i32,
    pub column_index: u32,
    pub bidi_level: i8,
    pub bottom_to_top: bool,
    pub bounds: NativeVerticalRectangle,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeVerticalCaretStop {
    pub input_position: i32,
    pub column_index: u32,
    pub position: [f32; 2],
    pub width: f32,
    pub bidi_level: i8,
    pub trailing: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeVerticalHit {
    pub input_position: i32,
    pub column_index: u32,
    pub bounds: NativeVerticalRectangle,
    pub bidi_level: i8,
    pub trailing: bool,
    pub inside: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVerticalInteractionRequirements {
    pub cluster_capacity: usize,
    pub caret_capacity: usize,
}
fn valid_box(b: NativeVerticalClusterBox) -> bool {
    let r = b.bounds;
    b.input_end > b.input_start
        && (0..=125).contains(&b.bidi_level)
        && [r.x, r.y, r.width, r.height]
            .into_iter()
            .all(f32::is_finite)
        && r.width >= 0.0
        && r.height >= 0.0
}
fn valid_caret(c: NativeVerticalCaretStop) -> bool {
    c.position.into_iter().all(f32::is_finite)
        && c.width.is_finite()
        && c.width >= 0.0
        && (0..=125).contains(&c.bidi_level)
}
/// Admit every original positioned column and complete per-occurrence source map.
pub fn native_vertical_interaction_requirements(
    glyphs: &[NativePositionedTextGlyph],
    columns: &[NativePositionedTextColumn],
    ends: &[i32],
    levels: &[i8],
    direction: GposDirection,
) -> Result<NativeVerticalInteractionRequirements, FontError> {
    if !vertical(direction)
        || ends.len() != glyphs.len()
        || levels.len() != glyphs.len()
        || glyphs.len() > u32::MAX as usize
        || columns.len() > u32::MAX as usize
    {
        return Err(FontError::InvalidArgument);
    }
    let mut expected = 0;
    let mut count = 0;
    for column in columns {
        if column.glyph_start as usize != expected
            || column.glyph_count as usize > glyphs.len() - expected
            || !column.x.is_finite()
            || !column.width.is_finite()
            || column.width < 0.0
            || !column.height.is_finite()
            || column.height < 0.0
        {
            return Err(FontError::InvalidArgument);
        }
        let end = expected + column.glyph_count as usize;
        for i in expected..end {
            let g = glyphs[i];
            if !g.position.into_iter().chain(g.advance).all(f32::is_finite)
                || ends[i] <= g.cluster
                || !(0..=125).contains(&levels[i])
            {
                return Err(FontError::InvalidArgument);
            }
            if i == expected || g.cluster != glyphs[i - 1].cluster {
                count += 1;
            }
        }
        expected = end;
    }
    if expected != glyphs.len() || count > u32::MAX as usize / 2 {
        return Err(FontError::InvalidArgument);
    }
    Ok(NativeVerticalInteractionRequirements {
        cluster_capacity: count,
        caret_capacity: count * 2,
    })
}
/// Caller-buffer original builder. Capacities and all source inputs are admitted
/// before output writing; deduplication retains the original position tolerance.
pub fn native_build_vertical_interaction_into(
    glyphs: &[NativePositionedTextGlyph],
    columns: &[NativePositionedTextColumn],
    ends: &[i32],
    levels: &[i8],
    direction: GposDirection,
    boxes: &mut [NativeVerticalClusterBox],
    carets: &mut [NativeVerticalCaretStop],
) -> Result<[usize; 2], FontError> {
    let required =
        native_vertical_interaction_requirements(glyphs, columns, ends, levels, direction)?;
    if boxes.len() < required.cluster_capacity || carets.len() < required.caret_capacity {
        return Err(FontError::Capacity);
    }
    let bottom_to_top = direction == GposDirection::BottomToTop;
    let mut box_count = 0;
    let mut caret_count = 0;
    for (column_index, column) in columns.iter().enumerate() {
        let end = (column.glyph_start + column.glyph_count) as usize;
        let mut index = column.glyph_start as usize;
        while index < end {
            let start = index;
            let cluster = glyphs[index].cluster;
            let mut top = f32::INFINITY;
            let mut bottom = f32::NEG_INFINITY;
            let mut cluster_end = ends[index];
            loop {
                let g = glyphs[index];
                let far = g.position[1] + g.advance[1];
                top = source_min(top, source_min(g.position[1], far));
                bottom = source_max(bottom, source_max(g.position[1], far));
                cluster_end = cluster_end.max(ends[index]);
                index += 1;
                if index == end || glyphs[index].cluster != cluster {
                    break;
                }
            }
            if bottom - top < 1.0 {
                if bottom_to_top {
                    top = bottom - 1.0;
                } else {
                    bottom = top + 1.0;
                }
            }
            let b = NativeVerticalClusterBox {
                input_start: cluster,
                input_end: cluster_end,
                column_index: column_index as u32,
                bidi_level: levels[start],
                bottom_to_top,
                bounds: NativeVerticalRectangle {
                    x: column.x,
                    y: top,
                    width: source_max(1.0, column.width),
                    height: bottom - top,
                },
            };
            boxes[box_count] = b;
            box_count += 1;
            for (position, y, trailing) in [
                (
                    b.input_start,
                    if bottom_to_top { bottom } else { top },
                    false,
                ),
                (b.input_end, if bottom_to_top { top } else { bottom }, true),
            ] {
                let c = NativeVerticalCaretStop {
                    input_position: position,
                    column_index: b.column_index,
                    position: [b.bounds.x, y],
                    width: b.bounds.width,
                    bidi_level: b.bidi_level,
                    trailing,
                };
                if caret_count != 0 {
                    let previous = carets[caret_count - 1];
                    if previous.input_position == c.input_position
                        && previous.trailing == c.trailing
                        && (previous.position[0] - c.position[0]).abs() < 0.0001
                        && (previous.position[1] - c.position[1]).abs() < 0.0001
                    {
                        continue;
                    }
                }
                carets[caret_count] = c;
                caret_count += 1;
            }
        }
    }
    Ok([box_count, caret_count])
}
pub fn native_hit_test_vertical_text(
    boxes: &[NativeVerticalClusterBox],
    position: [f32; 2],
) -> Result<NativeVerticalHit, FontError> {
    if boxes.is_empty() || !position.into_iter().all(f32::is_finite) {
        return Err(FontError::InvalidArgument);
    }
    let [x, y] = position;
    let mut best = None;
    let mut distance = f32::INFINITY;
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
        let current = dx * dx + dy * dy;
        if current >= distance {
            continue;
        }
        distance = current;
        best = Some(b);
        inside = dx == 0.0 && dy == 0.0;
    }
    let b = best.ok_or(FontError::InvalidArgument)?;
    let lower = y >= b.bounds.y + b.bounds.height * 0.5;
    let trailing = if b.bottom_to_top { !lower } else { lower };
    Ok(NativeVerticalHit {
        input_position: if trailing { b.input_end } else { b.input_start },
        column_index: b.column_index,
        bounds: b.bounds,
        bidi_level: b.bidi_level,
        trailing,
        inside,
    })
}
pub fn native_vertical_caret(
    carets: &[NativeVerticalCaretStop],
    input: i32,
    trailing_affinity: bool,
) -> Result<NativeVerticalCaretStop, FontError> {
    if carets.is_empty() {
        return Err(FontError::InvalidArgument);
    }
    let mut best = 0;
    let mut distance = i64::MAX;
    for (i, &c) in carets.iter().enumerate() {
        if !valid_caret(c) {
            return Err(FontError::InvalidArgument);
        }
        let current = (i64::from(c.input_position) - i64::from(input)).abs();
        if current < distance
            || current == distance
                && c.trailing == trailing_affinity
                && carets[best].trailing != trailing_affinity
        {
            best = i;
            distance = current;
        }
    }
    Ok(carets[best])
}
pub fn native_move_vertical_caret(
    carets: &[NativeVerticalCaretStop],
    input: i32,
    trailing_affinity: bool,
    direction: i32,
) -> Result<NativeVerticalCaretStop, FontError> {
    if carets.is_empty() {
        return Err(FontError::InvalidArgument);
    }
    let mut current = 0;
    let mut distance = i64::MAX;
    for (i, &c) in carets.iter().enumerate() {
        if !valid_caret(c) {
            return Err(FontError::InvalidArgument);
        }
        let candidate = (i64::from(c.input_position) - i64::from(input)).abs() * 4
            + if c.trailing == trailing_affinity {
                0
            } else {
                1
            };
        if candidate < distance {
            distance = candidate;
            current = i;
        }
    }
    if direction < 0 && current != 0 {
        current -= 1;
    } else if direction > 0 && current + 1 < carets.len() {
        current += 1;
    }
    Ok(carets[current])
}
fn touches(previous: NativeVerticalRectangle, b: NativeVerticalClusterBox) -> bool {
    let r = b.bounds;
    if (previous.x - r.x).abs() >= 0.01 || (previous.width - r.width).abs() >= 0.01 {
        return false;
    }
    let previous_bottom = previous.y + previous.height;
    let bottom = r.y + r.height;
    r.y <= previous_bottom + 0.5 && previous.y <= bottom + 0.5
}
fn merge(previous: &mut NativeVerticalRectangle, b: NativeVerticalClusterBox) {
    let bottom = source_max(previous.y + previous.height, b.bounds.y + b.bounds.height);
    previous.y = source_min(previous.y, b.bounds.y);
    previous.height = bottom - previous.y;
}
/// Full source/size preflight precedes selection publication. Only consecutive
/// selected boxes in the same actual column may merge, with original tolerances.
pub fn native_vertical_selection_into(
    boxes: &[NativeVerticalClusterBox],
    start: i32,
    end: i32,
    output: &mut [NativeVerticalRectangle],
) -> Result<usize, FontError> {
    if end < start || boxes.len() > u32::MAX as usize {
        return Err(FontError::InvalidArgument);
    }
    let mut required = 0;
    let mut previous_column = u32::MAX;
    let mut previous = NativeVerticalRectangle::default();
    for &b in boxes {
        if !valid_box(b) {
            return Err(FontError::InvalidArgument);
        }
        if b.input_end <= start || b.input_start >= end {
            continue;
        }
        if required != 0 && b.column_index == previous_column && touches(previous, b) {
            merge(&mut previous, b);
        } else {
            required += 1;
            previous = b.bounds;
        }
        previous_column = b.column_index;
    }
    if output.len() < required {
        return Err(FontError::Capacity);
    }
    let mut written = 0;
    previous_column = u32::MAX;
    for &b in boxes {
        if b.input_end <= start || b.input_start >= end {
            continue;
        }
        if written != 0 && b.column_index == previous_column && touches(output[written - 1], b) {
            merge(&mut output[written - 1], b);
        } else {
            output[written] = b.bounds;
            written += 1;
        }
        previous_column = b.column_index;
    }
    Ok(written)
}
