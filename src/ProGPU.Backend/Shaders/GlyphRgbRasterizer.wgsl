// Algorithm: Evaluate three independent full-pixel box integrals of original glyph outlines at horizontal RGB/BGR stripe centers, sharing the canonical glyph winding walker. This explicit 8x8 box model is not an assertion of modern DirectWrite grid fitting/filter policy.
// Time complexity: O(P * (24*S + 192)) for P output pixels and S outline segments; flat geometry evaluates one 8x8 integral per pixel.
// Space complexity: O(1) invocation-private winding lanes and one packed RGBA8 output word per pixel; original segments are borrowed read-only and no grayscale texture is sampled.
// Composition prefix: the unchanged production GlyphRasterizer.wgsl declarations,
// winding functions and scalar entrypoints. Only these RGB entries use binding4.
struct RgbGlyphRasterPolicy {
    pixelGeometry: u32, // 0 flat, 1 RGB, 2 BGR; validated before dispatch.
    filterModel: u32,   // 1: independent full-pixel-wide 8x8 box filters.
    reserved0: u32,
    reserved1: u32,
};
@group(0) @binding(4) var<uniform> rgbGlyphPolicy: RgbGlyphRasterPolicy;

fn glyph_rgb_coverage_word(x: u32, y: u32) -> u32 {
    let outline = glyphRecords[uniforms.glyphIndex];
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);
    let green = glyph_row_coverage_byte(glyph_sample_row(px), py, outline);
    var red = green;
    var blue = green;
    if (rgbGlyphPolicy.pixelGeometry != 0u) {
        // Each displaced integration samples original geometry. Shifting a
        // quantized scalar atlas would perform a different second filter.
        let left = glyph_row_coverage_byte(glyph_sample_row(px - 1.0 / 3.0), py, outline);
        let right = glyph_row_coverage_byte(glyph_sample_row(px + 1.0 / 3.0), py, outline);
        red = select(left, right, rgbGlyphPolicy.pixelGeometry == 2u);
        blue = select(right, left, rgbGlyphPolicy.pixelGeometry == 2u);
    }
    // Alpha is not max(R,G,B) or glyph opacity. This texture stores independent
    // channel coverage; only the RGB-aware compositor can consume it.
    return red | (green << 8u) | (blue << 16u) | 0xFF000000u;
}

@compute @workgroup_size(16, 16)
fn cs_rgb(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= uniforms.width || id.y >= uniforms.height) { return; }
    coverageOutput[uniforms.outputOffsetWords + id.y * uniforms.outputRowWords + id.x] =
        glyph_rgb_coverage_word(id.x, id.y);
}

// Same algorithm on a compatible GPU stage; no CPU pixel generation or upload.
@fragment
fn fs_rgb(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let packed = glyph_rgb_coverage_word(u32(position.x - uniforms.atlasX),
        u32(position.y - uniforms.atlasY));
    return vec4<f32>(f32(packed & 255u), f32((packed >> 8u) & 255u),
        f32((packed >> 16u) & 255u), 255.0) / 255.0;
}
