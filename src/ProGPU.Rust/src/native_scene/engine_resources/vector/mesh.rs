//! Original mesh topology, skipped invalid triangles and explicit physical coverage.
use super::*;
pub(super) fn capacity(
    mesh: &wire::VertexMesh,
    vertices: &[wire::MeshVertex],
    indices: usize,
) -> Result<(usize, usize), Error> {
    if !crate::native_scene::semantic::vertex_mesh::valid(mesh, vertices, indices) {
        return Err(Error::InvalidInput);
    }
    let elements = if mesh.index_count != 0 {
        mesh.index_count
    } else {
        mesh.vertex_count
    };
    let triangles = if mesh.topology == VERTEX_MESH_TRIANGLES {
        elements / 3
    } else {
        elements.saturating_sub(2)
    };
    Ok((mesh.vertex_count as usize, triangles as usize * 3))
}
pub(super) fn append(
    mesh: &wire::VertexMesh,
    vertices: &[wire::MeshVertex],
    indices: &[u16],
    opacity: f32,
    brush: f32,
    output: &mut Output,
) -> Result<(), Error> {
    let (vc, ic) = capacity(mesh, vertices, indices.len())?;
    if !opacity.is_finite() || !(0.0..=1.0).contains(&opacity) {
        return Err(Error::InvalidInput);
    }
    output.reserve(vc, ic)?;
    let base = output.vertices.len() as u32;
    for source in
        &vertices[mesh.vertex_offset as usize..(mesh.vertex_offset + mesh.vertex_count) as usize]
    {
        let p = transform_point(mesh.transform, source.position);
        let c = source.color;
        output.vertices.push(VectorVertex {
            position: [p.x, p.y],
            color: [c.r * c.a, c.g * c.a, c.b * c.a, c.a],
            texture_coordinate: [source.texture_coordinate.x, source.texture_coordinate.y],
            brush_index: brush,
            corner_radius: mesh.color_blend_mode as f32,
            stroke_thickness: opacity,
            shape_type: 18.0
                + if mesh.flags & VERTEX_MESH_EDGE_ALIASED != 0 {
                    1000.0
                } else {
                    0.0
                },
            ..zero_vertex()
        });
    }
    let element = |offset: u32| {
        if mesh.index_count != 0 {
            u32::from(indices[(mesh.index_offset + offset) as usize])
        } else {
            offset
        }
    };
    for triangle in 0..ic as u32 / 3 {
        let [a, b, c] = match mesh.topology {
            VERTEX_MESH_TRIANGLE_STRIP => [
                element(triangle + if triangle & 1 == 0 { 0 } else { 1 }),
                element(triangle + if triangle & 1 == 0 { 1 } else { 0 }),
                element(triangle + 2),
            ],
            VERTEX_MESH_TRIANGLE_FAN => [element(0), element(triangle + 1), element(triangle + 2)],
            _ => [
                element(triangle * 3),
                element(triangle * 3 + 1),
                element(triangle * 3 + 2),
            ],
        };
        if a >= mesh.vertex_count || b >= mesh.vertex_count || c >= mesh.vertex_count {
            continue;
        }
        output
            .indices
            .extend_from_slice(&[base + a, base + b, base + c]);
    }
    Ok(())
}
pub(super) fn append_source(
    mesh: &wire::VertexMesh,
    vertices: &[wire::MeshVertex],
    offset: [f32; 2],
    opacity: f32,
    brush: f32,
    output: &mut Output,
) -> Result<(), Error> {
    output.reserve(mesh.vertex_count as usize, mesh.vertex_count as usize)?;
    let delta = wide::f32x4::from([offset[0], offset[1], offset[0], offset[1]]);
    for triangle in vertices
        [mesh.vertex_offset as usize..(mesh.vertex_offset + mesh.vertex_count) as usize]
        .chunks_exact(3)
    {
        let first = (wide::f32x4::from([
            triangle[0].position.x,
            triangle[0].position.y,
            triangle[1].position.x,
            triangle[1].position.y,
        ]) + delta)
            .to_array();
        let last = (wide::f32x4::from([triangle[2].position.x, triangle[2].position.y, 0.0, 0.0])
            + delta)
            .to_array();
        let coverage = triangle.iter().enumerate().fold(0u32, |bits, (i, v)| {
            bits | if v.color.a == 1.0 { 1 << i } else { 0 }
        });
        for (i, source) in triangle.iter().enumerate() {
            let position = if i < 2 {
                [first[i * 2], first[i * 2 + 1]]
            } else {
                [last[0], last[1]]
            };
            output.indices.push(output.vertices.len() as u32);
            output.vertices.push(VectorVertex {
                position,
                color: first,
                shape_size: [last[0], last[1]],
                texture_coordinate: [source.texture_coordinate.x, source.texture_coordinate.y],
                corner_radius: coverage as f32,
                stroke_thickness: opacity,
                brush_index: brush,
                shape_type: 1026.0,
                ..zero_vertex()
            });
        }
    }
    Ok(())
}
