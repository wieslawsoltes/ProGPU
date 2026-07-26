// Algorithm: Convert a physical framebuffer position into bounded mask-local UV coordinates and sample one coverage value.
// Time complexity: O(1) with one texture sample.
// Space complexity: O(1) shader-local storage.
fn wpf_active_mask_alpha(screenPosition: vec4<f32>) -> f32 {
    if (activeMaskSampling.options.x < 0.5) {
        return 1.0;
    }

    let screenUv = (screenPosition.xy - activeMaskSampling.origin) * activeMaskSampling.inverseSize;
    let sampled = textureSample(
        activeMaskTexture,
        activeMaskSampler,
        clamp(screenUv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(screenUv >= vec2<f32>(0.0)) && all(screenUv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}
