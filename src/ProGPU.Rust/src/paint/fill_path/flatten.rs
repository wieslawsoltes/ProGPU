//! Original eight-level source flatness and one-slot bounded point-list cache.
use super::*;
use std::ops::{Deref, DerefMut};
thread_local! {static CACHE:std::cell::RefCell<Option<Vec<[f32;2]>>>=const {std::cell::RefCell::new(None)};}
pub(super) struct Points(Vec<[f32; 2]>);
impl Deref for Points {
    type Target = Vec<[f32; 2]>;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl DerefMut for Points {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}
impl Drop for Points {
    fn drop(&mut self) {
        self.0.clear();
        if self.0.capacity() <= 4096 {
            CACHE.with(|cache| {
                let mut cache = cache.borrow_mut();
                if cache.is_none() {
                    *cache = Some(std::mem::take(&mut self.0));
                }
            });
        }
    }
}
fn push(points: &mut Vec<[f32; 2]>, value: [f32; 2], budget: &mut Budget) -> Result<(), Error> {
    budget.take(1)?;
    points.try_reserve(1).map_err(|_| Error::Capacity)?;
    points.push(value);
    Ok(())
}
pub(super) fn figure(
    figure: &PathFigure,
    scale: f32,
    budget: &mut Budget,
) -> Result<Points, Error> {
    let mut points = Points(CACHE.with(|c| c.borrow_mut().take()).unwrap_or_default());
    let tolerance = 0.25 / normalize_scale(scale);
    let squared = tolerance * tolerance;
    push(&mut points, figure.start, budget)?;
    let mut current = figure.start;
    for segment in &figure.segments {
        match segment.kind {
            PathSegmentKind::Line(end) => push(&mut points, end, budget)?,
            PathSegmentKind::Quadratic([control, end]) => {
                quadratic(&mut points, [current, control, end], squared, 8, budget)?
            }
            PathSegmentKind::Cubic([c1, c2, end]) => {
                cubic(&mut points, [current, c1, c2, end], squared, 8, budget)?
            }
            PathSegmentKind::Arc(arc) => {
                let vertices = arc.flattened(current, std::f32::consts::PI / 24.0)?;
                for p in vertices.into_iter().skip(1) {
                    push(&mut points, p, budget)?;
                }
            }
            PathSegmentKind::RationalQuadratic { .. } | PathSegmentKind::RationalCubic { .. } => {
                return Err(Error::UnsupportedExecution);
            }
        }
        current = segment.end();
    }
    Ok(points)
}
fn quadratic(
    out: &mut Vec<[f32; 2]>,
    p: [[f32; 2]; 3],
    tolerance: f32,
    depth: u8,
    budget: &mut Budget,
) -> Result<(), Error> {
    if depth == 0 || quadratic_flat(p, tolerance) {
        return push(out, p[2], budget);
    }
    let a = mul(add(p[0], p[1]), 0.5);
    let b = mul(add(p[1], p[2]), 0.5);
    let mid = mul(add(a, b), 0.5);
    quadratic(out, [p[0], a, mid], tolerance, depth - 1, budget)?;
    quadratic(out, [mid, b, p[2]], tolerance, depth - 1, budget)
}
fn cubic(
    out: &mut Vec<[f32; 2]>,
    p: [[f32; 2]; 4],
    tolerance: f32,
    depth: u8,
    budget: &mut Budget,
) -> Result<(), Error> {
    if depth == 0 || cubic_flat(p, tolerance) {
        return push(out, p[3], budget);
    }
    let a = mul(add(p[0], p[1]), 0.5);
    let b = mul(add(p[1], p[2]), 0.5);
    let c = mul(add(p[2], p[3]), 0.5);
    let ab = mul(add(a, b), 0.5);
    let bc = mul(add(b, c), 0.5);
    let mid = mul(add(ab, bc), 0.5);
    cubic(out, [p[0], a, ab, mid], tolerance, depth - 1, budget)?;
    cubic(out, [mid, bc, c, p[3]], tolerance, depth - 1, budget)
}
fn quadratic_flat(p: [[f32; 2]; 3], tolerance: f32) -> bool {
    let chord = sub(p[2], p[0]);
    let length = chord[0] * chord[0] + chord[1] * chord[1];
    if length <= 0.0000001 {
        return distance_squared(p[1], p[0]) <= tolerance;
    }
    let control = sub(p[1], p[0]);
    let projection = control[0] * chord[0] + control[1] * chord[1];
    let cross = chord[0] * control[1] - chord[1] * control[0];
    projection >= 0.0 && projection <= length && cross * cross <= tolerance * length
}
fn cubic_flat(p: [[f32; 2]; 4], tolerance: f32) -> bool {
    let chord = sub(p[3], p[0]);
    let length = chord[0] * chord[0] + chord[1] * chord[1];
    if length <= 0.0000001 {
        return maximum(distance_squared(p[1], p[0]), distance_squared(p[2], p[0])) <= tolerance;
    }
    let first = sub(p[1], p[0]);
    let second = sub(p[2], p[0]);
    let projection1 = first[0] * chord[0] + first[1] * chord[1];
    let projection2 = second[0] * chord[0] + second[1] * chord[1];
    let cross1 = chord[0] * first[1] - chord[1] * first[0];
    let cross2 = chord[0] * second[1] - chord[1] * second[0];
    let scaled = tolerance * length;
    projection1 >= 0.0
        && projection1 <= projection2
        && projection2 <= length
        && cross1 * cross1 <= scaled
        && cross2 * cross2 <= scaled
}
pub(super) fn deduplicate(points: &mut Vec<[f32; 2]>) {
    let mut write = 1;
    for read in 1..points.len() {
        if distance_squared(points[write - 1], points[read]) > 0.0000001 {
            points[write] = points[read];
            write += 1;
        }
    }
    if write < points.len() {
        points.truncate(write);
    }
}
