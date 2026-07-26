// Algorithm: Adapt the neutral ProGPU WGSL effect entry point to the compositor's internal image-effect pipeline.
// Time complexity: O(1) wrapper work plus the user-defined effect function.
// Space complexity: O(1) wrapper-local storage.
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    let input = ProGpuEffectInput(
        uv,
        inputColor,
        effect.textureSize.zw,
        effect.bounds.zw);
    return progpu_effect_main(input);
}
