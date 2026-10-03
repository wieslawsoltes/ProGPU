// Algorithm: rasterize an original homogeneous unit quad at final device samples.
// Time complexity: O(I + S) per covered fragment for I instructions and S samples; masked output adds one integer coverage load.
// Space complexity: O(1) vertices/uniforms plus the bounded translated register file.
// Original ProGPU final-device ShaderEffect path. The owned source capture and
// device-lattice output have separate extents. Rasterize the original unit quad
// at final samples; never filter an already evaluated effect as a substitute.
// Complexity: O(I + S) per covered fragment, constant vertex storage and the
// same bounded translated register file as the legacy bytecode effect.
struct SampleEffectUniforms {
    constants: array<vec4<f32>, 32>,
    // xy: complete source capture; zw: retained texture allocation.
    extent: vec4<f32>,
    sample_extent: vec4<f32>,
    // Original unit-quad transform AFTER the actual target's float projection.
    quad_scale_offset: vec4<f32>,
    // xy: actual parent device origin; zw: its raster viewport extent.
    output_lattice: vec4<f32>,
    // x: original homogeneous coordinate; yz: original target reciprocal size.
    homogeneous: vec4<f32>,
    physical_clip: vec4<f32>,
};
@group(0) @binding(0) var<uniform> effect: SampleEffectUniforms;
@group(0) @binding(1) var source_sampler: sampler;
@group(0) @binding(2) var source_texture: texture_2d<f32>;
// Existing retained vector-mask layout. Only fs_source_mask statically uses
// this binding; the ordinary pipeline neither initializes nor binds a mask.
@group(1) @binding(1) var source_clip_coverage: texture_2d<f32>;

struct SampleVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex fn vs_main(@builtin(vertex_index) index: u32) -> SampleVertex {
    // Six literal triangle corners avoid dynamic value-array indexing in the
    // pinned compiler. Both triangles share exactly the same edge/corner math.
    // Preserve the original unit-quad strip diagonal when expressing it as
    // independent triangles: BL,TL,BR then BR,TL,TR.
    var uv = vec2<f32>(0.0, 0.0);
    if (index == 0u) { uv = vec2<f32>(0.0, 1.0); }
    if (index == 2u || index == 3u) { uv = vec2<f32>(1.0, 1.0); }
    if (index == 5u) { uv = vec2<f32>(1.0, 0.0); }
    let w = effect.homogeneous.x;
    let projected = uv * effect.quad_scale_offset.xy + effect.quad_scale_offset.zw;
    var result: SampleVertex;
    // D3D9's original projection includes its half-pixel correction. WebGPU
    // samples at half-integer device positions; convert that raster convention
    // explicitly, after retaining the original full projected matrix.
    result.position = vec4<f32>(projected.x + w * effect.homogeneous.y,
        projected.y - w * effect.homogeneous.z, 0.0, w);
    result.uv = uv;
    return result;
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

fn wpf_final_sample_value(input: SampleVertex) -> vec4<f32> {
    // Translated derivatives execute before clip discard.
    let value = wpf_effect_main(input.uv);
    let physical = input.position.xy + effect.output_lattice.xy;
    if (physical.x < effect.physical_clip.x || physical.y < effect.physical_clip.y ||
        physical.x >= effect.physical_clip.z || physical.y >= effect.physical_clip.w) { discard; }
    return value;
}

@fragment fn fs_main(input: SampleVertex) -> @location(0) vec4<f32> {
    return wpf_final_sample_value(input);
}

@fragment fn fs_source_mask(input: SampleVertex) -> @location(0) vec4<f32> {
    // The existing clip rasterizer owns one R8 texel per actual parent device
    // pixel. No source-image UV or resized effect texture participates. Apply
    // coverage once to premultiplied output, after original shader evaluation.
    let value = wpf_final_sample_value(input);
    let coverage = textureLoad(source_clip_coverage, vec2<i32>(input.position.xy), 0).r;
    return value * coverage;
}
