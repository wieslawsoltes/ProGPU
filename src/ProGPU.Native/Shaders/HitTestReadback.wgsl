// Algorithm: pack fixed-width retained hit-test result records into an rgba32uint storage texture for browser WebGPU readback.
// Time complexity: O(R) work for R result records, with exactly two texture stores per record and a fixed 64-invocation workgroup.
// Space complexity: O(R) output texels (2R rgba32uint texels); no workgroup or private array storage beyond one record.
// Browser Emdawn does not complete MapAsync for the buffer-to-buffer result
// path on the qualified Chromium runtime. The canonical GpuHitTesting.wgsl
// remains authoritative for hit semantics; this module only changes the GPU
// readback transport to the already-qualified texture-to-buffer route.

struct HitTestResult {
    hit: u32,
    id: i32,
    primitive_index: u32,
    z_index: f32,
    candidate_count: u32,
    nodes_visited: u32,
    precise_tests: u32,
    intersection_detail: u32,
};

@group(0) @binding(0) var<storage, read> results: array<HitTestResult>;
@group(0) @binding(1) var output: texture_storage_2d<rgba32uint, write>;

const RESULT_COUNT: u32 = 65u;
const TEXEL_COUNT: u32 = RESULT_COUNT * 2u;

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let texel_index = global_id.x;
    if (texel_index >= TEXEL_COUNT) {
        return;
    }

    let result = results[texel_index / 2u];
    var packed: vec4<u32>;
    if ((texel_index & 1u) == 0u) {
        packed = vec4<u32>(
            result.hit,
            bitcast<u32>(result.id),
            result.primitive_index,
            bitcast<u32>(result.z_index));
    } else {
        packed = vec4<u32>(
            result.candidate_count,
            result.nodes_visited,
            result.precise_tests,
            result.intersection_detail);
    }
    textureStore(output, vec2<i32>(i32(texel_index), 0), packed);
}
