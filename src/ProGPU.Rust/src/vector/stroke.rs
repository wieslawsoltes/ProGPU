//! Original bounded cap/join writers. Generic, clipped-miter and WPF policies
//! remain independent; actual tangent vectors never become fabricated endpoints.
use super::*;
use std::f32::consts::PI;

/// Original maximum triangles in one cap or join.
pub const MAX_STROKE_TRIANGLES: usize = 8;
pub(super) const EPSILON: f32 = 0.0001;

/// Ordered source triangle, before any sink-specific winding normalization.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct StrokeTriangle {
    /// Original p0, p1 and p2.
    pub points: [[f32; 2]; 3],
}
impl StrokeTriangle {
    fn new(a: [f32; 2], b: [f32; 2], c: [f32; 2]) -> Self {
        Self { points: [a, b, c] }
    }
}
/// Actual join origin and incoming/outgoing directions in the expansion frame.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StrokeJoinFrame {
    /// Original join point.
    pub point: [f32; 2],
    /// Original incoming tangent (not required to be normalized).
    pub incoming: [f32; 2],
    /// Original outgoing tangent (not required to be normalized).
    pub outgoing: [f32; 2],
}
impl StrokeJoinFrame {
    /// Original line join: subtract each adjacent endpoint exactly once.
    pub fn from_line(previous: [f32; 2], point: [f32; 2], next: [f32; 2]) -> Self {
        Self {
            point,
            incoming: sub(point, previous),
            outgoing: sub(next, point),
        }
    }
}
/// Original join inputs; source pen policy is selected independently.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StrokeJoin {
    /// Full join semantics, including independent MiterOrBevel.
    pub join: PenLineJoin,
    /// Thickness in the actual expansion frame.
    pub thickness: f32,
    /// Original raw limit; invalid values become one at the join boundary.
    pub miter_limit: f32,
    /// Original smooth metadata (generic suppresses; WPF selects Round).
    pub smooth: bool,
    /// Original bounded fan limit, clamped to 1..=8.
    pub max_round_segments: i32,
}
impl StrokeJoin {
    /// Write a generic join. Capacity is checked before any destination write.
    pub fn write(
        self,
        destination: &mut [StrokeTriangle],
        frame: StrokeJoinFrame,
    ) -> Result<usize, Error> {
        self.write_core(destination, frame, false, false, false)
    }
    /// Write explicit WPF clipping, reversal and smooth-to-Round semantics.
    pub fn write_wpf(
        self,
        destination: &mut [StrokeTriangle],
        frame: StrokeJoinFrame,
    ) -> Result<usize, Error> {
        self.write_core(destination, frame, true, true, false)
    }
    /// Original allocating generic utility. Its round radius is measured from
    /// the rounded outer point, independently of the writer's half thickness.
    pub fn create(self, frame: StrokeJoinFrame) -> Result<Vec<StrokeTriangle>, Error> {
        self.create_core(frame, false)
    }
    /// Original allocating WPF utility, retaining its distinct round radius.
    pub fn create_wpf(self, frame: StrokeJoinFrame) -> Result<Vec<StrokeTriangle>, Error> {
        self.create_core(frame, true)
    }
    fn create_core(self, frame: StrokeJoinFrame, wpf: bool) -> Result<Vec<StrokeTriangle>, Error> {
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = self.write_core(&mut triangles, frame, wpf, wpf, true)?;
        copy_triangles(&triangles[..count])
    }
    fn write_core(
        mut self,
        destination: &mut [StrokeTriangle],
        frame: StrokeJoinFrame,
        mut wpf: bool,
        clip: bool,
        allocating: bool,
    ) -> Result<usize, Error> {
        wpf &= self.join != PenLineJoin::MiterOrBevel;
        if wpf && self.smooth {
            self.join = PenLineJoin::Round;
            self.smooth = false;
        }
        let clip = clip && self.join == PenLineJoin::Miter;
        if self.smooth || !self.thickness.is_finite() || self.thickness <= EPSILON {
            return Ok(0);
        }
        let Some(incoming) = normalize_direction(frame.incoming) else {
            return Ok(0);
        };
        let Some(outgoing) = normalize_direction(frame.outgoing) else {
            return Ok(0);
        };
        let point = frame.point;
        let turn = cross(incoming, outgoing);
        let max_segments = self
            .max_round_segments
            .clamp(1, MAX_STROKE_TRIANGLES as i32) as usize;
        if turn.abs() <= EPSILON {
            if wpf && dot(incoming, outgoing) < -1.0 + EPSILON {
                return self.write_reversal(destination, point, incoming, max_segments);
            }
            return Ok(0);
        }
        let radius = self.thickness * 0.5;
        let sign = if turn > 0.0 { -1.0 } else { 1.0 };
        let previous = add(point, mul(mul(normal(incoming), sign), radius));
        let next = add(point, mul(mul(normal(outgoing), sign), radius));
        match self.join {
            PenLineJoin::Bevel => write_bevel(destination, previous, point, next),
            PenLineJoin::Miter | PenLineJoin::MiterOrBevel => {
                let limit = if self.miter_limit.is_finite() && self.miter_limit >= 1.0 {
                    self.miter_limit
                } else {
                    1.0
                };
                if let Some(miter) = intersect_lines(previous, incoming, next, outgoing)
                    && length(sub(point, miter)) <= radius * limit + EPSILON
                {
                    ensure(destination, 2)?;
                    destination[0] = StrokeTriangle::new(previous, point, next);
                    destination[1] = StrokeTriangle::new(previous, miter, next);
                    return Ok(2);
                }
                if !clip {
                    return write_bevel(destination, previous, point, next);
                }
                let Some([first, second]) =
                    clipped_miter(previous, next, incoming, outgoing, radius, limit)
                else {
                    return write_bevel(destination, previous, point, next);
                };
                ensure(destination, 3)?;
                destination[0] = StrokeTriangle::new(point, previous, first);
                destination[1] = StrokeTriangle::new(point, first, second);
                destination[2] = StrokeTriangle::new(point, second, next);
                Ok(3)
            }
            PenLineJoin::Round => {
                let start = (previous[1] - point[1]).atan2(previous[0] - point[0]);
                let mut end = (next[1] - point[1]).atan2(next[0] - point[0]);
                if turn > 0.0 {
                    while end < start {
                        end += PI * 2.0;
                    }
                } else {
                    while end > start {
                        end -= PI * 2.0;
                    }
                }
                let sweep = end - start;
                let count = ((sweep.abs() / (PI / 8.0)).ceil() as i32).clamp(1, max_segments as i32)
                    as usize;
                ensure(destination, count)?;
                let radius = if allocating {
                    length(sub(point, previous))
                } else {
                    radius
                };
                for (index, triangle) in destination[..count].iter_mut().enumerate() {
                    let angle0 = start + sweep * index as f32 / count as f32;
                    let angle1 = start + sweep * (index + 1) as f32 / count as f32;
                    *triangle = StrokeTriangle::new(
                        point,
                        polar(point, angle0, radius),
                        polar(point, angle1, radius),
                    );
                }
                Ok(count)
            }
        }
    }
    fn write_reversal(
        self,
        destination: &mut [StrokeTriangle],
        point: [f32; 2],
        incoming: [f32; 2],
        count: usize,
    ) -> Result<usize, Error> {
        let radius = self.thickness * 0.5;
        let n = normal(incoming);
        if self.join == PenLineJoin::Round {
            ensure(destination, count)?;
            for (index, triangle) in destination[..count].iter_mut().enumerate() {
                let angle0 = PI * index as f32 / count as f32;
                let angle1 = PI * (index + 1) as f32 / count as f32;
                let p0 = add(
                    point,
                    mul(
                        add(mul(n, angle0.cos()), mul(incoming, angle0.sin())),
                        radius,
                    ),
                );
                let p1 = add(
                    point,
                    mul(
                        add(mul(n, angle1.cos()), mul(incoming, angle1.sin())),
                        radius,
                    ),
                );
                *triangle = StrokeTriangle::new(point, p0, p1);
            }
            return Ok(count);
        }
        ensure(destination, 3)?;
        let first = add(point, mul(n, radius));
        let second = sub(point, mul(n, radius));
        let first_outer = add(first, mul(incoming, radius));
        let second_outer = add(second, mul(incoming, radius));
        destination[0] = StrokeTriangle::new(point, first, first_outer);
        destination[1] = StrokeTriangle::new(point, first_outer, second_outer);
        destination[2] = StrokeTriangle::new(point, second_outer, second);
        Ok(3)
    }
}
impl Pen {
    /// Original typed-pen writer, preserving independent clipped-miter intent,
    /// WPF validation and the caller's actual physical thickness and tangents.
    pub fn write_join(
        &self,
        destination: &mut [StrokeTriangle],
        frame: StrokeJoinFrame,
        thickness: f32,
        smooth: bool,
        max_round_segments: i32,
    ) -> Result<usize, Error> {
        self.validate_join_semantics()?;
        StrokeJoin {
            join: self.options.join,
            thickness,
            miter_limit: self.options.miter_limit,
            smooth,
            max_round_segments,
        }
        .write_core(
            destination,
            frame,
            self.options.use_wpf_join_semantics,
            self.options.clip_miter_at_limit || self.options.use_wpf_join_semantics,
            false,
        )
    }
}
/// Original cap policy and bounded fan resolution.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StrokeCap {
    /// Flat, square, round or triangle.
    pub cap: PenLineCap,
    /// Actual expansion-frame thickness.
    pub thickness: f32,
    /// Whether the cap faces away from the start of the path.
    pub is_start: bool,
    /// Original fan limit, clamped to 1..=8.
    pub max_round_segments: i32,
}
impl StrokeCap {
    /// Write the original endpoint cap, checking full capacity before writes.
    pub fn write_line(
        self,
        destination: &mut [StrokeTriangle],
        start: [f32; 2],
        end: [f32; 2],
    ) -> Result<usize, Error> {
        self.write_directional(
            destination,
            if self.is_start { start } else { end },
            sub(end, start),
        )
    }
    /// Write using the actual tangent and center (no synthesized line endpoint).
    pub fn write_directional(
        self,
        destination: &mut [StrokeTriangle],
        center: [f32; 2],
        direction: [f32; 2],
    ) -> Result<usize, Error> {
        if self.cap == PenLineCap::Flat || !self.thickness.is_finite() || self.thickness <= EPSILON
        {
            return Ok(0);
        }
        let Some(direction) = normalize_direction(direction) else {
            return Ok(0);
        };
        let radius = self.thickness * 0.5;
        let outward = if self.is_start {
            [-direction[0], -direction[1]]
        } else {
            direction
        };
        let n = mul(normal(direction), radius);
        match self.cap {
            PenLineCap::Flat => Ok(0),
            PenLineCap::Square => {
                ensure(destination, 2)?;
                let inner0 = sub(center, n);
                let inner1 = add(center, n);
                let outer0 = sub(add(center, mul(outward, radius)), n);
                let outer1 = add(add(center, mul(outward, radius)), n);
                destination[0] = StrokeTriangle::new(inner0, outer0, outer1);
                destination[1] = StrokeTriangle::new(inner0, outer1, inner1);
                Ok(2)
            }
            PenLineCap::Triangle => {
                ensure(destination, 1)?;
                destination[0] = StrokeTriangle::new(
                    sub(center, n),
                    add(center, mul(outward, radius)),
                    add(center, n),
                );
                Ok(1)
            }
            PenLineCap::Round => {
                let count = self
                    .max_round_segments
                    .clamp(1, MAX_STROKE_TRIANGLES as i32) as usize;
                ensure(destination, count)?;
                let start = outward[1].atan2(outward[0]) - PI * 0.5;
                for (index, triangle) in destination[..count].iter_mut().enumerate() {
                    let angle0 = start + PI * index as f32 / count as f32;
                    let angle1 = start + PI * (index + 1) as f32 / count as f32;
                    *triangle = StrokeTriangle::new(
                        center,
                        polar(center, angle0, radius),
                        polar(center, angle1, radius),
                    );
                }
                Ok(count)
            }
        }
    }
    /// Allocate the original cap triangles for a line endpoint.
    pub fn create_line(self, start: [f32; 2], end: [f32; 2]) -> Result<Vec<StrokeTriangle>, Error> {
        self.create_directional(if self.is_start { start } else { end }, sub(end, start))
    }
    /// Allocate the original cap using an actual direction.
    pub fn create_directional(
        self,
        center: [f32; 2],
        direction: [f32; 2],
    ) -> Result<Vec<StrokeTriangle>, Error> {
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = self.write_directional(&mut triangles, center, direction)?;
        copy_triangles(&triangles[..count])
    }
}
fn copy_triangles(source: &[StrokeTriangle]) -> Result<Vec<StrokeTriangle>, Error> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    result.extend_from_slice(source);
    Ok(result)
}
fn ensure(destination: &[StrokeTriangle], count: usize) -> Result<(), Error> {
    if destination.len() < count {
        Err(Error::Capacity)
    } else {
        Ok(())
    }
}
pub(super) fn cross(a: [f32; 2], b: [f32; 2]) -> f32 {
    a[0] * b[1] - a[1] * b[0]
}
pub(super) fn distance_squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = sub(a, b);
    dot(d, d)
}
fn normal(direction: [f32; 2]) -> [f32; 2] {
    [-direction[1], direction[0]]
}
fn normalize_direction(direction: [f32; 2]) -> Option<[f32; 2]> {
    let size = length(direction);
    (size.is_finite() && size > EPSILON).then(|| [direction[0] / size, direction[1] / size])
}
fn polar(center: [f32; 2], angle: f32, radius: f32) -> [f32; 2] {
    [
        center[0] + angle.cos() * radius,
        center[1] + angle.sin() * radius,
    ]
}
fn write_bevel(
    destination: &mut [StrokeTriangle],
    previous: [f32; 2],
    point: [f32; 2],
    next: [f32; 2],
) -> Result<usize, Error> {
    ensure(destination, 1)?;
    destination[0] = StrokeTriangle::new(previous, point, next);
    Ok(1)
}
fn intersect_lines(
    point: [f32; 2],
    direction: [f32; 2],
    other: [f32; 2],
    other_direction: [f32; 2],
) -> Option<[f32; 2]> {
    let denominator = cross(direction, other_direction);
    if denominator.abs() <= EPSILON {
        return None;
    }
    let t = cross(sub(other, point), other_direction) / denominator;
    Some(add(point, mul(direction, t)))
}
fn clipped_miter(
    previous: [f32; 2],
    next: [f32; 2],
    incoming: [f32; 2],
    outgoing: [f32; 2],
    radius: f32,
    limit: f32,
) -> Option<[[f32; 2]; 2]> {
    let product = dot(incoming, outgoing);
    let denominator = radius * maximum(0.0, (1.0 - product) * 0.5).sqrt();
    let numerator = radius * maximum(0.0, (1.0 + product) * 0.5).sqrt();
    if !denominator.is_finite() || denominator <= EPSILON {
        return None;
    }
    let ratio = maximum(0.0, (limit * radius - numerator) / denominator);
    let offset = radius * ratio;
    let first = add(previous, mul(incoming, offset));
    let second = sub(next, mul(outgoing, offset));
    (finite(first) && finite(second)).then_some([first, second])
}
