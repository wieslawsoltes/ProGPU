//! Exact original point-batch quads, including source-local spatial paint centers.
use super::*;
pub(super) fn capacity(
    batch: &wire::PointBatch,
    points: &[wire::Point],
) -> Result<(usize, usize), Error> {
    let allowed = POINT_BATCH_EDGE_ALIASED
        | POINT_BATCH_ROUND
        | POINT_BATCH_HAIRLINE
        | POINT_BATCH_FIXED_DEVICE_RADIUS;
    if batch.struct_size != wire::PointBatch::WIRE_SIZE as u32
        || batch.flags & !allowed != 0
        || batch.point_count == 0
        || u64::from(batch.point_offset) + u64::from(batch.point_count) > points.len() as u64
        || !batch.radius.is_finite()
        || batch.radius <= 0.0
        || batch.reserved != 0.0
        || !finite_color(batch.color)
        || !finite_transform(batch.transform)
        || batch.flags & POINT_BATCH_HAIRLINE != 0
            && (batch.radius != 0.5 || batch.flags & POINT_BATCH_FIXED_DEVICE_RADIUS != 0)
        || batch.point_count > u32::MAX / 6
    {
        return Err(Error::InvalidInput);
    }
    let hairline = batch.flags & POINT_BATCH_HAIRLINE != 0;
    let fixed = batch.flags & POINT_BATCH_FIXED_DEVICE_RADIUS != 0;
    let extent = batch.radius
        + if batch.flags & POINT_BATCH_EDGE_ALIASED != 0 {
            0.0
        } else {
            1.5
        };
    for &center in
        &points[batch.point_offset as usize..(batch.point_offset + batch.point_count) as usize]
    {
        if !finite_point(center) {
            return Err(Error::InvalidInput);
        }
        for corner in &CORNERS[..if hairline { 1 } else { 4 }] {
            let local = if hairline {
                center
            } else {
                wire::Point {
                    x: center.x + corner[0] * extent,
                    y: center.y + corner[1] * extent,
                }
            };
            let position = if fixed && !hairline {
                let center = transform_point(batch.transform, center);
                wire::Point {
                    x: center.x + corner[0] * extent,
                    y: center.y + corner[1] * extent,
                }
            } else {
                transform_point(batch.transform, local)
            };
            if !finite_point(local) || !finite_point(position) {
                return Err(Error::InvalidInput);
            }
        }
    }
    Ok((
        batch.point_count as usize * 4,
        batch.point_count as usize * 6,
    ))
}
const CORNERS: [[f32; 2]; 4] = [[-1.0, -1.0], [1.0, -1.0], [1.0, 1.0], [-1.0, 1.0]];
pub(super) fn append(
    batch: &wire::PointBatch,
    points: &[wire::Point],
    brush: f32,
    local_brush: bool,
    output: &mut Output,
) -> Result<(), Error> {
    let (vertices, indices) = capacity(batch, points)?;
    output.reserve(vertices, indices)?;
    let aliased = batch.flags & POINT_BATCH_EDGE_ALIASED != 0;
    let round = batch.flags & POINT_BATCH_ROUND != 0;
    let hairline = batch.flags & POINT_BATCH_HAIRLINE != 0;
    let fixed = batch.flags & POINT_BATCH_FIXED_DEVICE_RADIUS != 0;
    let extent = batch.radius + if aliased { 0.0 } else { 1.5 };
    let diameter = batch.radius * 2.0;
    let shape = if hairline {
        if round { 20.0 } else { 19.0 }
    } else if round {
        1.0
    } else {
        0.0
    };
    for &center in
        &points[batch.point_offset as usize..(batch.point_offset + batch.point_count) as usize]
    {
        let transformed = transform_point(batch.transform, center);
        let quad = CORNERS.map(|corner| {
            let local = wire::Point {
                x: center.x + corner[0] * extent,
                y: center.y + corner[1] * extent,
            };
            let position = if fixed && !hairline {
                wire::Point {
                    x: transformed.x + corner[0] * extent,
                    y: transformed.y + corner[1] * extent,
                }
            } else if !hairline {
                transform_point(batch.transform, local)
            } else {
                transformed
            };
            VectorVertex {
                position: [position.x, position.y],
                color: if local_brush {
                    [center.x, center.y, 0.0, 0.0]
                } else {
                    color(batch.color)
                },
                texture_coordinate: [corner[0] * extent, corner[1] * extent],
                brush_index: brush,
                shape_size: [diameter, diameter],
                shape_type: shape + if aliased { 1000.0 } else { 0.0 },
                ..zero_vertex()
            }
        });
        output.quad(quad)?;
    }
    Ok(())
}
