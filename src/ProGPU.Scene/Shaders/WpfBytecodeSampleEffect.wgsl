// Original ProGPU final-device ShaderEffect path. The owned source capture and
// device-lattice output have separate extents. Rasterize the original unit quad
// at final samples; never filter an already evaluated effect as a substitute.
// Complexity: O(I + S) per covered fragment, constant vertex storage and the
// same bounded translated register file as the legacy bytecode effect.
struct SampleEffectUniforms {
    constants: array<vec4<f32>, 32>,
    // xy: complete source capture; zw: retained texture allocation.
    extent: vec4<f32>,
    // Original unit-quad transform before the device viewport projection.
    quad_scale_offset: vec4<f32>,
    // xy: signed output device origin; zw: output texture extent.
    output_lattice: vec4<f32>,
    // x: original homogeneous coordinate. Remaining fields reserved zero.
    homogeneous: vec4<f32>,
};
@group(0) @binding(0) var<uniform> effect: SampleEffectUniforms;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var source_texture: texture_2d<f32>;

struct SampleVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex fn vs_main(@builtin(vertex_index) index: u32) -> SampleVertex {
    // Six literal triangle corners avoid dynamic value-array indexing in the
    // pinned compiler. Both triangles share exactly the same edge/corner math.
    var uv = vec2<f32>(0.0, 0.0);
    if (index == 1u || index == 4u) { uv = vec2<f32>(1.0, 0.0); }
    if (index == 2u || index == 3u) { uv = vec2<f32>(0.0, 1.0); }
    if (index == 5u) { uv = vec2<f32>(1.0, 1.0); }
    let w = effect.homogeneous.x;
    let device = uv * effect.quad_scale_offset.xy + effect.quad_scale_offset.zw;
    let local = device - effect.output_lattice.xy * w;
    let projected = local / effect.output_lattice.zw;
    var result: SampleVertex;
    result.position = vec4<f32>(projected.x * 2.0 - w, w - projected.y * 2.0, 0.0, w);
    result.uv = uv;
    return result;
}

fn wpf_sample_input(uv: vec2<f32>) -> vec4<f32> {
    let pixel = clamp(uv * effect.extent.xy, vec2<f32>(0.5), effect.extent.xy - vec2<f32>(0.5));
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

@fragment fn fs_main(input: SampleVertex) -> @location(0) vec4<f32> {
    return wpf_effect_main(input.uv);
}
