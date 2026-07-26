// Algorithm: Preserve generated greedy-mesh positions and procedural base material colors without user modification.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) private storage with no additional texture or buffer access.
fn progpu_voxel_deform(input: ProGpuVoxelMaterialInput) -> vec3<f32> {
    return input.position;
}

fn progpu_voxel_shade(input: ProGpuVoxelMaterialInput, baseColor: vec3<f32>) -> vec3<f32> {
    return baseColor;
}
