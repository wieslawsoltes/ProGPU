//! Exact storage records. Independent columns use SIMD; each source matrix
//! accumulator retains its original four ordered multiply/add operations.
use super::*;
use bytemuck::{Pod, Zeroable};
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct NativeThreeDCamera {
    pub projection: [f32; 16],
    pub view: [f32; 16],
    pub camera_position: [f32; 4],
    pub viewport: [f32; 4],
    pub viewport_rect: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct NativeThreeDLine {
    pub start: [f32; 4],
    pub end: [f32; 4],
    pub color: [f32; 4],
    pub thickness: f32,
    pub opacity: f32,
    pub camera_index: u32,
    pub flags: u32,
    pub transform: [f32; 16],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct NativeThreeDMesh {
    pub flags: u32,
    pub topology: u32,
    pub render_mode: u32,
    pub camera_index: u32,
    pub vertex_offset: u32,
    pub vertex_count: u32,
    pub index_offset: u32,
    pub index_count: u32,
    pub model_transform: [f32; 16],
    pub normal_transform: [f32; 16],
    pub color: [f32; 4],
    pub light_direction: [f32; 4],
    pub ambient_color: [f32; 4],
    pub specular_color: [f32; 4],
    pub material_ambient: [f32; 4],
    pub opacity: f32,
    pub shading_mode: u32,
    pub material_image_resource_index: u32,
    pub material_factors: u32,
    pub light_offset: u32,
    pub light_count: u32,
    pub reserved0: u32,
    pub reserved1: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct NativeThreeDEdge {
    pub start: [f32; 4],
    pub end: [f32; 4],
    pub first_normal: [f32; 4],
    pub second_normal: [f32; 4],
    pub mesh_index: u32,
    pub topology: u32,
    pub reserved0: u32,
    pub reserved1: u32,
}
const _: () = assert!(
    size_of::<NativeThreeDCamera>() == 176
        && size_of::<NativeThreeDLine>() == 128
        && size_of::<NativeThreeDMesh>() == 272
        && size_of::<NativeThreeDEdge>() == 80
);
pub(super) fn matrix(m: wire::Matrix4x4) -> [f32; 16] {
    [
        m.m11, m.m12, m.m13, m.m14, m.m21, m.m22, m.m23, m.m24, m.m31, m.m32, m.m33, m.m34, m.m41,
        m.m42, m.m43, m.m44,
    ]
}
pub(super) fn affine(m: wire::Affine2d) -> [f32; 16] {
    [
        m.m11, m.m12, 0.0, 0.0, m.m21, m.m22, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, m.m31, m.m32, 0.0, 1.0,
    ]
}
pub(super) fn multiply(a: [f32; 16], b: [f32; 16], path: NativeThreeDLoweringPath) -> [f32; 16] {
    if path == NativeThreeDLoweringPath::ScalarReference {
        let mut result = [0.0; 16];
        for row in 0..4 {
            for column in 0..4 {
                let mut value = 0.0;
                for inner in 0..4 {
                    value += a[row * 4 + inner] * b[inner * 4 + column];
                }
                result[row * 4 + column] = value;
            }
        }
        return result;
    }
    use wide::f32x4;
    let rows: [f32x4; 4] =
        std::array::from_fn(|i| f32x4::new(b[i * 4..i * 4 + 4].try_into().expect("matrix row")));
    let mut result = [0.0; 16];
    for row in 0..4 {
        let mut value = f32x4::ZERO;
        for inner in 0..4 {
            value = value + f32x4::splat(a[row * 4 + inner]) * rows[inner];
        }
        result[row * 4..row * 4 + 4].copy_from_slice(&value.to_array());
    }
    result
}
pub(super) fn point(p: wire::Point3d, w: f32) -> [f32; 4] {
    [p.x, p.y, p.z, w]
}
pub(super) fn color(c: wire::Color) -> [f32; 4] {
    [c.r, c.g, c.b, c.a]
}
pub(super) fn float4(c: wire::Float4) -> [f32; 4] {
    [c.x, c.y, c.z, c.w]
}
