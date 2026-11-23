//! Original source transform, opacity and target-local projection arithmetic.
use super::*;
pub fn compose_affine(a: wire::Affine2d, b: wire::Affine2d) -> wire::Affine2d {
    wire::Affine2d {
        m11: a.m11 * b.m11 + a.m12 * b.m21,
        m12: a.m11 * b.m12 + a.m12 * b.m22,
        m21: a.m21 * b.m11 + a.m22 * b.m21,
        m22: a.m21 * b.m12 + a.m22 * b.m22,
        m31: a.m31 * b.m11 + a.m32 * b.m21 + b.m31,
        m32: a.m31 * b.m12 + a.m32 * b.m22 + b.m32,
    }
}
pub fn transform_point(t: wire::Affine2d, p: wire::Point) -> wire::Point {
    wire::Point {
        x: p.x * t.m11 + p.y * t.m21 + t.m31,
        y: p.x * t.m12 + p.y * t.m22 + t.m32,
    }
}
pub fn transform_vector(t: wire::Affine2d, p: wire::Point) -> wire::Point {
    wire::Point {
        x: p.x * t.m11 + p.y * t.m21,
        y: p.x * t.m12 + p.y * t.m22,
    }
}
pub fn apply_analytic_transform(value: &mut wire::AnalyticPrimitive, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_analytic_state(value: &mut wire::AnalyticPrimitive, state: &wire::State) {
    apply_analytic_transform(value, state);
    value.color.a *= state.opacity;
}
pub fn apply_geometry_transform(value: &mut wire::GeometryPrimitive, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_geometry_state(value: &mut wire::GeometryPrimitive, state: &wire::State) {
    apply_geometry_transform(value, state);
    value.color.a *= state.opacity;
}
pub fn apply_point_batch_transform(value: &mut wire::PointBatch, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_point_batch_state(value: &mut wire::PointBatch, state: &wire::State) {
    apply_point_batch_transform(value, state);
    value.color.a *= state.opacity;
}
pub fn apply_vertex_mesh_transform(value: &mut wire::VertexMesh, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_stroke_transform(value: &mut wire::Stroke, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_path_transform(value: &mut wire::PathFill, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
}
pub fn apply_path_state(value: &mut wire::PathFill, state: &wire::State) {
    apply_path_transform(value, state);
    value.color.a *= state.opacity;
}
pub fn apply_positioned_glyph_transform(value: &mut wire::PositionedGlyph, state: &wire::State) {
    value.position = transform_point(state.transform, value.position);
    value.basis_x = transform_vector(state.transform, value.basis_x);
    value.basis_y = transform_vector(state.transform, value.basis_y);
}
pub fn apply_positioned_glyph_state(value: &mut wire::PositionedGlyph, state: &wire::State) {
    apply_positioned_glyph_transform(value, state);
    value.color.a *= state.opacity;
}
pub fn apply_image_state(value: &mut wire::ImageDraw, state: &wire::State) {
    value.transform = compose_affine(value.transform, state.transform);
    value.opacity *= state.opacity;
}
pub fn round_to_even(value: f32) -> f32 {
    let lower = value.floor();
    let fraction = value - lower;
    if fraction < 0.5 {
        lower
    } else if fraction > 0.5 {
        lower + 1.0
    } else if lower.abs() % 2.0 == 0.0 {
        lower
    } else {
        lower + 1.0
    }
}
pub fn snap_image_point(point: &mut wire::Point, dpi: f32) {
    point.x = round_to_even(point.x * dpi) / dpi;
    point.y = round_to_even(point.y * dpi) / dpi;
}
pub fn localize_point(
    point: wire::Point,
    target: NativeScissor,
    p: wire::Presentation,
    dpi: f32,
) -> wire::Point {
    if p.dpi_scale_x == dpi && p.dpi_scale_y == dpi && p.viewport_x == 0 && p.viewport_y == 0 {
        return wire::Point {
            x: point.x - target.x as f32 / dpi,
            y: point.y - target.y as f32 / dpi,
        };
    }
    let offset_x = (f64::from(p.viewport_x) - f64::from(target.x)) as f32 / dpi;
    let offset_y = (f64::from(p.viewport_y) - f64::from(target.y)) as f32 / dpi;
    let mapped = scale_translate_four(
        [point.x, point.y, 0.0, 0.0],
        [p.dpi_scale_x / dpi, p.dpi_scale_y / dpi, 1.0, 1.0],
        [offset_x, offset_y, 0.0, 0.0],
    );
    wire::Point {
        x: mapped[0],
        y: mapped[1],
    }
}
pub fn localize_transform(
    mut t: wire::Affine2d,
    target: NativeScissor,
    p: wire::Presentation,
    dpi: f32,
) -> wire::Affine2d {
    if p.dpi_scale_x == dpi && p.dpi_scale_y == dpi && p.viewport_x == 0 && p.viewport_y == 0 {
        t.m31 -= target.x as f32 / dpi;
        t.m32 -= target.y as f32 / dpi;
        return t;
    }
    let linear = scale_translate_four(
        [t.m11, t.m12, t.m21, t.m22],
        [
            p.dpi_scale_x / dpi,
            p.dpi_scale_y / dpi,
            p.dpi_scale_x / dpi,
            p.dpi_scale_y / dpi,
        ],
        [0.0; 4],
    );
    t.m11 = linear[0];
    t.m12 = linear[1];
    t.m21 = linear[2];
    t.m22 = linear[3];
    let position = localize_point(wire::Point { x: t.m31, y: t.m32 }, target, p, dpi);
    t.m31 = position.x;
    t.m32 = position.y;
    t
}
pub fn localize_state(
    mut state: wire::State,
    target: NativeScissor,
    p: wire::Presentation,
    dpi: f32,
) -> wire::State {
    state.transform = localize_transform(state.transform, target, p, dpi);
    state
}
pub fn localize_scalar_state(
    mut state: wire::State,
    target: NativeScissor,
    dpi: f32,
) -> wire::State {
    state.transform.m31 -= target.x as f32 / dpi;
    state.transform.m32 -= target.y as f32 / dpi;
    state
}
pub fn resolve_mask_uv(
    t: wire::Affine2d,
    bounds: wire::ImageRect,
    target: NativeScissor,
    p: wire::Presentation,
    dpi: f32,
) -> Option<[f64; 6]> {
    if !dpi.is_finite()
        || dpi <= 0.0
        || !p.dpi_scale_x.is_finite()
        || p.dpi_scale_x <= 0.0
        || !p.dpi_scale_y.is_finite()
        || p.dpi_scale_y <= 0.0
        || !bounds.width.is_finite()
        || bounds.width <= 0.0
        || !bounds.height.is_finite()
        || bounds.height <= 0.0
    {
        return None;
    }
    let legacy =
        p.viewport_x == 0 && p.viewport_y == 0 && p.dpi_scale_x == dpi && p.dpi_scale_y == dpi;
    let scale_x = f64::from(p.dpi_scale_x) / f64::from(dpi);
    let scale_y = f64::from(p.dpi_scale_y) / f64::from(dpi);
    let m11 = if legacy {
        f64::from(t.m11)
    } else {
        f64::from(t.m11) * scale_x
    };
    let m12 = if legacy {
        f64::from(t.m12)
    } else {
        f64::from(t.m12) * scale_y
    };
    let m21 = if legacy {
        f64::from(t.m21)
    } else {
        f64::from(t.m21) * scale_x
    };
    let m22 = if legacy {
        f64::from(t.m22)
    } else {
        f64::from(t.m22) * scale_y
    };
    let m31 = if legacy {
        f64::from(t.m31) - f64::from(target.x) / f64::from(dpi)
    } else {
        f64::from(t.m31) * scale_x
            + (f64::from(p.viewport_x) - f64::from(target.x)) / f64::from(dpi)
    };
    let m32 = if legacy {
        f64::from(t.m32) - f64::from(target.y) / f64::from(dpi)
    } else {
        f64::from(t.m32) * scale_y
            + (f64::from(p.viewport_y) - f64::from(target.y)) / f64::from(dpi)
    };
    let determinant = m11 * m22 - m12 * m21;
    if !determinant.is_finite() || determinant == 0.0 {
        return None;
    }
    let i11 = m22 / determinant;
    let i12 = -m12 / determinant;
    let i21 = -m21 / determinant;
    let i22 = m11 / determinant;
    let i31 = (m21 * m32 - m22 * m31) / determinant;
    let i32 = (m12 * m31 - m11 * m32) / determinant;
    let px = if legacy {
        f64::from(dpi * bounds.width)
    } else {
        f64::from(dpi) * f64::from(bounds.width)
    };
    let py = if legacy {
        f64::from(dpi * bounds.height)
    } else {
        f64::from(dpi) * f64::from(bounds.height)
    };
    let candidate = [
        i11 / px,
        i21 / px,
        (i31 - f64::from(bounds.x)) / f64::from(bounds.width),
        i12 / py,
        i22 / py,
        (i32 - f64::from(bounds.y)) / f64::from(bounds.height),
    ];
    candidate
        .into_iter()
        .all(|v| v.is_finite() && v.abs() <= f64::from(f32::MAX))
        .then_some(candidate)
}
