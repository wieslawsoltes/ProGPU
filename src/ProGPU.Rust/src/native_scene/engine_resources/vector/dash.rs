//! One original ordered dash walk shared by capacity and emitted geometry.
use super::*;
pub(super) enum Event {
    Body(wire::Point, wire::Point),
    Cap(u32, wire::Point, wire::Point, bool),
    Join(wire::Point, wire::Point, wire::Point),
}
struct Pattern<'a> {
    intervals: &'a [f64],
    count: usize,
    index: usize,
    distance: f32,
    thickness: f32,
    cap: u32,
}
impl Pattern<'_> {
    fn interval(&self) -> f32 {
        let scaled = self.intervals[self.index % self.intervals.len()] * self.thickness as f64;
        if !scaled.is_finite() || scaled < 0.0 {
            return f32::NAN;
        }
        let value = scaled as f32;
        if value <= 0.0001 {
            (0.0001f32 * 2.0).max(self.thickness * 0.001)
        } else {
            value
        }
    }
    fn advance(&mut self, remaining: f32, step: f32) {
        if step >= remaining - 0.0001 {
            self.distance = 0.0;
            self.index = (self.index + 1) % self.count;
        } else {
            self.distance += step;
        }
    }
}
fn pattern<'a>(s: &wire::Stroke, doubles: &'a [f64]) -> Result<Pattern<'a>, Error> {
    let begin = usize::try_from(s.dash_interval_offset).map_err(|_| Error::Capacity)?;
    let end = begin
        .checked_add(s.dash_interval_count as usize)
        .ok_or(Error::Capacity)?;
    let intervals = doubles
        .get(begin..end)
        .filter(|v| !v.is_empty())
        .ok_or(Error::InvalidInput)?;
    if !s.dash_offset.is_finite() || s.dash_cap > STROKE_CAP_TRIANGLE {
        return Err(Error::InvalidInput);
    }
    let thickness = if s.flags & POLYLINE_FLAG_HAIRLINE != 0 {
        1.0
    } else {
        s.stroke_thickness
    };
    if !thickness.is_finite() || thickness <= 0.0 {
        return Err(Error::InvalidInput);
    }
    let count = if intervals.len() % 2 == 0 {
        intervals.len()
    } else {
        intervals.len().checked_mul(2).ok_or(Error::Capacity)?
    };
    let mut p = Pattern {
        intervals,
        count,
        index: 0,
        distance: 0.0,
        thickness,
        cap: s.dash_cap,
    };
    let mut length = 0.0f32;
    for index in 0..count {
        p.index = index;
        let interval = p.interval();
        if !interval.is_finite() || interval <= 0.0001 || length > f32::MAX - interval {
            return Err(Error::InvalidInput);
        }
        length += interval;
    }
    let offset = s.dash_offset * thickness as f64;
    if !offset.is_finite() || !length.is_finite() || length <= 0.0001 {
        return Err(Error::InvalidInput);
    }
    let mut distance = offset as f32 % length;
    if !distance.is_finite() {
        return Err(Error::InvalidInput);
    }
    if distance < 0.0 {
        distance += length;
    }
    p.index = 0;
    while distance >= p.interval() {
        distance -= p.interval();
        p.index = (p.index + 1) % count;
    }
    p.distance = distance;
    Ok(p)
}
pub(super) fn walk(
    s: &wire::Stroke,
    points: &[wire::Point],
    doubles: &[f64],
    mut emit: impl FnMut(Event) -> Result<(), Error>,
) -> Result<(), Error> {
    if points.len() < 2 {
        return Err(Error::InvalidInput);
    }
    let mut pattern = pattern(s, doubles)?;
    let closed = s.flags & POLYLINE_FLAG_CLOSED != 0;
    let device = s.flags & (POLYLINE_FLAG_HAIRLINE | POLYLINE_FLAG_FIXED_DEVICE_STROKE) != 0;
    let segments = if closed {
        points.len()
    } else {
        points.len() - 1
    };
    let at = |index| {
        if device {
            transform_point(s.transform, points[index])
        } else {
            points[index]
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
        wire::Point::default(),
        wire::Point::default(),
        wire::Point::default(),
        wire::Point::default(),
        wire::Point::default(),
    );
    for segment in 0..segments {
        let start = at(segment);
        let end = at((segment + 1) % points.len());
        let delta = sub(end, start);
        let length = delta.x.hypot(delta.y);
        if !length.is_finite() {
            return Err(Error::InvalidInput);
        }
        if length <= 0.0001 {
            continue;
        }
        let direction = point(delta.x / length, delta.y / length);
        if !have_first {
            have_first = true;
        }
        if continuing && have_previous {
            emit(Event::Join(start, previous_direction, direction))?;
        }
        let mut distance = 0.0;
        let (mut ends_visible, mut continues) = (false, false);
        while distance < length - 0.0001 {
            let interval = pattern.interval();
            if !interval.is_finite() || interval <= 0.0001 {
                return Err(Error::InvalidInput);
            }
            let remaining = interval - pattern.distance;
            let step = remaining.min(length - distance);
            let on = pattern.index & 1 == 0;
            let at_start = distance <= 0.0001;
            let at_end = distance + step >= length - 0.0001;
            let pattern_ends = step >= remaining - 0.0001;
            if on && step > 0.0001 {
                let body_start = point(
                    start.x + direction.x * distance,
                    start.y + direction.y * distance,
                );
                let body_end = point(
                    start.x + direction.x * (distance + step),
                    start.y + direction.y * (distance + step),
                );
                emit(Event::Body(body_start, body_end))?;
                if !(continuing && at_start) {
                    let source_start = !closed && segment == 0 && at_start;
                    let cap = if source_start {
                        s.start_cap
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
                let source_end = !closed && segment + 1 == segments && at_end;
                if source_end {
                    emit(Event::Cap(s.end_cap, body_end, direction, false))?;
                } else if closed && segment + 1 == segments && at_end {
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
        let end = at(points.len() - 1);
        emit(Event::Cap(pattern.cap, end, previous_direction, true))?;
        emit(Event::Cap(s.end_cap, end, previous_direction, false))?;
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
pub(super) fn capacity(
    s: &wire::Stroke,
    points: &[wire::Point],
    doubles: &[f64],
) -> Result<(usize, usize), Error> {
    if s.dash_interval_count == 0 {
        let closed = s.flags & POLYLINE_FLAG_CLOSED != 0;
        let n = points.len();
        if n < 2 || (closed && n < 3) {
            return Err(Error::InvalidInput);
        }
        let segments = if closed { n } else { n - 1 };
        let joins = if closed { n } else { n - 2 };
        let caps = if closed {
            0
        } else {
            usize::from(s.start_cap != 0) + usize::from(s.end_cap != 0)
        };
        let v = segments
            .checked_mul(4)
            .and_then(|v| v.checked_add(joins.checked_mul(32)?))
            .and_then(|v| v.checked_add(caps * 32))
            .ok_or(Error::Capacity)?;
        let i = segments
            .checked_mul(6)
            .and_then(|v| v.checked_add(joins.checked_mul(48)?))
            .and_then(|v| v.checked_add(caps * 48))
            .ok_or(Error::Capacity)?;
        return Ok((v, i));
    }
    let (mut v, mut i) = (0usize, 0usize);
    walk(s, points, doubles, |event| {
        let (a, b) = match event {
            Event::Body(..) => (4, 6),
            Event::Cap(STROKE_CAP_FLAT, ..) => (0, 0),
            _ => (32, 48),
        };
        v = v.checked_add(a).ok_or(Error::Capacity)?;
        i = i.checked_add(b).ok_or(Error::Capacity)?;
        Ok(())
    })?;
    Ok((v, i))
}
