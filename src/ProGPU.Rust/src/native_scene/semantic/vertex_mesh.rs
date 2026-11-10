//! Exact original mesh packing and transformed-vertex admission.
use super::wire;
use wire::constants::*;
pub(crate) fn layout(meshes: &[wire::VertexMesh], auxiliary_size: usize) -> Option<(usize, usize)> {
    if meshes.is_empty() {
        return None;
    }
    let mut vertices = 0u64;
    let mut indices = 0u64;
    for mesh in meshes {
        if !header(mesh)
            || u64::from(mesh.vertex_offset) != vertices
            || u64::from(mesh.index_offset) != indices
        {
            return None;
        }
        vertices = vertices.checked_add(u64::from(mesh.vertex_count))?;
        indices = indices.checked_add(u64::from(mesh.index_count))?;
    }
    if vertices > u64::from(u32::MAX)
        || indices > u64::from(u32::MAX)
        || vertices * wire::MeshVertex::WIRE_SIZE as u64 + indices * 2 != auxiliary_size as u64
    {
        return None;
    }
    Some((vertices as usize, indices as usize))
}
fn header(mesh: &wire::VertexMesh) -> bool {
    mesh.struct_size == wire::VertexMesh::WIRE_SIZE as u32
        && mesh.flags & !VERTEX_MESH_EDGE_ALIASED == 0
        && mesh.topology <= VERTEX_MESH_TRIANGLE_FAN
        && mesh.color_blend_mode <= 28
        && mesh.vertex_count != 0
        && finite_transform(mesh.transform)
        && mesh.reserved == [0; 2]
}
pub(crate) fn valid(
    mesh: &wire::VertexMesh,
    vertices: &[wire::MeshVertex],
    index_count: usize,
) -> bool {
    let vo = mesh.vertex_offset as usize;
    let io = mesh.index_offset as usize;
    if !header(mesh)
        || vo > vertices.len()
        || mesh.vertex_count as usize > vertices.len() - vo
        || io > index_count
        || mesh.index_count as usize > index_count - io
    {
        return false;
    }
    let elements = if mesh.index_count != 0 {
        mesh.index_count
    } else {
        mesh.vertex_count
    };
    if elements > u32::MAX / 3 {
        return false;
    }
    for source in &vertices[vo..vo + mesh.vertex_count as usize] {
        let p = source.position;
        let t = mesh.transform;
        // Source transform_point uses separate ordinary float products/additions.
        let transformed = [
            p.x * t.m11 + p.y * t.m21 + t.m31,
            p.x * t.m12 + p.y * t.m22 + t.m32,
        ];
        if ![
            p.x,
            p.y,
            source.texture_coordinate.x,
            source.texture_coordinate.y,
        ]
        .into_iter()
        .all(f32::is_finite)
            || !finite_color(source.color)
            || !transformed.into_iter().all(f32::is_finite)
        {
            return false;
        }
    }
    true
}
fn finite_transform(t: wire::Affine2d) -> bool {
    [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
        .into_iter()
        .all(f32::is_finite)
}
fn finite_color(c: wire::Color) -> bool {
    [c.r, c.g, c.b, c.a].into_iter().all(f32::is_finite)
}
