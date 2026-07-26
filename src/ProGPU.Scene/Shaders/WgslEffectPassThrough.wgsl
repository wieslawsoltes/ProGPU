// Algorithm: Return the source pixel through the public ProGPU custom-effect contract.
// Time complexity: O(1) per fragment.
// Space complexity: O(1) private storage.
fn progpu_effect_main(input: ProGpuEffectInput) -> vec4<f32> {
    return input.color;
}
