// Algorithm: Horizontally blur source alpha and pack four scalar coverages into one RGBA8 texel.
// Time complexity: O(4R) per packed texel, equivalent to O(R) per source texel for blur radius R; a zero horizontal sigma performs four direct alpha loads with no transcendental work.
// Space complexity: O(1) local storage with exactly 4(2R+1) reads and one RGBA8 output per four source texels.
struct Params {
    offset: vec2<f32>,
    color: vec4<f32>,
    blurRadius: vec2<f32>,
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
    let requestedSigma = params.blurRadius.x;
    let sourceX = clamp(x, 0, i32(size.x) - 1);
    let sourceAlpha = textureLoad(inputTex, vec2<i32>(sourceX, y), 0).a;
    if (requestedSigma <= 0.01) {
        return sourceAlpha;
    }

    let sigma = max(requestedSigma, 0.5);
    let radius = min(i32(ceil(sigma * 3.0)), 64);
    var alphaSum = sourceAlpha;
    var weightSum = 1.0;
    let inverseVariance = 0.5 / (sigma * sigma);
    var weight = exp(-inverseVariance);
    let ratioStep = exp(-2.0 * inverseVariance);
    var weightRatio = weight * ratioStep;
    for (var dx = 1; dx <= radius; dx = dx + 1) {
        let left = textureLoad(inputTex, vec2<i32>(clamp(x - dx, 0, i32(size.x) - 1), y), 0).a;
        let right = textureLoad(inputTex, vec2<i32>(clamp(x + dx, 0, i32(size.x) - 1), y), 0).a;
        alphaSum += (left + right) * weight;
        weightSum += 2.0 * weight;
        weight = weight * weightRatio;
        weightRatio = weightRatio * ratioStep;
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
