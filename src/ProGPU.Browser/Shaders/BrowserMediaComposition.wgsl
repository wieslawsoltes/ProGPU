// Algorithm: draw one textured composition layer as a destination rectangle, apply one fused affine straight-RGB transform, preserve source alpha, then premultiply for source-over blending.
// Time complexity: O(P) fragment work for P covered output pixels per layer, with one bilinear texture sample and three four-term dot products per fragment; total composition cost is O(sum(P_l)) across visible layers.
// Space complexity: O(1) private shader storage and O(1) uniform storage per layer; source textures and the browser-owned capture target require O(S + W*H) external storage.
// The vertex stage emits two triangles from six fixed vertex indices. Colors
// enter unpremultiplied from browser media and leave premultiplied for source-
// over blending. The terminal render attachment clamps representable output;
// ordered affine stages are not clamped between operations.

struct DrawParameters {
    // x/y are normalized top-left coordinates and z/w are normalized size.
    destination: vec4<f32>,
    red_transform: vec4<f32>,
    green_transform: vec4<f32>,
    blue_transform: vec4<f32>,
    // x=opacity, y/z/w=unused.
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
var<uniform> parameters: DrawParameters;

@vertex
fn vs_main(@builtin(vertex_index) vertex_index: u32) -> VertexOutput {
    var positions = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0)
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
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let sampled = textureSample(
        layer_texture,
        layer_sampler,
        input.uv);
    let affine_input = vec4<f32>(sampled.rgb, 1.0);
    let filtered = vec3<f32>(
        dot(parameters.red_transform, affine_input),
        dot(parameters.green_transform, affine_input),
        dot(parameters.blue_transform, affine_input));
    let alpha =
        clamp(sampled.a * parameters.layer.x, 0.0, 1.0);
    return vec4<f32>(
        clamp(filtered, vec3<f32>(0.0), vec3<f32>(1.0)) *
            alpha,
        alpha);
}
