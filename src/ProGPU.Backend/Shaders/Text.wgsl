// Algorithm: Transform glyph quads, resolve shared solid-run presentation state from a compact text-style stream, clamp filtered coverage to each retained atlas tile, modulate text color, and intersect an optional fixed chain of at most four analytic rounded masks.
// Time complexity: O(1) per vertex and fragment; nested semantic masking performs at most four bounded analytic mask evaluations.
// Space complexity: O(1) local storage with one 32-byte text-style record read, a flat four-float tile bound and certified four-float physical coverage frame plus admission bit per vertex, and one coverage-or-color atlas sample per fragment; texture masks add one sample, analytic rounded and uniform-opacity masks add no texture bandwidth, ClearType adds two coverage samples, and a nested analytic chain reads one primary 96-byte record plus one fixed 288-byte continuation record.
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
    @location(6) @interpolate(flat) physicalGlyphFrame: vec4<f32>,
    @location(7) @interpolate(flat) canonicalPhysicalFrame: u32,
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
    let style = textStyles[u32(max(input.brushIndex, 0.0))];
    let frame = text_glyph_vertex(
        TextGlyphInstance(input.vertexIndex, input.snappedLogicalPos, input.basisX,
            input.basisY, input.bearSize, input.texCoords, input.color,
            input.scaleBoldItalicUseMvp, input.brushIndex),
        style.color, style.textRenderingMode, input.brushIndex >= 0.0);
    return VertexOutput(frame.position, frame.color, frame.texCoord,
        frame.cornerRadius, frame.strokeThickness, frame.textMode, frame.texelBounds,
        frame.physicalGlyphFrame, frame.canonicalPhysicalFrame);
}

@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;
@group(1) @binding(2) var colorAtlasTexture: texture_2d<f32>;
fn text_fs_main_with_mask_alpha(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let texCoord = text_glyph_coverage_tex_coord(input.texCoord, input.position.xy,
        input.texelBounds, input.physicalGlyphFrame, input.canonicalPhysicalFrame);
    return text_glyph_color_with_mask_alpha(input.color, texCoord, input.texelBounds,
        input.textMode, input.cornerRadius, input.strokeThickness, maskAlpha);
}

fn text_fs_main(input: VertexOutput) -> vec4<f32> {
    let maskAlpha = sample_mask_alpha(input.position.xy);
    return text_fs_main_with_mask_alpha(input, maskAlpha);
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
    // Premultiplied R8 coverage: transparent fragments preserve earlier ink.
    return vec4<f32>(color.a, 0.0, 0.0, color.a);
}

@fragment
fn fs_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main_with_mask_alpha(input, 1.0);
    // Premultiplied R8 coverage: transparent fragments preserve earlier ink.
    return vec4<f32>(color.a, 0.0, 0.0, color.a);
}
