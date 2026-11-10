use super::*;
pub(crate) fn glyph_outline(outline: &wire::GlyphOutline, segment_count: u64) -> Option<u64> {
    if outline.segment_count == 0
        || outline.segment_offset > segment_count
        || outline.segment_count > segment_count - outline.segment_offset
        || ![
            outline.min_x,
            outline.min_y,
            outline.max_x,
            outline.max_y,
            outline.raster_scale,
            outline.subpixel_x,
        ]
        .into_iter()
        .all(f32::is_finite)
        || outline.max_x <= outline.min_x
        || outline.max_y <= outline.min_y
        || outline.raster_scale <= 0.0
        || outline.subpixel_x < 0.0
        || outline.subpixel_x > 0.75
        || (outline.subpixel_x * 4.0 - (outline.subpixel_x * 4.0).round()).abs() > 0.0001
    {
        return None;
    }
    let min_x = outline.min_x * outline.raster_scale;
    let min_y = -outline.max_y * outline.raster_scale;
    let max_x = outline.max_x * outline.raster_scale;
    let max_y = -outline.min_y * outline.raster_scale;
    coverage(
        max_x.ceil() + 4.0 - (min_x.floor() - 4.0),
        max_y.ceil() + 4.0 - (min_y.floor() - 4.0),
    )
}
pub(crate) fn positioned_glyph(glyph: &wire::PositionedGlyph, outline_count: u64) -> bool {
    u64::from(glyph.outline_index) < outline_count
        && glyph.reserved == 0
        && glyph.reserved2 == 0.0
        && finite(glyph.position)
        && finite(glyph.basis_x)
        && finite(glyph.basis_y)
        && finite(glyph.color)
        && glyph.atlas_to_logical_scale.is_finite()
        && glyph.atlas_to_logical_scale > 0.0
        && glyph.bold_offset.is_finite()
        && glyph.italic_skew.is_finite()
}
pub(crate) fn color_glyph_bitmap(bitmap: &wire::ColorGlyphBitmap, pixel_bytes: usize) -> bool {
    if bitmap.width == 0
        || bitmap.height == 0
        || bitmap.width > 16384
        || bitmap.height > 16384
        || bitmap.width > u32::MAX / 4
        || bitmap.row_bytes < bitmap.width * 4
        || bitmap.reserved0 != 0
        || bitmap.reserved1 != 0
        || bitmap.reserved2 != 0
        || ![
            bitmap.bear_x,
            bitmap.bear_y,
            bitmap.render_width,
            bitmap.render_height,
        ]
        .into_iter()
        .all(f32::is_finite)
        || bitmap.render_width < 0.0
        || bitmap.render_height < 0.0
    {
        return false;
    }
    let required =
        u64::from(bitmap.row_bytes) * u64::from(bitmap.height - 1) + u64::from(bitmap.width) * 4;
    bitmap.pixel_offset <= pixel_bytes as u64
        && required <= pixel_bytes as u64 - bitmap.pixel_offset
}
pub(crate) fn text_style(style: &wire::TextStyle) -> bool {
    finite(style.color)
        && style.text_rendering_mode <= SCENE_TEXT_CLEARTYPE
        && style.coverage_transfer <= 13
        && (style.coverage_transfer == 0 || style.text_rendering_mode == SCENE_TEXT_GRAYSCALE)
        && style.reserved1 == 0
        && style.reserved2 == 0
}
