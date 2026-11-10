//! Original stroke payload layout; renderer widening admission stays independent.
use super::wire;
use wire::{WireRecord, constants::*};
pub(crate) fn layout(strokes: &[wire::Stroke], auxiliary_size: usize) -> Option<(usize, usize)> {
    if strokes.is_empty() {
        return None;
    }
    let flags = POLYLINE_FLAG_EDGE_ALIASED
        | POLYLINE_FLAG_HAIRLINE
        | POLYLINE_FLAG_FIXED_DEVICE_STROKE
        | POLYLINE_FLAG_CLOSED
        | POLYLINE_FLAG_WPF_JOIN_SEMANTICS
        | POLYLINE_FLAG_CLIP_MITER_AT_LIMIT;
    let mut points = 0u64;
    let mut doubles = 0u64;
    for stroke in strokes {
        if stroke.struct_size != wire::Stroke::WIRE_SIZE as u32
            || stroke.kind > SCENE_STROKE_SPLINE
            || stroke.flags & !flags != 0
            || stroke.point_offset != points
            || stroke.point_count < 2
            || stroke.start_cap > STROKE_CAP_TRIANGLE
            || stroke.end_cap > STROKE_CAP_TRIANGLE
            || stroke.dash_cap > STROKE_CAP_TRIANGLE
            || stroke.line_join > STROKE_JOIN_MITER_OR_BEVEL
            || (stroke.flags & POLYLINE_FLAG_CLIP_MITER_AT_LIMIT != 0
                && stroke.line_join != STROKE_JOIN_MITER)
            || !finite_color(stroke.color)
            || !finite_transform(stroke.transform)
            || !stroke.stroke_thickness.is_finite()
            || !stroke.miter_limit.is_finite()
            || stroke.miter_limit < 1.0
            || (stroke.flags & POLYLINE_FLAG_WPF_JOIN_SEMANTICS != 0
                && stroke.flags & (POLYLINE_FLAG_HAIRLINE | POLYLINE_FLAG_FIXED_DEVICE_STROKE) != 0)
            || !stroke.dash_offset.is_finite()
            || stroke.reserved != [0; 2]
        {
            return None;
        }
        points = points.checked_add(stroke.point_count)?;
        if stroke.kind == SCENE_STROKE_SPLINE {
            if stroke.knot_offset != doubles || stroke.knot_count == 0 || stroke.degree > 1 << 20 {
                return None;
            }
            doubles = doubles.checked_add(stroke.knot_count)?;
            if stroke.weight_offset != doubles
                || (stroke.weight_count != 0 && stroke.weight_count != stroke.point_count)
            {
                return None;
            }
            doubles = doubles.checked_add(stroke.weight_count)?;
        } else if stroke.degree != 0
            || stroke.knot_offset != 0
            || stroke.knot_count != 0
            || stroke.weight_offset != 0
            || stroke.weight_count != 0
        {
            return None;
        }
        if stroke.dash_interval_offset != doubles {
            return None;
        }
        doubles = doubles.checked_add(stroke.dash_interval_count)?;
    }
    if points > u64::from(u32::MAX)
        || doubles > u64::from(u32::MAX)
        || points * wire::Point::WIRE_SIZE as u64 + doubles * 8 != auxiliary_size as u64
    {
        return None;
    }
    Some((points as usize, doubles as usize))
}

fn finite_color(c: wire::Color) -> bool {
    [c.r, c.g, c.b, c.a].into_iter().all(f32::is_finite)
}
fn finite_transform(t: wire::Affine2d) -> bool {
    [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
        .into_iter()
        .all(f32::is_finite)
}
