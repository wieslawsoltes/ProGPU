// Algorithm: Draw a fullscreen triangle and evaluate one normalized separable Gaussian axis using CPU-packed adjacent tap pairs and linear filtering, then apply an affine straight-RGB transform.
// Time complexity: O(K) per fragment and O(P * K) over P destination texels, where K = ceil(ceil(3 * sigma) / 2) and 0 <= K <= 48.
// Space complexity: O(1) fragment-local storage, one center sample plus 2K filtered texture samples, one output write, and a fixed 832-byte uniform block.
// The Gaussian is truncated at three standard deviations. Adjacent positive
// taps are combined into one bilinear lookup; mirrored sampling produces the
// negative taps. The sampler clamps to the source edge. The fixed loop bound
// supports sigma <= 32 pixels, so one axis performs at most 97 samples.
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

@group(0) @binding(0) var blurSampler: sampler;
@group(0) @binding(1) var sourceTexture: texture_2d<f32>;

struct BlurEffects {
    // xy: one-texel UV direction, z: normalized center weight,
    // w: active adjacent-tap pair count.
    axis: vec4<f32>,
    red: vec4<f32>,
    green: vec4<f32>,
    blue: vec4<f32>,
    // x: positive texel offset after bilinear pair reduction,
    // y: normalized weight for one side of the symmetric pair.
    taps: array<vec4<f32>, 48>,
};

@group(0) @binding(2) var<uniform> effects: BlurEffects;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var blurred =
        textureSampleLevel(
            sourceTexture,
            blurSampler,
            input.uv,
            0.0) *
        effects.axis.z;

    for (var index = 0u; index < 48u; index = index + 1u) {
        if (f32(index) < effects.axis.w) {
            let tap = effects.taps[index];
            let delta = effects.axis.xy * tap.x;
            blurred +=
                (textureSampleLevel(
                    sourceTexture,
                    blurSampler,
                    input.uv + delta,
                    0.0) +
                 textureSampleLevel(
                    sourceTexture,
                    blurSampler,
                    input.uv - delta,
                    0.0)) *
                tap.y;
        }
    }

    let source = vec4<f32>(blurred.rgb, 1.0);
    let processed = vec3<f32>(
        dot(source, effects.red),
        dot(source, effects.green),
        dot(source, effects.blue));
    return vec4<f32>(processed, blurred.a);
}
