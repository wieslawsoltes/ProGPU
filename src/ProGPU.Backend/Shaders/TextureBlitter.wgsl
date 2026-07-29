// Algorithm: Draw a fullscreen triangle, sample one 2D texture, and apply fused Rec.709 saturation/grayscale transforms.
// Time complexity: O(1) per vertex and fragment, O(P) over P destination texels.
// Space complexity: O(1) local storage with one texture sample, two dot products, and one output write per fragment.
// The fixed fragment footprint is independent of source dimensions. Identity
// saturation=1 and grayscale=0 reduces to an ordinary filtered blit.
struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0));
    var uvs = array<vec2<f32>, 3>(
        vec2<f32>(0.0, 1.0),
        vec2<f32>(2.0, 1.0),
        vec2<f32>(0.0, -1.0));
    var output: VertexOutput;
    output.position = vec4<f32>(positions[vertexIndex], 0.0, 1.0);
    output.uv = uvs[vertexIndex];
    return output;
}

@group(0) @binding(0) var blitSampler: sampler;
@group(0) @binding(1) var sourceTexture: texture_2d<f32>;

struct BlitEffects {
    saturation: f32,
    grayscale: f32,
    padding0: f32,
    padding1: f32,
};

@group(0) @binding(2) var<uniform> effects: BlitEffects;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let sampled = textureSampleLevel(
        sourceTexture,
        blitSampler,
        input.uv,
        0.0);
    let luminanceWeights = vec3<f32>(0.2126, 0.7152, 0.0722);
    let sourceLuminance = dot(sampled.rgb, luminanceWeights);
    let saturated = mix(
        vec3<f32>(sourceLuminance),
        sampled.rgb,
        effects.saturation);
    let resultLuminance = dot(saturated, luminanceWeights);
    let processed = mix(
        saturated,
        vec3<f32>(resultLuminance),
        effects.grayscale);
    return vec4<f32>(processed, sampled.a);
}
