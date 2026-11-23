//! Original per-glyph strip intercept producer, including control-hull extrema.
//! Paint and rotation/scale runs retain their original independent exclusion rules.
use super::*;
use crate::{paint::Paint, vector::PathSegmentKind};
use wide::f64x2 as V;
const EPS: f64 = 1e-12;
impl TextBlob {
    /// Original ordered minimum/maximum pair per admitted ordinary glyph. Paint
    /// is deliberately ignored; intervals are neither sorted nor unioned.
    pub fn intercepts(&self, lower: f32, upper: f32, paint: Option<&Paint>) -> Result<Vec<f32>> {
        if !valid(lower, upper) {
            return Ok(Vec::new());
        }
        let count = self.glyphs().len().checked_mul(2).ok_or(Error::Capacity)?;
        let mut output = allocate(count, 0.0)?;
        let count = self.intercepts_into(lower, upper, &mut output, paint)?;
        output.truncate(count);
        Ok(output)
    }
    /// Original full required count, independent of destination capacity.
    pub fn count_intercepts(&self, lower: f32, upper: f32, paint: Option<&Paint>) -> Result<usize> {
        self.intercepts_into(lower, upper, &mut [], paint)
    }
    /// Original prefix write: a short slice receives available values, untouched
    /// trailing slots stay unchanged, and the return value reports the full count.
    /// NaN/reversed bands perform no writes; infinities and equal edges are valid.
    pub fn intercepts_into(
        &self,
        lower: f32,
        upper: f32,
        output: &mut [f32],
        _paint: Option<&Paint>,
    ) -> Result<usize> {
        if !valid(lower, upper) {
            return Ok(0);
        }
        let mut count = 0usize;
        for run in self.runs() {
            if run.transforms.is_some() {
                continue;
            }
            for (index, position) in run.positions.iter().enumerate() {
                let Some(path) = run.glyph_path(index)? else {
                    continue;
                };
                let Some(interval) =
                    path_intercept(&path, lower - position[1], upper - position[1])?
                else {
                    continue;
                };
                if let Some(value) = output.get_mut(count) {
                    *value = interval[0] + position[0];
                }
                if let Some(value) = output.get_mut(count + 1) {
                    *value = interval[1] + position[0];
                }
                count = count.checked_add(2).ok_or(Error::Capacity)?;
            }
        }
        Ok(count)
    }
}
fn valid(lower: f32, upper: f32) -> bool {
    !lower.is_nan() && !upper.is_nan() && lower <= upper
}
struct Interval {
    min: f32,
    max: f32,
}
impl Interval {
    fn add(&mut self, value: f32) {
        if value.is_finite() {
            self.min = minimum(self.min, value);
            self.max = maximum(self.max, value);
        }
    }
    fn point(&mut self, point: [f32; 2], lower: f32, upper: f32) {
        if lower < point[1] && point[1] < upper {
            self.add(point[0]);
        }
    }
}
fn path_intercept(path: &RetainedPath, lower: f32, upper: f32) -> Result<Option<[f32; 2]>> {
    let bounds = path.bounds();
    if bounds[3] < lower || upper < bounds[1] {
        return Ok(None);
    }
    let geometry = path.geometry()?;
    if geometry.deferred.is_some() {
        return Err(Error::UnsupportedExecution.into());
    }
    let mut interval = Interval {
        min: f32::INFINITY,
        max: f32::NEG_INFINITY,
    };
    for figure in &geometry.figures {
        let mut current = figure.start;
        for segment in &figure.segments {
            match segment.kind {
                PathSegmentKind::Line(end) => line(current, end, lower, upper, &mut interval),
                PathSegmentKind::Quadratic([control, end]) => {
                    quadratic([current, control, end], lower, upper, &mut interval)
                }
                PathSegmentKind::Cubic([a, b, end]) => {
                    cubic([current, a, b, end], lower, upper, &mut interval)
                }
                PathSegmentKind::Arc(arc) => {
                    let points = arc.flattened(current, std::f32::consts::PI / 8.0)?;
                    for points in points.windows(2) {
                        line(points[0], points[1], lower, upper, &mut interval);
                    }
                }
                PathSegmentKind::RationalQuadratic { .. }
                | PathSegmentKind::RationalCubic { .. } => {
                    return Err(Error::UnsupportedExecution.into());
                }
            }
            current = segment.end();
        }
        if figure.is_closed && current != figure.start {
            line(current, figure.start, lower, upper, &mut interval);
        }
    }
    Ok((interval.min < interval.max).then_some([interval.min, interval.max]))
}
fn line(start: [f32; 2], end: [f32; 2], lower: f32, upper: f32, out: &mut Interval) {
    for axis in [lower, upper] {
        if !axis.is_finite() {
            continue;
        }
        let t = (axis - start[1]) / (end[1] - start[1]);
        if t >= 0.0 && t < 1.0 {
            out.add(start[0] + t * (end[0] - start[0]));
        }
    }
    out.point(start, lower, upper);
    out.point(end, lower, upper);
}
fn quadratic(p: [[f32; 2]; 3], lower: f32, upper: f32, out: &mut Interval) {
    let min = minimum(p[0][1], minimum(p[1][1], p[2][1]));
    let max = maximum(p[0][1], maximum(p[1][1], p[2][1]));
    if upper < min || lower >= max {
        return;
    }
    for axis in [lower, upper] {
        if !axis.is_finite() {
            continue;
        }
        let mut roots = [0.0; 3];
        let count = solve_quadratic(
            f64::from(p[0][1]) - 2.0 * f64::from(p[1][1]) + f64::from(p[2][1]),
            2.0 * f64::from(p[1][1] - p[0][1]),
            f64::from(p[0][1] - axis),
            &mut roots,
        );
        let t = V::from([roots[0], roots[1]]);
        let u = V::splat(1.0) - t;
        let x = (u * u * V::splat(f64::from(p[0][0]))
            + V::splat(2.0) * u * t * V::splat(f64::from(p[1][0]))
            + t * t * V::splat(f64::from(p[2][0])))
        .to_array();
        for i in 0..count {
            if roots[i] < 0.0 || roots[i] > 1.0 {
                continue;
            }
            out.add(x[i] as f32);
        }
    }
    for point in p {
        out.point(point, lower, upper);
    }
}
fn cubic(p: [[f32; 2]; 4], lower: f32, upper: f32, out: &mut Interval) {
    let min = minimum(minimum(p[0][1], p[1][1]), minimum(p[2][1], p[3][1]));
    let max = maximum(maximum(p[0][1], p[1][1]), maximum(p[2][1], p[3][1]));
    if upper < min || lower >= max {
        return;
    }
    for axis in [lower, upper] {
        if !axis.is_finite() {
            continue;
        }
        let mut roots = [0.0; 3];
        let count = solve_cubic(
            f64::from(-p[0][1]) + 3.0 * f64::from(p[1][1]) - 3.0 * f64::from(p[2][1])
                + f64::from(p[3][1]),
            3.0 * f64::from(p[0][1]) - 6.0 * f64::from(p[1][1]) + 3.0 * f64::from(p[2][1]),
            -3.0 * f64::from(p[0][1]) + 3.0 * f64::from(p[1][1]),
            f64::from(p[0][1] - axis),
            &mut roots,
        );
        for pair in roots[..count].chunks(2) {
            let t = V::from([pair[0], pair.get(1).copied().unwrap_or(0.0)]);
            let u = V::splat(1.0) - t;
            let x = (u * u * u * V::splat(f64::from(p[0][0]))
                + V::splat(3.0) * u * u * t * V::splat(f64::from(p[1][0]))
                + V::splat(3.0) * u * t * t * V::splat(f64::from(p[2][0]))
                + t * t * t * V::splat(f64::from(p[3][0])))
            .to_array();
            for (i, &t) in pair.iter().enumerate() {
                if t < 0.0 || t > 1.0 {
                    continue;
                }
                out.add(x[i] as f32);
            }
        }
    }
    for point in p {
        out.point(point, lower, upper);
    }
}
fn solve_quadratic(a: f64, b: f64, c: f64, roots: &mut [f64; 3]) -> usize {
    if a.abs() <= EPS {
        if b.abs() <= EPS {
            return 0;
        }
        roots[0] = -c / b;
        return 1;
    }
    let d = b * b - 4.0 * a * c;
    if d < -EPS {
        return 0;
    }
    if d.abs() <= EPS {
        roots[0] = -b / (2.0 * a);
        return 1;
    }
    let numerator = -0.5 * (b + d.sqrt().copysign(b));
    roots[0] = numerator / a;
    roots[1] = c / numerator;
    2
}
fn solve_cubic(a: f64, b: f64, c: f64, d: f64, roots: &mut [f64; 3]) -> usize {
    if a.abs() <= EPS {
        return solve_quadratic(b, c, d, roots);
    }
    let b = b / a;
    let c = c / a;
    let d = d / a;
    let p = c - b * b / 3.0;
    let q = 2.0 * b * b * b / 27.0 - b * c / 3.0 + d;
    let discriminant = q * q / 4.0 + p * p * p / 27.0;
    let offset = b / 3.0;
    if discriminant > EPS {
        let root = discriminant.sqrt();
        roots[0] = (-q / 2.0 + root).cbrt() + (-q / 2.0 - root).cbrt() - offset;
        return 1;
    }
    if discriminant.abs() <= EPS {
        let root = (-q / 2.0).cbrt();
        roots[0] = 2.0 * root - offset;
        roots[1] = -root - offset;
        return if (roots[0] - roots[1]).abs() <= EPS {
            1
        } else {
            2
        };
    }
    let radius = 2.0 * (-p / 3.0).sqrt();
    let cosine = (-q / (2.0 * (-(p * p * p) / 27.0).sqrt())).clamp(-1.0, 1.0);
    let angle = cosine.acos() / 3.0;
    roots[0] = radius * angle.cos() - offset;
    roots[1] = radius * (angle - 2.0 * std::f64::consts::PI / 3.0).cos() - offset;
    roots[2] = radius * (angle - 4.0 * std::f64::consts::PI / 3.0).cos() - offset;
    3
}
