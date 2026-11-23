use super::*;
pub(super) fn validate_color_glyph_resource(
    input: &Input<'_>,
    r: &wire::Resource,
) -> TransportResult<()> {
    let bad = || error(E::Value, r.payload_offset);
    if r.kind != 3
        || r.flags & COLOR_GLYPH_BITMAPS == 0
        || r.payload_size == 0
        || r.auxiliary_size == 0
        || r.payload_size % 48 != 0
        || r.payload_size / 48 > 1 << 20
    {
        return Err(bad());
    }
    for index in 0..r.payload_size / 48 {
        let offset = r.payload_offset + index * 48;
        let b: wire::ColorGlyphBitmap = input.read(offset)?;
        if !values::color_glyph_bitmap(&b, r.auxiliary_size as usize) {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
