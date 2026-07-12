// Algorithm: Invoke mainImage, clamp output, and premultiply RGB for compositor output.
// Time complexity: O(1) wrapper work plus mainImage complexity.
// Space complexity: O(1) wrapper-local storage.
@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let fragCoord = vec2<f32>(input.texCoord.x * inputs.iResolution.x, (1.0 - input.texCoord.y) * inputs.iResolution.y);
    let maskSize = max(vec2<f32>(textureDimensions(activeMaskTexture)), vec2<f32>(1.0));
    let screenUv = input.position.xy / maskSize;
    let maskAlpha = textureSample(activeMaskTexture, activeMaskSampler, screenUv).r;
    let shaderColor = mainImage(fragCoord);
    let coverage = input.color.a * maskAlpha;
    return vec4<f32>(shaderColor.rgb * shaderColor.a * input.color.rgb * coverage, shaderColor.a * coverage);
}
