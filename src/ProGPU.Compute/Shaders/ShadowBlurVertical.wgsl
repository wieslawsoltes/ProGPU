// Algorithm: Vertically blur a four-coverages-per-RGBA8 packed alpha mask, then tint it.
// Time complexity: O(R) per output texel for blur radius R.
// Space complexity: O(1) local storage with O(R) reads from the packed coverage intermediate.
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

fn load_coverage(x: i32, y: i32) -> f32 {
    let packed = textureLoad(inputTex, vec2<i32>(x / 4, y), 0);
    switch (x & 3) {
        case 0: { return packed.r; }
        case 1: { return packed.g; }
        case 2: { return packed.b; }
        default: { return packed.a; }
    }
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(outputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    let sigma = max(params.blurRadius, 0.5);
    let radius = min(i32(ceil(sigma * 3.0)), 64);
    var alphaSum = 0.0;
    var weightSum: f32 = 0.0;
    for (var dy = -radius; dy <= radius; dy = dy + 1) {
        let sampleY = clamp(y + dy, 0, i32(size.y) - 1);
        let distance = f32(dy);
        let weight = exp(-0.5 * distance * distance / (sigma * sigma));
        alphaSum += load_coverage(x, sampleY) * weight;
        weightSum += weight;
    }

    let shadowAlpha = params.color.a * alphaSum / max(weightSum, 0.0001);
    let shadowColor = vec4<f32>(params.color.rgb * shadowAlpha, shadowAlpha);
    textureStore(outputTex, vec2<i32>(x, y), shadowColor);
}
