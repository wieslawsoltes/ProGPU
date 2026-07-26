// Algorithm: Expose a stable, renderer-neutral input and sampling contract to a user WGSL image-effect function.
// Time complexity: O(1) for each helper call; user functions determine the complete fragment cost.
// Space complexity: O(1) private storage; samples use the already-bound source or auxiliary textures.
struct ProGpuEffectInput {
    uv: vec2<f32>,
    color: vec4<f32>,
    pixelSize: vec2<f32>,
    boundsSize: vec2<f32>,
};

fn progpu_constant(index: u32) -> vec4<f32> {
    return wpf_constant(index);
}

fn progpu_sample(binding: u32, uv: vec2<f32>) -> vec4<f32> {
    return wpf_sample_register(binding, uv);
}

fn progpu_sample_source(uv: vec2<f32>) -> vec4<f32> {
    return wpf_sample_source(uv);
}
