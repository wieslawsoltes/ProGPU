// Algorithm: Transform glyph quads and modulate premultiplied text color by glyph-atlas coverage.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage with one coverage-or-color atlas sample per fragment; masked variants add one mask sample and ClearType adds two coverage samples.
struct VertexInput {
    @builtin(vertex_index) vertexIndex: u32,
    @location(0) snappedLogicalPos: vec2<f32>,
    @location(1) basisX: vec2<f32>,
    @location(2) basisY: vec2<f32>,
    @location(3) bearSize: vec4<f32>,
    @location(4) texCoords: vec4<f32>,
    @location(5) color: vec4<f32>,
    @location(6) scaleBoldItalicUseMvp: vec4<f32>,
    @location(7) brushIndex: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) cornerRadius: f32,
    @location(3) strokeThickness: f32,
    @location(4) textMode: f32,
};

struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    pad0: f32,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;

    var local_uv = vec2<f32>(0.0, 0.0);
    var corner = 0u;
    if (input.vertexIndex == 1u) {
        local_uv = vec2<f32>(1.0, 0.0);
        corner = 1u;
    } else if (input.vertexIndex == 2u) {
        local_uv = vec2<f32>(1.0, 1.0);
        corner = 2u;
    } else if (input.vertexIndex == 4u) {
        local_uv = vec2<f32>(1.0, 1.0);
        corner = 2u;
    } else if (input.vertexIndex == 5u) {
        local_uv = vec2<f32>(0.0, 1.0);
        corner = 3u;
    }

    let bear = input.bearSize.xy / uniforms.dpiScale;
    let size = input.bearSize.zw / uniforms.dpiScale;
    let texCoordMin = input.texCoords.xy;
    let texCoordMax = input.texCoords.zw;

    let scaleRatio = input.scaleBoldItalicUseMvp.x;
    let boldOffset = input.scaleBoldItalicUseMvp.y;
    let italicSkew = input.scaleBoldItalicUseMvp.z;
    let encodedTextFlags = input.scaleBoldItalicUseMvp.w;
    let colorGlyph = encodedTextFlags > 5.5;
    let textFlags = select(encodedTextFlags, encodedTextFlags - 8.0, colorGlyph);
    let aliasedText = textFlags < -0.5;
    let clearTypeText = textFlags > 1.5;
    let useMvp = select(
        select(textFlags, textFlags - 2.0, clearTypeText),
        -textFlags - 1.0,
        aliasedText);

    let lx0 = bear.x * scaleRatio + boldOffset;
    let ly0 = bear.y * scaleRatio;
    let lx1 = lx0 + size.x * scaleRatio;
    let ly1 = ly0 + size.y * scaleRatio;

    let lsx0 = lx0 - ly0 * italicSkew;
    let lsx1 = lx1 - ly0 * italicSkew;
    let lsx2 = lx1 - ly1 * italicSkew;
    let lsx3 = lx0 - ly1 * italicSkew;

    var localOffset = vec2<f32>(0.0, 0.0);
    if (corner == 0u) {
        localOffset = vec2<f32>(lsx0, ly0);
    } else if (corner == 1u) {
        localOffset = vec2<f32>(lsx1, ly0);
    } else if (corner == 2u) {
        localOffset = vec2<f32>(lsx2, ly1);
    } else {
        localOffset = vec2<f32>(lsx3, ly1);
    }

    let physicalOffset = localOffset.x * input.basisX + localOffset.y * input.basisY;
    var finalPosLogical = input.snappedLogicalPos + physicalOffset;

    if (useMvp > 0.5) {
        finalPosLogical = (uniforms.mvp * vec4<f32>(finalPosLogical, 0.0, 1.0)).xy;
    }

    output.position = uniforms.projection * vec4<f32>(finalPosLogical, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = mix(texCoordMin, texCoordMax, local_uv);
    output.cornerRadius = select(1.43, -1.43, aliasedText); // DefaultTextGamma, sign encodes aliased text
    output.strokeThickness = 1.15; // DefaultTextContrast
    output.textMode = select(
        select(select(0.0, 2.0, clearTypeText), 1.0, aliasedText),
        3.0,
        colorGlyph);
    return output;
}

@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;
@group(1) @binding(2) var colorAtlasTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

fn text_coverage_to_alpha(alpha: f32, contrast: f32, gamma: f32, aliasedText: bool) -> f32 {
    let dilated = clamp(alpha * contrast, 0.0, 1.0);
    return select(pow(dilated, gamma), select(0.0, 1.0, alpha >= 0.5), aliasedText);
}

fn text_fs_main_with_mask_alpha(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let aliasedText = input.cornerRadius < 0.0;
    let coverageDims = textureDimensions(atlasTexture);
    let colorDims = textureDimensions(colorAtlasTexture);
    let selectedDims = select(coverageDims, colorDims, input.textMode > 2.5);
    let selectedSize = vec2<f32>(f32(selectedDims.x), f32(selectedDims.y));
    let atlasCoord = input.texCoord / selectedSize;
    let atlasCoordDx = dpdx(atlasCoord);
    let atlasCoordDy = dpdy(atlasCoord);
    if (input.textMode > 2.5) {
        let atlasColor = textureSampleGrad(colorAtlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
        return vec4<f32>(atlasColor.rgb, atlasColor.a * input.color.a * maskAlpha);
    }
    let atlasColor = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
    let alpha = atlasColor.r;
    let gamma = abs(input.cornerRadius);
    let grayscaleAlpha = text_coverage_to_alpha(alpha, input.strokeThickness, gamma, aliasedText);

    if (input.textMode > 1.5) {
        let atlasDims = textureDimensions(atlasTexture);
        let atlasSize = vec2<f32>(f32(atlasDims.x), f32(atlasDims.y));
        let subpixelOffset = vec2<f32>(1.0 / max(atlasSize.x * 3.0, 1.0), 0.0);
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord - subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord + subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(redCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(greenCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(blueCoverage, input.strokeThickness, gamma, false)) * input.color.a * maskAlpha;
        let finalAlpha = max(max(rgbCoverage.r, rgbCoverage.g), rgbCoverage.b);
        if (finalAlpha <= 0.0001) {
            return vec4<f32>(0.0);
        }

        return vec4<f32>(input.color.rgb * (rgbCoverage / finalAlpha), finalAlpha);
    }

    return vec4<f32>(input.color.rgb, input.color.a * grayscaleAlpha * maskAlpha);
}

fn text_fs_main(input: VertexOutput) -> vec4<f32> {
    let aliasedText = input.cornerRadius < 0.0;
    let coverageDims = textureDimensions(atlasTexture);
    let colorDims = textureDimensions(colorAtlasTexture);
    let selectedDims = select(coverageDims, colorDims, input.textMode > 2.5);
    let selectedSize = vec2<f32>(f32(selectedDims.x), f32(selectedDims.y));
    let atlasCoord = input.texCoord / selectedSize;
    let atlasCoordDx = dpdx(atlasCoord);
    let atlasCoordDy = dpdy(atlasCoord);
    let screen_uv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
    if (maskAlpha <= 0.0) {
        discard;
    }
    if (input.textMode > 2.5) {
        let atlasColor = textureSampleGrad(colorAtlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
        return vec4<f32>(atlasColor.rgb, atlasColor.a * input.color.a * maskAlpha);
    }
    let atlasColor = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord, atlasCoordDx, atlasCoordDy);
    let alpha = atlasColor.r;
    let gamma = abs(input.cornerRadius);
    let grayscaleAlpha = text_coverage_to_alpha(alpha, input.strokeThickness, gamma, aliasedText);

    if (input.textMode > 1.5) {
        let atlasDims = textureDimensions(atlasTexture);
        let atlasSize = vec2<f32>(f32(atlasDims.x), f32(atlasDims.y));
        let subpixelOffset = vec2<f32>(1.0 / max(atlasSize.x * 3.0, 1.0), 0.0);
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord - subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord + subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(redCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(greenCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(blueCoverage, input.strokeThickness, gamma, false)) * input.color.a * maskAlpha;
        let finalAlpha = max(max(rgbCoverage.r, rgbCoverage.g), rgbCoverage.b);
        if (finalAlpha <= 0.0001) {
            return vec4<f32>(0.0);
        }

        return vec4<f32>(input.color.rgb * (rgbCoverage / finalAlpha), finalAlpha);
    }

    return vec4<f32>(input.color.rgb, input.color.a * grayscaleAlpha * maskAlpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return text_fs_main(input);
}

@fragment
fn fs_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return text_fs_main_with_mask_alpha(input, 1.0);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_main_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main_with_mask_alpha(input, 1.0);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

@fragment
fn fs_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main_with_mask_alpha(input, 1.0);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
