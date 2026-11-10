//! Original normal-width cap/join triangles shared by native paint/input policy.
use super::*;
pub(super) type Triangle = [wire::Point; 3];
pub(super) type Triangles = ([Triangle; 8], usize);
fn normalize(p: wire::Point) -> Option<wire::Point> {
    let length = p.x.hypot(p.y);
    (length.is_finite() && length > 0.0001).then(|| p2(p.x / length, p.y / length))
}
fn cross(a: wire::Point, b: wire::Point) -> f32 {
    a.x * b.y - a.y * b.x
}
fn intersect(
    a: wire::Point,
    ad: wire::Point,
    b: wire::Point,
    bd: wire::Point,
) -> Option<wire::Point> {
    let denominator = cross(ad, bd);
    if !denominator.is_finite() || denominator.abs() <= 0.0001 {
        return None;
    }
    let distance = cross(p2(b.x - a.x, b.y - a.y), bd) / denominator;
    let result = p2(a.x + ad.x * distance, a.y + ad.y * distance);
    finite_point(result).then_some(result)
}
pub(super) fn cap(
    kind: u32,
    thickness: f32,
    center: wire::Point,
    direction: wire::Point,
    start: bool,
) -> Triangles {
    let mut result = [[wire::Point::default(); 3]; 8];
    let Some(direction) = normalize(direction) else {
        return (result, 0);
    };
    if kind == STROKE_CAP_FLAT || !thickness.is_finite() || thickness <= 0.0001 {
        return (result, 0);
    }
    let radius = thickness * 0.5;
    let outward = if start {
        p2(-direction.x, -direction.y)
    } else {
        direction
    };
    let normal = p2(-direction.y * radius, direction.x * radius);
    let a = p2(center.x - normal.x, center.y - normal.y);
    let b = p2(center.x + normal.x, center.y + normal.y);
    if kind == STROKE_CAP_SQUARE {
        let c = p2(a.x + outward.x * radius, a.y + outward.y * radius);
        let d = p2(b.x + outward.x * radius, b.y + outward.y * radius);
        result[0] = [a, c, d];
        result[1] = [a, d, b];
        return (result, 2);
    }
    if kind == STROKE_CAP_TRIANGLE {
        result[0] = [
            a,
            p2(center.x + outward.x * radius, center.y + outward.y * radius),
            b,
        ];
        return (result, 1);
    }
    let cosine = [
        0.0,
        0.3826834324,
        0.7071067812,
        0.9238795325,
        1.0,
        0.9238795325,
        0.7071067812,
        0.3826834324,
        0.0,
    ];
    let sine = [
        -1.0,
        -0.9238795325,
        -0.7071067812,
        -0.3826834324,
        0.0,
        0.3826834324,
        0.7071067812,
        0.9238795325,
        1.0,
    ];
    let perpendicular = p2(-outward.y, outward.x);
    let point = |i: usize| {
        p2(
            center.x + (outward.x * cosine[i] + perpendicular.x * sine[i]) * radius,
            center.y + (outward.y * cosine[i] + perpendicular.y * sine[i]) * radius,
        )
    };
    for (i, triangle) in result.iter_mut().enumerate() {
        *triangle = [center, point(i), point(i + 1)];
    }
    (result, 8)
}
pub(super) fn join(
    kind: u32,
    thickness: f32,
    limit: f32,
    center: wire::Point,
    incoming: wire::Point,
    outgoing: wire::Point,
    wpf: bool,
    clipped: bool,
) -> Triangles {
    let mut result = [[wire::Point::default(); 3]; 8];
    let (Some(incoming), Some(outgoing)) = (normalize(incoming), normalize(outgoing)) else {
        return (result, 0);
    };
    if kind > STROKE_JOIN_MITER_OR_BEVEL || !thickness.is_finite() || thickness <= 0.0001 {
        return (result, 0);
    }
    let wpf = wpf && kind != STROKE_JOIN_MITER_OR_BEVEL;
    let clipped = clipped && kind == STROKE_JOIN_MITER;
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
        let normal = p2(-incoming.y, incoming.x);
        if kind == STROKE_JOIN_ROUND {
            let point = |angle: f32| {
                p2(
                    center.x + (normal.x * angle.cos() + incoming.x * angle.sin()) * radius,
                    center.y + (normal.y * angle.cos() + incoming.y * angle.sin()) * radius,
                )
            };
            for (i, triangle) in result.iter_mut().enumerate() {
                *triangle = [
                    center,
                    point(std::f32::consts::PI * i as f32 / 8.0),
                    point(std::f32::consts::PI * (i + 1) as f32 / 8.0),
                ];
            }
            return (result, 8);
        }
        let first = p2(center.x + normal.x * radius, center.y + normal.y * radius);
        let second = p2(center.x - normal.x * radius, center.y - normal.y * radius);
        let first_outer = p2(first.x + incoming.x * radius, first.y + incoming.y * radius);
        let second_outer = p2(
            second.x + incoming.x * radius,
            second.y + incoming.y * radius,
        );
        result[0] = [center, first, first_outer];
        result[1] = [center, first_outer, second_outer];
        result[2] = [center, second_outer, second];
        return (result, 3);
    }
    let sign = if turn > 0.0 { -1.0 } else { 1.0 };
    let previous = p2(
        center.x - incoming.y * sign * radius,
        center.y + incoming.x * sign * radius,
    );
    let next = p2(
        center.x - outgoing.y * sign * radius,
        center.y + outgoing.x * sign * radius,
    );
    result[0] = [previous, center, next];
    if kind == STROKE_JOIN_BEVEL {
        return (result, 1);
    }
    if kind == STROKE_JOIN_MITER || kind == STROKE_JOIN_MITER_OR_BEVEL {
        let limit = if limit.is_finite() && limit >= 1.0 {
            limit
        } else {
            1.0
        };
        let miter = intersect(previous, incoming, next, outgoing)
            .filter(|p| (p.x - center.x).hypot(p.y - center.y) <= radius * limit + 0.0001);
        if let Some(miter) = miter {
            result[1] = [previous, miter, next];
            return (result, 2);
        }
        if !wpf && !clipped {
            return (result, 1);
        }
        let dot = incoming.x * outgoing.x + incoming.y * outgoing.y;
        let denominator = radius * maximum(0.0, (1.0 - dot) * 0.5).sqrt();
        let numerator = radius * maximum(0.0, (1.0 + dot) * 0.5).sqrt();
        if !denominator.is_finite() || denominator <= 0.0001 {
            return (result, 1);
        }
        let ratio = maximum(0.0, (limit * radius - numerator) / denominator);
        let first = p2(
            previous.x + incoming.x * radius * ratio,
            previous.y + incoming.y * radius * ratio,
        );
        let second = p2(
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
            end += std::f32::consts::PI * 2.0;
        }
    } else {
        while end > start {
            end -= std::f32::consts::PI * 2.0;
        }
    }
    let sweep = end - start;
    let count = ((sweep.abs() / (std::f32::consts::PI / 8.0)).ceil() as usize).clamp(1, 8);
    for (i, triangle) in result[..count].iter_mut().enumerate() {
        let a = start + sweep * i as f32 / count as f32;
        let b = start + sweep * (i + 1) as f32 / count as f32;
        *triangle = [
            center,
            p2(center.x + a.cos() * radius, center.y + a.sin() * radius),
            p2(center.x + b.cos() * radius, center.y + b.sin() * radius),
        ];
    }
    (result, count)
}
