//! Original bounded NURBS evaluator and transform-sensitive spline sampling.
use super::*;
use crate::vector::{PathFigure, PathSegment, PathSegmentKind};
impl ArchiveGeometryDraw {
    pub(super) fn spline_scene(&self) -> Result<Scene, Error> {
        let mut scene = Scene::default();
        let c = &self.command;
        let Some(pen) = &c.pen else {
            return Ok(scene);
        };
        let pen = pen.to_pen().map_err(drawing_error)?;
        pen.validate_join_semantics()?;
        if !pen.options.thickness.is_finite()
            || (!pen.is_hairline() && pen.options.thickness <= 0.0)
        {
            return Ok(scene);
        }
        let points = selected(
            c.polyline_points.as_deref(),
            &self.points,
            c.point_buffer_offset,
            c.point_buffer_count,
        )?;
        let knots = selected(
            c.spline_knots.as_deref(),
            &self.doubles,
            c.double_buffer_offset,
            c.double_buffer_count,
        )?;
        let weights = selected(
            c.spline_weights.as_deref(),
            &self.doubles,
            c.weight_buffer_offset,
            c.weight_buffer_count,
        )?;
        if points.len() < 2 || knots.is_empty() {
            return Ok(scene);
        }
        let Some(domain) = domain(points, knots, c.spline_degree) else {
            self.stroke(&mut scene, Arc::new(polyline(points, c.is_closed)?))?;
            return Ok(scene);
        };
        let count = screen_count(points, self.transform);
        if count == 0 {
            return Ok(scene);
        }
        let mut sampled = Vec::new();
        sampled
            .try_reserve_exact(count + 1)
            .map_err(|_| Error::Capacity)?;
        let delta = (domain[1] - domain[0]) / count as f64;
        for i in 0..=count {
            sampled.push(evaluate(
                c.spline_degree as usize,
                points,
                knots,
                weights,
                domain[0] + i as f64 * delta,
            )?);
        }
        self.stroke(&mut scene, Arc::new(polyline(&sampled, c.is_closed)?))?;
        Ok(scene)
    }
}
fn selected<'a, T>(
    inline: Option<&'a [T]>,
    buffer: &'a [T],
    offset: i32,
    count: i32,
) -> Result<&'a [T], Error> {
    if let Some(inline) = inline.filter(|v| !v.is_empty()) {
        return Ok(inline);
    }
    if count <= 0 {
        return Ok(&[]);
    }
    let first = usize::try_from(offset).map_err(|_| Error::InvalidInput)?;
    let end = first.checked_add(count as usize).ok_or(Error::Capacity)?;
    buffer.get(first..end).ok_or(Error::InvalidInput)
}
fn domain(points: &[[f32; 2]], knots: &[f64], degree: i32) -> Option<[f64; 2]> {
    let d = usize::try_from(degree).ok()?;
    if points.len() < 2 || knots.len() < points.len().checked_add(d)?.checked_add(1)? {
        return None;
    }
    let end = knots.len().checked_sub(d)?.checked_sub(1)?;
    if d >= knots.len() || end <= d || end >= knots.len() {
        return None;
    }
    let range = [knots[d], knots[end]];
    (range.iter().all(|v| v.is_finite()) && range[1] > range[0]).then_some(range)
}
fn screen_count(points: &[[f32; 2]], transform: Matrix4) -> usize {
    if points.is_empty() {
        return 0;
    }
    let mut lo = [f32::MAX; 2];
    let mut hi = [f32::MIN; 2];
    for p in points {
        let p = transform.transform_point(*p);
        if p.iter().any(|v| !v.is_finite()) {
            return 0;
        }
        for a in 0..2 {
            lo[a] = lo[a].min(p[a]);
            hi[a] = hi[a].max(p[a]);
        }
    }
    let dx = hi[0] - lo[0];
    let dy = hi[1] - lo[1];
    let size = (dx * dx + dy * dy).sqrt();
    if !size.is_finite() || size < 2.0 {
        0
    } else if size < 20.0 {
        10
    } else if size < 80.0 {
        25
    } else if size < 250.0 {
        50
    } else {
        100
    }
}
fn evaluate(
    degree: usize,
    points: &[[f32; 2]],
    knots: &[f64],
    weights: &[f64],
    u: f64,
) -> Result<[f32; 2], Error> {
    evaluate_with_transform(degree, points, knots, weights, u, Matrix4::IDENTITY)
}
fn evaluate_with_transform(
    degree: usize,
    points: &[[f32; 2]],
    knots: &[f64],
    weights: &[f64],
    mut u: f64,
    transform: Matrix4,
) -> Result<[f32; 2], Error> {
    u = if u < knots[degree] { knots[degree] } else { u };
    let end = knots.len() - degree - 1;
    if u > knots[end] {
        u = knots[end];
    }
    let k = (degree..knots.len() - 1)
        .find(|&i| u >= knots[i] && u <= knots[i + 1])
        .unwrap_or(knots.len() - degree - 2);
    let count = degree.checked_add(1).ok_or(Error::Capacity)?;
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    for j in 0..=degree {
        let index = k as i64 - degree as i64 + j as i64;
        let value = if let Some(p) = usize::try_from(index).ok().and_then(|i| points.get(i)) {
            let w = weights.get(index as usize).copied().unwrap_or(1.0) as f32;
            [p[0] * w, p[1] * w, w]
        } else {
            [0.0; 3]
        };
        values.push(value);
    }
    for r in 1..=degree {
        for j in (r..=degree).rev() {
            let i = k as i64 - degree as i64 + j as i64;
            let lo = usize::try_from(i)
                .ok()
                .and_then(|i| knots.get(i))
                .ok_or(Error::InvalidInput)?;
            let hi = usize::try_from(i + degree as i64 + 1 - r as i64)
                .ok()
                .and_then(|i| knots.get(i))
                .ok_or(Error::InvalidInput)?;
            let denom = *hi - *lo;
            let alpha = if denom > 1e-9 {
                ((u - *lo) / denom) as f32
            } else {
                0.0
            };
            values[j] =
                std::array::from_fn(|a| (1.0 - alpha) * values[j - 1][a] + alpha * values[j][a]);
        }
    }
    let h = values[degree];
    let point = if h[2].abs() > 1e-9 {
        [h[0] / h[2], h[1] / h[2]]
    } else {
        [h[0], h[1]]
    };
    // Original EvaluatePoint always executes Vector2.Transform, even Identity.
    Ok(transform.transform_point(point))
}
fn polyline(points: &[[f32; 2]], closed: bool) -> Result<PathGeometry, Error> {
    if points.len() < 2 {
        return Ok(PathGeometry::default());
    }
    let mut f = PathFigure::new(points[0], closed);
    f.segments
        .try_reserve_exact(points.len() - 1)
        .map_err(|_| Error::Capacity)?;
    f.segments.extend(
        points[1..]
            .iter()
            .map(|p| PathSegment::new(PathSegmentKind::Line(*p))),
    );
    Ok(PathGeometry {
        figures: vec![f],
        ..Default::default()
    })
}

/// Original owned spline geometry helper, independent of target compilation.
/// Degree, knot order and weights retain source values; safe range failures are
/// reported before publishing a partially evaluated path or point generation.
pub struct SplineGeometry;
impl SplineGeometry {
    pub const MAX_SEGMENT_COUNT: usize = 100;
    /// Original fixed 100-segment source path, with invalid-domain polyline fallback.
    pub fn create_path(
        points: &[[f32; 2]],
        knots: &[f64],
        weights: &[f64],
        degree: i32,
        closed: bool,
    ) -> Result<PathGeometry, Error> {
        let Some(range) = domain(points, knots, degree) else {
            return polyline(points, closed);
        };
        let mut figure = PathFigure::new(
            evaluate(degree as usize, points, knots, weights, range[0])?,
            closed,
        );
        figure
            .segments
            .try_reserve_exact(Self::MAX_SEGMENT_COUNT)
            .map_err(|_| Error::Capacity)?;
        let delta = (range[1] - range[0]) / Self::MAX_SEGMENT_COUNT as f64;
        for i in 1..=Self::MAX_SEGMENT_COUNT {
            figure
                .segments
                .push(PathSegment::new(PathSegmentKind::Line(evaluate(
                    degree as usize,
                    points,
                    knots,
                    weights,
                    range[0] + i as f64 * delta,
                )?)));
        }
        Ok(PathGeometry {
            figures: vec![figure],
            ..Default::default()
        })
    }
    /// Exact original finite transformed control-envelope density selection.
    pub fn screen_segment_count(points: &[[f32; 2]], transform: Matrix4) -> usize {
        screen_count(points, transform)
    }
    /// Original domain selection; internal knot ordering is not rewritten.
    pub fn domain(points: &[[f32; 2]], knots: &[f64], degree: i32) -> Option<[f64; 2]> {
        domain(points, knots, degree)
    }
    /// Complete original evenly parameterized source points, transformed once
    /// after homogeneous evaluation. None preserves original invalid-domain/size.
    pub fn evaluate_points(
        points: &[[f32; 2]],
        knots: &[f64],
        weights: &[f64],
        degree: i32,
        transform: Matrix4,
        point_count: usize,
    ) -> Result<Option<Vec<[f32; 2]>>, Error> {
        if point_count < 2 {
            return Ok(None);
        }
        let Some(range) = domain(points, knots, degree) else {
            return Ok(None);
        };
        let mut output = Vec::new();
        output
            .try_reserve_exact(point_count)
            .map_err(|_| Error::Capacity)?;
        let delta = (range[1] - range[0]) / (point_count - 1) as f64;
        for i in 0..point_count {
            // `evaluate` includes the source Identity transform used by archive
            // compilation. Public nonidentity evaluation must avoid applying
            // an extra Identity arithmetic stage before its actual matrix.
            output.push(evaluate_with_transform(
                degree as usize,
                points,
                knots,
                weights,
                range[0] + i as f64 * delta,
                transform,
            )?);
        }
        Ok(Some(output))
    }
}
