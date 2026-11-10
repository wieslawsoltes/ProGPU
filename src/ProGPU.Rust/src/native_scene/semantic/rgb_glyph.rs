//! Independent explicit RGB outline integration model, not ordinary text modes.
use super::*;
pub(crate) fn descriptor(d: &wire::RgbGlyphDraw) -> bool {
    d.struct_size == 40
        && d.glyph_count != 0
        && d.glyph_count <= 65536
        && d.filter_model == 1
        && d.pixel_geometry <= 2
        && d.gamma == 1.0
        && d.enhanced_contrast == 0.0
        && d.cleartype_level == 1.0
        && d.dpi_scale.is_finite()
        && d.dpi_scale > 0.0
        && d.reserved0 == 0
        && d.reserved1 == 0
}
pub(crate) fn tile(t: &wire::RgbGlyphTile, outlines: u32) -> bool {
    if t.outline_index >= outlines
        || t.reserved != 0
        || t.width == 0
        || t.height == 0
        || t.width > 4096
        || t.height > 4096
        || !(-4096..=4096).contains(&t.target_x)
        || !(-4096..=4096).contains(&t.target_y)
        || ![t.x_start, t.y_start, t.scale, t.subpixel_x]
            .into_iter()
            .all(f32::is_finite)
        || t.scale <= 0.0
        || ![
            t.foreground.r,
            t.foreground.g,
            t.foreground.b,
            t.foreground.a,
        ]
        .into_iter()
        .all(|v| v.is_finite() && (0.0..=1.0).contains(&v))
    {
        return false;
    }
    [
        ((t.x_start - 1.0 / 3.0) + 0.0625 - t.subpixel_x) / t.scale,
        ((t.x_start + (t.width - 1) as f32 + 1.0 / 3.0) + 0.9375 - t.subpixel_x) / t.scale,
        -(t.y_start + 0.0625) / t.scale,
        -(t.y_start + (t.height - 1) as f32 + 0.9375) / t.scale,
    ]
    .into_iter()
    .all(f32::is_finite)
}
pub(crate) fn validate_rgb_glyph_draw(input: &Input<'_>, c: &wire::Command) -> TransportResult<()> {
    let bad = || error(E::Value, c.payload_offset);
    if c.kind != 27 || c.resource_index >= input.header.resource_count || c.payload_size < 40 {
        return Err(bad());
    }
    let d: wire::RgbGlyphDraw = input.read(c.payload_offset)?;
    if !descriptor(&d) || 40 + u64::from(d.glyph_count) * 56 != u64::from(c.payload_size) {
        return Err(bad());
    }
    let r = input.resource(c.resource_index)?;
    if r.kind != 3
        || r.flags & COLOR_GLYPH_BITMAPS != 0
        || r.payload_size == 0
        || r.payload_size % 40 != 0
        || r.auxiliary_size == 0
        || r.auxiliary_size % 48 != 0
    {
        return Err(bad());
    }
    let outlines = r.payload_size / 40;
    let segments = r.auxiliary_size / 48;
    if outlines > 65536 || segments > 1048576 {
        return Err(bad());
    }
    let mut covered = 0u64;
    for index in 0..outlines {
        let offset = r.payload_offset + index * 40;
        let outline: wire::GlyphOutline = input.read(offset)?;
        if outline.segment_offset > covered
            || values::glyph_outline(&outline, u64::from(segments)).is_none()
        {
            return Err(error(E::Value, offset));
        }
        covered = covered.max(outline.segment_offset + outline.segment_count);
    }
    if covered != u64::from(segments) {
        return Err(bad());
    }
    for index in 0..segments {
        let offset = r.auxiliary_offset + index * 48;
        if !values::segment(&input.read(offset)?, false) {
            return Err(error(E::Value, offset));
        }
    }
    let mut pixels = 0u64;
    for index in 0..d.glyph_count {
        let offset = c.payload_offset + 40 + index * 56;
        let t: wire::RgbGlyphTile = input.read(offset)?;
        pixels += u64::from(t.width) * u64::from(t.height);
        if !tile(&t, outlines) || pixels > 4096 * 4096 {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
