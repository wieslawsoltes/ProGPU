//! Original ordered dash state and line/curve traversal. Curve length sampling
//! and drawn subcurve creation remain separate; no polyline replaces a curve.
use super::*;
const EPSILON: f32 = 0.0001;
/// Exact source dash position; signed index rejects before array access.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct DashState {
    /// Original interval index.
    pub index: i32,
    /// Original consumed distance within the selected interval.
    pub distance: f32,
}
/// Source segment with its actual starting point and retained segment flags.
#[derive(Clone, Debug, PartialEq)]
pub struct PositionedSegment {
    /// Original start of this span.
    pub start: [f32; 2],
    /// Analytic line/curve, including original stroke/smooth metadata.
    pub segment: PathSegment,
}
/// Original boolean/out-value result. A rejected subcurve can retain an advanced
/// final dash state; None is distinct from a successful empty drawn inventory.
#[derive(Clone, Debug, PartialEq)]
pub struct DashResult {
    /// Complete successful output, or original source rejection.
    pub segments: Option<Vec<PositionedSegment>>,
    /// Original output state, including failure-after-count semantics.
    pub final_state: DashState,
}
impl DashResult {
    pub(super) fn rejected(state: DashState) -> Self {
        Self {
            segments: None,
            final_state: state,
        }
    }
}
/// Original thickness-scaled single-precision dash intervals with initial phase.
#[derive(Clone, Debug, PartialEq)]
pub struct DashPattern {
    intervals: Vec<f32>,
    initial: DashState,
}
impl DashPattern {
    /// Original double-input construction, odd-array duplication, zero-interval
    /// repair and single-precision ordered phase reduction. Nonfinite offsets
    /// retain original constructor behavior; later state admission is separate.
    pub fn new(dashes: &[f64], offset: f64, thickness: f64) -> Result<Option<Self>, Error> {
        if dashes.is_empty() || !thickness.is_finite() || thickness <= 0.0 {
            return Ok(None);
        }
        let count = dashes
            .len()
            .checked_mul(if dashes.len().is_multiple_of(2) { 1 } else { 2 })
            .ok_or(Error::Capacity)?;
        if count > i32::MAX as usize {
            return Err(Error::Capacity);
        }
        let mut intervals = Vec::new();
        intervals
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        let mut pattern_length = 0.0;
        for index in 0..count {
            let mut interval = dashes[index % dashes.len()] * thickness;
            if !interval.is_finite() || interval < 0.0 {
                return Ok(None);
            }
            if interval <= f64::from(EPSILON) {
                interval = f64::from(maximum(EPSILON * 2.0, thickness as f32 * 0.001));
            }
            intervals.push(interval as f32);
            pattern_length += interval as f32;
        }
        if pattern_length <= EPSILON {
            return Ok(None);
        }
        let mut distance = (offset * thickness) as f32 % pattern_length;
        if distance < 0.0 {
            distance += pattern_length;
        }
        let mut initial = DashState { index: 0, distance };
        normalize(&intervals, &mut initial)?;
        Ok(Some(Self { intervals, initial }))
    }
    /// Complete original ordered intervals, without lossy double reconstruction.
    pub fn intervals(&self) -> &[f32] {
        &self.intervals
    }
    /// Original initial index/distance.
    pub fn initial_state(&self) -> DashState {
        self.initial
    }
    /// Original whole-inventory state validation, shared by all segment families.
    pub fn validate_state(intervals: &[f32], state: DashState) -> bool {
        !intervals.is_empty()
            && state.index >= 0
            && (state.index as usize) < intervals.len()
            && state.distance.is_finite()
            && state.distance >= 0.0
            && intervals.iter().all(|v| v.is_finite() && *v > EPSILON)
    }
    /// Original two-pass line dash creation. No output is published before full
    /// traversal; the final state includes skipped gaps and trailing fragments.
    pub fn line_segments(
        &self,
        start: [f32; 2],
        end: [f32; 2],
        state: DashState,
    ) -> Result<DashResult, Error> {
        if !Self::validate_state(&self.intervals, state) {
            return Ok(DashResult::rejected(state));
        }
        let mut normalized = state;
        normalize(&self.intervals, &mut normalized)?;
        let delta = sub(end, start);
        let total = length(delta);
        if total <= EPSILON {
            return Ok(DashResult::rejected(state));
        }
        let mut count = 0usize;
        let final_state = walk(&self.intervals, normalized, total, EPSILON, |_, _| {
            count = count.checked_add(1).ok_or(Error::Capacity)?;
            Ok(())
        })?;
        let mut segments = Vec::new();
        segments
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        if count > 0 {
            let direction = [delta[0] / total, delta[1] / total];
            walk(
                &self.intervals,
                normalized,
                total,
                EPSILON,
                |distance, end| {
                    segments.push(PositionedSegment {
                        start: add(start, mul(direction, distance)),
                        segment: PathSegment::new(PathSegmentKind::Line(add(
                            start,
                            mul(direction, end),
                        ))),
                    });
                    Ok(())
                },
            )?;
        }
        Ok(DashResult {
            segments: Some(segments),
            final_state,
        })
    }
}
pub(super) fn normalize(intervals: &[f32], state: &mut DashState) -> Result<(), Error> {
    let mut stalled = 0usize;
    while state.distance >= intervals[state.index as usize] {
        let next = state.distance - intervals[state.index as usize];
        // A small interval may round away while a later large one still reduces
        // the phase. Reject only a full inventory with no subtraction progress.
        if next == state.distance {
            stalled += 1;
            if stalled >= intervals.len() {
                return Err(Error::InvalidInput);
            }
        } else {
            stalled = 0;
        }
        state.distance = next;
        state.index = ((state.index as usize + 1) % intervals.len()) as i32;
    }
    Ok(())
}
pub(super) fn advance(intervals: &[f32], state: &mut DashState, remaining: f32, step: f32) {
    if step >= remaining - EPSILON {
        state.distance = 0.0;
        state.index = ((state.index as usize + 1) % intervals.len()) as i32;
    } else {
        state.distance += step;
    }
}
pub(super) fn walk(
    intervals: &[f32],
    mut state: DashState,
    total: f32,
    epsilon: f32,
    mut drawn: impl FnMut(f32, f32) -> Result<(), Error>,
) -> Result<DashState, Error> {
    let mut distance = 0.0;
    let mut stalled = 0usize;
    while distance < total - epsilon {
        let remaining = intervals[state.index as usize] - state.distance;
        let step = minimum(remaining, total - distance);
        if state.index % 2 == 0 && step > epsilon {
            drawn(distance, distance + step)?;
        }
        advance(intervals, &mut state, remaining, step);
        let next = distance + step;
        if next == distance {
            stalled += 1;
            // Include one extra visit because the first stalled element may
            // have started partway through its interval.
            if stalled > intervals.len() {
                return Err(Error::InvalidInput);
            }
        } else {
            stalled = 0;
        }
        distance = next;
    }
    Ok(state)
}
pub(super) fn curve_segments(
    lengths: &[f32],
    intervals: &[f32],
    state: DashState,
    mut subcurve: impl FnMut(f32, f32) -> Option<PositionedSegment>,
) -> Result<DashResult, Error> {
    if lengths.len() < 2 || !DashPattern::validate_state(intervals, state) {
        return Ok(DashResult::rejected(state));
    }
    let mut normalized = state;
    normalize(intervals, &mut normalized)?;
    let total = lengths[lengths.len() - 1];
    let mut count = 0usize;
    let final_state = walk(intervals, normalized, total, 0.00001, |_, _| {
        count = count.checked_add(1).ok_or(Error::Capacity)?;
        Ok(())
    })?;
    let mut segments = Vec::new();
    segments
        .try_reserve_exact(count)
        .map_err(|_| Error::Capacity)?;
    let mut rejected = false;
    if count > 0 {
        // First failed source subcurve rejects the entire candidate. A local
        // sentinel unwinds traversal without changing the already counted state.
        let result = walk(
            intervals,
            normalized,
            total,
            0.00001,
            |start, end| match subcurve(parameter(lengths, start), parameter(lengths, end)) {
                Some(segment) => {
                    segments.push(segment);
                    Ok(())
                }
                None => {
                    rejected = true;
                    Err(Error::InvalidInput)
                }
            },
        );
        if rejected {
            return Ok(DashResult::rejected(final_state));
        }
        result?;
    }
    Ok(DashResult {
        segments: Some(segments),
        final_state,
    })
}
fn parameter(lengths: &[f32], distance: f32) -> f32 {
    if lengths.len() <= 1 || distance <= 0.0 {
        return 0.0;
    }
    if distance >= lengths[lengths.len() - 1] {
        return 1.0;
    }
    for i in 1..lengths.len() {
        let end = lengths[i];
        if distance > end {
            continue;
        }
        let start = lengths[i - 1];
        let length = end - start;
        let local = if length > 0.00001 {
            (distance - start) / length
        } else {
            0.0
        };
        return (i as f32 - 1.0 + local) / (lengths.len() - 1) as f32;
    }
    1.0
}
