//! Native semantic stroke layout, collapsed-frame early-out and ordered widening.
use super::*;
fn selected<'a>(s: &wire::Stroke, points: &'a [wire::Point]) -> Result<&'a [wire::Point], Error> {
    let start = s.point_offset as usize;
    let end = start
        .checked_add(s.point_count as usize)
        .ok_or(Error::Capacity)?;
    points.get(start..end).ok_or(Error::InvalidInput)
}
fn doubles<'a>(s: &wire::Stroke, values: &'a [f64]) -> Result<(&'a [f64], &'a [f64]), Error> {
    let range = |offset: u64, count: u64| {
        let start = usize::try_from(offset).map_err(|_| Error::Capacity)?;
        let end = start
            .checked_add(usize::try_from(count).map_err(|_| Error::Capacity)?)
            .ok_or(Error::Capacity)?;
        values.get(start..end).ok_or(Error::InvalidInput)
    };
    Ok((
        range(s.knot_offset, s.knot_count)?,
        range(s.weight_offset, s.weight_count)?,
    ))
}
fn collapsed(s: &wire::Stroke) -> bool {
    finite_transform(s.transform) && affine(s.transform).scales().is_err()
}
pub(super) fn capacity(
    s: &wire::Stroke,
    points: &[wire::Point],
    values: &[f64],
) -> Result<(usize, usize), Error> {
    if collapsed(s) {
        return Ok((0, 0));
    }
    let points = selected(s, points)?;
    if s.kind == SCENE_STROKE_POLYLINE {
        return dash::capacity(s, points, values);
    }
    let (knots, weights) = doubles(s, values)?;
    if s.degree > 1 << 20 {
        return Err(Error::InvalidInput);
    }
    if points.len() < 2 || knots.is_empty() {
        return Ok((0, 0));
    }
    if spline::domain(s, knots).is_none() {
        return dash::capacity(s, points, values);
    }
    let count = spline::count(s, points)?;
    if count == 0 {
        return Ok((0, 0));
    }
    let mut sampled = [wire::Point::default(); 101];
    if s.dash_interval_count != 0 {
        spline::sample(s, points, knots, weights, count, &mut sampled)?;
    }
    dash::capacity(s, &sampled[..=count], values)
}
pub(super) fn append(
    s: &wire::Stroke,
    points: &[wire::Point],
    values: &[f64],
    brush: f32,
    output: &mut Output,
) -> Result<(), Error> {
    if collapsed(s) {
        return Ok(());
    }
    let points = selected(s, points)?;
    if s.kind == SCENE_STROKE_POLYLINE {
        return polyline(s, points, values, brush, output);
    }
    let (knots, weights) = doubles(s, values)?;
    if spline::domain(s, knots).is_none() {
        return polyline(s, points, values, brush, output);
    }
    let count = spline::count(s, points)?;
    if count == 0 {
        return Ok(());
    }
    let mut sampled = [wire::Point::default(); 101];
    spline::sample(s, points, knots, weights, count, &mut sampled)?;
    polyline(s, &sampled[..=count], values, brush, output)
}
fn polyline(
    s: &wire::Stroke,
    points: &[wire::Point],
    values: &[f64],
    brush: f32,
    output: &mut Output,
) -> Result<(), Error> {
    let hairline = s.flags & POLYLINE_FLAG_HAIRLINE != 0;
    let fixed = s.flags & POLYLINE_FLAG_FIXED_DEVICE_STROKE != 0;
    let wpf = s.flags & POLYLINE_FLAG_WPF_JOIN_SEMANTICS != 0;
    let clip = s.flags & POLYLINE_FLAG_CLIP_MITER_AT_LIMIT != 0;
    let closed = s.flags & POLYLINE_FLAG_CLOSED != 0;
    let all = POLYLINE_FLAG_EDGE_ALIASED
        | POLYLINE_FLAG_HAIRLINE
        | POLYLINE_FLAG_FIXED_DEVICE_STROKE
        | POLYLINE_FLAG_CLOSED
        | POLYLINE_FLAG_WPF_JOIN_SEMANTICS
        | POLYLINE_FLAG_CLIP_MITER_AT_LIMIT;
    if points.len() < 2
        || (closed && points.len() < 3)
        || s.flags & !all != 0
        || s.line_join > STROKE_JOIN_MITER_OR_BEVEL
        || (clip && s.line_join != STROKE_JOIN_MITER)
        || !finite_color(s.color)
        || !finite_transform(s.transform)
        || !s.stroke_thickness.is_finite()
        || !s.miter_limit.is_finite()
        || (hairline && fixed)
        || (wpf && (hairline || fixed))
        || (hairline && s.stroke_thickness != 0.0)
        || (!hairline && s.stroke_thickness <= 0.0)
        || points.iter().any(|p| !finite_point(*p))
    {
        return Err(Error::InvalidInput);
    }
    let (v, i) = dash::capacity(s, points, values)?;
    output.reserve(v, i)?;
    let initial = (output.vertices.len(), output.indices.len());
    let maximum = affine(s.transform).scales()?[0];
    let outline = !hairline && !fixed && affine_stroke(s.transform);
    let encoded = geometry::encoded(s.stroke_thickness, hairline, fixed, maximum);
    let aliased = s.flags & POLYLINE_FLAG_EDGE_ALIASED != 0;
    let emit =
        |event: dash::Event, device_coordinates: bool, output: &mut Output| -> Result<(), Error> {
            match event {
                dash::Event::Body(a, b) => geometry::line(
                    a,
                    b,
                    if device_coordinates {
                        identity_transform()
                    } else {
                        s.transform
                    },
                    s.color,
                    s.stroke_thickness,
                    encoded,
                    outline,
                    brush,
                    aliased,
                    output,
                ),
                dash::Event::Cap(cap, center, direction, start) => {
                    if hairline || fixed {
                        caps::device(
                            cap,
                            if device_coordinates {
                                center
                            } else {
                                transform_point(s.transform, center)
                            },
                            if device_coordinates {
                                direction
                            } else {
                                transform_vector(s.transform, direction)
                            },
                            start,
                            encoded,
                            brush,
                            aliased,
                            output,
                        )
                    } else if outline {
                        caps::cpu(
                            cap,
                            s.stroke_thickness,
                            center,
                            direction,
                            start,
                            Some(s.transform),
                            brush,
                            aliased,
                            output,
                        )
                    } else {
                        caps::cpu(
                            cap,
                            s.stroke_thickness * maximum,
                            transform_point(s.transform, center),
                            transform_vector(s.transform, direction),
                            start,
                            None,
                            brush,
                            aliased,
                            output,
                        )
                    }
                }
                dash::Event::Join(center, incoming, outgoing) => {
                    if hairline || fixed {
                        joins::device(
                            s.line_join,
                            s.miter_limit,
                            if device_coordinates {
                                center
                            } else {
                                transform_point(s.transform, center)
                            },
                            if device_coordinates {
                                incoming
                            } else {
                                transform_vector(s.transform, incoming)
                            },
                            if device_coordinates {
                                outgoing
                            } else {
                                transform_vector(s.transform, outgoing)
                            },
                            encoded,
                            brush,
                            aliased,
                            clip,
                            output,
                        )
                    } else if outline {
                        joins::cpu(
                            s.line_join,
                            s.stroke_thickness,
                            s.miter_limit,
                            center,
                            incoming,
                            outgoing,
                            Some(s.transform),
                            brush,
                            aliased,
                            wpf,
                            clip,
                            output,
                        )
                    } else {
                        joins::cpu(
                            s.line_join,
                            s.stroke_thickness * maximum,
                            s.miter_limit,
                            transform_point(s.transform, center),
                            transform_vector(s.transform, incoming),
                            transform_vector(s.transform, outgoing),
                            None,
                            brush,
                            aliased,
                            wpf,
                            clip,
                            output,
                        )
                    }
                }
            }
        };
    let result = (|| {
        if s.dash_interval_count != 0 {
            return dash::walk(s, points, values, |event| {
                emit(event, hairline || fixed, output)
            });
        }
        let segment = |a, b, cap, shift| wire::GeometryPrimitive {
            kind: GEOMETRY_LINE,
            flags: (if aliased {
                PRIMITIVE_FLAG_EDGE_ALIASED
            } else {
                0
            }) | (if hairline { PRIMITIVE_FLAG_HAIRLINE } else { 0 })
                | (if fixed {
                    PRIMITIVE_FLAG_FIXED_DEVICE_STROKE
                } else {
                    0
                })
                | (cap << shift),
            p0: points[a],
            p1: points[b],
            stroke_thickness: s.stroke_thickness,
            color: s.color,
            transform: s.transform,
            ..Default::default()
        };
        if !closed {
            let p = segment(0, 1, s.start_cap, PRIMITIVE_START_CAP_SHIFT);
            caps::primitive(
                &p, hairline, fixed, outline, maximum, encoded, brush, aliased, 1, output,
            )?;
        }
        let count = if closed {
            points.len()
        } else {
            points.len() - 1
        };
        for index in 0..count {
            let next = (index + 1) % points.len();
            emit(
                dash::Event::Body(points[index], points[next]),
                false,
                output,
            )?;
            if closed || next + 1 < points.len() {
                let after = (next + 1) % points.len();
                emit(
                    dash::Event::Join(
                        points[next],
                        sub(points[next], points[index]),
                        sub(points[after], points[next]),
                    ),
                    false,
                    output,
                )?;
            }
        }
        if !closed {
            let n = points.len();
            let p = segment(n - 2, n - 1, s.end_cap, PRIMITIVE_END_CAP_SHIFT);
            caps::primitive(
                &p, hairline, fixed, outline, maximum, encoded, brush, aliased, 2, output,
            )?;
        }
        Ok(())
    })();
    if result.is_err() {
        output.vertices.truncate(initial.0);
        output.indices.truncate(initial.1);
    }
    result
}
