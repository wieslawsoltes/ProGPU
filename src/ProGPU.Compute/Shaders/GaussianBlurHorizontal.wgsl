// Algorithm: Convolve each row with a truncated normalized Gaussian kernel.
// Time complexity: O(R) per output texel for blur radius R; Gaussian weights
// use two transcendental evaluations plus an O(R) multiplicative recurrence.
// Space complexity: O(1) local storage with exactly 2R+1 texture reads.
struct Params {
    sigma: f32,
    radius: u32,
    kernelType: u32,
    padding1: u32,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> blurParams: Params;

fn sample_input(x: i32, y: i32, size: vec2<u32>) -> vec4<f32> {
    if (x < 0 || y < 0 || x >= i32(size.x) || y >= i32(size.y)) {
        return vec4<f32>(0.0);
    }
    return textureLoad(inputTex, vec2<i32>(x, y), 0);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    if (blurParams.radius == 0u) {
        textureStore(outputTex, vec2<i32>(x, y), textureLoad(inputTex, vec2<i32>(x, y), 0));
        return;
    }

    let radius = i32(min(blurParams.radius, 128u));
    if (blurParams.kernelType == 1u) {
        var boxColor = sample_input(x, y, size);
        for (var offset = 1; offset <= radius; offset = offset + 1) {
            boxColor = boxColor +
                sample_input(x - offset, y, size) +
                sample_input(x + offset, y, size);
        }
        let sampleCount = f32(radius * 2 + 1);
        textureStore(outputTex, vec2<i32>(x, y), boxColor / sampleCount);
        return;
    }

    if (blurParams.sigma <= 0.0001) {
        textureStore(outputTex, vec2<i32>(x, y), textureLoad(inputTex, vec2<i32>(x, y), 0));
        return;
    }

    var color = sample_input(x, y, size);
    var weightSum = 1.0;
    let inverseVariance = 0.5 / (blurParams.sigma * blurParams.sigma);
    // For w(i)=exp(-i*i*a), w(i+1)/w(i)=exp(-(2*i+1)*a).
    // Advancing that ratio multiplies by the constant exp(-2*a), avoiding
    // one transcendental evaluation per tap while preserving the same kernel.
    var weight = exp(-inverseVariance);
    let ratioStep = exp(-2.0 * inverseVariance);
    var weightRatio = weight * ratioStep;
    for (var offset = 1; offset <= radius; offset = offset + 1) {
        color = color +
            (sample_input(x - offset, y, size) + sample_input(x + offset, y, size)) * weight;
        weightSum = weightSum + 2.0 * weight;
        weight = weight * weightRatio;
        weightRatio = weightRatio * ratioStep;
    }

    textureStore(outputTex, vec2<i32>(x, y), color / weightSum);
}
