// Algorithm: Horizontally blur source alpha and pack four scalar coverages into one RGBA8 texel.
// Time complexity: O(4R) per packed texel, equivalent to O(R) per source texel for blur radius R.
// Space complexity: O(1) local storage with O(4R) reads and one RGBA8 output per four source texels.
struct Params {
    offset: vec2<f32>,
    color: vec4<f32>,
    blurRadius: f32,
    padding: f32,
    sourceSize: vec2<f32>,
    padding1: vec4<f32>,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: Params;

fn blur_coverage(x: i32, y: i32, size: vec2<u32>) -> f32 {
    if (x >= i32(size.x)) {
        return 0.0;
    }
    let sigma = max(params.blurRadius, 0.5);
    let radius = min(i32(ceil(sigma * 3.0)), 64);
    var alphaSum: f32 = 0.0;
    var weightSum: f32 = 0.0;
    for (var dx = -radius; dx <= radius; dx = dx + 1) {
        let sampleX = clamp(x + dx, 0, i32(size.x) - 1);
        let distance = f32(dx);
        let weight = exp(-0.5 * distance * distance / (sigma * sigma));
        alphaSum += textureLoad(inputTex, vec2<i32>(sampleX, y), 0).a * weight;
        weightSum += weight;
    }

    return alphaSum / max(weightSum, 0.0001);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let packedSize = textureDimensions(outputTex);
    if (id.x >= packedSize.x || id.y >= packedSize.y) {
        return;
    }

    let size = textureDimensions(inputTex);
    let x = i32(id.x * 4u);
    let y = i32(id.y);
    let packedCoverage = vec4<f32>(
        blur_coverage(x, y, size),
        blur_coverage(x + 1, y, size),
        blur_coverage(x + 2, y, size),
        blur_coverage(x + 3, y, size));
    textureStore(outputTex, vec2<i32>(i32(id.x), y), packedCoverage);
}
