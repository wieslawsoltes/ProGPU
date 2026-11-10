//! Original normal-width join fans, clipping and device descriptors.
use super::*;
fn intersect(
    first: wire::Point,
    a: wire::Point,
    second: wire::Point,
    b: wire::Point,
) -> Option<wire::Point> {
    let denominator = cross(a, b);
    if !denominator.is_finite() || denominator.abs() <= 0.0001 {
        return None;
    }
    let distance = cross(sub(second, first), b) / denominator;
    let result = add(first, mul(a, distance));
    finite_point(result).then_some(result)
}
fn triangles(
    join: u32,
    thickness: f32,
    limit: f32,
    center: wire::Point,
    incoming: wire::Point,
    outgoing: wire::Point,
    wpf: bool,
    clip: bool,
) -> ([[wire::Point; 3]; 8], usize) {
    let mut result = [[wire::Point::default(); 3]; 8];
    let (Some(incoming), Some(outgoing)) = (
        normalize(incoming, wire::Point::default()),
        normalize(outgoing, wire::Point::default()),
    ) else {
        return (result, 0);
    };
    if join > STROKE_JOIN_MITER_OR_BEVEL || !thickness.is_finite() || thickness <= 0.0001 {
        return (result, 0);
    }
    let wpf = wpf && join != STROKE_JOIN_MITER_OR_BEVEL;
    let clip = clip && join == STROKE_JOIN_MITER;
    let turn = cross(incoming, outgoing);
    if !turn.is_finite() {
        return (result, 0);
    }
    let radius = thickness * 0.5;
    if turn.abs() <= 0.0001 {
        let dot = incoming.x * outgoing.x + incoming.y * outgoing.y;
        if !wpf || !dot.is_finite() || dot >= -1.0 + 0.0001 {
            return (result, 0);
        }
        let normal = point(-incoming.y, incoming.x);
        if join == STROKE_JOIN_ROUND {
            let at = |angle: f32| {
                point(
                    center.x + (normal.x * angle.cos() + incoming.x * angle.sin()) * radius,
                    center.y + (normal.y * angle.cos() + incoming.y * angle.sin()) * radius,
                )
            };
            for (index, triangle) in result.iter_mut().enumerate() {
                *triangle = [
                    center,
                    at(PI * index as f32 / 8.0),
                    at(PI * (index + 1) as f32 / 8.0),
                ];
            }
            return (result, 8);
        }
        let first = add(center, mul(normal, radius));
        let second = sub(center, mul(normal, radius));
        let a = add(first, mul(incoming, radius));
        let b = add(second, mul(incoming, radius));
        result[0] = [center, first, a];
        result[1] = [center, a, b];
        result[2] = [center, b, second];
        return (result, 3);
    }
    let sign = if turn > 0.0 { -1.0 } else { 1.0 };
    let previous = point(
        center.x - incoming.y * sign * radius,
        center.y + incoming.x * sign * radius,
    );
    let next = point(
        center.x - outgoing.y * sign * radius,
        center.y + outgoing.x * sign * radius,
    );
    result[0] = [previous, center, next];
    if join == STROKE_JOIN_BEVEL {
        return (result, 1);
    }
    if join == STROKE_JOIN_MITER || join == STROKE_JOIN_MITER_OR_BEVEL {
        let limit = if limit.is_finite() && limit >= 1.0 {
            limit
        } else {
            1.0
        };
        if let Some(miter) = intersect(previous, incoming, next, outgoing)
            .filter(|p| (p.x - center.x).hypot(p.y - center.y) <= radius * limit + 0.0001)
        {
            result[1] = [previous, miter, next];
            return (result, 2);
        }
        if !wpf && !clip {
            return (result, 1);
        }
        let dot = incoming.x * outgoing.x + incoming.y * outgoing.y;
        let denominator = radius * ((1.0 - dot) * 0.5).max(0.0).sqrt();
        let numerator = radius * ((1.0 + dot) * 0.5).max(0.0).sqrt();
        if !denominator.is_finite() || denominator <= 0.0001 {
            return (result, 1);
        }
        let ratio = ((limit * radius - numerator) / denominator).max(0.0);
        let first = point(
            previous.x + incoming.x * radius * ratio,
            previous.y + incoming.y * radius * ratio,
        );
        let second = point(
            next.x - outgoing.x * radius * ratio,
            next.y - outgoing.y * radius * ratio,
        );
        if !finite_point(first) || !finite_point(second) {
            return (result, 1);
        }
        result[0] = [center, previous, first];
        result[1] = [center, first, second];
        result[2] = [center, second, next];
        return (result, 3);
    }
    let start = (previous.y - center.y).atan2(previous.x - center.x);
    let mut end = (next.y - center.y).atan2(next.x - center.x);
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
    let count = ((sweep.abs() / (PI / 8.0)).ceil() as usize).clamp(1, 8);
    let at = |angle: f32| {
        point(
            center.x + angle.cos() * radius,
            center.y + angle.sin() * radius,
        )
    };
    for (index, triangle) in result[..count].iter_mut().enumerate() {
        *triangle = [
            center,
            at(start + sweep * index as f32 / count as f32),
            at(start + sweep * (index + 1) as f32 / count as f32),
        ];
    }
    (result, count)
}
pub(super) fn cpu(
    join: u32,
    thickness: f32,
    limit: f32,
    center: wire::Point,
    incoming: wire::Point,
    outgoing: wire::Point,
    transform: Option<wire::Affine2d>,
    brush: f32,
    aliased: bool,
    wpf: bool,
    clip: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let (triangles, count) = triangles(
        join, thickness, limit, center, incoming, outgoing, wpf, clip,
    );
    for (index, triangle) in triangles[..count].iter().enumerate() {
        let masks = if count == 1 {
            [7, 0]
        } else if (join == STROKE_JOIN_MITER || join == STROKE_JOIN_MITER_OR_BEVEL) && count == 2 {
            [3, if index == 0 { 4 } else { 0 }]
        } else {
            [
                2 | if index == 0 { 1 } else { 0 } | if index + 1 == count { 4 } else { 0 },
                if index + 1 < count { 4 } else { 0 },
            ]
        };
        stroke_triangle(
            triangle.map(|p| transform.map_or(p, |t| transform_point(t, p))),
            brush,
            masks,
            aliased,
            output,
        )?;
    }
    Ok(())
}
pub(super) fn device(
    join: u32,
    limit: f32,
    center: wire::Point,
    incoming: wire::Point,
    outgoing: wire::Point,
    encoded: f32,
    brush: f32,
    aliased: bool,
    clip: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let descriptor = VectorVertex {
        position: [center.x, center.y],
        color: [
            join as f32,
            if limit.is_finite() && limit >= 1.0 {
                limit
            } else {
                1.0
            },
            output.vertices.len() as f32,
            if clip && join == STROKE_JOIN_MITER {
                1.0
            } else {
                0.0
            },
        ],
        texture_coordinate: [incoming.x, incoming.y],
        brush_index: brush,
        shape_size: [outgoing.x, outgoing.y],
        stroke_thickness: encoded,
        shape_type: 23.0 + if aliased { 1000.0 } else { 0.0 },
        ..zero_vertex()
    };
    output.quad([descriptor; 4])
}
