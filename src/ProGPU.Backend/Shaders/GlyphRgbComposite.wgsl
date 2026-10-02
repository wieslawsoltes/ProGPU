// Algorithm: Three instanced channel-write passes independently interpolate opaque destination R/G/B with each original channel's coverage times foreground opacity. Explicit linear gamma1/contrast0 only; no scalar-alpha substitute, destination copy or nonlinear-parameter default.
// Time complexity: O(G + P) per channel for G glyph instances and P covered fragments; exactly one integer RGB coverage load per fragment, three fixed channel passes per batch.
// Space complexity: O(G) immutable instance storage and O(1) fragment-private values; target alpha is never written and no readback or per-glyph submission occurs.
struct RgbGlyphInstance {
    targetOrigin: vec2<f32>,
    extent: vec2<f32>,
    atlasOrigin: vec2<u32>,
    reserved: vec2<u32>,
    foreground: vec4<f32>,
};
struct RgbGlyphFrame { extent: vec2<f32>, reserved: vec2<u32>, };
@group(0) @binding(0) var rgbCoverage: texture_2d<f32>;
@group(0) @binding(1) var<storage, read> rgbInstances: array<RgbGlyphInstance>;
@group(0) @binding(2) var<uniform> rgbFrame: RgbGlyphFrame;

struct RgbGlyphVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) instanceIndex: u32,
};

@vertex
fn vs_rgb_composite(@builtin(vertex_index) index: u32,
    @builtin(instance_index) instanceIndex: u32) -> RgbGlyphVertex {
    let glyph = rgbInstances[instanceIndex];
    var corner = vec2<f32>(0.0);
    if (index == 1u || index == 2u || index == 4u) { corner.x = 1.0; }
    if (index == 2u || index == 4u || index == 5u) { corner.y = 1.0; }
    let physical = glyph.targetOrigin + corner * glyph.extent;
    var output: RgbGlyphVertex;
    output.position = vec4<f32>(physical / rgbFrame.extent * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), 0.0, 1.0);
    output.instanceIndex = instanceIndex;
    return output;
}

fn rgb_channel_output(input: RgbGlyphVertex, channel: u32) -> vec4<f32> {
    let glyph = rgbInstances[input.instanceIndex];
    let pixel = vec2<i32>(floor(input.position.xy) - glyph.targetOrigin) + vec2<i32>(glyph.atlasOrigin);
    let coverage = textureLoad(rgbCoverage, pixel, 0).rgb;
    return vec4<f32>(glyph.foreground.rgb, coverage[channel] * glyph.foreground.a);
}

// Each pipeline uses SrcAlpha/OneMinusSrcAlpha and ONLY its channel's write
// mask. The opaque parent alpha survives all three passes unchanged.
@fragment fn fs_rgb_red(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 0u); }
@fragment fn fs_rgb_green(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 1u); }
@fragment fn fs_rgb_blue(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 2u); }
