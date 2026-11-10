// Algorithm: Apply an explicit GDI+ grayscale contrast transfer to sampled coverage before source paint, opacity and clipping. Zero selects the caller's unchanged legacy policy.
// Time complexity: O(1), one bounded power and byte rounding per explicit sample.
// Space complexity: O(1); no lookup textures, readback or intermediate image.
// Encoding 1..13 retains original contrast 0..12. Encoding 14..78 names the
// exponent of the background term directly, 0.5 + (encoding - 14) / 32, for
// callers that match a measured text weight: below one text gets lighter,
// above one heavier. This transfer does not determine spatial coverage, font
// hinting, glyph phase or RGB subpixel policy.
fn source_grayscale_coverage(coverage: f32, encoding: f32) -> f32 {
    let exponent = select(
        1.0 / (1.0 + (encoding - 1.0) / 10.0),
        0.5 + (encoding - 14.0) / 32.0,
        encoding > 13.5);
    let background = pow(clamp(1.0 - coverage, 0.0, 1.0), exponent);
    return (255.0 - floor(background * 255.0 + 0.5)) / 255.0;
}
