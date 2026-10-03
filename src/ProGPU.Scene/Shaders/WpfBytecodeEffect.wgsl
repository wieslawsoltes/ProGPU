// Algorithm: rasterize a full capture rectangle and call its validated D3D9
// straight-line program. Source UVs clamp to the retained capture, not spare
// allocation pixels. Input and output retain WPF premultiplied texture values.
// Time complexity: O(I + S) per fragment for I instructions and S texture reads.
// Space complexity: O(1) vertex state; 12 temporary and 32 constant vec4 registers
// per fragment, one bounded immutable uniform block and one source texture.
struct EffectUniforms {
    constants: array<vec4<f32>, 32>,
    // xy: actual capture extent; zw: retained texture allocation extent.
    extent: vec4<f32>,
    // xy: independently owned sampler's valid pixels (not output dimensions).
    sample_extent: vec4<f32>,
};
@group(0) @binding(0) var<uniform> effect: EffectUniforms;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var source_texture: texture_2d<f32>;

@vertex fn vs_main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let x = f32((index << 1u) & 2u);
    let y = f32(index & 2u);
    return vec4<f32>(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0);
}

fn wpf_sample_input(uv: vec2<f32>) -> vec4<f32> {
    let pixel = clamp(uv * effect.sample_extent.xy, vec2<f32>(0.5), effect.sample_extent.xy - vec2<f32>(0.5));
    return textureSample(source_texture, source_sampler, pixel / effect.extent.zw);
}

fn wpf_effect_main(uv: vec2<f32>) -> vec4<f32> {
    let t0 = vec4<f32>(uv, 0.0, 1.0);
    var r: array<vec4<f32>, 12>;
    var c = effect.constants;
    var o = vec4<f32>(0.0);
    // PROGPU_VALIDATED_BYTECODE_BODY
    return o;
}

@fragment fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    return wpf_effect_main(position.xy / effect.extent.xy);
}
