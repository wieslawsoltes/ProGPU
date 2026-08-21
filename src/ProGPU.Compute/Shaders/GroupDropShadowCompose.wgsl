// Algorithm: Offset a blurred source-alpha field, tint it, and composite the original premultiplied source over the shadow.
// Time complexity: O(1) per output texel with four blurred-alpha loads and one source load.
// Space complexity: O(1) local storage and one rgba8unorm output texel per invocation.
// The 16x16 workgroup covers one output texel per invocation. Fractional offsets use
// explicit bilinear interpolation with transparent out-of-bounds samples so all WebGPU
// backends share the same edge behavior. Input and output colors are premultiplied.
struct Params {
    offset: vec2<f32>,
    padding: vec2<f32>,
    color: vec4<f32>,
};

@group(0) @binding(0) var sourceTex: texture_2d<f32>;
@group(0) @binding(1) var blurredTex: texture_2d<f32>;
@group(0) @binding(2) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(3) var<uniform> params: Params;

fn load_alpha(coordinate: vec2<i32>, size: vec2<u32>) -> f32 {
    if (coordinate.x < 0 || coordinate.y < 0 ||
        coordinate.x >= i32(size.x) || coordinate.y >= i32(size.y)) {
        return 0.0;
    }
    return textureLoad(blurredTex, coordinate, 0).a;
}

fn sample_alpha(position: vec2<f32>, size: vec2<u32>) -> f32 {
    let base = vec2<i32>(floor(position));
    let fraction = fract(position);
    let top = mix(
        load_alpha(base, size),
        load_alpha(base + vec2<i32>(1, 0), size),
        fraction.x);
    let bottom = mix(
        load_alpha(base + vec2<i32>(0, 1), size),
        load_alpha(base + vec2<i32>(1, 1), size),
        fraction.x);
    return mix(top, bottom, fraction.y);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(sourceTex);
    if (id.x >= size.x || id.y >= size.y) {
        return;
    }

    let coordinate = vec2<i32>(id.xy);
    let source = textureLoad(sourceTex, coordinate, 0);
    let shadowAlpha = sample_alpha(vec2<f32>(id.xy) - params.offset, size) *
        params.color.a;
    let shadow = vec4<f32>(params.color.rgb * shadowAlpha, shadowAlpha);
    textureStore(outputTex, coordinate, source + shadow * (1.0 - source.a));
}
