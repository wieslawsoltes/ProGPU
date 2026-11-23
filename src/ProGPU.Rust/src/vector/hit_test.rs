//! Original geometric hit queries. These source input queries do not replace
//! canonical GPU coverage or infer painted geometry from a raster mask.
use super::stroke::{EPSILON, cross, distance_squared};
use super::*;

/// Original tolerance value and independent relative-to-geometry selection.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GeometryTolerance {
    /// Original value; negative is zero padding, nonfinite rejects.
    pub value: f32,
    /// Scale padding by the original query's maximum axis extent.
    pub relative: bool,
}
impl GeometryTolerance {
    fn padding(self, extents: [f32; 2]) -> Option<f32> {
        if !self.value.is_finite() {
            return None;
        }
        let mut value = maximum(0.0, self.value);
        if self.relative {
            value *= maximum(extents[0], extents[1]);
        }
        value.is_finite().then_some(value)
    }
    fn half_stroke(self, thickness: f32, extents: [f32; 2]) -> Option<f32> {
        if !thickness.is_finite() || thickness <= 0.0 {
            return None;
        }
        let value = thickness.abs() * 0.5 + self.padding(extents)?;
        (value > 0.0 && value.is_finite()).then_some(value)
    }
}
/// A line has no filled interior, including its degenerate source cases.
pub fn line_contains_fill(_point: [f32; 2], _start: [f32; 2], _end: [f32; 2]) -> bool {
    false
}
/// Original line hit query, including the separate degenerate endpoint rule.
pub fn line_contains_stroke(
    point: [f32; 2],
    start: [f32; 2],
    end: [f32; 2],
    thickness: f32,
    tolerance: GeometryTolerance,
    caps: [PenLineCap; 2],
) -> bool {
    if !finite(point) || !finite(start) || !finite(end) {
        return false;
    }
    let segment = sub(end, start);
    let Some(half) = tolerance.half_stroke(thickness, [segment[0].abs(), segment[1].abs()]) else {
        return false;
    };
    let size = length(segment);
    if !size.is_finite() || size <= EPSILON {
        return (caps[0] != PenLineCap::Flat || caps[1] != PenLineCap::Flat)
            && distance_squared(point, start) <= half * half;
    }
    let direction = [segment[0] / size, segment[1] / size];
    let offset = sub(point, start);
    let along = dot(offset, direction);
    let signed_distance = cross(offset, direction);
    if along >= 0.0 && along <= size && signed_distance.abs() <= half {
        return true;
    }
    if along < 0.0 {
        match caps[0] {
            PenLineCap::Flat => false,
            PenLineCap::Square => along >= -half && signed_distance.abs() <= half,
            PenLineCap::Round => distance_squared(point, start) <= half * half,
            PenLineCap::Triangle => {
                triangle_cap(point, start, [-direction[0], -direction[1]], half)
            }
        }
    } else {
        match caps[1] {
            PenLineCap::Flat => false,
            PenLineCap::Square => along - size <= half && signed_distance.abs() <= half,
            PenLineCap::Round => distance_squared(point, end) <= half * half,
            PenLineCap::Triangle => triangle_cap(point, end, direction, half),
        }
    }
}
fn triangle_cap(point: [f32; 2], center: [f32; 2], outward: [f32; 2], half: f32) -> bool {
    let n = [-outward[1], outward[0]];
    let a = sub(center, mul(n, half));
    let b = add(center, mul(n, half));
    let c = add(center, mul(outward, half));
    let denominator = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1]);
    if !denominator.is_finite() || denominator.abs() <= EPSILON {
        return false;
    }
    let alpha =
        ((b[1] - c[1]) * (point[0] - c[0]) + (c[0] - b[0]) * (point[1] - c[1])) / denominator;
    let beta =
        ((c[1] - a[1]) * (point[0] - c[0]) + (a[0] - c[0]) * (point[1] - c[1])) / denominator;
    let gamma = 1.0 - alpha - beta;
    alpha >= 0.0 && beta >= 0.0 && gamma >= 0.0
}
/// Original rounded-rectangle fill query, including signed-radius normalization.
pub fn rectangle_contains_fill(
    point: [f32; 2],
    min: [f32; 2],
    max: [f32; 2],
    radius: [f32; 2],
) -> bool {
    if !finite(point)
        || !finite(min)
        || !finite(max)
        || !finite(radius)
        || max[0] < min[0]
        || max[1] < min[1]
    {
        return false;
    }
    if !(point[0] >= min[0] && point[0] <= max[0] && point[1] >= min[1] && point[1] <= max[1]) {
        return false;
    }
    let rx = minimum(radius[0].abs(), (max[0] - min[0]) * 0.5);
    let ry = minimum(radius[1].abs(), (max[1] - min[1]) * 0.5);
    if rx <= 0.0 || ry <= 0.0 {
        return true;
    }
    let left = min[0] + rx;
    let right = max[0] - rx;
    let top = min[1] + ry;
    let bottom = max[1] - ry;
    if (point[0] >= left && point[0] <= right) || (point[1] >= top && point[1] <= bottom) {
        return true;
    }
    let cx = if point[0] < left { left } else { right };
    let cy = if point[1] < top { top } else { bottom };
    let x = (point[0] - cx) / rx;
    let y = (point[1] - cy) / ry;
    x * x + y * y <= 1.0
}
/// Original outer-minus-inner rounded-rectangle stroke query.
pub fn rectangle_contains_stroke(
    point: [f32; 2],
    min: [f32; 2],
    max: [f32; 2],
    radius: [f32; 2],
    thickness: f32,
    tolerance: GeometryTolerance,
) -> bool {
    if !finite(point)
        || !finite(min)
        || !finite(max)
        || !finite(radius)
        || max[0] < min[0]
        || max[1] < min[1]
    {
        return false;
    }
    let Some(half) = tolerance.half_stroke(
        thickness,
        [(max[0] - min[0]).abs(), (max[1] - min[1]).abs()],
    ) else {
        return false;
    };
    let padding = [half; 2];
    let outer_radius = [radius[0].abs() + half, radius[1].abs() + half];
    if !rectangle_contains_fill(point, sub(min, padding), add(max, padding), outer_radius) {
        return false;
    }
    let inner_min = add(min, padding);
    let inner_max = sub(max, padding);
    if inner_max[0] <= inner_min[0] || inner_max[1] <= inner_min[1] {
        return true;
    }
    let inner_radius = [
        maximum(0.0, radius[0].abs() - half),
        maximum(0.0, radius[1].abs() - half),
    ];
    !rectangle_contains_fill(point, inner_min, inner_max, inner_radius)
}
/// Original ellipse fill query. Zero-axis ellipses have no interior.
pub fn ellipse_contains_fill(point: [f32; 2], center: [f32; 2], radius: [f32; 2]) -> bool {
    if !finite(point) || !finite(center) || !finite(radius) {
        return false;
    }
    let rx = radius[0].abs();
    let ry = radius[1].abs();
    if rx <= 0.0 || ry <= 0.0 {
        return false;
    }
    let x = (point[0] - center[0]) / rx;
    let y = (point[1] - center[1]) / ry;
    x * x + y * y <= 1.0
}
/// Original ellipse stroke query; this is not an implicit curve offset solver.
pub fn ellipse_contains_stroke(
    point: [f32; 2],
    center: [f32; 2],
    radius: [f32; 2],
    thickness: f32,
    tolerance: GeometryTolerance,
) -> bool {
    if !finite(point) || !finite(center) || !finite(radius) {
        return false;
    }
    let rx = radius[0].abs();
    let ry = radius[1].abs();
    if rx <= 0.0 || ry <= 0.0 {
        return false;
    }
    let Some(half) = tolerance.half_stroke(thickness, [rx * 2.0, ry * 2.0]) else {
        return false;
    };
    if !ellipse_contains_fill(point, center, [rx + half, ry + half]) {
        return false;
    }
    let inner = [rx - half, ry - half];
    inner[0] <= 0.0 || inner[1] <= 0.0 || !ellipse_contains_fill(point, center, inner)
}
impl PathGeometry {
    /// Original geometric fill query: 16 quadratic / 24 cubic samples, original
    /// bounded arc flattening, rational admission, boundary padding and winding.
    /// Combined paths visit both actual operands with the source 256-depth gate.
    /// None means source rejection, distinct from a valid empty or outside result.
    pub fn contains_fill(
        &self,
        point: [f32; 2],
        tolerance: GeometryTolerance,
    ) -> Result<Option<bool>, Error> {
        contains_path(self, point, tolerance, 0)
    }
}
fn contains_path(
    path: &PathGeometry,
    point: [f32; 2],
    tolerance: GeometryTolerance,
    depth: u16,
) -> Result<Option<bool>, Error> {
    if !finite(point) || !tolerance.value.is_finite() {
        return Ok(None);
    }
    if let Some(combined) = &path.deferred {
        if depth >= 256 {
            return Ok(None);
        }
        let (a, b) = combined.operands();
        let Some(a) = contains_path(a, point, tolerance, depth + 1)? else {
            return Ok(None);
        };
        let Some(b) = contains_path(b, point, tolerance, depth + 1)? else {
            return Ok(None);
        };
        return Ok(Some(match combined.operation() {
            BooleanOperation::Difference => a && !b,
            BooleanOperation::Intersect => a && b,
            BooleanOperation::Union => a || b,
            BooleanOperation::ExclusiveOr => a != b,
            BooleanOperation::ReverseDifference => b && !a,
        }));
    }
    let mut polygons = Vec::new();
    polygons
        .try_reserve_exact(path.figures.len().max(1))
        .map_err(|_| Error::Capacity)?;
    let mut min = [f32::INFINITY; 2];
    let mut max = [f32::NEG_INFINITY; 2];
    for figure in &path.figures {
        if !figure.is_filled {
            continue;
        }
        let Some(polygon) = figure_polygon(figure)? else {
            return Ok(None);
        };
        if polygon.len() < 3 {
            continue;
        }
        for p in &polygon {
            min = [minimum(min[0], p[0]), minimum(min[1], p[1])];
            max = [maximum(max[0], p[0]), maximum(max[1], p[1])];
        }
        polygons.push(polygon);
    }
    if polygons.is_empty() {
        return Ok(Some(false));
    }
    let Some(padding) = tolerance.padding([(max[0] - min[0]).abs(), (max[1] - min[1]).abs()])
    else {
        return Ok(None);
    };
    let boundary = maximum(padding, EPSILON);
    let mut winding = 0i32;
    let mut even_odd = false;
    for polygon in &polygons {
        if on_boundary(polygon, point, boundary) {
            return Ok(Some(true));
        }
        if path.fill_rule == FillRule::EvenOdd {
            if contains_even_odd(polygon, point) {
                even_odd = !even_odd;
            }
        } else {
            winding = winding.wrapping_add(winding_contribution(polygon, point));
        }
    }
    Ok(Some(if path.fill_rule == FillRule::EvenOdd {
        even_odd
    } else {
        winding != 0
    }))
}
fn figure_polygon(figure: &PathFigure) -> Result<Option<Vec<[f32; 2]>>, Error> {
    if !finite(figure.start) {
        return Ok(None);
    }
    let mut capacity = 1usize;
    for segment in &figure.segments {
        capacity = capacity
            .checked_add(match segment.kind {
                PathSegmentKind::Line(_) => 1,
                PathSegmentKind::Quadratic(_) | PathSegmentKind::RationalQuadratic { .. } => 16,
                _ => 24,
            })
            .ok_or(Error::Capacity)?;
    }
    let mut points = Vec::new();
    points
        .try_reserve_exact(capacity)
        .map_err(|_| Error::Capacity)?;
    points.push(figure.start);
    let mut current = figure.start;
    for segment in &figure.segments {
        match segment.kind {
            PathSegmentKind::Line(end) => {
                if !add_point(&mut points, end)? {
                    return Ok(None);
                }
            }
            PathSegmentKind::Quadratic([control, end]) => {
                if !finite(control) || !finite(end) {
                    return Ok(None);
                }
                for index in 1..=16 {
                    if !add_point(
                        &mut points,
                        evaluate_quadratic([current, control, end], index as f32 / 16.0),
                    )? {
                        return Ok(None);
                    }
                }
            }
            PathSegmentKind::Cubic([first, second, end]) => {
                if !finite(first) || !finite(second) || !finite(end) {
                    return Ok(None);
                }
                for index in 1..=24 {
                    if !add_point(
                        &mut points,
                        hit_cubic([current, first, second, end], index as f32 / 24.0),
                    )? {
                        return Ok(None);
                    }
                }
            }
            PathSegmentKind::RationalQuadratic {
                points: [control, end],
                weight,
            } => {
                let scale = coordinate_scale(&[current, control, end]);
                if !finite(control)
                    || !finite(end)
                    || !weight.is_finite()
                    || weight <= 0.0
                    || f64::from(weight) > f64::from(f32::MAX) / (4.0 * scale)
                {
                    return Ok(None);
                }
                for index in 1..=16 {
                    let t = index as f32 / 16.0;
                    let u = 1.0 - t;
                    let a = u * u;
                    let b = 2.0 * weight * u * t;
                    let c = t * t;
                    let sum = add(add(mul(current, a), mul(control, b)), mul(end, c));
                    let divisor = a + b + c;
                    if !add_point(&mut points, [sum[0] / divisor, sum[1] / divisor])? {
                        return Ok(None);
                    }
                }
            }
            PathSegmentKind::RationalCubic {
                points: [first, second, end],
                weights: [w1, w2],
            } => {
                let limit =
                    f64::from(f32::MAX) / (8.0 * coordinate_scale(&[current, first, second, end]));
                if !finite(first)
                    || !finite(second)
                    || !finite(end)
                    || !w1.is_finite()
                    || !w2.is_finite()
                    || w1 <= 0.0
                    || w2 <= 0.0
                    || f64::from(w1) > limit
                    || f64::from(w2) > limit
                {
                    return Ok(None);
                }
                for index in 1..=24 {
                    let t = index as f32 / 24.0;
                    let u = 1.0 - t;
                    let a = u * u * u;
                    let b = 3.0 * w1 * u * u * t;
                    let c = 3.0 * w2 * u * t * t;
                    let d = t * t * t;
                    let sum = add(
                        add(add(mul(current, a), mul(first, b)), mul(second, c)),
                        mul(end, d),
                    );
                    let divisor = a + b + c + d;
                    if !add_point(&mut points, [sum[0] / divisor, sum[1] / divisor])? {
                        return Ok(None);
                    }
                }
            }
            PathSegmentKind::Arc(arc) => {
                let flattened = arc.flattened(current, std::f32::consts::PI / 8.0)?;
                for p in flattened.into_iter().skip(1) {
                    if !add_point(&mut points, p)? {
                        return Ok(None);
                    }
                }
            }
        }
        current = segment.end();
    }
    Ok(Some(points))
}
fn coordinate_scale(points: &[[f32; 2]]) -> f64 {
    // All nonfinite coordinates are rejected separately before evaluation.
    f64::from(
        points
            .iter()
            .fold(1.0f32, |v, p| maximum(v, maximum(p[0].abs(), p[1].abs()))),
    )
}
fn add_point(points: &mut Vec<[f32; 2]>, point: [f32; 2]) -> Result<bool, Error> {
    if !finite(point) {
        return Ok(false);
    }
    if points
        .last()
        .is_none_or(|last| distance_squared(*last, point) > EPSILON * EPSILON)
    {
        points.try_reserve(1).map_err(|_| Error::Capacity)?;
        points.push(point);
    }
    Ok(true)
}
fn hit_cubic(p: [[f32; 2]; 4], t: f32) -> [f32; 2] {
    // This source hit-query polynomial groups tt/uu before multiplication;
    // the independent curve/dash evaluator intentionally keeps its own order.
    let u = 1.0 - t;
    let tt = t * t;
    let uu = u * u;
    add(
        add(
            add(mul(p[0], uu * u), mul(p[1], 3.0 * uu * t)),
            mul(p[2], 3.0 * u * tt),
        ),
        mul(p[3], tt * t),
    )
}
fn contains_even_odd(points: &[[f32; 2]], point: [f32; 2]) -> bool {
    let mut inside = false;
    let mut previous = points.len() - 1;
    for (index, a) in points.iter().enumerate() {
        let b = points[previous];
        if (a[1] > point[1]) != (b[1] > point[1]) {
            let x = (b[0] - a[0]) * (point[1] - a[1]) / (b[1] - a[1]) + a[0];
            if point[0] < x {
                inside = !inside;
            }
        }
        previous = index;
    }
    inside
}
fn winding_contribution(points: &[[f32; 2]], point: [f32; 2]) -> i32 {
    let mut result = 0i32;
    let mut previous = points.len() - 1;
    for (index, b) in points.iter().enumerate() {
        let a = points[previous];
        let c = cross(sub(*b, a), sub(point, a));
        if a[1] <= point[1] {
            if b[1] > point[1] && c > 0.0 {
                result = result.wrapping_add(1);
            }
        } else if b[1] <= point[1] && c < 0.0 {
            result = result.wrapping_sub(1);
        }
        previous = index;
    }
    result
}
fn on_boundary(points: &[[f32; 2]], point: [f32; 2], tolerance: f32) -> bool {
    let tolerance_squared = tolerance * tolerance;
    let mut previous = points.len() - 1;
    for (index, end) in points.iter().enumerate() {
        let start = points[previous];
        let segment = sub(*end, start);
        let size_squared = dot(segment, segment);
        let distance = if size_squared <= EPSILON * EPSILON {
            distance_squared(point, start)
        } else {
            let t = (dot(sub(point, start), segment) / size_squared).clamp(0.0, 1.0);
            distance_squared(point, add(start, mul(segment, t)))
        };
        if distance <= tolerance_squared {
            return true;
        }
        previous = index;
    }
    false
}
