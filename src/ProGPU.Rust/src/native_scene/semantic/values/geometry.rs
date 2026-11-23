//! Native stroked geometry admission before any widening or source hit work.
use super::*;
pub(crate) fn geometry(p: &wire::GeometryPrimitive) -> bool {
    let line_flags = PRIMITIVE_FLAG_EDGE_ALIASED
        | PRIMITIVE_FLAG_HAIRLINE
        | PRIMITIVE_FLAG_FIXED_DEVICE_STROKE
        | PRIMITIVE_START_CAP_MASK
        | PRIMITIVE_END_CAP_MASK
        | PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT
        | PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS;
    if p.kind > GEOMETRY_PATH_JOIN
        || !finite(p.p0)
        || !finite(p.p1)
        || !finite(p.p2)
        || !finite(p.p3)
        || !p.stroke_thickness.is_finite()
        || !p.reserved.is_finite()
        || p.reserved != 0.0
        || !finite(p.color)
        || !finite(p.transform)
    {
        return false;
    }
    if p.kind == GEOMETRY_DOT_GRID {
        if p.flags & !PRIMITIVE_FLAG_EDGE_ALIASED != 0
            || p.p1.x < 0.0
            || p.p1.y < 0.0
            || p.p3.x <= 0.0
            || p.p3.y <= 0.0
            || p.stroke_thickness < 0.0
        {
            return false;
        }
        if p.stroke_thickness == 0.0 {
            return true;
        }
        return (p.p2.x == 0.0 && p.p2.y == 0.0)
            || (p.p2.x == 1.0 && p.p2.y >= 1.0 && p.p2.y <= 100.0 && p.p2.y == p.p2.y.round());
    }
    if p.kind == GEOMETRY_TRIANGLE || p.kind == GEOMETRY_QUADRILATERAL {
        return p.flags & !PRIMITIVE_FLAG_EDGE_ALIASED == 0 && p.stroke_thickness == 0.0;
    }
    let wpf = p.flags & PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS != 0;
    if p.flags & !line_flags != 0
        || (wpf && p.kind != GEOMETRY_PATH_JOIN)
        || (p.flags & PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT != 0
            && (p.kind != GEOMETRY_PATH_JOIN || p.flags & PRIMITIVE_START_CAP_MASK != 0))
    {
        return false;
    }
    if p.kind == GEOMETRY_ARC
        && ((p.p1.x * p.p2.y - p.p1.y * p.p2.x).abs() <= 0.000001
            || p.p3.y.abs() <= 0.000001
            || p.p3.y.abs() > std::f32::consts::PI * 2.0 + 0.001
            || p.flags & (PRIMITIVE_START_CAP_MASK | PRIMITIVE_END_CAP_MASK) != 0)
    {
        return false;
    }
    if p.kind == GEOMETRY_PATH_CAP {
        let cap = (p.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT;
        if cap == STROKE_CAP_FLAT
            || p.flags & PRIMITIVE_END_CAP_MASK != 0
            || (p.p2.x != 0.0 && p.p2.x != 1.0)
            || p.p2.y != 0.0
            || p.p3.x != 0.0
            || p.p3.y != 0.0
        {
            return false;
        }
    }
    if p.kind == GEOMETRY_PATH_JOIN {
        let join = (p.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT;
        if join > STROKE_JOIN_MITER_OR_BEVEL
            || (wpf && join > STROKE_JOIN_ROUND)
            || p.flags & PRIMITIVE_END_CAP_MASK != 0
            || !(p.p3.x >= 1.0)
            || p.p3.y != 0.0
        {
            return false;
        }
    }
    let hairline = p.flags & PRIMITIVE_FLAG_HAIRLINE != 0;
    let fixed = p.flags & PRIMITIVE_FLAG_FIXED_DEVICE_STROKE != 0;
    if (hairline && fixed)
        || (wpf && (hairline || fixed))
        || (hairline && p.stroke_thickness != 0.0)
        || (!hairline && p.stroke_thickness <= 0.0)
    {
        return false;
    }
    stroke_scales(&p.transform).is_some()
}
