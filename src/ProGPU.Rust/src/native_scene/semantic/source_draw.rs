//! Original explicit physical path/triangle admission from the owned native
//! `progpu_native_source_path.hpp` and `progpu_native_vertex_mesh.hpp`.
//! These source policies are never inferred from ordinary path or mesh data.
use super::*;

pub(crate) fn valid_source_coverage_frame(frame: &wire::SourceCoverageFrame) -> bool {
    frame.struct_size == wire::SourceCoverageFrame::WIRE_SIZE as u32
        && frame.version == 1
        && frame.dpi_scale_x.is_finite()
        && frame.dpi_scale_x > 0.0
        && frame.dpi_scale_y.is_finite()
        && frame.dpi_scale_y > 0.0
        && frame.pixel_width > 0
        && frame.pixel_width <= 16384
        && frame.pixel_height > 0
        && frame.pixel_height <= 16384
        && frame.flags == 0
        && frame.reserved == 0
}

fn identity(transform: &wire::Affine2d) -> bool {
    transform.m11 == 1.0
        && transform.m12 == 0.0
        && transform.m21 == 0.0
        && transform.m22 == 1.0
        && transform.m31 == 0.0
        && transform.m32 == 0.0
}

fn valid_source_path_point(point: wire::Point) -> bool {
    point.x.is_finite()
        && point.y.is_finite()
        && point.x.abs() <= 16384.0
        && point.y.abs() <= 16384.0
        && point.x * 16.0 == (point.x * 16.0).floor()
        && point.y * 16.0 == (point.y * 16.0).floor()
}

pub(crate) fn valid_source_path_shape(path: &wire::PathFill, count: u64) -> bool {
    path.segment_count > 0
        && path.segment_offset <= count
        && path.segment_count <= count - path.segment_offset
        && path.boolean_node_offset == 0
        && path.boolean_node_count == 0
        && path.sample_grid == 8
        && path.fill_rule <= 1
        && path.color.r == 1.0
        && path.color.g == 1.0
        && path.color.b == 1.0
        && path.color.a == 1.0
        && identity(&path.transform)
        && path.min_x.is_finite()
        && path.min_y.is_finite()
        && path.max_x.is_finite()
        && path.max_y.is_finite()
        && path.min_x < path.max_x
        && path.min_y < path.max_y
        && path.min_x >= -16384.0
        && path.min_y >= -16384.0
        && path.max_x <= 16384.0
        && path.max_y <= 16384.0
}

/// Each original contour closes independently. The reader copies one record
/// at a time, permitting an unaligned external byte slice without typed casts.
pub(crate) fn valid_source_path_contours(
    path: &wire::PathFill,
    count: u64,
    mut read: impl FnMut(u64) -> Option<wire::PathSegment>,
) -> bool {
    if !valid_source_path_shape(path, count) {
        return false;
    }
    let mut open = false;
    let mut start = wire::Point::default();
    let mut current = wire::Point::default();
    let equal = |a: wire::Point, b: wire::Point| a.x == b.x && a.y == b.y;
    for index in path.segment_offset..path.segment_offset + path.segment_count {
        let Some(segment) = read(index) else {
            return false;
        };
        if segment.kind != PATH_SEGMENT_LINE
            || segment.pad0 != 0
            || segment.pad1 != 0
            || segment.pad2 != 0
            || segment.p2.x != 0.0
            || segment.p2.y != 0.0
            || segment.p3.x != 0.0
            || segment.p3.y != 0.0
            || !valid_source_path_point(segment.p0)
            || !valid_source_path_point(segment.p1)
        {
            return false;
        }
        for point in [segment.p0, segment.p1] {
            if point.x < path.min_x
                || point.y < path.min_y
                || point.x > path.max_x
                || point.y > path.max_y
            {
                return false;
            }
        }
        if !open {
            start = segment.p0;
            open = true;
        } else if !equal(segment.p0, current) {
            return false;
        }
        current = segment.p1;
        if equal(current, start) {
            open = false;
        }
    }
    !open
}

fn frame_for_draw(
    input: &Input<'_>,
    command: &wire::Command,
) -> TransportResult<wire::SourceCoverageFrame> {
    let fail = || error(E::Value, command.payload_offset);
    if command.resource_index >= input.header.resource_count
        || command.payload_size
            < (wire::DrawBrushes::WIRE_SIZE + wire::SourceCoverageFrame::WIRE_SIZE) as u32
    {
        return Err(fail());
    }
    // The enclosing command validator has already admitted the complete span.
    let frame: wire::SourceCoverageFrame = input
        .read(
            command.payload_offset + command.payload_size
                - wire::SourceCoverageFrame::WIRE_SIZE as u32,
        )
        .map_err(|_| fail())?;
    if !valid_source_coverage_frame(&frame) {
        return Err(fail());
    }
    Ok(frame)
}

pub(super) fn validate_source_path_draw(
    input: &Input<'_>,
    command: &wire::Command,
) -> TransportResult<()> {
    frame_for_draw(input, command)?;
    let fail = || error(E::Value, command.payload_offset);
    let resource = input.resource(command.resource_index).map_err(|_| fail())?;
    if resource.kind != SCENE_RESOURCE_PATH_BATCH
        || resource.payload_size == 0
        || resource.payload_size % wire::PathFill::WIRE_SIZE as u32 != 0
        || resource.auxiliary_size % wire::PathSegment::WIRE_SIZE as u32 != 0
    {
        return Err(fail());
    }
    let count = resource.auxiliary_size / wire::PathSegment::WIRE_SIZE as u32;
    if count > 1048576 {
        return Err(fail());
    }
    let mut end = 0_u64;
    for offset in (0..resource.payload_size).step_by(wire::PathFill::WIRE_SIZE) {
        let offset = resource.payload_offset + offset;
        let path: wire::PathFill = input.read(offset).map_err(|_| error(E::Value, offset))?;
        if path.segment_offset != end
            || !valid_source_path_contours(&path, u64::from(count), |index| {
                input
                    .read(
                        resource.auxiliary_offset
                            + index as u32 * wire::PathSegment::WIRE_SIZE as u32,
                    )
                    .ok()
            })
        {
            return Err(error(E::Value, offset));
        }
        end += path.segment_count;
    }
    if end != u64::from(count) {
        return Err(fail());
    }
    Ok(())
}

pub(crate) fn valid_source_coverage_mesh_shape(mesh: &wire::VertexMesh, count: u32) -> bool {
    // Original vertex_mesh_capacity with zero available indices, followed by
    // the independent explicit source policy. Preserve the capacity bound even
    // though an admitted source resource also has the smaller inventory cap.
    mesh.struct_size == wire::VertexMesh::WIRE_SIZE as u32
        && mesh.flags == VERTEX_MESH_EDGE_ALIASED
        && mesh.topology == VERTEX_MESH_TRIANGLES
        && mesh.color_blend_mode == 5
        && mesh.vertex_count > 0
        && mesh.vertex_offset <= count
        && mesh.vertex_count <= count - mesh.vertex_offset
        && mesh.vertex_count <= u32::MAX / 3
        && mesh.vertex_count % 3 == 0
        && mesh.index_offset == 0
        && mesh.index_count == 0
        && identity(&mesh.transform)
        && mesh.reserved == [0, 0]
}

pub(crate) fn valid_source_coverage_vertex(
    vertex: &wire::MeshVertex,
    frame: &wire::SourceCoverageFrame,
) -> bool {
    vertex.position.x.is_finite()
        && vertex.position.y.is_finite()
        && vertex.texture_coordinate.x.is_finite()
        && vertex.texture_coordinate.y.is_finite()
        && vertex.color.r == 1.0
        && vertex.color.g == 1.0
        && vertex.color.b == 1.0
        && (vertex.color.a == 0.0 || vertex.color.a == 1.0)
        && vertex.position.x >= 0.0
        && vertex.position.y >= 0.0
        && vertex.position.x <= frame.pixel_width as f32
        && vertex.position.y <= frame.pixel_height as f32
        && vertex.texture_coordinate.x == vertex.position.x / frame.dpi_scale_x
        && vertex.texture_coordinate.y == vertex.position.y / frame.dpi_scale_y
}

pub(super) fn validate_source_coverage_draw(
    input: &Input<'_>,
    command: &wire::Command,
) -> TransportResult<()> {
    let frame = frame_for_draw(input, command)?;
    let fail = || error(E::Value, command.payload_offset);
    let resource = input.resource(command.resource_index).map_err(|_| fail())?;
    if resource.kind != SCENE_RESOURCE_VERTEX_MESH
        || resource.payload_size == 0
        || resource.payload_size % wire::VertexMesh::WIRE_SIZE as u32 != 0
        || resource.auxiliary_size % wire::MeshVertex::WIRE_SIZE as u32 != 0
        || resource.payload_offset % 4 != 0
        || resource.auxiliary_offset % 4 != 0
    {
        return Err(fail());
    }
    let count = resource.auxiliary_size / wire::MeshVertex::WIRE_SIZE as u32;
    if count > 1048576 {
        return Err(fail());
    }
    let mut expected = 0_u32;
    for offset in (0..resource.payload_size).step_by(wire::VertexMesh::WIRE_SIZE) {
        let offset = resource.payload_offset + offset;
        let mesh: wire::VertexMesh = input.read(offset).map_err(|_| error(E::Value, offset))?;
        if !valid_source_coverage_mesh_shape(&mesh, count)
            || mesh.vertex_offset != expected
            || mesh.index_offset != 0
        {
            return Err(error(E::Value, offset));
        }
        expected += mesh.vertex_count;
    }
    if expected != count {
        return Err(fail());
    }
    for index in 0..count {
        let vertex: wire::MeshVertex = input
            .read(resource.auxiliary_offset + index * wire::MeshVertex::WIRE_SIZE as u32)
            .map_err(|_| fail())?;
        if !valid_source_coverage_vertex(&vertex, &frame) {
            return Err(fail());
        }
    }
    Ok(())
}
