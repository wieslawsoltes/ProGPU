// Algorithm: Paint each original hinted glyph occurrence directly with the shared registered material or original image sampler, then apply canonical Text coverage. Bounded image paint retains its original triangle pair for each canonical affine glyph triangle; exact positive-axis frames use one pair. Extended paint retains its original axis mapping. No coverage union or R8 intermediate is used.
// Time complexity: O(1) vertex work plus the original bounded material/sampling work per fragment; bounded paint deliberately preserves original image geometry rather than approximating its edges.
// Space complexity: One immutable 96-byte paint record per command and one original 96-byte glyph instance per occurrence; O(1) fragment storage, no new coverage texture.
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

struct HintedGlyphPaint {
    kind: u32,
    brushIndex: u32,
    flags: u32,
    reserved: u32,
    sourceOffsetOpacity: vec4<f32>,
    uvBounds: vec4<f32>,
    textureQuad01: vec4<f32>,
    textureQuad23: vec4<f32>,
    sampling: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read> brushes: array<Brush>;
@group(0) @binding(2) var<storage, read> gradientStops: array<GradientStop>;
@group(0) @binding(3) var<storage, read> glyphPaints: array<HintedGlyphPaint>;
@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;
@group(1) @binding(2) var colorAtlasTexture: texture_2d<f32>;
@group(3) @binding(0) var texSampler: sampler;
@group(3) @binding(1) var texTexture: texture_2d<f32>;

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
    @location(8) paintIndex: u32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) texCoord: vec2<f32>,
    @location(1) @interpolate(flat) texelBounds: vec4<f32>,
    @location(2) @interpolate(flat) textPolicy: vec3<f32>,
    @location(3) sourceLogical: vec2<f32>,
    @location(4) paintUV: vec2<f32>,
    @location(5) @interpolate(flat) paintIndex: u32,
    @location(6) @interpolate(flat) glyphLogicalFrame: vec4<f32>,
    @location(7) @interpolate(flat) liveGlyphFrame: u32,
    @location(8) @interpolate(flat) glyphTriangle012: vec4<f32>,
    @location(9) @interpolate(flat) glyphTriangle023: vec4<f32>,
    @location(10) @interpolate(flat) glyphMappingFlags: u32,
    @location(11) @interpolate(flat) glyphDiagonal: vec2<f32>,
    @location(12) @interpolate(flat) glyphOtherCorners: vec4<f32>,
};

struct HintedGlyphTriangle {
    inverse: vec4<f32>,
    valid: bool,
    positive: bool,
};

fn hinted_glyph_triangle(edge1: vec2<f32>, edge2: vec2<f32>) -> HintedGlyphTriangle {
    let determinant = edge1.x * edge2.y - edge1.y * edge2.x;
    // Exact degeneracy, not a tolerance that discards small invertible frames.
    // Select a safe divisor before evaluating invalid/empty triangles.
    let finiteLimit = 0x1.fffffep+127f;
    let live = determinant != 0.0 && abs(determinant) <= finiteLimit;
    let safeDeterminant = select(1.0, determinant, live);
    let inverse = vec4<f32>(edge2.y, -edge2.x, -edge1.y, edge1.x) / safeDeterminant;
    let valid = live && all(abs(inverse) <= vec4<f32>(finiteLimit));
    return HintedGlyphTriangle(select(vec4<f32>(0.0), inverse, valid), valid, determinant > 0.0);
}

fn hinted_glyph_triangle_weights(inverse: vec4<f32>, local: vec2<f32>) -> vec2<f32> {
    return vec2<f32>(dot(inverse.xy, local), dot(inverse.zw, local));
}

fn hinted_glyph_cross(a: vec2<f32>, b: vec2<f32>) -> f32 {
    return a.x * b.y - a.y * b.x;
}

fn hinted_glyph_inside_edge(value: f32, edge: vec2<f32>) -> bool {
    // Canonical logical coordinates map to a top-left-origin framebuffer.
    // Shared edges have one owner; reflected winding reverses the edge too.
    let topLeft = edge.y < 0.0 || (edge.y == 0.0 && edge.x > 0.0);
    return value > 0.0 || (value == 0.0 && topLeft);
}

fn hinted_glyph_inside_triangle(local: vec2<f32>, edge1: vec2<f32>, edge2: vec2<f32>, positive: bool) -> bool {
    let direction = select(-1.0, 1.0, positive);
    // The common diagonal uses cross(diagonal, local) in BOTH triangles,
    // with exact negation, never a separately rounded local-minus-diagonal.
    return hinted_glyph_inside_edge(hinted_glyph_cross(edge1, local) * direction, edge1 * direction) &&
        hinted_glyph_inside_edge(hinted_glyph_cross(edge2 - edge1, local - edge1) * direction, (edge2 - edge1) * direction) &&
        hinted_glyph_inside_edge(-hinted_glyph_cross(edge2, local) * direction, -edge2 * direction);
}

fn glyph_instance(input: VertexInput, vertexIndex: u32) -> TextGlyphInstance {
    return TextGlyphInstance(vertexIndex, input.snappedLogicalPos,
        input.basisX, input.basisY, input.bearSize, input.texCoords,
        input.color, input.scaleBoldItalicUseMvp, input.brushIndex);
}

fn original_quad_uv(vertexIndex: u32) -> vec2<f32> {
    if (vertexIndex == 1u) { return vec2<f32>(1.0, 0.0); }
    if (vertexIndex == 2u || vertexIndex == 4u) { return vec2<f32>(1.0, 1.0); }
    if (vertexIndex == 5u) { return vec2<f32>(0.0, 1.0); }
    return vec2<f32>(0.0);
}

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    let paint = glyphPaints[input.paintIndex];
    let vertexIndex = input.vertexIndex % 6u;
    let secondTriangle = input.vertexIndex >= 6u;
    let boundedTexture = paint.kind == 1u && (paint.flags & 2u) != 0u;
    let frame = text_glyph_vertex(glyph_instance(input, vertexIndex), vec4<f32>(1.0), 0u, false);
    let minimum = text_glyph_vertex(glyph_instance(input, 0u), vec4<f32>(1.0), 0u, false).logicalPosition;
    let corner1 = text_glyph_vertex(glyph_instance(input, 1u), vec4<f32>(1.0), 0u, false).logicalPosition;
    let maximum = text_glyph_vertex(glyph_instance(input, 2u), vec4<f32>(1.0), 0u, false).logicalPosition;
    let corner3 = text_glyph_vertex(glyph_instance(input, 5u), vec4<f32>(1.0), 0u, false).logicalPosition;
    let extent = maximum - minimum;
    let axisFrame = all(extent > vec2<f32>(0.0)) &&
        corner1.y == minimum.y && corner3.x == minimum.x &&
        corner1.x == maximum.x && corner3.y == maximum.y;
    let triangle012 = hinted_glyph_triangle(corner1 - minimum, maximum - minimum);
    let triangle023 = hinted_glyph_triangle(maximum - minimum, corner3 - minimum);
    let mappingFlags = select(0u, 1u, triangle012.valid) |
        select(0u, 2u, triangle023.valid) | select(0u, 4u, axisFrame) |
        select(0u, 8u, triangle012.positive) | select(0u, 16u, triangle023.positive) |
        select(0u, 32u, secondTriangle);
    // Material/extended paint already emits the canonical glyph triangles.
    // A rotated or mirrored diagonal is not a reason to discard those draws.
    var liveFrame = true;
    let safeExtent = select(vec2<f32>(1.0), extent, extent > vec2<f32>(0.0));
    let cornerUV = original_quad_uv(vertexIndex);
    var world = frame.logicalPosition;
    var texCoord = frame.texCoord;
    var paintUV = vec2<f32>(0.0);
    if (paint.kind == 1u) {
        if ((paint.flags & 2u) != 0u) {
            liveFrame = (mappingFlags & 7u) != 0u;
            world = paint.textureQuad01.xy;
            if (vertexIndex == 1u) {
                world = paint.textureQuad01.zw;
            } else if (vertexIndex == 2u || vertexIndex == 4u) {
                world = paint.textureQuad23.xy;
            } else if (vertexIndex == 5u) {
                world = paint.textureQuad23.zw;
            }
            paintUV = mix(paint.uvBounds.xy, paint.uvBounds.zw, cornerUV);
        } else {
            // Extend admission supplies the ORIGINAL snapped axis-preserving
            // source quad, not a padded domain or arbitrary affine bbox.
            let paintExtent = paint.textureQuad23.xy - paint.textureQuad01.xy;
            let livePaintAxes = paintExtent != vec2<f32>(0.0);
            liveFrame = liveFrame && all(livePaintAxes);
            let safePaintExtent = select(vec2<f32>(1.0), paintExtent, livePaintAxes);
            let sourceUV = (world - paint.textureQuad01.xy) / safePaintExtent;
            paintUV = mix(paint.uvBounds.xy, paint.uvBounds.zw, sourceUV);
        }
    }
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(world, 0.0, 1.0);
    if (secondTriangle && (!boundedTexture || axisFrame)) {
        // Positive-axis coverage keeps its original single image quad and
        // arithmetic. The extra vertices collapse without fragment/blend work.
        output.position = vec4<f32>(0.0, 0.0, 0.0, 1.0);
    }
    output.texCoord = texCoord;
    output.texelBounds = frame.texelBounds;
    output.textPolicy = vec3<f32>(frame.cornerRadius, frame.strokeThickness, frame.textMode);
    // Texture coverage needs the actual image-fragment position, without an
    // unrelated material-origin subtraction before hardware interpolation.
    output.sourceLogical = select(world - paint.sourceOffsetOpacity.xy, world, paint.kind == 1u);
    output.paintUV = paintUV;
    output.paintIndex = input.paintIndex;
    output.glyphLogicalFrame = vec4<f32>(minimum, safeExtent);
    output.liveGlyphFrame = select(0u, 1u, liveFrame);
    output.glyphTriangle012 = triangle012.inverse;
    output.glyphTriangle023 = triangle023.inverse;
    output.glyphMappingFlags = mappingFlags;
    output.glyphDiagonal = maximum - minimum;
    output.glyphOtherCorners = vec4<f32>(corner1 - minimum, corner3 - minimum);
    return output;
}

fn hinted_glyph_paint_color(input: VertexOutput, maskAlpha: f32, premultipliedOutput: bool) -> vec4<f32> {
    let paint = glyphPaints[input.paintIndex];
    let boundedTexture = paint.kind == 1u && (paint.flags & 2u) != 0u;
    var texCoord = input.texCoord;
    var glyphFrameUV = vec2<f32>(0.0);
    var outsideGlyph = false;
    if (boundedTexture) {
        // Keep the original image triangles and UV interpolation. Evaluate
        // glyph coverage in its own retained frame at the actual fragment,
        // instead of extrapolating atlas coordinates to distant image corners
        // and interpolating those large values back into the small glyph.
        let glyphLocal = input.sourceLogical - input.glyphLogicalFrame.xy;
        let atlasMinimum = input.texelBounds.xy - vec2<f32>(0.5);
        let atlasSpan = input.texelBounds.zw + vec2<f32>(0.5) - atlasMinimum;
        if ((input.glyphMappingFlags & 4u) != 0u) {
            // Preserve the original positive-axis arithmetic exactly.
            glyphFrameUV = glyphLocal / input.glyphLogicalFrame.zw;
            texCoord = atlasMinimum + glyphLocal * (atlasSpan / input.glyphLogicalFrame.zw);
            outsideGlyph = any(glyphFrameUV < vec2<f32>(0.0)) || any(glyphFrameUV > vec2<f32>(1.0));
        } else {
            // Match both actual canonical triangles. Rounded corner positions
            // need not form one exact parallelogram; do not invent corner 3.
            // Each original glyph triangle has its own image-quad primitive.
            // Rounded/folded triangles therefore retain both contributions;
            // they are never unioned or reduced to one selected coverage value.
            let secondTriangle = (input.glyphMappingFlags & 32u) != 0u;
            if (secondTriangle) {
                let second = hinted_glyph_triangle_weights(input.glyphTriangle023, glyphLocal);
                glyphFrameUV = vec2<f32>(second.x, second.x + second.y);
                outsideGlyph = (input.glyphMappingFlags & 2u) == 0u ||
                    !hinted_glyph_inside_triangle(glyphLocal, input.glyphDiagonal,
                        input.glyphOtherCorners.zw, (input.glyphMappingFlags & 16u) != 0u);
            } else {
                let first = hinted_glyph_triangle_weights(input.glyphTriangle012, glyphLocal);
                glyphFrameUV = vec2<f32>(first.x + first.y, first.y);
                outsideGlyph = (input.glyphMappingFlags & 1u) == 0u ||
                    !hinted_glyph_inside_triangle(glyphLocal, input.glyphOtherCorners.xy,
                        input.glyphDiagonal, (input.glyphMappingFlags & 8u) != 0u);
            }
            texCoord = atlasMinimum + glyphFrameUV * atlasSpan;
        }
    }
    // Evaluate derivatives before material policy, tile guards or masks can
    // discard. The white input is only a float coverage calculation, never a
    // published draw, style stream or intermediate quantized texture.
    let coverage = text_glyph_color_with_mask_alpha(vec4<f32>(1.0), texCoord,
        input.texelBounds, input.textPolicy.z, input.textPolicy.x, input.textPolicy.y, 1.0).a;
    let sourceDx = dpdx(input.sourceLogical);
    let sourceDy = dpdy(input.sourceLogical);
    let paintDx = dpdx(input.paintUV);
    let paintDy = dpdy(input.paintUV);
    if (maskAlpha <= 0.0 || input.liveGlyphFrame == 0u ||
        (boundedTexture && outsideGlyph)) {
        discard;
    }
    if (paint.kind == 0u) {
        let color = sample_registered_material(brushes[paint.brushIndex],
            vec4<f32>(1.0), true, input.sourceLogical, sourceDx, sourceDy);
        let alpha = color.a * coverage * maskAlpha;
        return vec4<f32>(select(color.rgb, color.rgb * alpha, premultipliedOutput), alpha);
    }

    let opacity = paint.sourceOffsetOpacity.z;
    let sourcePremultiplied = (paint.flags & 1u) != 0u;
    let cubic = (paint.flags & 4u) != 0u;
    let sampleInput = TextureImageSampleInput(
        vec4<f32>(1.0, select(0.0, 1.0, sourcePremultiplied), 0.0,
            select(opacity, -opacity, cubic)),
        input.paintUV, paint.sampling.xy, 0.0, 0.0);
    let modes = paint.sampling.zw;
    let addressedUV = address_texture_coordinates(input.paintUV, modes);
    var color = sample_image(sampleInput, addressedUV, modes, paintDx, paintDy);
    let alpha = color.a * opacity * coverage * maskAlpha;
    if (sourcePremultiplied) {
        if (premultipliedOutput) {
            return vec4<f32>(color.rgb * opacity * coverage * maskAlpha, alpha);
        }
        return vec4<f32>(atlas_unpremultiply(color).rgb, alpha);
    }
    return vec4<f32>(select(color.rgb, color.rgb * alpha, premultipliedOutput), alpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy), false);
}
@fragment
fn fs_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, 1.0, false);
}
@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy), true);
}
@fragment
fn fs_main_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, 1.0, true);
}
@fragment
fn fs_main_mask_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy) *
        sample_mask_chain_alpha(input.position.xy), false);
}
@fragment
fn fs_main_mask_chain_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    return hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy) *
        sample_mask_chain_alpha(input.position.xy), true);
}
@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let alpha = hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy), false).a;
    return vec4<f32>(alpha, 0.0, 0.0, alpha);
}
@fragment
fn fs_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let alpha = hinted_glyph_paint_color(input, 1.0, false).a;
    return vec4<f32>(alpha, 0.0, 0.0, alpha);
}
@fragment
fn fs_mask_chain(input: VertexOutput) -> @location(0) vec4<f32> {
    let alpha = hinted_glyph_paint_color(input, sample_mask_alpha(input.position.xy) *
        sample_mask_chain_alpha(input.position.xy), false).a;
    return vec4<f32>(alpha, 0.0, 0.0, alpha);
}
