// Algorithm: Map an 8-bit accumulated coverage count to one of six retained overdraw colors and premultiply the selected result.
// Time complexity: O(1) per output texel with one texture read, one bounded palette selection, and one texture write.
// Space complexity: O(1) local storage; the six-color palette occupies a fixed 96-byte uniform block.
// A 16x16 workgroup covers 256 output texels. Input alpha is an rgba8unorm count,
// zero remains transparent, counts one through five select colors zero through
// four, and all counts of six or greater select the final color.
struct OverdrawColorFilterParams {
    color0: vec4<f32>,
    color1: vec4<f32>,
    color2: vec4<f32>,
    color3: vec4<f32>,
    color4: vec4<f32>,
    color5: vec4<f32>,
};

@group(0) @binding(0) var inputTex: texture_2d<f32>;
@group(0) @binding(1) var outputTex: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(2) var<uniform> params: OverdrawColorFilterParams;

fn palette_color(index: u32) -> vec4<f32> {
    switch index {
        case 0u: { return params.color0; }
        case 1u: { return params.color1; }
        case 2u: { return params.color2; }
        case 3u: { return params.color3; }
        case 4u: { return params.color4; }
        default: { return params.color5; }
    }
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(inputTex);
    if (id.x >= size.x || id.y >= size.y) {
        return;
    }

    let count = u32(round(clamp(
        textureLoad(inputTex, vec2<i32>(id.xy), 0).a,
        0.0,
        1.0) * 255.0));
    if (count == 0u) {
        textureStore(outputTex, vec2<i32>(id.xy), vec4<f32>(0.0));
        return;
    }

    let color = palette_color(min(count - 1u, 5u));
    textureStore(
        outputTex,
        vec2<i32>(id.xy),
        vec4<f32>(color.rgb * color.a, color.a));
}
