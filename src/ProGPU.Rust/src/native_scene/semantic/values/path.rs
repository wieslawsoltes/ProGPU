//! Native segment, postfix Boolean and physical path storage admission.
use super::*;
pub(crate) fn glyph_path_sampling(value: u32) -> bool {
    let grid = value & 0xff;
    let transfer = value >> 8;
    matches!(grid, 1 | 4 | 8) && transfer <= 13 && (transfer == 0 || grid != 1)
}
pub(crate) fn segment(segment: &wire::PathSegment, allow_arc: bool) -> bool {
    let arc = segment.kind == PATH_SEGMENT_ARC;
    let quadratic = segment.kind == PATH_SEGMENT_RATIONAL_QUADRATIC;
    let cubic = segment.kind == PATH_SEGMENT_RATIONAL_CUBIC;
    let maximum = if allow_arc {
        PATH_SEGMENT_RATIONAL_CUBIC
    } else {
        PATH_SEGMENT_CUBIC
    };
    let weight1 = f32::from_bits(segment.pad0);
    let weight2 = f32::from_bits(segment.pad1);
    let a = segment.p0.x.abs().max(segment.p0.y.abs());
    let b = segment.p1.x.abs().max(segment.p1.y.abs());
    let c = segment.p2.x.abs().max(segment.p2.y.abs());
    let d = segment.p3.x.abs().max(segment.p3.y.abs());
    let scale = f64::from(a.max(b.max(c.max(d)))).max(1.0);
    let quadratic_limit = f64::from(f32::MAX) / (4.0 * scale);
    let cubic_limit = f64::from(f32::MAX) / (8.0 * scale);
    segment.kind <= maximum
        && finite(segment.p0)
        && finite(segment.p1)
        && finite(segment.p2)
        && finite(segment.p3)
        && ((!arc
            && !quadratic
            && !cubic
            && segment.pad0 == 0
            && segment.pad1 == 0
            && segment.pad2 == 0)
            || (allow_arc
                && arc
                && segment.p3.x > 0.0
                && segment.p3.y > 0.0
                && weight1.is_finite()
                && weight2.is_finite()
                && f32::from_bits(segment.pad2).is_finite())
            || (allow_arc
                && quadratic
                && segment.p3.x == 0.0
                && segment.p3.y == 0.0
                && weight1.is_finite()
                && weight1 > 0.0
                && f64::from(weight1) <= quadratic_limit
                && segment.pad1 == 0
                && segment.pad2 == 0)
            || (allow_arc
                && cubic
                && weight1.is_finite()
                && weight2.is_finite()
                && weight1 > 0.0
                && weight2 > 0.0
                && f64::from(weight1) <= cubic_limit
                && f64::from(weight2) <= cubic_limit
                && segment.pad2 == 0))
}
pub(crate) fn path_boolean(path: &wire::PathFill, nodes: &[wire::PathBooleanNode]) -> bool {
    boolean(
        path.segment_offset,
        path.segment_count,
        path.boolean_node_offset,
        path.boolean_node_count,
        path.fill_rule,
        nodes,
    )
}
pub(crate) fn boolean(
    segment_offset: u64,
    segment_count: u64,
    offset: u64,
    count: u64,
    fill_rule: u32,
    nodes: &[wire::PathBooleanNode],
) -> bool {
    if count == 0 {
        return offset == 0;
    }
    if count > 63 || offset > nodes.len() as u64 || count > nodes.len() as u64 - offset {
        return false;
    }
    let segment_end = segment_offset.wrapping_add(segment_count);
    let mut stack = 0u32;
    let mut signed = false;
    for node in &nodes[offset as usize..(offset + count) as usize] {
        if node.kind > PATH_BOOLEAN_WINDING_NEGATE || node.reserved0 != 0 || node.reserved1 != 0 {
            return false;
        }
        if node.kind == PATH_BOOLEAN_LEAF || node.kind == PATH_BOOLEAN_WINDING_LEAF {
            if stack >= 16
                || node.segment_count == 0
                || node.segment_offset < segment_offset
                || node.segment_offset > segment_end
                || node.segment_count > segment_end - node.segment_offset
                || ![node.min_x, node.min_y, node.max_x, node.max_y]
                    .into_iter()
                    .all(f32::is_finite)
                || node.max_x <= node.min_x
                || node.max_y <= node.min_y
                || node.fill_rule > FILL_RULE_EVEN_ODD
                || (node.kind == PATH_BOOLEAN_WINDING_LEAF && node.fill_rule != FILL_RULE_NON_ZERO)
            {
                return false;
            }
            signed |= node.kind == PATH_BOOLEAN_WINDING_LEAF;
            stack += 1;
        } else {
            let zero = node.segment_offset == 0
                && node.segment_count == 0
                && node.min_x == 0.0
                && node.min_y == 0.0
                && node.max_x == 0.0
                && node.max_y == 0.0
                && node.fill_rule == 0;
            if node.kind == PATH_BOOLEAN_EMPTY {
                if stack >= 16 || !zero {
                    return false;
                }
                stack += 1;
            } else if node.kind == PATH_BOOLEAN_WINDING_NEGATE {
                if stack < 1 || !zero {
                    return false;
                }
                signed = true;
            } else {
                if stack < 2 || !zero {
                    return false;
                }
                signed |= node.kind == PATH_BOOLEAN_WINDING_ADD;
                stack -= 1;
            }
        }
    }
    stack == 1 && (!signed || fill_rule == FILL_RULE_NON_ZERO)
}
pub(crate) fn path(
    path: &wire::PathFill,
    segment_count: u64,
    nodes: &[wire::PathBooleanNode],
) -> Option<u64> {
    if path.segment_count == 0
        || path.segment_offset > segment_count
        || path.segment_count > segment_count - path.segment_offset
        || ![path.min_x, path.min_y, path.max_x, path.max_y]
            .into_iter()
            .all(f32::is_finite)
        || path.max_x <= path.min_x
        || path.max_y <= path.min_y
        || !finite(path.color)
        || !finite(path.transform)
        || path.fill_rule > FILL_RULE_EVEN_ODD
        || !glyph_path_sampling(path.sample_grid)
        || !path_boolean(path, nodes)
    {
        return None;
    }
    let (scale, _) = stroke_scales(&path.transform)?;
    // std::ceil(float)/floor(float) retain float arithmetic before the source
    // expression is assigned to double; preserve both separate padding steps.
    coverage(
        (path.max_x * scale).ceil() + 4.0 - ((path.min_x * scale).floor() - 4.0),
        (path.max_y * scale).ceil() + 4.0 - ((path.min_y * scale).floor() - 4.0),
    )
}
