// Algorithm: Load retained R8 coverage through a proven integer pixel translation, without interpolated UVs or sampler weights.
// Time complexity: O(1), one texture load per admitted fragment; no rasterization or CPU readback.
// Space complexity: O(1) private state, one flat integer offset; atlas ownership and storage are unchanged.
fn load_aligned_path_coverage(position: vec2<f32>, offset: vec2<i32>, atlas: texture_2d<f32>) -> f32 {
    return textureLoad(atlas, vec2<i32>(position) + offset, 0).r;
}
