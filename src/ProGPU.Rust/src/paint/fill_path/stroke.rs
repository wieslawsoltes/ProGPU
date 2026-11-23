//! Original ordered body, oval, dash and shared cap/join writers.
use super::*;

pub(super) fn segment(
    destination: &mut PathBuilder,
    start: [f32; 2],
    end: [f32; 2],
    half: f32,
    budget: &mut Budget,
) -> Result<(), Error> {
    let direction = sub(end, start);
    let squared = direction[0] * direction[0] + direction[1] * direction[1];
    if squared <= 0.0000001 {
        return Ok(());
    }
    budget.take(5)?;
    let direction = point(vector(direction) / V::splat(squared.sqrt()));
    let normal = mul([-direction[1], direction[0]], half);
    destination.move_to(add(start, normal))?;
    destination.line_to(add(end, normal))?;
    destination.line_to(sub(end, normal))?;
    destination.line_to(sub(start, normal))?;
    destination.close()
}
fn triangles(
    destination: &mut PathBuilder,
    source: &[StrokeTriangle],
    budget: &mut Budget,
) -> Result<(), Error> {
    budget.take(source.len() * 4)?;
    let mut points = [[[0.0; 2]; 3]; MAX_STROKE_TRIANGLES];
    for (output, input) in points.iter_mut().zip(source) {
        *output = input.points;
    }
    destination.add_triangles(&points[..source.len()])
}
pub(super) fn joins(
    paint: &Paint,
    destination: &mut PathBuilder,
    points: &[[f32; 2]],
    closed: bool,
    width: f32,
    budget: &mut Budget,
) -> Result<(), Error> {
    let count = points.len();
    if count < 3 {
        return Ok(());
    }
    let first = if closed { 0 } else { 1 };
    let end = if closed { count } else { count - 1 };
    // SKPaint maps only its three original public joins; the independent vector
    // MiterOrBevel extension does not silently expand the SKPaint source policy.
    let join = match paint.stroke_join {
        PenLineJoin::Round => PenLineJoin::Round,
        PenLineJoin::Bevel => PenLineJoin::Bevel,
        _ => PenLineJoin::Miter,
    };
    let writer = StrokeJoin {
        join,
        thickness: width,
        miter_limit: paint.stroke_miter,
        smooth: false,
        max_round_segments: MAX_STROKE_TRIANGLES as i32,
    };
    let mut output = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
    for index in first..end {
        budget.take(1)?;
        let previous = points[if index == 0 { count - 1 } else { index - 1 }];
        let current = points[index];
        let next = points[if index + 1 == count { 0 } else { index + 1 }];
        let count = writer.write(
            &mut output,
            StrokeJoinFrame::from_line(previous, current, next),
        )?;
        triangles(destination, &output[..count], budget)?;
    }
    Ok(())
}
pub(super) fn caps(
    paint: &Paint,
    destination: &mut PathBuilder,
    points: &[[f32; 2]],
    width: f32,
    budget: &mut Budget,
) -> Result<(), Error> {
    if points.len() < 2 || paint.stroke_cap == PenLineCap::Flat {
        return Ok(());
    }
    let cap = match paint.stroke_cap {
        PenLineCap::Round => PenLineCap::Round,
        PenLineCap::Square => PenLineCap::Square,
        _ => PenLineCap::Flat,
    };
    let mut output = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
    if let Some(neighbor) = points
        .iter()
        .skip(1)
        .find(|p| distance_squared(points[0], **p) > 0.0000001)
    {
        let count = StrokeCap {
            cap,
            thickness: width,
            is_start: true,
            max_round_segments: MAX_STROKE_TRIANGLES as i32,
        }
        .write_line(&mut output, points[0], *neighbor)?;
        triangles(destination, &output[..count], budget)?;
    }
    let last = points[points.len() - 1];
    if let Some(neighbor) = points
        .iter()
        .rev()
        .skip(1)
        .find(|p| distance_squared(last, **p) > 0.0000001)
    {
        let count = StrokeCap {
            cap,
            thickness: width,
            is_start: false,
            max_round_segments: MAX_STROKE_TRIANGLES as i32,
        }
        .write_line(&mut output, *neighbor, last)?;
        triangles(destination, &output[..count], budget)?;
    }
    Ok(())
}
pub(super) fn oval(
    destination: &mut PathBuilder,
    figure: &PathFigure,
    half: f32,
) -> Result<bool, Error> {
    if !figure.is_closed || figure.segments.len() != 2 {
        return Ok(false);
    }
    let (PathSegmentKind::Arc(first), PathSegmentKind::Arc(second)) =
        (figure.segments[0].kind, figure.segments[1].kind)
    else {
        return Ok(false);
    };
    if !first.large
        || !second.large
        || first.sweep != second.sweep
        || first.rotation.abs() > 0.0001
        || second.rotation.abs() > 0.0001
        || distance_squared(first.size, second.size) > 0.0001
        || distance_squared(second.end, figure.start) > 0.0001
        || ((first.end[0] - figure.start[0]).abs() - 2.0 * first.size[0]).abs() > 0.0001
        || (first.end[1] - figure.start[1]).abs() > 0.0001
    {
        return Ok(false);
    }
    let center = point((vector(figure.start) + vector(first.end)) / V::splat(2.0));
    let [x, y] = center;
    let [rx, ry] = first.size.map(f32::abs);
    destination.add_oval(
        [x - rx - half, y - ry - half, x + rx + half, y + ry + half],
        first.sweep,
    )?;
    let inner_x = rx - half;
    let inner_y = ry - half;
    if inner_x > 0.0 && inner_y > 0.0 {
        destination.add_oval(
            [x - inner_x, y - inner_y, x + inner_x, y + inner_y],
            opposite(first.sweep),
        )?;
    }
    Ok(true)
}
pub(super) fn opposite(direction: SweepDirection) -> SweepDirection {
    match direction {
        SweepDirection::Clockwise => SweepDirection::Counterclockwise,
        SweepDirection::Counterclockwise => SweepDirection::Clockwise,
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn dashed(
    paint: &Paint,
    destination: &mut PathBuilder,
    points: &[[f32; 2]],
    closed: bool,
    half: f32,
    intervals: &[f32],
    phase: f32,
    budget: &mut Budget,
) -> Result<(), Error> {
    if points.len() < 2 {
        return Ok(());
    }
    let mut length = 0.0;
    for &interval in intervals {
        budget.take(1)?;
        if interval.is_finite() && interval > 0.0 {
            length += interval;
        }
    }
    if length <= 0.0 {
        return Ok(());
    }
    let mut phase = phase % length;
    if phase < 0.0 {
        phase += length;
    }
    let mut index = 0;
    while phase >= intervals[index] && intervals[index] > 0.0 {
        budget.take(1)?;
        phase -= intervals[index];
        index = (index + 1) % intervals.len();
    }
    let mut remaining = maximum(0.0, intervals[index] - phase);
    let segment_count = if closed {
        points.len()
    } else {
        points.len() - 1
    };
    for segment_index in 0..segment_count {
        budget.take(1)?;
        let start = points[segment_index];
        let end = points[(segment_index + 1) % points.len()];
        let delta = sub(end, start);
        let length = (delta[0] * delta[0] + delta[1] * delta[1]).sqrt();
        if !length.is_finite() || length <= 0.0001 {
            continue;
        }
        let direction = point(vector(delta) / V::splat(length));
        let mut distance = 0.0;
        while distance < length - 0.0001 {
            budget.take(1)?;
            if remaining <= 0.0001 {
                for _ in 0..intervals.len() {
                    budget.take(1)?;
                    index = (index + 1) % intervals.len();
                    remaining = intervals[index];
                    if remaining > 0.0001 {
                        break;
                    }
                }
            }
            let step = minimum(remaining, length - distance);
            if index & 1 == 0 && step > 0.0001 {
                let mut dash_start = add(start, mul(direction, distance));
                let mut dash_end = add(start, mul(direction, distance + step));
                if paint.stroke_cap == PenLineCap::Square {
                    dash_start = sub(dash_start, mul(direction, half));
                    dash_end = add(dash_end, mul(direction, half));
                }
                segment(destination, dash_start, dash_end, half, budget)?;
                if paint.stroke_cap == PenLineCap::Round {
                    budget.take(8)?;
                    destination.add_circle(dash_start, half, SweepDirection::Clockwise)?;
                    destination.add_circle(dash_end, half, SweepDirection::Clockwise)?;
                }
            }
            distance += step;
            remaining -= step;
        }
    }
    Ok(())
}
