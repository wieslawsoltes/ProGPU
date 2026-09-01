// Algorithm: Transform glyph quads, resolve shared solid-run presentation state from a compact text-style stream, clamp filtered coverage to each retained atlas tile, modulate text color, and intersect an optional fixed chain of at most four analytic rounded masks.
// Time complexity: O(1) per vertex and fragment; nested semantic masking performs at most four bounded analytic mask evaluations.
// Space complexity: O(1) local storage with one 32-byte text-style record read and one flat four-float tile bound per vertex, plus one coverage-or-color atlas sample per fragment; texture masks add one sample, analytic rounded and uniform-opacity masks add no texture bandwidth, ClearType adds two coverage samples, and a nested analytic chain reads one primary 96-byte record plus one fixed 288-byte continuation record.
struct TextStyle {
    color: vec4<f32>,
    textRenderingMode: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

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
    @location(5) @interpolate(flat) texelBounds: vec4<f32>,
};

struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    pad0: f32,
    renderOrigin: vec2<f32>,
    pad1: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read> textStyles: array<TextStyle>;

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
    let legacyAliasedText = textFlags < -0.5;
    let legacyClearTypeText = textFlags > 1.5;
    let legacyRenderingMode = select(
        select(0.0, 2.0, legacyClearTypeText),
        1.0,
        legacyAliasedText);
    let brushIndex = u32(max(input.brushIndex, 0.0));
    let textStyle = textStyles[brushIndex];
    let hasSharedTextStyle = input.brushIndex >= 0.0;
    let renderingMode = select(
        legacyRenderingMode,
        f32(textStyle.textRenderingMode),
        hasSharedTextStyle);
    let aliasedText =
        renderingMode > 0.5 && renderingMode < 1.5;
    let clearTypeText = renderingMode > 1.5;
    let useMvp = select(
        select(textFlags, textFlags - 2.0, legacyClearTypeText),
        -textFlags - 1.0,
        legacyAliasedText);

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
    output.color = select(
        input.color,
        textStyle.color,
        hasSharedTextStyle);
    output.texCoord = mix(texCoordMin, texCoordMax, local_uv);
    // Linear filtering must remain inside this glyph's atlas allocation. The
    // sampler clamps to the whole atlas, not to an individual retained tile,
    // so an explicit half-texel inset prevents adjacent glyph coverage from
    // leaking into the quad at minified or fractional device transforms.
    output.texelBounds = vec4<f32>(
        texCoordMin + vec2<f32>(0.5),
        texCoordMax - vec2<f32>(0.5));
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

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(2) @binding(2) var<uniform> maskSampling: MaskSamplingUniforms;

struct MaskChainUniforms {
    masks: array<MaskSamplingUniforms, 3>,
};

@group(2) @binding(3) var<uniform> maskChain: MaskChainUniforms;

fn rounded_mask_alpha_local(local: vec2<f32>, bounds: vec4<f32>, radiiX: vec4<f32>, radiiY: vec4<f32>) -> f32 {
    let edge = max(max(bounds.x - local.x, local.x - bounds.z), max(bounds.y - local.y, local.y - bounds.w));
    var center = vec2<f32>(0.0);
    var radius = vec2<f32>(0.0);
    var usesCorner = false;
    if (local.x < bounds.x + radiiX.x && local.y < bounds.y + radiiY.x) {
        radius = vec2<f32>(radiiX.x, radiiY.x);
        center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - radiiX.y && local.y < bounds.y + radiiY.y) {
        radius = vec2<f32>(radiiX.y, radiiY.y);
        center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - radiiX.z && local.y > bounds.w - radiiY.z) {
        radius = vec2<f32>(radiiX.z, radiiY.z);
        center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x < bounds.x + radiiX.w && local.y > bounds.w - radiiY.w) {
        radius = vec2<f32>(radiiX.w, radiiY.w);
        center = vec2<f32>(bounds.x + radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    }
    let safeRadius = max(radius, vec2<f32>(0.000001));
    let ellipsePoint = (local - center) / safeRadius;
    let ellipse = dot(ellipsePoint, ellipsePoint) - 1.0;
    let implicit = select(edge, ellipse, usesCorner);
    let antialiasWidth = max(fwidth(implicit), 0.0001);
    return clamp(0.5 - implicit / antialiasWidth, 0.0, 1.0);
}

fn analytic_rounded_mask_alpha_for(position: vec2<f32>, sampling: MaskSamplingUniforms) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), sampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), sampling.coordinate1.xyz));
    let outerAlpha = rounded_mask_alpha_local(local, sampling.bounds, sampling.cornerRadiiX, sampling.cornerRadiiY);
    if (sampling.options.x < 2.5) {
        return outerAlpha;
    }
    if (sampling.options.x > 3.5) {
        let innerAlpha = rounded_mask_alpha_local(
            local,
            vec4<f32>(sampling.coordinate0.w, sampling.coordinate1.w, sampling.options.z, sampling.options.w),
            vec4<f32>(0.0),
            vec4<f32>(0.0));
        return outerAlpha * (1.0 - innerAlpha);
    }
    let inset = sampling.options.z;
    let innerAlpha = rounded_mask_alpha_local(
        local,
        sampling.bounds + vec4<f32>(inset, inset, -inset, -inset),
        max(sampling.cornerRadiiX - vec4<f32>(inset), vec4<f32>(0.0)),
        max(sampling.cornerRadiiY - vec4<f32>(inset), vec4<f32>(0.0)));
    return outerAlpha * (1.0 - innerAlpha);
}

fn analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    return analytic_rounded_mask_alpha_for(position, maskSampling);
}

fn sample_mask_chain_alpha(position: vec2<f32>) -> f32 {
    let targetPosition = position + uniforms.renderOrigin;
    var alpha = 1.0;
    for (var index = 0u; index < 3u; index++) {
        let sampling = maskChain.masks[index];
        if (sampling.options.x > 1.5) {
            alpha *= analytic_rounded_mask_alpha_for(targetPosition, sampling) * sampling.options.y;
        }
    }
    return alpha;
}

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }

    let targetPosition = position + uniforms.renderOrigin;
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha(targetPosition) *
            maskSampling.options.y;
    }
    var uv = (targetPosition - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    if (maskSampling.options.z > 0.5) {
        uv = vec2<f32>(
            dot(vec3<f32>(targetPosition, 1.0), maskSampling.coordinate0.xyz),
            dot(vec3<f32>(targetPosition, 1.0), maskSampling.coordinate1.xyz));
    }
    let sample = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0)));
    let sampled = select(sample.r, sample.a, maskSampling.options.w > 1.5);
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    let textureOpacity = select(1.0, maskSampling.options.y,
        maskSampling.options.w > 0.5);
    return select(0.0, sampled * textureOpacity, inside);
}

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
    let atlasCoord = clamp(
        input.texCoord,
        input.texelBounds.xy,
        input.texelBounds.zw) / selectedSize;
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
        let atlasMin = input.texelBounds.xy / atlasSize;
        let atlasMax = input.texelBounds.zw / atlasSize;
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord - subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord + subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
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
    let atlasCoord = clamp(
        input.texCoord,
        input.texelBounds.xy,
        input.texelBounds.zw) / selectedSize;
    let atlasCoordDx = dpdx(atlasCoord);
    let atlasCoordDy = dpdy(atlasCoord);
    let maskAlpha = sample_mask_alpha(input.position.xy);
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
        let atlasMin = input.texelBounds.xy / atlasSize;
        let atlasMax = input.texelBounds.zw / atlasSize;
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord - subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, clamp(atlasCoord + subpixelOffset, atlasMin, atlasMax), atlasCoordDx, atlasCoordDy).r;
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
fn fs_main_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    let maskAlpha = sample_mask_alpha(input.position.xy) *
        sample_mask_chain_alpha(input.position.xy);
    if (maskAlpha <= 0.0) {
        discard;
    }
    return text_fs_main_with_mask_alpha(input, maskAlpha);
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
