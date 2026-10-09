// Algorithm: Disjoint physical cells walk original glyphs in source order, blending each channel against an owned GPU destination snapshot and explicitly rounding each result to a byte before the next glyph. Mask helpers run before the divergent glyph loop; three unblended channel-write passes preserve destination alpha.
// Time complexity: O(256 * R) per channel for R glyph/cell references in bounded 16x16 cells; glyph coverage is loaded only inside its original physical tile. Three fixed draws per batch, with no per-glyph copy or submission.
// Space complexity: O(G + C + R + B) immutable storage for G glyphs, C cells, R references and B copied destination pixels; O(1) fragment-private state. No CPU pixels, readback or nonlinear-parameter default.
struct RgbGlyphInstance {
    targetOrigin: vec2<f32>,
    extent: vec2<f32>,
    atlasOrigin: vec2<u32>,
    reserved: vec2<u32>,
    foreground: vec4<f32>,
};
// Physical target-local masks use the same retained mask helpers as ordinary
// text. RGB placement has no bounded-pass origin shift, so renderOrigin is zero.
struct RgbGlyphFrame {
    extent: vec2<f32>, renderOrigin: vec2<f32>,
    backdropOrigin: vec2<u32>, reserved: vec2<u32>,
};
struct RgbGlyphCell {
    origin: vec2<u32>, extent: vec2<u32>,
    first: u32, count: u32, reserved: vec2<u32>,
};
@group(0) @binding(0) var rgbCoverage: texture_2d<f32>;
@group(0) @binding(1) var<storage, read> rgbInstances: array<RgbGlyphInstance>;
@group(0) @binding(2) var<uniform> uniforms: RgbGlyphFrame;
@group(0) @binding(3) var rgbBackdrop: texture_2d<f32>;
@group(0) @binding(4) var<storage, read> rgbCells: array<RgbGlyphCell>;
@group(0) @binding(5) var<storage, read> rgbReferences: array<u32>;

struct RgbGlyphVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) instanceIndex: u32,
};

@vertex
fn vs_rgb_composite(@builtin(vertex_index) index: u32,
    @builtin(instance_index) instanceIndex: u32) -> RgbGlyphVertex {
    let cell = rgbCells[instanceIndex];
    var corner = vec2<f32>(0.0);
    if (index == 1u || index == 2u || index == 4u) { corner.x = 1.0; }
    if (index == 2u || index == 4u || index == 5u) { corner.y = 1.0; }
    let physical = vec2<f32>(cell.origin) + corner * vec2<f32>(cell.extent);
    var output: RgbGlyphVertex;
    output.position = vec4<f32>(physical / uniforms.extent * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), 0.0, 1.0);
    output.instanceIndex = instanceIndex;
    return output;
}

fn rgb_channel_output(input: RgbGlyphVertex, channel: u32, primaryMask: f32, chainMask: f32) -> vec4<f32> {
    let cell = rgbCells[input.instanceIndex];
    let targetPixel = floor(input.position.xy);
    let backdropPixel = vec2<i32>(targetPixel) - vec2<i32>(uniforms.backdropOrigin);
    // Recover the original stored byte, then retain byte units through every
    // source blend. Explicit byte-normalized writes avoid an extra device-
    // dependent rounding decision at the final UNORM conversion.
    var destination = floor(textureLoad(rgbBackdrop, backdropPixel, 0)[channel] * 255.0 + 0.5);
    for (var index = 0u; index < cell.count; index++) {
        let glyph = rgbInstances[rgbReferences[cell.first + index]];
        let local = targetPixel - glyph.targetOrigin;
        if (all(local >= vec2<f32>(0.0)) && all(local < glyph.extent)) {
            let pixel = vec2<i32>(local) + vec2<i32>(glyph.atlasOrigin);
            let coverage = textureLoad(rgbCoverage, pixel, 0)[channel];
            let amount = ((coverage * glyph.foreground.a) * primaryMask) * chainMask;
            let value = glyph.foreground[channel] * 255.0 * amount + destination * (1.0 - amount);
            destination = floor(value + 0.5);
        }
    }
    return vec4<f32>(vec3<f32>(destination / 255.0), 1.0);
}

// Each pipeline writes ONLY its channel, with hardware blending disabled.
// The actual opaque parent alpha survives all three passes unchanged.
@fragment fn fs_rgb_red(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 0u, 1.0, 1.0); }
@fragment fn fs_rgb_green(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 1u, 1.0, 1.0); }
@fragment fn fs_rgb_blue(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_channel_output(input, 2u, 1.0, 1.0); }

fn rgb_masked_output(input: RgbGlyphVertex, channel: u32) -> vec4<f32> {
    // Sample before per-glyph membership branches so original implicit
    // derivatives and filtering remain defined at all cell/tile edges.
    let primary = sample_mask_alpha(input.position.xy);
    return rgb_channel_output(input, channel, primary, 1.0);
}

fn rgb_mask_chain_output(input: RgbGlyphVertex, channel: u32) -> vec4<f32> {
    let primary = sample_mask_alpha(input.position.xy);
    let chain = sample_mask_chain_alpha(input.position.xy);
    return rgb_channel_output(input, channel, primary, chain);
}

@fragment fn fs_rgb_red_masked(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_masked_output(input, 0u); }
@fragment fn fs_rgb_green_masked(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_masked_output(input, 1u); }
@fragment fn fs_rgb_blue_masked(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_masked_output(input, 2u); }
@fragment fn fs_rgb_red_chain(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_mask_chain_output(input, 0u); }
@fragment fn fs_rgb_green_chain(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_mask_chain_output(input, 1u); }
@fragment fn fs_rgb_blue_chain(input: RgbGlyphVertex) -> @location(0) vec4<f32> { return rgb_mask_chain_output(input, 2u); }
