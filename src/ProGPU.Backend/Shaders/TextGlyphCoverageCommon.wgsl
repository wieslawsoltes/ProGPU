// Algorithm: Sample the original glyph tile and apply canonical gamma/contrast, aliased or ClearType coverage to one original draw color.
// Time complexity: O(1); one grayscale sample or three ClearType samples.
// Space complexity: O(1), no intermediate coverage target or occurrence union.
fn text_coverage_to_alpha(alpha: f32, contrast: f32, gamma: f32, aliasedText: bool) -> f32 {
    let dilated = clamp(alpha * contrast, 0.0, 1.0);
    return select(pow(dilated, gamma), select(0.0, 1.0, alpha >= 0.5), aliasedText);
}

fn text_glyph_color_with_mask_alpha(color: vec4<f32>, texCoord: vec2<f32>, texelBounds: vec4<f32>, textMode: f32, cornerRadius: f32, strokeThickness: f32, maskAlpha: f32) -> vec4<f32> {
    let aliasedText = cornerRadius < 0.0;
    let coverageDims = textureDimensions(atlasTexture);
    let colorDims = textureDimensions(colorAtlasTexture);
    let selectedDims = select(coverageDims, colorDims, textMode > 2.5);
    let selectedSize = vec2<f32>(f32(selectedDims.x), f32(selectedDims.y));
    let atlasCoord = clamp(
        texCoord,
        texelBounds.xy,
        texelBounds.zw) / selectedSize;
    let atlasCoordDx = dpdx(atlasCoord);
    let atlasCoordDy = dpdy(atlasCoord);
    // Preserve ordinary Text's derivative-before-mask-discard ordering.
    if (maskAlpha <= 0.0) {
        discard;
    }
    if (textMode > 2.5) {
        let atlasColor = textureSampleGrad(colorAtlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
        return vec4<f32>(atlasColor.rgb, atlasColor.a * color.a * maskAlpha);
    }
    let atlasColor = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
    let alpha = atlasColor.r;
    let gamma = abs(cornerRadius);
    let grayscaleAlpha = text_coverage_to_alpha(alpha, strokeThickness, gamma, aliasedText);

    if (textMode > 1.5) {
        let atlasDims = textureDimensions(atlasTexture);
        let atlasSize = vec2<f32>(f32(atlasDims.x), f32(atlasDims.y));
        let subpixelOffset = vec2<f32>(1.0 / max(atlasSize.x * 3.0, 1.0), 0.0);
        let atlasMin = texelBounds.xy / atlasSize;
        let atlasMax = texelBounds.zw / atlasSize;
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord - subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord + subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(redCoverage, strokeThickness, gamma, false),
            text_coverage_to_alpha(greenCoverage, strokeThickness, gamma, false),
            text_coverage_to_alpha(blueCoverage, strokeThickness, gamma, false)) * color.a * maskAlpha;
        let finalAlpha = max(max(rgbCoverage.r, rgbCoverage.g), rgbCoverage.b);
        if (finalAlpha <= 0.0001) {
            return vec4<f32>(0.0);
        }

        return vec4<f32>(color.rgb * (rgbCoverage / finalAlpha), finalAlpha);
    }

    return vec4<f32>(color.rgb, color.a * grayscaleAlpha * maskAlpha);
}
