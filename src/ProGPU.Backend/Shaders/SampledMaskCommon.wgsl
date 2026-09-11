// Algorithm: Map target physical positions to affine or offset/scale UVs, sample retained R8 coverage or RGBA alpha, and apply optional mask opacity with transparent out-of-bounds coverage.
// Time complexity: O(1) per fragment, with one filtered texture sample and fixed affine arithmetic.
// Space complexity: O(1) private state; source texture and sampler are borrowed, with no intermediate texture or storage writes.
struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

fn sample_texture_mask_alpha(position: vec2<f32>, sampling: MaskSamplingUniforms,
    source: texture_2d<f32>, sourceSampler: sampler) -> f32 {
    var uv = (position - sampling.coordinate0.xy) * sampling.coordinate1.xy;
    if (sampling.options.z > 0.5) {
        uv = vec2<f32>(
            dot(vec3<f32>(position, 1.0), sampling.coordinate0.xyz),
            dot(vec3<f32>(position, 1.0), sampling.coordinate1.xyz));
    }
    let sample = textureSample(source, sourceSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0)));
    let sampled = select(sample.r, sample.a, sampling.options.w > 1.5);
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    let textureOpacity = select(1.0, sampling.options.y, sampling.options.w > 0.5);
    return select(0.0, sampled * textureOpacity, inside);
}
