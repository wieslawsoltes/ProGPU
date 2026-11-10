//! Original cap fan topology and analytic/affine/device descriptor routes.
use super::*;
pub(super) fn triangles(
    cap: u32,
    thickness: f32,
    center: wire::Point,
    mut direction: wire::Point,
    start: bool,
) -> ([[wire::Point; 3]; 8], usize) {
    let mut result = [[wire::Point::default(); 3]; 8];
    let length = direction.x.hypot(direction.y);
    if cap == STROKE_CAP_FLAT
        || !length.is_finite()
        || length <= 0.0001
        || !thickness.is_finite()
        || thickness <= 0.0001
    {
        return (result, 0);
    }
    direction.x /= length;
    direction.y /= length;
    let radius = thickness * 0.5;
    let outward = if start {
        point(-direction.x, -direction.y)
    } else {
        direction
    };
    let normal = point(-direction.y * radius, direction.x * radius);
    if cap == STROKE_CAP_SQUARE {
        let inner0 = sub(center, normal);
        let inner1 = add(center, normal);
        let outer0 = add(inner0, mul(outward, radius));
        let outer1 = add(inner1, mul(outward, radius));
        result[0] = [inner0, outer0, outer1];
        result[1] = [inner0, outer1, inner1];
        return (result, 2);
    }
    if cap == STROKE_CAP_TRIANGLE {
        result[0] = [
            sub(center, normal),
            add(center, mul(outward, radius)),
            add(center, normal),
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
    let perpendicular = point(-outward.y, outward.x);
    let circle = |i: usize| {
        point(
            center.x + (outward.x * cosine[i] + perpendicular.x * sine[i]) * radius,
            center.y + (outward.y * cosine[i] + perpendicular.y * sine[i]) * radius,
        )
    };
    for (i, triangle) in result.iter_mut().enumerate() {
        *triangle = [center, circle(i), circle(i + 1)];
    }
    (result, 8)
}
pub(super) fn edge_masks(cap: u32, count: usize, index: usize) -> [u32; 2] {
    if index >= count || count == 0 {
        return [0, 0];
    }
    if cap == STROKE_CAP_SQUARE && count == 2 {
        return if index == 0 { [3, 4] } else { [2, 0] };
    }
    if cap == STROKE_CAP_TRIANGLE && count == 1 {
        return [3, 0];
    }
    [2, if index + 1 < count { 4 } else { 0 }]
}
pub(super) fn cpu(
    cap: u32,
    thickness: f32,
    center: wire::Point,
    direction: wire::Point,
    start: bool,
    transform: Option<wire::Affine2d>,
    brush: f32,
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    if cap == STROKE_CAP_ROUND {
        return round(
            thickness, center, direction, start, transform, brush, aliased, output,
        );
    }
    let (triangles, count) = triangles(cap, thickness, center, direction, start);
    for (index, triangle) in triangles[..count].iter().enumerate() {
        let triangle = triangle.map(|point| transform.map_or(point, |t| transform_point(t, point)));
        stroke_triangle(
            triangle,
            brush,
            edge_masks(cap, count, index),
            aliased,
            output,
        )?;
    }
    Ok(())
}
fn round(
    thickness: f32,
    center: wire::Point,
    direction: wire::Point,
    start: bool,
    transform: Option<wire::Affine2d>,
    brush: f32,
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let Some(direction) = normalize(direction, wire::Point::default()) else {
        return Ok(());
    };
    if !thickness.is_finite() || thickness <= 0.0001 {
        return Ok(());
    }
    let outward = if start {
        point(-direction.x, -direction.y)
    } else {
        direction
    };
    let normal = point(-outward.y, outward.x);
    let mut padding = 1.5;
    if let Some(t) = transform {
        padding /= affine(t).scales()?[1];
    }
    let radius = thickness * 0.5;
    let coordinates = corners(
        point(-padding, -radius - padding),
        point(radius + padding, radius + padding),
    );
    output.quad(coordinates.map(|coordinate| {
        let position = point(
            center.x + outward.x * coordinate.x + normal.x * coordinate.y,
            center.y + outward.y * coordinate.x + normal.y * coordinate.y,
        );
        let position = transform.map_or(position, |t| transform_point(t, position));
        VectorVertex {
            position: [position.x, position.y],
            texture_coordinate: [coordinate.x, coordinate.y],
            brush_index: brush,
            shape_size: [thickness, 0.0],
            corner_radius: STROKE_CAP_ROUND as f32,
            shape_type: 24.0 + if aliased { 1000.0 } else { 0.0 },
            ..zero_vertex()
        }
    }))
}
pub(super) fn device(
    cap: u32,
    center: wire::Point,
    direction: wire::Point,
    start: bool,
    encoded: f32,
    brush: f32,
    aliased: bool,
    output: &mut Output,
) -> Result<(), Error> {
    if cap == STROKE_CAP_FLAT {
        return Ok(());
    }
    let descriptor = VectorVertex {
        position: [center.x, center.y],
        color: [cap as f32, if start { 1.0 } else { 0.0 }, 0.0, 0.0],
        texture_coordinate: [direction.x, direction.y],
        brush_index: brush,
        shape_size: [output.vertices.len() as f32, 0.0],
        stroke_thickness: encoded,
        shape_type: 22.0 + if aliased { 1000.0 } else { 0.0 },
        ..zero_vertex()
    };
    output.quad([descriptor; 4])
}
pub(super) fn primitive(
    p: &wire::GeometryPrimitive,
    hairline: bool,
    fixed: bool,
    outline: bool,
    maximum: f32,
    encoded: f32,
    brush: f32,
    aliased: bool,
    selection: u32,
    output: &mut Output,
) -> Result<(), Error> {
    let caps = [
        (p.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT,
        (p.flags & PRIMITIVE_END_CAP_MASK) >> PRIMITIVE_END_CAP_SHIFT,
    ];
    if caps == [STROKE_CAP_FLAT; 2] {
        return Ok(());
    }
    let mut centers = [p.p0, p.p1];
    let mut first = [
        sub(p.p1, p.p0),
        wire::Point::default(),
        wire::Point::default(),
    ];
    let mut last = first;
    if p.kind == GEOMETRY_QUADRATIC_BEZIER {
        centers[1] = p.p2;
        first[1] = sub(p.p2, p.p0);
        last[0] = sub(p.p2, p.p1);
        last[1] = first[1];
    } else if p.kind == GEOMETRY_CUBIC_BEZIER {
        centers[1] = p.p3;
        first[1] = sub(p.p2, p.p0);
        first[2] = sub(p.p3, p.p0);
        last[0] = sub(p.p3, p.p2);
        last[1] = sub(p.p3, p.p1);
        last[2] = first[2];
    }
    for i in 0..2 {
        if selection & (1 << i) == 0 {
            continue;
        }
        let Some(direction) = select_direction(if i == 0 { first } else { last }) else {
            continue;
        };
        if hairline || fixed {
            device(
                caps[i],
                transform_point(p.transform, centers[i]),
                transform_vector(p.transform, direction),
                i == 0,
                encoded,
                brush,
                aliased,
                output,
            )?;
        } else if outline {
            cpu(
                caps[i],
                p.stroke_thickness,
                centers[i],
                direction,
                i == 0,
                Some(p.transform),
                brush,
                aliased,
                output,
            )?;
        } else {
            cpu(
                caps[i],
                p.stroke_thickness * maximum,
                transform_point(p.transform, centers[i]),
                transform_vector(p.transform, direction),
                i == 0,
                None,
                brush,
                aliased,
                output,
            )?;
        }
    }
    Ok(())
}
