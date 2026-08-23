// Algorithm: Offset a blurred alpha mask, tint it, and composite the original source over the shadow.
// Time complexity: O(Rx * Ry) per output texel for horizontal and vertical radii Rx and Ry; the retained effect path uses this shader only for the zero-radius case.
// Space complexity: O(1) local storage with exactly (2Rx+1)(2Ry+1) texture reads and one output write.
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

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    let x = i32(id.x);
    let y = i32(id.y);

    if (x >= i32(size.x) || y >= i32(size.y)) {
        return;
    }

    var alphaSum: f32 = 0.0;
    let radius = vec2<i32>(params.blurRadius);
    var count: f32 = 0.0;

    for (var dy = -radius.y; dy <= radius.y; dy++) {
        for (var dx = -radius.x; dx <= radius.x; dx++) {
            let srcX = clamp(x - dx, 0, i32(size.x) - 1);
            let srcY = clamp(y - dy, 0, i32(size.y) - 1);

            let pixel = textureLoad(inputTex, vec2<i32>(srcX, srcY), 0);
            alphaSum += pixel.a;
            count += 1.0;
        }
    }

    let avgAlpha = alphaSum / count;
    let shadowAlpha = params.color.a * avgAlpha;
    let shadowColor = vec4<f32>(params.color.rgb * shadowAlpha, shadowAlpha);

    textureStore(outputTex, vec2<i32>(x, y), shadowColor);
}
