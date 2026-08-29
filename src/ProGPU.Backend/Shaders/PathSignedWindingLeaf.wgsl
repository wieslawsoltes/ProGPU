// Algorithm: Emit raw signed winding for one path leaf and one supersample row.
// Time complexity: O(S) per supersample row for S path segments.
// Space complexity: O(1) private storage and 64 u32 staging words per texel.
@compute @workgroup_size(16, 16)
fn cs_main(
    @builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = pathUniforms[global_id.z];
    let x = global_id.x;
    let y = global_id.y;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let pixelY = y / 8u;
    let sampleY = y % 8u;
    if (x >= uniforms.width || pixelY >= uniforms.height ||
        sampleY >= sampleGrid) {
        return;
    }
    let record = pathRecords[uniforms.pathIndex];
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(pixelY);
    let pixelOffset = uniforms.outputOffsetWords +
        pixelY * uniforms.outputRowWords + x * 64u;
    let samplePositionY =
        py + (f32(sampleY) + 0.5) / f32(sampleGrid);
    var winding = row_winding(
        px,
        samplePositionY / uniforms.scaleY,
        sampleGrid,
        uniforms.scaleX,
        record);
    if (record._pad1 == 0u) {
        winding = winding_fill_predicate(
            winding,
            record.fillRule);
    }
    let rowOffset = pixelOffset + sampleY * 8u;
    coverageOutput[rowOffset + 0u] = bitcast<u32>(winding.low.x);
    coverageOutput[rowOffset + 1u] = bitcast<u32>(winding.low.y);
    coverageOutput[rowOffset + 2u] = bitcast<u32>(winding.low.z);
    coverageOutput[rowOffset + 3u] = bitcast<u32>(winding.low.w);
    coverageOutput[rowOffset + 4u] = bitcast<u32>(winding.high.x);
    coverageOutput[rowOffset + 5u] = bitcast<u32>(winding.high.y);
    coverageOutput[rowOffset + 6u] = bitcast<u32>(winding.high.z);
    coverageOutput[rowOffset + 7u] = bitcast<u32>(winding.high.w);
}

