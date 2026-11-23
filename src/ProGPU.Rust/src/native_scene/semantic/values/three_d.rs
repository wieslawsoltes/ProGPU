//! Exact native camera/light/mesh scalar gates, including raw edge-list policy.
use super::*;
fn matrix(value: wire::Matrix4x4) -> bool {
    [
        value.m11, value.m12, value.m13, value.m14, value.m21, value.m22, value.m23, value.m24,
        value.m31, value.m32, value.m33, value.m34, value.m41, value.m42, value.m43, value.m44,
    ]
    .into_iter()
    .all(f32::is_finite)
}
fn point(value: wire::Point3d) -> bool {
    value.x.is_finite() && value.y.is_finite() && value.z.is_finite() && value.reserved == 0.0
}
fn float4(value: wire::Float4) -> bool {
    [value.x, value.y, value.z, value.w]
        .into_iter()
        .all(f32::is_finite)
}
pub(crate) fn camera_3d(camera: &wire::Camera3d) -> bool {
    camera.struct_size == wire::Camera3d::WIRE_SIZE as u32
        && camera.flags == 0
        && camera.reserved0 == 0
        && camera.reserved1 == 0
        && matrix(camera.projection)
        && matrix(camera.view)
        && point(camera.camera_position)
}
pub(crate) fn line_3d(line: &wire::Line3d) -> bool {
    line.struct_size == wire::Line3d::WIRE_SIZE as u32
        && line.flags == 0
        && line.reserved0 == 0
        && line.reserved1 == 0
        && line.reserved2 == 0
        && line.reserved3 == 0
        && point(line.start)
        && point(line.end)
        && finite(line.color)
        && line.thickness.is_finite()
        && line.thickness > 0.0
        && line.thickness <= 16384.0
        && line.opacity.is_finite()
        && line.opacity >= 0.0
        && line.opacity <= 1.0
        && matrix(line.transform)
}
pub(crate) fn mesh_3d_vertex(vertex: &wire::Mesh3dVertex) -> bool {
    point(vertex.position)
        && point(vertex.normal)
        && finite(vertex.texture_coordinate)
        && vertex.reserved0 == 0
        && vertex.reserved1 == 0
}
pub(crate) fn mesh_3d_edge_pair(first: &wire::Mesh3dVertex, second: &wire::Mesh3dVertex) -> bool {
    mesh_3d_vertex(first)
        && mesh_3d_vertex(second)
        && first.texture_coordinate.x >= MESH_3D_EDGE_MANIFOLD as f32
        && first.texture_coordinate.x <= MESH_3D_EDGE_NON_MANIFOLD as f32
        && first.texture_coordinate.x.floor() == first.texture_coordinate.x
        && first.texture_coordinate.y == 0.0
        && second.texture_coordinate.x == 0.0
        && second.texture_coordinate.y == 0.0
}
pub(crate) fn light_3d(light: &wire::Light3d) -> bool {
    let zero = |v: wire::Float4| v.x == 0.0 && v.y == 0.0 && v.z == 0.0 && v.w == 0.0;
    let direction = |v: wire::Float4| v.x * v.x + v.y * v.y + v.z * v.z > 0.000001;
    if light.struct_size != wire::Light3d::WIRE_SIZE as u32
        || light.flags != 0
        || light.reserved0 != 0
        || !finite(light.color)
        || !float4(light.position_range)
        || !float4(light.direction_inner_cos)
        || !float4(light.attenuation_outer_cos)
    {
        return false;
    }
    let attenuation = || {
        light.attenuation_outer_cos.x >= 0.0
            && light.attenuation_outer_cos.y >= 0.0
            && light.attenuation_outer_cos.z >= 0.0
            && (light.attenuation_outer_cos.x > 0.0
                || light.attenuation_outer_cos.y > 0.0
                || light.attenuation_outer_cos.z > 0.0)
    };
    match light.kind {
        LIGHT_3D_AMBIENT => {
            zero(light.position_range)
                && zero(light.direction_inner_cos)
                && zero(light.attenuation_outer_cos)
        }
        LIGHT_3D_DIRECTIONAL => {
            zero(light.position_range)
                && direction(light.direction_inner_cos)
                && light.direction_inner_cos.w == 0.0
                && zero(light.attenuation_outer_cos)
        }
        LIGHT_3D_POINT => {
            light.position_range.w > 0.0
                && zero(light.direction_inner_cos)
                && light.attenuation_outer_cos.w == 0.0
                && attenuation()
        }
        LIGHT_3D_SPOT => {
            light.position_range.w > 0.0
                && direction(light.direction_inner_cos)
                && light.direction_inner_cos.w >= -1.0
                && light.direction_inner_cos.w <= 1.0
                && light.attenuation_outer_cos.w >= -1.0
                && light.attenuation_outer_cos.w <= 1.0
                && light.direction_inner_cos.w >= light.attenuation_outer_cos.w
                && attenuation()
        }
        _ => false,
    }
}
pub(crate) fn mesh_3d(
    mesh: &wire::Mesh3d,
    vertex_count: usize,
    index_count: usize,
    light_count: usize,
) -> bool {
    let material = MESH_3D_MATERIAL_IMAGE
        | MESH_3D_TILING_MASK
        | MESH_3D_FRONT_FACE
        | MESH_3D_BACK_FACE
        | MESH_3D_SPECULAR_MATERIAL;
    let edge = mesh.topology == MESH_3D_EDGE_LIST;
    let known = if edge {
        MESH_3D_EDGE_DISPLAY_MASK
    } else {
        material
    };
    let image = mesh.flags & MESH_3D_MATERIAL_IMAGE != 0;
    let vo = mesh.vertex_offset as usize;
    let io = mesh.index_offset as usize;
    let lo = mesh.light_offset as usize;
    let faces = MESH_3D_FRONT_FACE | MESH_3D_BACK_FACE;
    mesh.struct_size == wire::Mesh3d::WIRE_SIZE as u32
        && mesh.flags & !known == 0
        && mesh.flags & faces != faces
        && (image || mesh.flags & MESH_3D_TILING_MASK == 0)
        && mesh.topology <= MESH_3D_EDGE_LIST
        && mesh.render_mode <= MESH_3D_SOLID_WIREFRAME
        && (if edge {
            mesh.vertex_count >= 2
                && mesh.vertex_count & 1 == 0
                && mesh.index_count == 0
                && mesh.flags & MESH_3D_EDGE_DISPLAY_MASK != 0
        } else {
            mesh.vertex_count >= 3 && mesh.index_count >= 3
        })
        && vo <= vertex_count
        && mesh.vertex_count as usize <= vertex_count - vo
        && io <= index_count
        && mesh.index_count as usize <= index_count - io
        && mesh.light_count <= SCENE_MAX_3D_LIGHTS_PER_MESH
        && lo <= light_count
        && mesh.light_count as usize <= light_count - lo
        && (mesh.light_count != 0 || mesh.light_offset == 0)
        && matrix(mesh.model_transform)
        && matrix(mesh.normal_transform)
        && finite(mesh.color)
        && float4(mesh.light_direction)
        && mesh.light_direction.w >= 0.0
        && float4(mesh.ambient_color)
        && mesh.ambient_color.w >= 0.0
        && float4(mesh.specular_color)
        && (edge || mesh.specular_color.w > 0.0)
        && float4(mesh.material_ambient)
        && mesh.opacity.is_finite()
        && mesh.opacity >= 0.0
        && mesh.opacity <= 1.0
        && mesh.shading_mode <= MESH_3D_WPF_LIGHTING
        && (image || mesh.material_image_resource_index == 0)
        && (!edge
            || (!image
                && mesh.light_count == 0
                && mesh.light_direction.x > 0.0
                && mesh.light_direction.x <= 64.0
                && mesh.light_direction.y >= -1.0
                && mesh.light_direction.y <= 1.0
                && mesh.light_direction.z > 0.0
                && mesh.light_direction.w >= 0.0
                && mesh.specular_color.x >= 0.0
                && mesh.specular_color.x <= 64.0
                && mesh.specular_color.y >= 0.0
                && mesh.specular_color.y <= 16.0
                && mesh.specular_color.z == 0.0
                && mesh.specular_color.w == 0.0))
}
