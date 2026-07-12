// Algorithm: Invoke the user effect function and clamp its premultiplied result to valid alpha bounds.
// Time complexity: O(1) wrapper work plus the user effect function complexity.
// Space complexity: O(1) wrapper-local storage.
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let inputColor = wpf_sample_source(input.texCoord);
    let shaded = wpf_effect_main(input.texCoord, inputColor);
    let shadedColor = clamp(shaded, vec4<f32>(0.0), vec4<f32>(1.0));
    var maskAlpha = 1.0;
    if (wpf_has_active_mask()) {
        maskAlpha = wpf_active_mask_alpha(input.position);
    }

    let coverage = input.color.a * maskAlpha;
    return vec4<f32>(shadedColor.rgb * input.color.rgb * coverage, shadedColor.a * coverage);
}
