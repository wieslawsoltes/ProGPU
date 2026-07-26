// Algorithm: Invoke mainImage and clamp its straight-alpha output.
// Time complexity: O(1) wrapper work plus mainImage complexity.
// Space complexity: O(1) wrapper-local storage.
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragCoord = vec2<f32>(input.texCoord.x * inputs.iResolution.x, (1.0 - input.texCoord.y) * inputs.iResolution.y);
    let maskAlpha = sample_active_mask_alpha(input.position.xy);
    let shaderColor = mainImage(fragCoord);
    return vec4<f32>(shaderColor.rgb * input.color.rgb, shaderColor.a * input.color.a * maskAlpha);
}
