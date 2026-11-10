//! Original bounded NEON/SSE2 source-coordinate arithmetic through safe SIMD.
use super::*;
use wide::f32x4;
const SIMD: bool = cfg!(any(
    target_arch = "aarch64",
    target_arch = "x86_64",
    all(target_arch = "x86", target_feature = "sse2")
));
pub(super) fn coordinates(x: &mut [f32; 4], y: &mut [f32; 4], t: wire::Affine2d) -> bool {
    if !SIMD || !finite_transform(t) {
        return false;
    }
    let (xs, ys) = (f32x4::from(*x), f32x4::from(*y));
    *x = ((xs * f32x4::splat(t.m11) + ys * f32x4::splat(t.m21)) + f32x4::splat(t.m31)).to_array();
    *y = ((xs * f32x4::splat(t.m12) + ys * f32x4::splat(t.m22)) + f32x4::splat(t.m32)).to_array();
    x.iter().chain(y.iter()).all(|v| v.is_finite())
}
pub(super) fn input_frame(a: wire::Affine2d, b: wire::Affine2d) -> Option<wire::Affine2d> {
    if !SIMD {
        return None;
    }
    let x = f32x4::from([a.m11, a.m21, a.m31, 0.0]);
    let y = f32x4::from([a.m12, a.m22, a.m32, 0.0]);
    let ox = ((x * f32x4::splat(b.m11) + y * f32x4::splat(b.m21))
        + f32x4::from([0.0, 0.0, b.m31, 0.0]))
    .to_array();
    let oy = ((x * f32x4::splat(b.m12) + y * f32x4::splat(b.m22))
        + f32x4::from([0.0, 0.0, b.m32, 0.0]))
    .to_array();
    let result = wire::Affine2d {
        m11: ox[0],
        m12: oy[0],
        m21: ox[1],
        m22: oy[1],
        m31: ox[2],
        m32: oy[2],
    };
    finite_transform(result).then_some(result)
}
pub(super) fn compose(a: wire::Affine2d, b: wire::Affine2d) -> wire::Affine2d {
    wire::Affine2d {
        m11: a.m11 * b.m11 + a.m12 * b.m21,
        m12: a.m11 * b.m12 + a.m12 * b.m22,
        m21: a.m21 * b.m11 + a.m22 * b.m21,
        m22: a.m21 * b.m12 + a.m22 * b.m22,
        m31: a.m31 * b.m11 + a.m32 * b.m21 + b.m31,
        m32: a.m31 * b.m12 + a.m32 * b.m22 + b.m32,
    }
}
pub(super) fn point(t: wire::Affine2d, p: wire::Point) -> wire::Point {
    p2(
        p.x * t.m11 + p.y * t.m21 + t.m31,
        p.x * t.m12 + p.y * t.m22 + t.m32,
    )
}
pub(super) fn direction(t: wire::Affine2d, p: wire::Point) -> wire::Point {
    p2(p.x * t.m11 + p.y * t.m21, p.x * t.m12 + p.y * t.m22)
}
pub(super) fn place(
    output: &mut wire::HitTestPrimitive,
    min: wire::Point,
    max: wire::Point,
    t: wire::Affine2d,
) -> bool {
    if !finite_transform(t) {
        return false;
    }
    let determinant = f64::from(t.m11) * f64::from(t.m22) - f64::from(t.m12) * f64::from(t.m21);
    if !determinant.is_finite() || determinant == 0.0 {
        return false;
    }
    let inverse = 1.0 / determinant;
    let (a, b, c, d) = (
        f64::from(t.m22) * inverse,
        -f64::from(t.m12) * inverse,
        -f64::from(t.m21) * inverse,
        f64::from(t.m11) * inverse,
    );
    output.inverse_transform0 = f4(
        a as f32,
        c as f32,
        -(f64::from(t.m31) * a + f64::from(t.m32) * c) as f32,
        0.0,
    );
    output.inverse_transform1 = f4(
        b as f32,
        d as f32,
        -(f64::from(t.m31) * b + f64::from(t.m32) * d) as f32,
        0.0,
    );
    if ![
        output.inverse_transform0.x,
        output.inverse_transform0.y,
        output.inverse_transform0.z,
        output.inverse_transform1.x,
        output.inverse_transform1.y,
        output.inverse_transform1.z,
    ]
    .into_iter()
    .all(f32::is_finite)
    {
        return false;
    }
    let mut x = [min.x, max.x, max.x, min.x];
    let mut y = [min.y, min.y, max.y, max.y];
    if !coordinates(&mut x, &mut y, t) {
        return false;
    }
    output.bounds_min = p2(
        x.into_iter().reduce(minimum).unwrap(),
        y.into_iter().reduce(minimum).unwrap(),
    );
    output.bounds_max = p2(
        x.into_iter().reduce(maximum).unwrap(),
        y.into_iter().reduce(maximum).unwrap(),
    );
    true
}
pub(super) fn line(start: wire::Point, end: wire::Point) -> Option<wire::Float4> {
    if !SIMD {
        return None;
    }
    let delta = f32x4::from([end.x, end.y, 0.0, 0.0]) - f32x4::from([start.x, start.y, 0.0, 0.0]);
    let square = (delta * delta).to_array();
    let delta = delta.to_array();
    let length = (square[0] + square[1]).sqrt();
    (length.is_finite() && length > 0.0001)
        .then(|| f4(delta[0] / length, delta[1] / length, length, 0.0))
}
pub(super) fn intersect(a: wire::ImageRect, b: wire::ImageRect) -> wire::ImageRect {
    let first = [-a.x, -a.y, a.x + a.width, a.y + a.height];
    let second = [-b.x, -b.y, b.x + b.width, b.y + b.height];
    if !SIMD || !first.into_iter().chain(second).all(f32::is_finite) {
        return wire::ImageRect {
            x: f32::NAN,
            y: f32::NAN,
            width: f32::NAN,
            height: f32::NAN,
        };
    }
    // fast_min is the native SSE min / NEON vmin operation, after finite admission.
    let clipped = f32x4::from(first).fast_min(f32x4::from(second)).to_array();
    wire::ImageRect {
        x: -clipped[0],
        y: -clipped[1],
        width: maximum(0.0, clipped[2] + clipped[0]),
        height: maximum(0.0, clipped[3] + clipped[1]),
    }
}
pub(super) fn rectangular(edges: &[wire::PathSegment], min: wire::Point, max: wire::Point) -> bool {
    if edges.len() != 4 || !(min.x < max.x && min.y < max.y) {
        return false;
    }
    let mut corners = 0u32;
    for (i, edge) in edges.iter().enumerate() {
        let next = edges[(i + 1) % 4];
        if edge.kind != PATH_SEGMENT_LINE
            || edge.p1.x != next.p0.x
            || edge.p1.y != next.p0.y
            || (edge.p0.x == edge.p1.x) == (edge.p0.y == edge.p1.y)
            || (edge.p0.x != min.x && edge.p0.x != max.x)
            || (edge.p0.y != min.y && edge.p0.y != max.y)
        {
            return false;
        }
        let corner = u32::from(edge.p0.x == max.x) | if edge.p0.y == max.y { 2 } else { 0 };
        if corners & (1 << corner) != 0 {
            return false;
        }
        corners |= 1 << corner;
    }
    corners == 15
}
pub(super) fn affine(t: wire::Affine2d) -> bool {
    let x = t.m11.hypot(t.m12);
    let y = t.m21.hypot(t.m22);
    if !x.is_finite() || !y.is_finite() || x <= 0.000001 || y <= 0.000001 {
        return false;
    }
    (x - y).abs() > maximum(x, y) * 0.0001 || (t.m11 * t.m21 + t.m12 * t.m22).abs() > x * y * 0.0001
}
