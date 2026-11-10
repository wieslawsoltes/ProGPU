//! Native vector page producers, preserving original source payload encodings.
use super::*;
use crate::geometry::{Affine, AnalyticPrimitive, Shape};
use crate::native_scene::{
    draw::NativeAnalyticDraw, engine_materials::NativeBrushPage, execution::*, semantic::values,
};
use std::f32::consts::PI;
mod caps;
mod dash;
mod geometry;
mod joins;
mod mesh;
mod page;
mod points;
mod spline;
mod stroke;
pub use page::{NativeVectorDrawRange, NativeVectorPage, NativeVectorPageMetrics};

/// Original count-only command route; no output vertex or index page is emitted.
pub(crate) fn preflight_command(
    frame: &NativeCommandFrame,
    materials: &NativeBrushPage,
    padding: f32,
) -> Result<(usize, usize), Error> {
    page::compile_command(frame, materials, padding, None)
}

/// Geometry-mask production uses this same native primitive encoder and brush
/// slot zero, after the caller has applied the original mask target transform.
pub(crate) fn compile_mask_geometry(
    primitives: &[wire::GeometryPrimitive],
) -> Result<(Vec<VectorVertex>, Vec<u32>), Error> {
    let mut counts = (0usize, 0usize);
    for primitive in primitives {
        let (v, i) = geometry::capacity(primitive)?;
        counts.0 = counts.0.checked_add(v).ok_or(Error::Capacity)?;
        counts.1 = counts.1.checked_add(i).ok_or(Error::Capacity)?;
    }
    let mut output = Output::default();
    output.reserve(counts.0, counts.1)?;
    for primitive in primitives {
        geometry::append(primitive, 0.0, &mut output)?;
    }
    Ok((output.vertices, output.indices))
}

#[derive(Default)]
struct Output {
    vertices: Vec<VectorVertex>,
    indices: Vec<u32>,
}
impl Output {
    fn reserve(&mut self, vertices: usize, indices: usize) -> Result<(), Error> {
        if vertices > u32::MAX as usize || self.vertices.len() > u32::MAX as usize - vertices {
            return Err(Error::Capacity);
        }
        self.vertices
            .try_reserve(vertices)
            .map_err(|_| Error::Capacity)?;
        self.indices
            .try_reserve(indices)
            .map_err(|_| Error::Capacity)?;
        Ok(())
    }
    fn quad(&mut self, vertices: [VectorVertex; 4]) -> Result<(), Error> {
        self.reserve(4, 6)?;
        let base = self.vertices.len() as u32;
        self.vertices.extend_from_slice(&vertices);
        self.indices
            .extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
        Ok(())
    }
}
fn zero_vertex() -> VectorVertex {
    VectorVertex {
        position: [0.0; 2],
        color: [0.0; 4],
        texture_coordinate: [0.0; 2],
        brush_index: 0.0,
        shape_size: [0.0; 2],
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        shape_type: 0.0,
    }
}
fn point(x: f32, y: f32) -> wire::Point {
    wire::Point { x, y }
}
fn sub(a: wire::Point, b: wire::Point) -> wire::Point {
    point(a.x - b.x, a.y - b.y)
}
fn add(a: wire::Point, b: wire::Point) -> wire::Point {
    point(a.x + b.x, a.y + b.y)
}
fn mul(a: wire::Point, b: f32) -> wire::Point {
    point(a.x * b, a.y * b)
}
fn color(c: wire::Color) -> [f32; 4] {
    [c.r, c.g, c.b, c.a]
}
fn finite_color(c: wire::Color) -> bool {
    color(c).into_iter().all(f32::is_finite)
}
fn finite_point(p: wire::Point) -> bool {
    p.x.is_finite() && p.y.is_finite()
}
fn finite_transform(t: wire::Affine2d) -> bool {
    [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
        .into_iter()
        .all(f32::is_finite)
}
fn affine(t: wire::Affine2d) -> Affine {
    Affine([t.m11, t.m12, t.m21, t.m22, t.m31, t.m32])
}
fn affine_stroke(t: wire::Affine2d) -> bool {
    let x = t.m11.hypot(t.m12);
    let y = t.m21.hypot(t.m22);
    if !x.is_finite() || !y.is_finite() || x <= 0.000001 || y <= 0.000001 {
        return false;
    }
    let scale = x.max(y);
    let dot = t.m11 * t.m21 + t.m12 * t.m22;
    (x - y).abs() > scale * 0.0001 || dot.abs() > x * y * 0.0001
}
fn normalize(mut direction: wire::Point, fallback: wire::Point) -> Option<wire::Point> {
    let mut length = direction.x.hypot(direction.y);
    if !length.is_finite() || length <= 0.0001 {
        direction = fallback;
        length = direction.x.hypot(direction.y);
    }
    if !length.is_finite() || length <= 0.0001 {
        return None;
    }
    Some(point(direction.x / length, direction.y / length))
}
fn select_direction(values: [wire::Point; 3]) -> Option<wire::Point> {
    values.into_iter().find(|p| {
        let n = p.x.hypot(p.y);
        n.is_finite() && n > 0.0001
    })
}
fn corners(min: wire::Point, max: wire::Point) -> [wire::Point; 4] {
    [min, point(max.x, min.y), max, point(min.x, max.y)]
}
fn bounds(points: &[wire::Point]) -> (wire::Point, wire::Point) {
    let (mut min, mut max) = (points[0], points[0]);
    for p in &points[1..] {
        min.x = ordered_min(min.x, p.x);
        min.y = ordered_min(min.y, p.y);
        max.x = ordered_max(max.x, p.x);
        max.y = ordered_max(max.y, p.y);
    }
    (min, max)
}
fn cross(a: wire::Point, b: wire::Point) -> f32 {
    a.x * b.y - a.y * b.x
}
fn stroke_quad(
    points: [wire::Point; 4],
    brush: f32,
    aliased: bool,
    kind: f32,
    output: &mut Output,
) -> Result<(), Error> {
    let area = cross(sub(points[1], points[0]), sub(points[2], points[0]));
    if !area.is_finite() || area.abs() <= 0.0001 {
        return Ok(());
    }
    let (min, max) = bounds(&points);
    let min = sub(min, point(1.5, 1.5));
    let max = add(max, point(1.5, 1.5));
    if !finite_point(min) || !finite_point(max) {
        return Err(Error::InvalidInput);
    }
    output.quad(corners(min, max).map(|p| VectorVertex {
        position: [p.x, p.y],
        color: [points[0].x, points[0].y, points[1].x, points[1].y],
        texture_coordinate: [p.x, p.y],
        brush_index: brush,
        shape_size: [points[2].x, points[2].y],
        corner_radius: points[3].x,
        stroke_thickness: points[3].y,
        shape_type: kind + if aliased { 1000.0 } else { 0.0 },
    }))
}
fn stroke_triangle(
    triangle: [wire::Point; 3],
    brush: f32,
    masks: [u32; 2],
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let area = cross(sub(triangle[1], triangle[0]), sub(triangle[2], triangle[0]));
    if !area.is_finite() || area.abs() <= 0.0001 {
        return Ok(());
    }
    let (min, max) = bounds(&triangle);
    let min = sub(min, point(1.5, 1.5));
    let max = add(max, point(1.5, 1.5));
    if !finite_point(min) || !finite_point(max) {
        return Err(Error::InvalidInput);
    }
    output.quad(corners(min, max).map(|p| VectorVertex {
        position: [p.x, p.y],
        color: [triangle[0].x, triangle[0].y, triangle[1].x, triangle[1].y],
        texture_coordinate: [p.x, p.y],
        brush_index: brush,
        shape_size: [triangle[2].x, triangle[2].y],
        corner_radius: masks[0] as f32,
        stroke_thickness: masks[1] as f32,
        shape_type: 13.0 + if aliased { 1000.0 } else { 0.0 },
    }))
}

// C++ std::min/max preserve the first operand on an unordered comparison.
// Some original geometry routes deliberately defer non-finite emitted vertices.
fn ordered_min<T: PartialOrd + Copy>(a: T, b: T) -> T {
    if b < a { b } else { a }
}
fn ordered_max<T: PartialOrd + Copy>(a: T, b: T) -> T {
    if a < b { b } else { a }
}
