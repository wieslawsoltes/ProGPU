// Algorithm: Expand each original glyph instance with the canonical Text vertex arithmetic and decode its unchanged legacy/shared rendering policy.
// Time complexity: O(1) per vertex.
// Space complexity: O(1), including one flat four-float physical coverage frame and its admission bit; this helper does not allocate, rasterize or combine occurrences.
struct TextGlyphInstance {
    vertexIndex: u32,
    snappedLogicalPos: vec2<f32>,
    basisX: vec2<f32>,
    basisY: vec2<f32>,
    bearSize: vec4<f32>,
    texCoords: vec4<f32>,
    color: vec4<f32>,
    scaleBoldItalicUseMvp: vec4<f32>,
    brushIndex: f32,
};

struct TextGlyphVertexFrame {
    position: vec4<f32>,
    logicalPosition: vec2<f32>,
    color: vec4<f32>,
    texCoord: vec2<f32>,
    cornerRadius: f32,
    strokeThickness: f32,
    textMode: f32,
    texelBounds: vec4<f32>,
    physicalGlyphFrame: vec4<f32>,
    canonicalPhysicalFrame: u32,
};

fn text_glyph_vertex(input: TextGlyphInstance, sharedColor: vec4<f32>, sharedRenderingMode: u32, hasSharedTextStyle: bool) -> TextGlyphVertexFrame {
    var output: TextGlyphVertexFrame;

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
    let renderingMode = select(
        legacyRenderingMode,
        f32(sharedRenderingMode),
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

    output.logicalPosition = finalPosLogical;
    output.position = uniforms.projection * vec4<f32>(finalPosLogical, 0.0, 1.0);
    output.color = select(
        input.color,
        sharedColor,
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
    // pad0 == -1 is a private actual-pass certificate, not an inference from
    // canvas size. Positive pad0 values belong to original bounded Texture
    // source/ROP passes and must never certify glyphs. Only an unshifted,
    // full-target native/managed owner may set this distinct negative tag.
    // Preserve the original interpolation for all unproven passes, late MVP,
    // ClearType/color and non-axis glyphs. Prove ALL original quad corners;
    // a diagonal alone cannot prove an italic/sheared rectangle.
    let q0 = input.snappedLogicalPos + (lsx0 * input.basisX + ly0 * input.basisY);
    let q1 = input.snappedLogicalPos + (lsx1 * input.basisX + ly0 * input.basisY);
    let q2 = input.snappedLogicalPos + (lsx2 * input.basisX + ly1 * input.basisY);
    let q3 = input.snappedLogicalPos + (lsx3 * input.basisX + ly1 * input.basisY);
    let exactPositiveAxes = q0.y == q1.y && q1.x == q2.x &&
        q2.y == q3.y && q3.x == q0.x && all(q2 > q0);
    // Compute directly in the certified physical pass, never invert clip
    // coordinates (that cancellation can itself lose the original dyadic min).
    let physicalFrame = vec4<f32>(q0 * uniforms.dpiScale, (q2 - q0) * uniforms.dpiScale);
    let finiteFrame = all(abs(physicalFrame) <= vec4<f32>(3.402823466e+38)) &&
        all(abs(q2 * uniforms.dpiScale) <= vec2<f32>(3.402823466e+38)) &&
        all(abs((texCoordMax - texCoordMin) / physicalFrame.zw) <= vec2<f32>(3.402823466e+38));
    let admitted = uniforms.pad0 == -1.0 && useMvp == 0.0 &&
        output.textMode < 1.5 && exactPositiveAxes && finiteFrame &&
        all(physicalFrame.zw > vec2<f32>(0.0));
    output.physicalGlyphFrame = select(vec4<f32>(0.0, 0.0, 1.0, 1.0), physicalFrame, admitted);
    output.canonicalPhysicalFrame = select(0u, 1u, admitted);
    return output;
}

// Both ordinary Text and painted occurrences use this same address, retaining
// the existing half-texel clamp, sampler, filtering, contrast and gamma below it.
// @builtin(position).xy is the actual framebuffer fragment center; no pixel
// snapping, bias, integer load or interpolation-dependent UV correction occurs.
fn text_glyph_coverage_tex_coord(interpolated: vec2<f32>, fragmentPosition: vec2<f32>,
    texelBounds: vec4<f32>, physicalFrame: vec4<f32>, canonical: u32) -> vec2<f32> {
    if (canonical == 0u) {
        return interpolated;
    }
    let atlasMinimum = texelBounds.xy - vec2<f32>(0.5);
    let atlasSpan = texelBounds.zw + vec2<f32>(0.5) - atlasMinimum;
    return atlasMinimum + (fragmentPosition - physicalFrame.xy) * (atlasSpan / physicalFrame.zw);
}
