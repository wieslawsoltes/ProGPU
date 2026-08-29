// Algorithm: Count the two staged supersample-mask words per texel and pack four adjacent R8Unorm texels into each storage-buffer word.
// Time complexity: O(1) per output texel after vectorized signed-winding evaluation.
// Space complexity: O(1) private storage.
struct CoverageCombineUniforms {
    sourceOffsetWords: u32,
    sourceStrideWords: u32,
    sourceCount: u32,
    programIndex: u32,
    programCount: u32,
    destinationOffsetWords: u32,
    destinationRowWords: u32,
    width: u32,
    height: u32,
    sampleGrid: u32,
};

@group(0) @binding(3) var<storage, read_write> coverageOutput: array<u32>;
@group(0) @binding(4) var<storage, read> coverageCombineUniforms: array<CoverageCombineUniforms>;

fn split_signed_result_offset(
    uniforms: CoverageCombineUniforms) -> u32 {
    return uniforms.sourceOffsetWords +
        uniforms.sourceStrideWords * uniforms.sourceCount;
}

fn split_signed_program_coverage(
    uniforms: CoverageCombineUniforms,
    x: u32,
    y: u32) -> u32 {
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let resultOffset = split_signed_result_offset(uniforms) +
        (y * uniforms.width + x) * 2u;
    let coveredSamples =
        countOneBits(coverageOutput[resultOffset]) +
        countOneBits(coverageOutput[resultOffset + 1u]);
    let sampleWeight = 1.0 / f32(sampleGrid * sampleGrid);
    return min(
        255u,
        u32(round(f32(coveredSamples) * sampleWeight * 255.0)));
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = coverageCombineUniforms[global_id.z];
    let wordX = global_id.x;
    let y = global_id.y;
    if (wordX * 4u >= uniforms.width || y >= uniforms.height) {
        return;
    }
    var packed = 0u;
    for (var lane = 0u; lane < 4u; lane = lane + 1u) {
        let x = wordX * 4u + lane;
        if (x < uniforms.width) {
            let coverage = split_signed_program_coverage(
                uniforms,
                x,
                y);
            packed = packed | (coverage << (lane * 8u));
        }
    }
    coverageOutput[
        uniforms.destinationOffsetWords +
        y * uniforms.destinationRowWords + wordX] = packed;
}
