//! Native dash phase, odd-pattern duplication and source endpoint/seam traversal.
use super::*;
pub(super) enum Event {
    Body(wire::Point, wire::Point),
    Cap(u32, wire::Point, wire::Point, bool),
    Join(wire::Point, wire::Point, wire::Point),
}
#[derive(Clone, Copy)]
struct Pattern<'a> {
    intervals: &'a [f64],
    effective: usize,
    index: usize,
    distance: f32,
    thickness: f32,
    cap: u32,
}
impl Pattern<'_> {
    fn interval(self, index: usize) -> f32 {
        let scaled = self.intervals[index % self.intervals.len()] * f64::from(self.thickness);
        if !scaled.is_finite() || scaled < 0.0 {
            return f32::NAN;
        }
        let interval = scaled as f32;
        if interval <= 0.0001 {
            maximum(0.0001 * 2.0, self.thickness * 0.001)
        } else {
            interval
        }
    }
    fn advance(&mut self, remaining: f32, step: f32) {
        if step >= remaining - 0.0001 {
            self.distance = 0.0;
            self.index = (self.index + 1) % self.effective;
        } else {
            self.distance += step;
        }
    }
}
fn pattern<'a>(stroke: &wire::Stroke, intervals: &'a [f64]) -> BuildResult<Pattern<'a>> {
    if intervals.is_empty()
        || intervals.len() > usize::MAX / 2
        || !stroke.dash_offset.is_finite()
        || stroke.dash_cap > STROKE_CAP_TRIANGLE
    {
        return Err(B::UnsupportedHitTest);
    }
    let thickness = if stroke.flags & POLYLINE_FLAG_HAIRLINE != 0 {
        1.0
    } else {
        stroke.stroke_thickness
    };
    if !thickness.is_finite() || thickness <= 0.0 {
        return Err(B::UnsupportedHitTest);
    }
    let mut p = Pattern {
        intervals,
        effective: if intervals.len() & 1 == 0 {
            intervals.len()
        } else {
            intervals.len() * 2
        },
        index: 0,
        distance: 0.0,
        thickness,
        cap: stroke.dash_cap,
    };
    let mut length = 0.0f32;
    for i in 0..p.effective {
        let interval = p.interval(i);
        if !interval.is_finite() || interval <= 0.0001 || length > f32::MAX - interval {
            return Err(B::UnsupportedHitTest);
        }
        length += interval;
    }
    let scaled_offset = stroke.dash_offset * f64::from(thickness);
    if !scaled_offset.is_finite() || !length.is_finite() || length <= 0.0001 {
        return Err(B::UnsupportedHitTest);
    }
    let mut distance = (scaled_offset as f32) % length;
    if !distance.is_finite() {
        return Err(B::UnsupportedHitTest);
    }
    if distance < 0.0 {
        distance += length;
    }
    while distance >= p.interval(p.index) {
        distance -= p.interval(p.index);
        p.index = (p.index + 1) % p.effective;
    }
    p.distance = distance;
    Ok(p)
}
pub(super) fn walk(
    stroke: &wire::Stroke,
    transform: wire::Affine2d,
    points: &[wire::Point],
    intervals: &[f64],
    mut emit: impl FnMut(Event) -> BuildResult<()>,
) -> BuildResult<()> {
    let mut pattern = pattern(stroke, intervals)?;
    let closed = stroke.flags & POLYLINE_FLAG_CLOSED != 0;
    let device = stroke.flags & (POLYLINE_FLAG_HAIRLINE | POLYLINE_FLAG_FIXED_DEVICE_STROKE) != 0;
    let count = if closed {
        points.len()
    } else {
        points.len() - 1
    };
    let point = |i: usize| {
        if device {
            math::point(transform, points[i])
        } else {
            points[i]
        }
    };
    let (mut first_visible, mut last_visible, mut continuing, mut have_first, mut have_previous) =
        (false, false, false, false, false);
    let (
        mut previous_direction,
        mut first_visible_direction,
        mut last_visible_direction,
        mut first_visible_point,
        mut last_visible_point,
    ) = (
        p2(0.0, 0.0),
        p2(0.0, 0.0),
        p2(0.0, 0.0),
        p2(0.0, 0.0),
        p2(0.0, 0.0),
    );
    for segment in 0..count {
        let start = point(segment);
        let end = point((segment + 1) % points.len());
        let delta = p2(end.x - start.x, end.y - start.y);
        let length = delta.x.hypot(delta.y);
        if !length.is_finite() {
            return Err(B::UnsupportedHitTest);
        }
        if length <= 0.0001 {
            continue;
        }
        let direction = p2(delta.x / length, delta.y / length);
        have_first = true;
        if continuing && have_previous {
            emit(Event::Join(start, previous_direction, direction))?;
        }
        let mut distance = 0.0f32;
        let mut ends_visible = false;
        let mut continues = false;
        while distance < length - 0.0001 {
            let interval = pattern.interval(pattern.index);
            if !interval.is_finite() || interval <= 0.0001 {
                return Err(B::UnsupportedHitTest);
            }
            let remaining = interval - pattern.distance;
            let step = minimum(remaining, length - distance);
            let on = pattern.index & 1 == 0;
            let at_start = distance <= 0.0001;
            let at_end = distance + step >= length - 0.0001;
            let pattern_ends = step >= remaining - 0.0001;
            if on && step > 0.0001 {
                let body_start = p2(
                    start.x + direction.x * distance,
                    start.y + direction.y * distance,
                );
                let body_end = p2(
                    start.x + direction.x * (distance + step),
                    start.y + direction.y * (distance + step),
                );
                emit(Event::Body(body_start, body_end))?;
                if !(continuing && at_start) {
                    let cap = if !closed && segment == 0 && at_start {
                        stroke.start_cap
                    } else {
                        pattern.cap
                    };
                    if closed && segment == 0 && at_start {
                        first_visible = true;
                        first_visible_point = body_start;
                        first_visible_direction = direction;
                    } else {
                        emit(Event::Cap(cap, body_start, direction, true))?;
                    }
                }
                let source_end = !closed && segment + 1 == count && at_end;
                if source_end {
                    emit(Event::Cap(stroke.end_cap, body_end, direction, false))?;
                } else if closed && segment + 1 == count && at_end {
                    last_visible = true;
                    last_visible_point = body_end;
                    last_visible_direction = direction;
                } else if pattern_ends {
                    emit(Event::Cap(pattern.cap, body_end, direction, false))?;
                }
                ends_visible = at_end;
                continues = at_end && !pattern_ends && !source_end;
            } else if at_end {
                ends_visible = false;
                continues = false;
            }
            pattern.advance(remaining, step);
            distance += step;
        }
        continuing = ends_visible && continues;
        previous_direction = direction;
        have_previous = true;
    }
    if !closed && have_previous && pattern.index & 1 == 0 && pattern.distance == 0.0 {
        let end = point(points.len() - 1);
        emit(Event::Cap(pattern.cap, end, previous_direction, true))?;
        emit(Event::Cap(stroke.end_cap, end, previous_direction, false))?;
    }
    if closed {
        if first_visible && last_visible && have_first && have_previous {
            emit(Event::Join(
                first_visible_point,
                last_visible_direction,
                first_visible_direction,
            ))?;
        } else {
            if first_visible {
                emit(Event::Cap(
                    pattern.cap,
                    first_visible_point,
                    first_visible_direction,
                    true,
                ))?;
            }
            if last_visible {
                emit(Event::Cap(
                    pattern.cap,
                    last_visible_point,
                    last_visible_direction,
                    false,
                ))?;
            }
        }
    }
    Ok(())
}
