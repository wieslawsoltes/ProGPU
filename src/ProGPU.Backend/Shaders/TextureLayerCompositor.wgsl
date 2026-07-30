// Algorithm: draw one straight-alpha texture into a normalized destination rectangle, apply one affine straight-RGB transform, multiply opacity, premultiply the result, and source-over blend it onto the retained render target.
// Time complexity: O(P) fragment work for P covered destination pixels, with one bilinear texture sample, three four-term dot products, and one fixed-function blend per fragment.
// Space complexity: O(1) shader-private storage, one sampled texture, one output attachment, and one shared fixed 80-byte uniform block.
// The vertex stage emits two triangles from six fixed vertices. The fragment
// result is premultiplied exactly once so the pipeline can use One /
// OneMinusSrcAlpha source-over blending. The render pass loads the existing
// destination, preserving all lower layers.
// There are no loops or compute workgroups. The CPU rejects non-finite
// rectangle endpoints and supplies a finite opacity in [0, 1]. Linear
// filtering is the intentional scaling approximation; clamp-to-edge prevents
// sampling beyond the source extent.

struct LayerParameters {
    // xy are normalized top-left coordinates; zw are normalized dimensions.
    destination: vec4<f32>,
    red_transform: vec4<f32>,
    green_transform: vec4<f32>,
    blue_transform: vec4<f32>,
    // x is opacity; yzw are reserved.
    layer: vec4<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@group(0) @binding(0)
var layer_sampler: sampler;

@group(0) @binding(1)
var layer_texture: texture_2d<f32>;

@group(0) @binding(2)
var<uniform> parameters: LayerParameters;

@vertex
fn vs_main(
    @builtin(vertex_index) vertex_index: u32
) -> VertexOutput {
    var positions = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(1.0, 0.0)
    );
    let local = positions[vertex_index];
    let normalized =
        parameters.destination.xy +
        local * parameters.destination.zw;
    var output: VertexOutput;
    output.position = vec4<f32>(
        normalized.x * 2.0 - 1.0,
        1.0 - normalized.y * 2.0,
        0.0,
        1.0);
    output.uv = local;
    return output;
}

@fragment
fn fs_main(
    input: VertexOutput
) -> @location(0) vec4<f32> {
    let sampled = textureSampleLevel(
        layer_texture,
        layer_sampler,
        input.uv,
        0.0);
    let affine_input = vec4<f32>(
        sampled.rgb,
        1.0);
    let filtered = vec3<f32>(
        dot(
            parameters.red_transform,
            affine_input),
        dot(
            parameters.green_transform,
            affine_input),
        dot(
            parameters.blue_transform,
            affine_input));
    let alpha = clamp(
        sampled.a * parameters.layer.x,
        0.0,
        1.0);
    return vec4<f32>(
        clamp(
            filtered,
            vec3<f32>(0.0),
            vec3<f32>(1.0)) * alpha,
        alpha);
}
