//! Float-narrowed emitted coverage. Its extrema differ from ideal solid-stroke
//! bounds; no outline allocation is made for a bounds-only preparation.
use super::*;

pub(super) fn measure(
    path: &PathGeometry,
    pen: &Pen,
    outline: &mut Option<PathGeometry>,
    terminals: &[DirectedStrokeCaps],
) -> Result<[f32; 4], Error> {
    pen.validate_join_semantics()?;
    if path.deferred.is_some()
        || pen.has_dash_pattern()
        || pen.options.transform != PenStrokeTransform::Normal
        || !pen.options.thickness.is_finite()
        || pen.options.thickness < 0.0
        || !pen.options.miter_limit.is_finite()
        || !admit(path, true)
    {
        return Err(Error::InvalidInput);
    }
    if pen.options.thickness == 0.0 {
        return Ok([0.0; 4]);
    }
    let radius = f64::from(pen.options.thickness) * 0.5;
    let mut state = Bounds::default();
    let mut any = false;
    for source in &path.figures {
        let count = source.segments.len();
        if count == 0 {
            continue;
        }
        let mut start = wide(source.start);
        let mut first_direction = Pair::ZERO;
        let mut previous_direction = Pair::ZERO;
        for (index, segment) in source.segments.iter().enumerate() {
            let end = wide(segment.end());
            let delta = end - start;
            let direction = delta / Pair::splat(magnitude(delta));
            let normal = left(direction) * Pair::splat(radius);
            let [a, b, c, d] =
                [start + normal, start - normal, end - normal, end + normal].map(narrow);
            let area = cross(b - a, c - a) + cross(c - a, d - a);
            if !area.is_finite() || area == 0.0 {
                return Err(Error::InvalidInput);
            }
            for p in [a, b, c, d] {
                state.include(p);
            }
            let mut body = outline.as_ref().map(|_| figure(a));
            for p in [b, c, d] {
                line(&mut body, p)?;
            }
            publish(outline, body)?;
            if index == 0 {
                first_direction = direction;
            } else if pen.options.use_wpf_join_semantics {
                let previous = if index == 1 {
                    source.start
                } else {
                    source.segments[index - 2].end()
                };
                source_join(
                    &mut state,
                    pen,
                    [previous, source.segments[index - 1].end(), segment.end()],
                    segment.is_smooth_join,
                    outline,
                )?;
            } else {
                join(
                    &mut state,
                    [start, previous_direction, direction],
                    radius,
                    if segment.is_smooth_join {
                        PenLineJoin::Round
                    } else {
                        pen.options.join
                    },
                    f64::from(pen.options.miter_limit.max(1.0)),
                    outline,
                )?;
            }
            previous_direction = direction;
            start = end;
        }
        if source.is_closed {
            if pen.options.use_wpf_join_semantics {
                let previous = if count == 1 {
                    source.start
                } else {
                    source.segments[count - 2].end()
                };
                source_join(
                    &mut state,
                    pen,
                    [previous, source.start, source.segments[0].end()],
                    source.segments[0].is_smooth_join,
                    outline,
                )?;
            } else {
                join(
                    &mut state,
                    [wide(source.start), previous_direction, first_direction],
                    radius,
                    if source.segments[0].is_smooth_join {
                        PenLineJoin::Round
                    } else {
                        pen.options.join
                    },
                    f64::from(pen.options.miter_limit.max(1.0)),
                    outline,
                )?;
            }
        } else {
            cap(
                &mut state,
                wide(source.start),
                -first_direction,
                radius,
                source.start_cap.unwrap_or(pen.options.start_cap),
                outline,
            )?;
            cap(
                &mut state,
                start,
                previous_direction,
                radius,
                source.end_cap.unwrap_or(pen.options.end_cap),
                outline,
            )?;
        }
        any = true;
    }
    for terminal in terminals {
        if !finite(terminal.point) || !finite(terminal.adjacent) {
            return Err(Error::InvalidInput);
        }
        let point = wide(terminal.point);
        let delta = point - wide(terminal.adjacent);
        let length = magnitude(delta);
        if length == 0.0 || !length.is_finite() {
            return Err(Error::InvalidInput);
        }
        let outward = delta / Pair::splat(length);
        cap(
            &mut state,
            point,
            -outward,
            radius,
            terminal.start_cap,
            outline,
        )?;
        cap(
            &mut state,
            point,
            outward,
            radius,
            terminal.end_cap,
            outline,
        )?;
        any |= terminal.start_cap != PenLineCap::Flat || terminal.end_cap != PenLineCap::Flat;
    }
    if any { state.rect() } else { Ok([0.0; 4]) }
}
pub(super) fn source_join(
    state: &mut Bounds,
    pen: &Pen,
    points: [[f32; 2]; 3],
    smooth: bool,
    outline: &mut Option<PathGeometry>,
) -> Result<(), Error> {
    let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
    let count = pen.write_join(
        &mut triangles,
        StrokeJoinFrame::from_line(points[0], points[1], points[2]),
        pen.options.thickness,
        smooth,
        MAX_STROKE_TRIANGLES as i32,
    )?;
    for triangle in &triangles[..count] {
        if triangle.points.iter().any(|p| !finite(*p)) {
            return Err(Error::InvalidInput);
        }
        let [a, b, c] = triangle.points;
        let edge0 = sub(b, a);
        let edge1 = sub(c, a);
        let area = edge0[0] * edge1[1] - edge0[1] * edge1[0];
        if !area.is_finite() {
            return Err(Error::InvalidInput);
        }
        if area == 0.0 || (outline.is_some() && area.abs() <= 0.0001) {
            continue;
        }
        let [a, b, c] = [a, b, c].map(wide);
        for p in [a, b, c] {
            state.include(p);
        }
        let mut triangle = outline.as_ref().map(|_| figure(a));
        line(&mut triangle, b)?;
        line(&mut triangle, c)?;
        publish(outline, triangle)?;
    }
    Ok(())
}
fn cap(
    state: &mut Bounds,
    center: Pair,
    outward: Pair,
    radius: f64,
    cap: PenLineCap,
    outline: &mut Option<PathGeometry>,
) -> Result<(), Error> {
    if cap == PenLineCap::Flat {
        return Ok(());
    }
    let normal = left(outward) * Pair::splat(radius);
    let first = narrow(center + normal);
    let last = narrow(center - normal);
    state.include(first);
    state.include(last);
    let mut piece = outline.as_ref().map(|_| figure(first));
    line(&mut piece, last)?;
    let along = outward * Pair::splat(radius);
    if cap == PenLineCap::Square {
        let far_first = narrow(first + along);
        let far_last = narrow(last + along);
        state.include(far_first);
        state.include(far_last);
        line(&mut piece, far_last)?;
        line(&mut piece, far_first)?;
        return publish(outline, piece);
    }
    let outer = narrow(center + along);
    if cap == PenLineCap::Triangle {
        state.include(outer);
        line(&mut piece, outer)?;
        return publish(outline, piece);
    }
    let side = first - center;
    let side_length = magnitude(side);
    if side_length == 0.0 || !side_length.is_finite() {
        return Err(Error::InvalidInput);
    }
    let unit = side / Pair::splat(side_length);
    let distance = Pair::splat(radius * KAPPA);
    state.cubic(
        first,
        first + outward * distance,
        outer + unit * distance,
        outer,
    );
    state.cubic(
        outer,
        outer - unit * distance,
        last + outward * distance,
        last,
    );
    cubic(
        &mut piece,
        last + outward * distance,
        outer - unit * distance,
        outer,
    )?;
    cubic(
        &mut piece,
        outer + unit * distance,
        first + outward * distance,
        first,
    )?;
    publish(outline, piece)
}
fn join(
    state: &mut Bounds,
    [center, incoming, outgoing]: [Pair; 3],
    radius: f64,
    join: PenLineJoin,
    limit: f64,
    outline: &mut Option<PathGeometry>,
) -> Result<(), Error> {
    let turn = cross(incoming, outgoing);
    if turn.abs() <= 1e-6 {
        return if join != PenLineJoin::Round || pair_dot(incoming, outgoing) >= 0.0 {
            Ok(())
        } else {
            cap(state, center, incoming, radius, PenLineCap::Round, outline)
        };
    }
    let side = if turn > 0.0 { -1.0 } else { 1.0 };
    let a = narrow(center + left(incoming) * Pair::splat(radius * side));
    let b = narrow(center + left(outgoing) * Pair::splat(radius * side));
    let mut wedge = outline.as_ref().map(|_| figure(center));
    let (first, last) = if turn > 0.0 { (a, b) } else { (b, a) };
    line(&mut wedge, first)?;
    if join == PenLineJoin::Round {
        let reverse = wedge.is_some() && turn < 0.0;
        arc(
            state,
            center,
            if reverse { b } else { a },
            if reverse { a } else { b },
            &mut wedge,
        )?;
        return publish(outline, wedge);
    }
    if join == PenLineJoin::Bevel {
        line(&mut wedge, last)?;
        return publish(outline, wedge);
    }
    let intersection = narrow(a + incoming * Pair::splat(cross(b - a, outgoing) / turn));
    let delta = intersection - center;
    let length = magnitude(delta);
    if !length.is_finite() {
        return Err(Error::InvalidInput);
    }
    if length <= limit * radius {
        state.include(intersection);
        line(&mut wedge, intersection)?;
    } else if join != PenLineJoin::MiterOrBevel {
        let bisector = delta / Pair::splat(length);
        for offset in [first, last] {
            let projection = pair_dot(offset - center, bisector);
            let divisor = length - projection;
            if divisor <= 0.0 {
                return Err(Error::InvalidInput);
            }
            let amount = (limit * radius - projection) / divisor;
            if !amount.is_finite() || !(0.0..=1.0).contains(&amount) {
                return Err(Error::InvalidInput);
            }
            let point = narrow(offset + narrow(intersection - offset) * Pair::splat(amount));
            state.include(point);
            line(&mut wedge, point)?;
        }
    }
    line(&mut wedge, last)?;
    publish(outline, wedge)
}
fn arc(
    state: &mut Bounds,
    center: Pair,
    first: Pair,
    last: Pair,
    figure: &mut Option<PathFigure>,
) -> Result<(), Error> {
    let a = first - center;
    let b = last - center;
    let ra = magnitude(a);
    let rb = magnitude(b);
    if ra == 0.0 || rb == 0.0 {
        return Err(Error::InvalidInput);
    }
    let radius = (ra + rb) * 0.5;
    let angle = cross(a, b).atan2(pair_dot(a, b));
    if !angle.is_finite() || angle == 0.0 {
        return Err(Error::InvalidInput);
    }
    let count = (angle.abs() / (std::f64::consts::PI * 0.5)).ceil() as i32;
    if !(1..=2).contains(&count) {
        return Err(Error::InvalidInput);
    }
    let step = angle / f64::from(count);
    let [x, y] = a.to_array();
    let mut current_angle = y.atan2(x);
    let mut current = first;
    for i in 0..count {
        let next_angle = current_angle + step;
        let next = if i + 1 == count {
            last
        } else {
            narrow(center + Pair::new([next_angle.cos(), next_angle.sin()]) * Pair::splat(radius))
        };
        let factor = Pair::splat(4.0 / 3.0 * (step * 0.25).tan());
        let current_unit = (current - center) / Pair::splat(radius);
        let next_unit = (next - center) / Pair::splat(radius);
        let c1 = current + left(current_unit) * Pair::splat(radius) * factor;
        let c2 = next - left(next_unit) * Pair::splat(radius) * factor;
        state.cubic(current, c1, c2, next);
        cubic(figure, c1, c2, next)?;
        current = next;
        current_angle = next_angle;
    }
    Ok(())
}
fn figure(start: Pair) -> PathFigure {
    PathFigure::new(point(start), true)
}
fn line(figure: &mut Option<PathFigure>, p: Pair) -> Result<(), Error> {
    if let Some(figure) = figure {
        push(
            &mut figure.segments,
            PathSegment::new(PathSegmentKind::Line(point(p))),
        )?;
    }
    Ok(())
}
fn cubic(figure: &mut Option<PathFigure>, a: Pair, b: Pair, end: Pair) -> Result<(), Error> {
    if let Some(figure) = figure {
        push(
            &mut figure.segments,
            PathSegment::new(PathSegmentKind::Cubic([a, b, end].map(point))),
        )?;
    }
    Ok(())
}
fn publish(outline: &mut Option<PathGeometry>, figure: Option<PathFigure>) -> Result<(), Error> {
    let Some(figure) = figure else { return Ok(()) };
    let anchor = wide(figure.start);
    let mut previous = anchor;
    let mut area = 0.0;
    for segment in &figure.segments {
        if !finite(segment.end()) {
            return Err(Error::InvalidInput);
        }
        if let PathSegmentKind::Cubic([a, b, _]) = segment.kind
            && (!finite(a) || !finite(b))
        {
            return Err(Error::InvalidInput);
        }
        let current = wide(segment.end());
        area += cross(previous - anchor, current - anchor);
        previous = current;
    }
    if !area.is_finite() || area <= 0.0 {
        return Err(Error::InvalidInput);
    }
    push(
        &mut outline.as_mut().ok_or(Error::InvalidInput)?.figures,
        figure,
    )
}
