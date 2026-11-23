//! Original polynomial Bezier evaluation, de Casteljau subcurves and length-
//! sampled dash parameters. Rational curves retain their separate contract.
use super::*;
const EPSILON: f32 = 0.00001;
/// Original default number of length samples, independent of rendering quality.
pub const DEFAULT_LENGTH_SEGMENTS: i32 = 32;
/// Original explicitly ordered quadratic polynomial evaluation.
pub fn evaluate_quadratic(p: [[f32; 2]; 3], t: f32) -> [f32; 2] {
    let inverse = 1.0 - t;
    add(
        add(mul(p[0], inverse * inverse), mul(p[1], 2.0 * inverse * t)),
        mul(p[2], t * t),
    )
}
/// Original explicitly ordered cubic polynomial evaluation.
pub fn evaluate_cubic(p: [[f32; 2]; 4], t: f32) -> [f32; 2] {
    let inverse = 1.0 - t;
    add(
        add(
            add(
                mul(p[0], inverse * inverse * inverse),
                mul(p[1], 3.0 * inverse * inverse * t),
            ),
            mul(p[2], 3.0 * inverse * t * t),
        ),
        mul(p[3], t * t * t),
    )
}
impl PathSegment {
    /// Original polynomial Bezier subsegment with retained stroke/smooth flags.
    /// Other segment families return None; arc subdivision has its own API.
    pub fn bezier_subsegment(
        &self,
        start: [f32; 2],
        first: f32,
        last: f32,
    ) -> Option<PositionedSegment> {
        if !first.is_finite() || !last.is_finite() || last <= first || !finite(start) {
            return None;
        }
        let first = first.clamp(0.0, 1.0);
        let last = last.clamp(0.0, 1.0);
        if last <= first + EPSILON {
            return None;
        }
        let (start, kind) = match self.kind {
            PathSegmentKind::Quadratic(p) => {
                if !p.into_iter().all(finite) {
                    return None;
                }
                let (left, _) = split_quadratic([start, p[0], p[1]], last);
                let points = if first <= EPSILON {
                    left
                } else {
                    split_quadratic(left, first / last).1
                };
                (
                    points[0],
                    PathSegmentKind::Quadratic([points[1], points[2]]),
                )
            }
            PathSegmentKind::Cubic(p) => {
                if !p.into_iter().all(finite) {
                    return None;
                }
                let (left, _) = split_cubic([start, p[0], p[1], p[2]], last);
                let points = if first <= EPSILON {
                    left
                } else {
                    split_cubic(left, first / last).1
                };
                (
                    points[0],
                    PathSegmentKind::Cubic([points[1], points[2], points[3]]),
                )
            }
            _ => return None,
        };
        let segment = Self {
            kind,
            source_conic: None,
            ..self.clone()
        };
        segment.bezier_lengths(start, DEFAULT_LENGTH_SEGMENTS)?;
        Some(PositionedSegment { start, segment })
    }
    /// Original quadratic/cubic dash routing. Length sampling is bounded to
    /// 1..256 exactly as the source; emitted pieces remain analytic curves.
    pub fn bezier_dashes(
        &self,
        start: [f32; 2],
        intervals: &[f32],
        state: DashState,
        length_segments: i32,
    ) -> Result<DashResult, Error> {
        let Some((lengths, count)) = self.bezier_lengths(start, length_segments) else {
            return Ok(DashResult::rejected(state));
        };
        dash::curve_segments(&lengths[..=count], intervals, state, |a, b| {
            self.bezier_subsegment(start, a, b)
        })
    }
    fn bezier_lengths(&self, start: [f32; 2], count: i32) -> Option<([f32; 257], usize)> {
        if !finite(start) {
            return None;
        }
        match self.kind {
            PathSegmentKind::Quadratic(p) if p.into_iter().all(finite) => {}
            PathSegmentKind::Cubic(p) if p.into_iter().all(finite) => {}
            _ => return None,
        }
        let count = count.clamp(1, 256) as usize;
        let mut lengths = [0.0; 257];
        let mut previous = start;
        let mut total = 0.0;
        for (i, slot) in lengths.iter_mut().enumerate().take(count + 1).skip(1) {
            let t = i as f32 / count as f32;
            let current = match self.kind {
                PathSegmentKind::Quadratic(p) => evaluate_quadratic([start, p[0], p[1]], t),
                PathSegmentKind::Cubic(p) => evaluate_cubic([start, p[0], p[1], p[2]], t),
                _ => return None,
            };
            total += length(sub(previous, current));
            *slot = total;
            previous = current;
        }
        (total.is_finite() && total > EPSILON).then_some((lengths, count))
    }
    /// Original arc dash route, retaining both source segment flags on every
    /// successful analytic subarc. Other segment kinds reject explicitly.
    pub fn arc_dashes(
        &self,
        start: [f32; 2],
        intervals: &[f32],
        state: DashState,
        max_angle: f32,
    ) -> Result<DashResult, Error> {
        let PathSegmentKind::Arc(arc) = self.kind else {
            return Ok(DashResult::rejected(state));
        };
        if !DashPattern::validate_state(intervals, state) || arc.center(start).is_none() {
            return Ok(DashResult::rejected(state));
        }
        let points = arc.flattened(start, max_angle)?;
        if points.len() < 2 {
            return Ok(DashResult::rejected(state));
        }
        let mut lengths = [0.0; ArcSegment::MAX_FLATTEN_SEGMENTS + 1];
        let mut total = 0.0;
        for i in 1..points.len() {
            total += length(sub(points[i - 1], points[i]));
            lengths[i] = total;
        }
        if !total.is_finite() || total <= EPSILON {
            return Ok(DashResult::rejected(state));
        }
        dash::curve_segments(&lengths[..points.len()], intervals, state, |a, b| {
            arc.subarc(start, a, b)
                .map(|(start, arc)| PositionedSegment {
                    start,
                    segment: Self {
                        kind: PathSegmentKind::Arc(arc),
                        source_conic: None,
                        ..self.clone()
                    },
                })
        })
    }
}
// Preserve the source Lerp weighted-sum contract and split ordering. Exact
// default-runtime SIMD/FMA results remain in the consolidated float gate.
fn lerp(a: [f32; 2], b: [f32; 2], t: f32) -> [f32; 2] {
    add(mul(a, 1.0 - t), mul(b, t))
}
fn split_quadratic(p: [[f32; 2]; 3], t: f32) -> ([[f32; 2]; 3], [[f32; 2]; 3]) {
    let a = lerp(p[0], p[1], t);
    let b = lerp(p[1], p[2], t);
    let c = lerp(a, b, t);
    ([p[0], a, c], [c, b, p[2]])
}
fn split_cubic(p: [[f32; 2]; 4], t: f32) -> ([[f32; 2]; 4], [[f32; 2]; 4]) {
    let a = lerp(p[0], p[1], t);
    let b = lerp(p[1], p[2], t);
    let c = lerp(p[2], p[3], t);
    let d = lerp(a, b, t);
    let e = lerp(b, c, t);
    let f = lerp(d, e, t);
    ([p[0], a, d, f], [f, e, c, p[3]])
}
