//! Native primitive capacity and original shader descriptor production.
use super::*;
fn sections(p: &wire::GeometryPrimitive) -> usize {
    ((p.p3.y.abs() / (PI / 24.0)).ceil() as usize).clamp(1, 48)
}
fn curve_segments(p: &wire::GeometryPrimitive) -> usize {
    let [a, b, c, d] = [p.p0, p.p1, p.p2, p.p3].map(|v| transform_point(p.transform, v));
    let x = a.x as f64 - 2.0 * b.x as f64 + c.x as f64;
    let y = a.y as f64 - 2.0 * b.y as f64 + c.y as f64;
    let count = if p.kind == GEOMETRY_QUADRATIC_BEZIER {
        x.hypot(y) / (4.0 * 0.25)
    } else {
        let x1 = b.x as f64 - 2.0 * c.x as f64 + d.x as f64;
        let y1 = b.y as f64 - 2.0 * c.y as f64 + d.y as f64;
        0.75 * ordered_max(x.hypot(y), x1.hypot(y1)) / 0.25
    };
    if !count.is_finite() || count >= 1024.0 * 1024.0 {
        1024
    } else if count <= 24.0 * 24.0 {
        24
    } else {
        count.sqrt().ceil() as usize
    }
}
pub(super) fn capacity(p: &wire::GeometryPrimitive) -> Result<(usize, usize), Error> {
    if !values::geometry(p) {
        return Err(Error::InvalidInput);
    }
    let caps = usize::from(p.flags & PRIMITIVE_START_CAP_MASK != 0)
        + usize::from(p.flags & PRIMITIVE_END_CAP_MASK != 0);
    let outline = p.flags & (PRIMITIVE_FLAG_HAIRLINE | PRIMITIVE_FLAG_FIXED_DEVICE_STROKE) == 0
        && affine_stroke(p.transform);
    Ok(match p.kind {
        GEOMETRY_TRIANGLE => (3, 3),
        GEOMETRY_PATH_CAP | GEOMETRY_PATH_JOIN => (32, 48),
        GEOMETRY_ARC => {
            let n = if outline { sections(p) } else { 1 };
            (n * 4, n * 6)
        }
        GEOMETRY_QUADRILATERAL | GEOMETRY_DOT_GRID | GEOMETRY_LINE => {
            (4 + caps * 32, 6 + caps * 48)
        }
        _ => {
            let n = if outline { curve_segments(p) } else { 24 };
            (
                if outline { n * 4 } else { 2 * (n + 1) } + caps * 32,
                n * 6 + caps * 48,
            )
        }
    })
}
pub(super) fn encoded(thickness: f32, hairline: bool, fixed: bool, maximum: f32) -> f32 {
    if hairline {
        -1.0
    } else if fixed {
        -(thickness + 1.0).max(f32::from_bits(1.0f32.to_bits() + 1))
    } else {
        thickness * maximum
    }
}
pub(super) fn line(
    start: wire::Point,
    end: wire::Point,
    transform: wire::Affine2d,
    source_color: wire::Color,
    thickness: f32,
    encoded: f32,
    outline: bool,
    brush: f32,
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let delta = sub(end, start);
    let length = delta.x.hypot(delta.y);
    if !length.is_finite() {
        return Err(Error::InvalidInput);
    }
    if length <= 0.000001 {
        return Ok(());
    }
    let alias = if aliased { 1000.0 } else { 0.0 };
    let base = output.vertices.len() as u32;
    output.reserve(4, 6)?;
    if outline {
        let inverse = 1.0 / length;
        let half = thickness * 0.5;
        let normal = point(-delta.y * inverse * half, delta.x * inverse * half);
        let points = [
            add(start, normal),
            add(end, normal),
            sub(end, normal),
            sub(start, normal),
        ]
        .map(|v| transform_point(transform, v));
        let (min, max) = bounds(&points);
        for p in corners(sub(min, point(1.5, 1.5)), add(max, point(1.5, 1.5))) {
            output.vertices.push(VectorVertex {
                position: [p.x, p.y],
                color: [points[0].x, points[0].y, points[1].x, points[1].y],
                texture_coordinate: [p.x, p.y],
                brush_index: brush,
                shape_size: [points[2].x, points[2].y],
                corner_radius: points[3].x,
                stroke_thickness: points[3].y,
                shape_type: 14.0 + alias,
            });
        }
        output
            .indices
            .extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
    } else {
        let start = transform_point(transform, start);
        let end = transform_point(transform, end);
        for (p, corner) in [(start, 1.0), (start, -1.0), (end, 2.0), (end, -2.0)] {
            output.vertices.push(VectorVertex {
                position: [p.x, p.y],
                color: color(source_color),
                texture_coordinate: [start.x, start.y],
                brush_index: brush,
                shape_size: [end.x, end.y],
                corner_radius: corner,
                stroke_thickness: encoded,
                shape_type: 3.0 + alias,
            });
        }
        output
            .indices
            .extend_from_slice(&[base, base + 1, base + 2, base + 1, base + 3, base + 2]);
    }
    Ok(())
}
fn evaluate(p: &wire::GeometryPrimitive, t: f32) -> wire::Point {
    let a = 1.0 - t;
    let [p0, p1, p2, p3] = [p.p0, p.p1, p.p2, p.p3];
    if p.kind == GEOMETRY_QUADRATIC_BEZIER {
        point(
            a * a * p0.x + 2.0 * a * t * p1.x + t * t * p2.x,
            a * a * p0.y + 2.0 * a * t * p1.y + t * t * p2.y,
        )
    } else {
        point(
            a * a * a * p0.x + 3.0 * a * a * t * p1.x + 3.0 * a * t * t * p2.x + t * t * t * p3.x,
            a * a * a * p0.y + 3.0 * a * a * t * p1.y + 3.0 * a * t * t * p2.y + t * t * t * p3.y,
        )
    }
}
fn tangent(p: &wire::GeometryPrimitive, t: f32) -> wire::Point {
    let a = 1.0 - t;
    let [p0, p1, p2, p3] = [p.p0, p.p1, p.p2, p.p3];
    if p.kind == GEOMETRY_QUADRATIC_BEZIER {
        point(
            2.0 * ((1.0 - t) * (p1.x - p0.x) + t * (p2.x - p1.x)),
            2.0 * ((1.0 - t) * (p1.y - p0.y) + t * (p2.y - p1.y)),
        )
    } else {
        point(
            3.0 * (a * a * (p1.x - p0.x) + 2.0 * a * t * (p2.x - p1.x) + t * t * (p3.x - p2.x)),
            3.0 * (a * a * (p1.y - p0.y) + 2.0 * a * t * (p2.y - p1.y) + t * t * (p3.y - p2.y)),
        )
    }
}
fn section(
    p: &wire::GeometryPrimitive,
    previous: wire::Point,
    current: wire::Point,
    previous_tangent: wire::Point,
    current_tangent: wire::Point,
    index: usize,
    count: usize,
    brush: f32,
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let chord = sub(current, previous);
    let (Some(start), Some(end)) = (
        normalize(previous_tangent, chord),
        normalize(current_tangent, chord),
    ) else {
        return Ok(());
    };
    let radius = p.stroke_thickness * 0.5;
    let a = point(-start.y * radius, start.x * radius);
    let b = point(-end.y * radius, end.x * radius);
    let points = [
        add(previous, a),
        add(current, b),
        sub(current, b),
        sub(previous, a),
    ]
    .map(|v| transform_point(p.transform, v));
    let kind = if index == 1 {
        if index == count { 14.0 } else { 16.0 }
    } else if index == count {
        17.0
    } else {
        15.0
    };
    stroke_quad(points, brush, aliased, kind, output)
}
pub(super) fn append(
    p: &wire::GeometryPrimitive,
    brush: f32,
    output: &mut Output,
) -> Result<(), Error> {
    let (v, i) = capacity(p)?;
    if !brush.is_finite() || brush < 0.0 {
        return Err(Error::InvalidInput);
    }
    output.reserve(v, i)?;
    let start = (output.vertices.len(), output.indices.len());
    let result = append_inner(p, brush, output);
    if result.is_err() {
        output.vertices.truncate(start.0);
        output.indices.truncate(start.1);
    }
    result
}
fn append_inner(p: &wire::GeometryPrimitive, brush: f32, output: &mut Output) -> Result<(), Error> {
    let aliased = p.flags & PRIMITIVE_FLAG_EDGE_ALIASED != 0;
    let alias = if aliased { 1000.0 } else { 0.0 };
    let base = output.vertices.len() as u32;
    if matches!(
        p.kind,
        GEOMETRY_TRIANGLE | GEOMETRY_QUADRILATERAL | GEOMETRY_DOT_GRID
    ) {
        let points = if p.kind == GEOMETRY_DOT_GRID {
            corners(p.p0, add(p.p0, p.p1))
        } else {
            [p.p0, p.p1, p.p2, p.p3]
        };
        let count = if p.kind == GEOMETRY_TRIANGLE { 3 } else { 4 };
        for local in &points[..count] {
            let position = transform_point(p.transform, *local);
            let mut v = VectorVertex {
                position: [position.x, position.y],
                color: color(p.color),
                texture_coordinate: [local.x, local.y],
                brush_index: brush,
                shape_type: 7.0 + alias,
                ..zero_vertex()
            };
            if p.kind == GEOMETRY_DOT_GRID {
                v.shape_size = [p.p3.x, p.p3.y];
                if p.stroke_thickness > 0.0 {
                    let line = p.p2.x == 1.0;
                    v.corner_radius = if line {
                        -p.stroke_thickness
                    } else {
                        p.stroke_thickness
                    };
                    v.stroke_thickness = if line { p.p2.y } else { 0.0 };
                    v.shape_type = 25.0 + alias;
                } else {
                    v.corner_radius = p.p2.x;
                    v.stroke_thickness = p.p2.y;
                    v.shape_type = 21.0 + alias;
                }
            }
            output.vertices.push(v);
        }
        output
            .indices
            .extend_from_slice(&[base, base + 1, base + 2]);
        if count == 4 {
            output
                .indices
                .extend_from_slice(&[base, base + 2, base + 3]);
        }
        return Ok(());
    }
    let hairline = p.flags & PRIMITIVE_FLAG_HAIRLINE != 0;
    let fixed = p.flags & PRIMITIVE_FLAG_FIXED_DEVICE_STROKE != 0;
    let maximum = affine(p.transform).scales()?[0];
    let encoded = encoded(p.stroke_thickness, hairline, fixed, maximum);
    let outline = !hairline && !fixed && affine_stroke(p.transform);
    if p.kind == GEOMETRY_PATH_CAP {
        let cap = (p.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT;
        let start = p.p2.x == 1.0;
        if hairline || fixed {
            return caps::device(
                cap,
                transform_point(p.transform, p.p0),
                transform_vector(p.transform, p.p1),
                start,
                encoded,
                brush,
                aliased,
                output,
            );
        }
        return caps::cpu(
            cap,
            if outline {
                p.stroke_thickness
            } else {
                p.stroke_thickness * maximum
            },
            if outline {
                p.p0
            } else {
                transform_point(p.transform, p.p0)
            },
            if outline {
                p.p1
            } else {
                transform_vector(p.transform, p.p1)
            },
            start,
            outline.then_some(p.transform),
            brush,
            aliased,
            output,
        );
    }
    if p.kind == GEOMETRY_PATH_JOIN {
        let join = (p.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT;
        let clip = p.flags & PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT != 0;
        if hairline || fixed {
            return joins::device(
                join,
                p.p3.x,
                transform_point(p.transform, p.p0),
                transform_vector(p.transform, p.p1),
                transform_vector(p.transform, p.p2),
                encoded,
                brush,
                aliased,
                clip,
                output,
            );
        }
        return joins::cpu(
            join,
            if outline {
                p.stroke_thickness
            } else {
                p.stroke_thickness * maximum
            },
            p.p3.x,
            if outline {
                p.p0
            } else {
                transform_point(p.transform, p.p0)
            },
            if outline {
                p.p1
            } else {
                transform_vector(p.transform, p.p1)
            },
            if outline {
                p.p2
            } else {
                transform_vector(p.transform, p.p2)
            },
            outline.then_some(p.transform),
            brush,
            aliased,
            p.flags & PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS != 0,
            clip,
            output,
        );
    }
    if p.kind == GEOMETRY_ARC {
        if outline {
            let count = sections(p);
            let at = |theta: f32| {
                point(
                    p.p0.x + p.p1.x * theta.cos() + p.p2.x * theta.sin(),
                    p.p0.y + p.p1.y * theta.cos() + p.p2.y * theta.sin(),
                )
            };
            let tan = |theta: f32| {
                point(
                    (-p.p1.x * theta.sin() + p.p2.x * theta.cos()) * 1.0f32.copysign(p.p3.y),
                    (-p.p1.y * theta.sin() + p.p2.y * theta.cos()) * 1.0f32.copysign(p.p3.y),
                )
            };
            let (mut previous, mut previous_tangent) = (at(p.p3.x), tan(p.p3.x));
            for index in 1..=count {
                let theta = p.p3.x + p.p3.y * (index as f32 / count as f32);
                let current = at(theta);
                let current_tangent = tan(theta);
                section(
                    p,
                    previous,
                    current,
                    previous_tangent,
                    current_tangent,
                    index,
                    count,
                    brush,
                    aliased,
                    output,
                )?;
                previous = current;
                previous_tangent = current_tangent;
            }
            return Ok(());
        }
        let center = transform_point(p.transform, p.p0);
        let x = transform_vector(p.transform, p.p1);
        let y = transform_vector(p.transform, p.p2);
        let padding = if hairline {
            2.0
        } else if fixed {
            p.stroke_thickness * 0.5 + 1.5
        } else {
            encoded * 0.5 + 2.0
        };
        let extent = point(
            x.x.abs() + y.x.abs() + padding,
            x.y.abs() + y.y.abs() + padding,
        );
        return output.quad(corners(sub(center, extent), add(center, extent)).map(|v| {
            VectorVertex {
                position: [v.x, v.y],
                color: [center.x, center.y, p.p3.x, p.p3.y],
                texture_coordinate: [x.x, x.y],
                brush_index: brush,
                shape_size: [y.x, y.y],
                stroke_thickness: encoded,
                shape_type: 12.0 + alias,
                ..zero_vertex()
            }
        }));
    }
    caps::primitive(
        p, hairline, fixed, outline, maximum, encoded, brush, aliased, 1, output,
    )?;
    if matches!(p.kind, GEOMETRY_QUADRATIC_BEZIER | GEOMETRY_CUBIC_BEZIER) {
        if outline {
            let count = curve_segments(p);
            let (mut previous, mut previous_tangent) = (p.p0, tangent(p, 0.0));
            for index in 1..=count {
                let t = index as f32 / count as f32;
                let current = evaluate(p, t);
                let current_tangent = tangent(p, t);
                section(
                    p,
                    previous,
                    current,
                    previous_tangent,
                    current_tangent,
                    index,
                    count,
                    brush,
                    aliased,
                    output,
                )?;
                previous = current;
                previous_tangent = current_tangent;
            }
        } else {
            let [a, b, c, d] = [p.p0, p.p1, p.p2, p.p3].map(|v| transform_point(p.transform, v));
            let base = output.vertices.len() as u32;
            let v = VectorVertex {
                position: [a.x, a.y],
                color: if p.kind == GEOMETRY_CUBIC_BEZIER {
                    [d.x, d.y, 0.0, 0.0]
                } else {
                    [0.0; 4]
                },
                texture_coordinate: [b.x, b.y],
                brush_index: brush,
                shape_size: [c.x, c.y],
                corner_radius: base as f32,
                stroke_thickness: encoded,
                shape_type: if p.kind == GEOMETRY_QUADRATIC_BEZIER {
                    5.0 + alias
                } else {
                    6.0 + alias
                },
            };
            output.vertices.extend(std::iter::repeat_n(v, 50));
            for section in 0..24 {
                let left = base + section * 2;
                output.indices.extend_from_slice(&[
                    left,
                    left + 1,
                    left + 2,
                    left + 1,
                    left + 3,
                    left + 2,
                ]);
            }
        }
    } else {
        let delta = sub(p.p1, p.p0);
        if delta.x.hypot(delta.y) <= 0.000001 {
            return Ok(());
        }
        line(
            p.p0,
            p.p1,
            p.transform,
            p.color,
            p.stroke_thickness,
            encoded,
            outline,
            brush,
            aliased,
            output,
        )?;
    }
    caps::primitive(
        p, hairline, fixed, outline, maximum, encoded, brush, aliased, 2, output,
    )
}
