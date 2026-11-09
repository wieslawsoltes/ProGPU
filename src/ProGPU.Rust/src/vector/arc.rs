//! Original endpoint arcs, target transforms, analytic extrema and explicit
//! bounded flattening. Rendering does not implicitly select the flattening API.
use super::*;
use std::f32::consts::PI;
const EPSILON: f32 = 0.00001;
const TWO_PI: f32 = 2.0 * PI;
/// Original successful center conversion, including chord-corrected radii.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ArcCenter {
    /// Original center coordinates.
    pub center: [f32; 2],
    /// Original adjusted positive radius pair.
    pub radii: [f32; 2],
    /// Original first analytic angle.
    pub theta: f32,
    /// Original signed angular sweep, retaining exact antipodal half turns.
    pub delta: f32,
}
impl ArcCenter {
    /// Original explicitly ordered point expression, with rotation in degrees.
    pub fn evaluate(self, rotation: f32, theta: f32) -> [f32; 2] {
        let phi = rotation * PI / 180.0;
        let cos_phi = phi.cos();
        let sin_phi = phi.sin();
        let cos_theta = theta.cos();
        let sin_theta = theta.sin();
        [
            self.radii[0] * cos_theta * cos_phi - self.radii[1] * sin_theta * sin_phi
                + self.center[0],
            self.radii[0] * cos_theta * sin_phi
                + self.radii[1] * sin_theta * cos_phi
                + self.center[1],
        ]
    }
}
/// Original transformed analytic ellipse axes, preserving full affine shear.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ArcShaderParameters {
    /// Original target-space center.
    pub center: [f32; 2],
    /// Original transformed X axis.
    pub axis_x: [f32; 2],
    /// Original transformed Y axis.
    pub axis_y: [f32; 2],
    /// Original first angle.
    pub theta: f32,
    /// Original signed sweep.
    pub delta: f32,
}
impl ArcShaderParameters {
    /// Original ordered center/axis expression, distinct from endpoint-form evaluation.
    pub fn evaluate(self, theta: f32) -> [f32; 2] {
        add(
            add(self.center, mul(self.axis_x, theta.cos())),
            mul(self.axis_y, theta.sin()),
        )
    }
    /// Original analytic axis-extrema bounds, without an envelope substitution.
    pub fn bounds(self) -> Option<[f32; 4]> {
        if !finite(self.center)
            || !finite(self.axis_x)
            || !finite(self.axis_y)
            || !self.theta.is_finite()
            || !self.delta.is_finite()
            || self.delta.abs() <= EPSILON
        {
            return None;
        }
        let a = self.evaluate(self.theta);
        let b = self.evaluate(self.theta + self.delta);
        let mut bounds = [
            minimum(a[0], b[0]),
            minimum(a[1], b[1]),
            maximum(a[0], b[0]),
            maximum(a[1], b[1]),
        ];
        let x = self.axis_y[0].atan2(self.axis_x[0]);
        let y = self.axis_y[1].atan2(self.axis_x[1]);
        for theta in [x, x + PI, y, y + PI] {
            if inside(theta, self.theta, self.delta) {
                expand(&mut bounds, self.evaluate(theta));
            }
        }
        bounds.iter().all(|v| v.is_finite()).then_some(bounds)
    }
}
impl ArcSegment {
    /// Original optional endpoint-to-center conversion. Degenerate/nonfinite
    /// admission retains source tolerances and corrected-radius arithmetic.
    pub fn center(self, start: [f32; 2]) -> Option<ArcCenter> {
        let end = self.end;
        let mut rx = self.size[0].abs();
        let mut ry = self.size[1].abs();
        let chord = sub(start, end);
        if !finite(start)
            || !finite(end)
            || !finite(self.size)
            || !self.rotation.is_finite()
            || dot(chord, chord) <= EPSILON * EPSILON
            || rx <= EPSILON
            || ry <= EPSILON
        {
            return None;
        }
        let phi = self.rotation * PI / 180.0;
        let cos_phi = phi.cos();
        let sin_phi = phi.sin();
        let dx = (start[0] - end[0]) * 0.5;
        let dy = (start[1] - end[1]) * 0.5;
        let x = cos_phi * dx + sin_phi * dy;
        let y = -sin_phi * dx + cos_phi * dy;
        let mut rx2 = rx * rx;
        let mut ry2 = ry * ry;
        let x2 = x * x;
        let y2 = y * y;
        let check = x2 / rx2 + y2 / ry2;
        if !check.is_finite() {
            return None;
        }
        if check > 1.0 {
            let scale = check.sqrt();
            rx *= scale;
            ry *= scale;
            rx2 = rx * rx;
            ry2 = ry * ry;
        }
        let denominator = rx2 * y2 + ry2 * x2;
        if denominator <= 0.0 || !denominator.is_finite() {
            return None;
        }
        let sign = if self.large == (self.sweep == SweepDirection::Clockwise) {
            -1.0
        } else {
            1.0
        };
        let mut square = (rx2 * ry2 - rx2 * y2 - ry2 * x2) / denominator;
        if !square.is_finite() {
            return None;
        }
        if square < 0.0 {
            square = 0.0;
        }
        let coefficient = sign * square.sqrt();
        let cx = coefficient * ((rx * y) / ry);
        let cy = coefficient * (-((ry * x) / rx));
        let center = [
            cos_phi * cx - sin_phi * cy + (start[0] + end[0]) * 0.5,
            sin_phi * cx + cos_phi * cy + (start[1] + end[1]) * 0.5,
        ];
        let ux = (x - cx) / rx;
        let uy = (y - cy) / ry;
        let vx = (-x - cx) / rx;
        let vy = (-y - cy) / ry;
        let theta = uy.atan2(ux);
        let theta2 = vy.atan2(vx);
        let mut delta = if square == 0.0 {
            if self.sweep == SweepDirection::Clockwise {
                PI
            } else {
                -PI
            }
        } else {
            theta2 - theta
        };
        if self.sweep == SweepDirection::Clockwise {
            if delta < 0.0 {
                delta += TWO_PI;
            }
        } else if delta > 0.0 {
            delta -= TWO_PI;
        }
        if !finite(center) || ![theta, delta, rx, ry].iter().all(|v| v.is_finite()) {
            return None;
        }
        Some(ArcCenter {
            center,
            radii: [rx, ry],
            theta,
            delta,
        })
    }
    /// Original endpoint-seeded bounds with only in-sweep analytic extrema.
    pub fn bounds(self, start: [f32; 2]) -> Option<[f32; 4]> {
        let center = self.center(start)?;
        let mut bounds = [
            minimum(start[0], self.end[0]),
            minimum(start[1], self.end[1]),
            maximum(start[0], self.end[0]),
            maximum(start[1], self.end[1]),
        ];
        let phi = self.rotation * PI / 180.0;
        let cos = phi.cos();
        let sin = phi.sin();
        let x = (-center.radii[1] * sin).atan2(center.radii[0] * cos);
        let y = (center.radii[1] * cos).atan2(center.radii[0] * sin);
        for theta in [x, x + PI, y, y + PI] {
            if inside(theta, center.theta, center.delta) {
                expand(&mut bounds, center.evaluate(self.rotation, theta));
            }
        }
        bounds.iter().all(|v| v.is_finite()).then_some(bounds)
    }
    /// Original complete transformed arc, preserving similarity and positive
    /// axis fast routes before principal-axis decomposition. Reflections reverse
    /// only sweep direction; the original large-arc and corrected radii survive.
    pub fn transformed(self, start: [f32; 2], matrix: Matrix4) -> Option<([f32; 2], Self)> {
        if !affine(matrix) {
            return None;
        }
        let transformed_start = matrix.transform_point(start);
        let end = matrix.transform_point(self.end);
        if !finite(transformed_start) || !finite(end) {
            return None;
        }
        if let Some((scale, rotation)) = similarity(matrix) {
            return Some((
                transformed_start,
                Self {
                    end,
                    size: self.size.map(|v| v.abs() * scale),
                    rotation: normalize_rotation(self.rotation + rotation),
                    ..self
                },
            ));
        }
        let m = matrix.0;
        if normalize_axis(self.rotation).abs() <= EPSILON
            && m[0][1].abs() <= EPSILON
            && m[1][0].abs() <= EPSILON
            && m[0][0] > EPSILON
            && m[1][1] > EPSILON
        {
            return Some((
                transformed_start,
                Self {
                    end,
                    size: [self.size[0].abs() * m[0][0], self.size[1].abs() * m[1][1]],
                    rotation: normalize_rotation(self.rotation),
                    ..self
                },
            ));
        }
        let center = self.center(start)?;
        let determinant = det(matrix);
        if !determinant.is_finite() || determinant.abs() <= EPSILON {
            return None;
        }
        let phi = self.rotation * PI / 180.0;
        let cos = phi.cos();
        let sin = phi.sin();
        let x = matrix.direction([center.radii[0] * cos, center.radii[0] * sin]);
        let y = matrix.direction([-center.radii[1] * sin, center.radii[1] * cos]);
        let (size, rotation) = principal_axes(x, y)?;
        let sweep = if determinant < 0.0 {
            match self.sweep {
                SweepDirection::Clockwise => SweepDirection::Counterclockwise,
                SweepDirection::Counterclockwise => SweepDirection::Clockwise,
            }
        } else {
            self.sweep
        };
        Some((
            transformed_start,
            Self {
                end,
                size,
                rotation,
                sweep,
                ..self
            },
        ))
    }
    /// Original affine-axis shader transport; this keeps actual axes rather than
    /// decomposing them back into endpoint-form radii and rotation.
    pub fn shader_parameters(
        self,
        start: [f32; 2],
        matrix: Matrix4,
    ) -> Option<ArcShaderParameters> {
        let source = self.center(start)?;
        let phi = self.rotation * PI / 180.0;
        let cos = phi.cos();
        let sin = phi.sin();
        let center = matrix.transform_point(source.center);
        let axis_x = matrix.direction([source.radii[0] * cos, source.radii[0] * sin]);
        let axis_y = matrix.direction([-source.radii[1] * sin, source.radii[1] * cos]);
        if !finite(center)
            || !finite(axis_x)
            || !finite(axis_y)
            || !source.theta.is_finite()
            || !source.delta.is_finite()
            || dot(axis_x, axis_x) <= EPSILON * EPSILON
            || dot(axis_y, axis_y) <= EPSILON * EPSILON
            || source.delta.abs() <= EPSILON
        {
            return None;
        }
        Some(ArcShaderParameters {
            center,
            axis_x,
            axis_y,
            theta: source.theta,
            delta: source.delta,
        })
    }
    /// Original default explicit flattening angle (pi/8); never selected by rendering.
    pub const DEFAULT_FLATTEN_ANGLE: f32 = PI / 8.0;
    /// Original maximum explicit flattening segment count.
    pub const MAX_FLATTEN_SEGMENTS: usize = 64;
    /// Original default arc-length sampling angle used for dash construction.
    pub const DEFAULT_DASH_ANGLE: f32 = PI / 64.0;
    /// Original bounded explicit flattening count, including degenerate fallback.
    pub fn flattened_segment_count(self, start: [f32; 2], max_angle: f32) -> usize {
        match self.center(start) {
            Some(c) => count_segments(c.delta, max_angle),
            None => {
                usize::from(dot(sub(start, self.end), sub(start, self.end)) > EPSILON * EPSILON)
            }
        }
    }
    /// Explicit source flattening with original exact endpoints and at most 64
    /// spans. This is an opt-in geometry utility, not the GPU arc representation.
    pub fn flattened(self, start: [f32; 2], max_angle: f32) -> Result<Vec<[f32; 2]>, Error> {
        let Some(c) = self.center(start) else {
            return Ok(
                if dot(sub(start, self.end), sub(start, self.end)) > EPSILON * EPSILON {
                    vec![start, self.end]
                } else {
                    vec![start]
                },
            );
        };
        let count = count_segments(c.delta, max_angle);
        let mut points = Vec::new();
        points
            .try_reserve_exact(count + 1)
            .map_err(|_| Error::Capacity)?;
        points.push(start);
        for i in 1..count {
            let t = i as f32 / count as f32;
            points.push(c.evaluate(self.rotation, c.theta + t * c.delta));
        }
        points.push(self.end);
        Ok(points)
    }
    /// Original analytic subarc, with independently preserved exact endpoint
    /// shortcuts and no reconstruction from a flattened polyline.
    pub fn subarc(self, start: [f32; 2], first: f32, last: f32) -> Option<([f32; 2], Self)> {
        if !first.is_finite() || !last.is_finite() || last <= first {
            return None;
        }
        let first = first.clamp(0.0, 1.0);
        let last = last.clamp(0.0, 1.0);
        if last <= first + EPSILON {
            return None;
        }
        let c = self.center(start)?;
        let a = c.theta + c.delta * first;
        let b = c.theta + c.delta * last;
        let delta = b - a;
        let sub_start = if first <= EPSILON {
            start
        } else {
            c.evaluate(self.rotation, a)
        };
        let end = if last >= 1.0 - EPSILON {
            self.end
        } else {
            c.evaluate(self.rotation, b)
        };
        if !finite(sub_start)
            || !finite(end)
            || dot(sub(sub_start, end), sub(sub_start, end)) <= EPSILON * EPSILON
        {
            return None;
        }
        Some((
            sub_start,
            Self {
                end,
                size: c.radii,
                large: delta.abs() > PI,
                ..self
            },
        ))
    }
}
fn count_segments(delta: f32, mut angle: f32) -> usize {
    if !angle.is_finite() || angle <= EPSILON {
        angle = ArcSegment::DEFAULT_FLATTEN_ANGLE;
    }
    ((delta.abs() / angle).ceil() as usize).clamp(1, ArcSegment::MAX_FLATTEN_SEGMENTS)
}
fn expand(b: &mut [f32; 4], p: [f32; 2]) {
    for axis in 0..2 {
        b[axis] = minimum(b[axis], p[axis]);
        b[axis + 2] = maximum(b[axis + 2], p[axis]);
    }
}
fn inside(theta: f32, start: f32, delta: f32) -> bool {
    if !theta.is_finite() || !start.is_finite() || !delta.is_finite() || delta.abs() <= EPSILON {
        return false;
    }
    if delta.abs() >= TWO_PI - EPSILON {
        return true;
    }
    let mut distance = if delta > 0.0 {
        theta - start
    } else {
        start - theta
    } % TWO_PI;
    if distance < 0.0 {
        distance += TWO_PI;
    }
    distance <= delta.abs() + EPSILON
}
fn normalize_rotation(angle: f32) -> f32 {
    if !angle.is_finite() {
        return 0.0;
    }
    let mut angle = angle % 360.0;
    if angle < 0.0 {
        angle += 360.0;
    }
    angle
}
fn normalize_axis(angle: f32) -> f32 {
    let mut angle = normalize_rotation(angle);
    if angle > 180.0 {
        angle -= 360.0;
    }
    if angle > 90.0 {
        angle -= 180.0;
    } else if angle < -90.0 {
        angle += 180.0;
    }
    angle
}
fn det(matrix: Matrix4) -> f32 {
    let m = matrix.0;
    m[0][0] * m[1][1] - m[0][1] * m[1][0]
}
fn affine(matrix: Matrix4) -> bool {
    let m = matrix.0;
    m[0][2].abs() <= EPSILON
        && m[0][3].abs() <= EPSILON
        && m[1][2].abs() <= EPSILON
        && m[1][3].abs() <= EPSILON
        && m[2][0].abs() <= EPSILON
        && m[2][1].abs() <= EPSILON
        && (m[2][2] - 1.0).abs() <= EPSILON
        && m[2][3].abs() <= EPSILON
        && m[3][2].abs() <= EPSILON
        && (m[3][3] - 1.0).abs() <= EPSILON
        && [m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]
            .iter()
            .all(|v| v.is_finite())
}
fn similarity(matrix: Matrix4) -> Option<(f32, f32)> {
    let m = matrix.0;
    let x = [m[0][0], m[0][1]];
    let y = [m[1][0], m[1][1]];
    let sx = length(x);
    let sy = length(y);
    if !sx.is_finite()
        || !sy.is_finite()
        || sx <= EPSILON
        || sy <= EPSILON
        || (sx - sy).abs() > EPSILON
        || dot(x, y).abs() > EPSILON * sx * sy
        || det(matrix) <= EPSILON
    {
        return None;
    }
    Some(((sx + sy) * 0.5, m[0][1].atan2(m[0][0]) * 180.0 / PI))
}
fn principal_axes(x: [f32; 2], y: [f32; 2]) -> Option<([f32; 2], f32)> {
    if !finite(x) || !finite(y) {
        return None;
    }
    let xx = x[0] * x[0] + y[0] * y[0];
    let xy = x[0] * x[1] + y[0] * y[1];
    let yy = x[1] * x[1] + y[1] * y[1];
    let trace = xx + yy;
    let diff = xx - yy;
    let root = maximum(0.0, diff * diff + 4.0 * xy * xy).sqrt();
    let a = (trace + root) * 0.5;
    let b = (trace - root) * 0.5;
    if !a.is_finite() || !b.is_finite() || a <= EPSILON * EPSILON || b <= EPSILON * EPSILON {
        return None;
    }
    let mut axis = [1.0, 0.0];
    if xy.abs() > EPSILON || (a - xx).abs() > EPSILON {
        axis = [xy, a - xx];
        let n = length(axis);
        axis = if !n.is_finite() || n <= EPSILON {
            [1.0, 0.0]
        } else {
            [axis[0] / n, axis[1] / n]
        };
    }
    let radii = [a.sqrt(), b.sqrt()];
    let rotation = normalize_rotation(axis[1].atan2(axis[0]) * 180.0 / PI);
    (finite(radii) && rotation.is_finite()).then_some((radii, rotation))
}
