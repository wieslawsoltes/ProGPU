//! Exact raw styles and direct material/texture glyph-paint admission.
use super::*;
fn finite(values: &[f32]) -> bool {
    values.iter().all(|v| v.is_finite())
}
fn zero(values: &[f32]) -> bool {
    values.iter().all(|&v| v == 0.0)
}
pub(crate) fn paint(p: &wire::GlyphPaint) -> bool {
    if p.kind > 1
        || p.coverage_transfer > 13
        || p.source_offset_opacity[3] != 0.0
        || !finite(&p.source_offset_opacity)
        || !finite(&p.uv_bounds)
        || !finite(&p.texture_quad01)
        || !finite(&p.texture_quad23)
        || !finite(&p.sampling)
    {
        return false;
    }
    if p.kind == 0 {
        return p.flags == 0
            && p.source_offset_opacity[2] == 0.0
            && zero(&p.uv_bounds)
            && zero(&p.texture_quad01)
            && zero(&p.texture_quad23)
            && zero(&p.sampling);
    }
    let mode = (p.flags & 0xff00) >> 8;
    let coefficients = if mode == 2 {
        p.sampling[0].abs() <= 16.0 && p.sampling[1].abs() <= 16.0
    } else {
        p.sampling[1] == 0.5
            && (p.sampling[0] == 0.0
                || mode == 0 && p.sampling[0] == -64.0
                || mode == 1 && p.sampling[0] == -128.0)
    };
    p.brush_index == 0
        && p.flags & !0xff07 == 0
        && mode <= 9
        && (p.flags & 4 != 0) == (mode == 2)
        && (0.0..=1.0).contains(&p.source_offset_opacity[2])
        && p.uv_bounds[2] >= p.uv_bounds[0]
        && p.uv_bounds[3] >= p.uv_bounds[1]
        && coefficients
        && p.sampling[2..]
            .iter()
            .all(|&v| v == 0.0 || v == 1.0 || v == 2.0)
}
pub(super) fn validate_text_style_table(
    input: &Input<'_>,
    r: &wire::Resource,
) -> TransportResult<()> {
    if r.payload_size == 0
        || r.auxiliary_size != 0
        || r.payload_size % 32 != 0
        || r.payload_size / 32 > 1 << 20
    {
        return Err(error(E::Value, r.payload_offset));
    }
    for index in 0..r.payload_size / 32 {
        let offset = r.payload_offset + index * 32;
        let s: wire::TextStyle = input.read(offset)?;
        if !values::text_style(&s) {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
pub(super) fn validate_styled_glyph_draw(
    input: &Input<'_>,
    c: &wire::Command,
) -> TransportResult<()> {
    let bad = || error(E::Value, c.payload_offset);
    if c.flags & GLYPH_STYLED == 0 || c.payload_size < 24 {
        return Err(bad());
    }
    let d: wire::GlyphDraw = input.read(c.payload_offset)?;
    if d.struct_size != 24
        || d.glyph_count == 0
        || d.glyph_count > 1 << 24
        || d.reserved0 != 0
        || d.reserved1 != 0
        || 24 + u64::from(d.glyph_count) * 64 != u64::from(c.payload_size)
        || d.style_resource_index >= input.header.resource_count
    {
        return Err(bad());
    }
    let r = input.resource(d.style_resource_index)?;
    if r.kind != 9 || r.payload_size % 32 != 0 || d.style_index >= r.payload_size / 32 {
        return Err(bad());
    }
    Ok(())
}
pub(super) fn validate_painted_glyph_draw(
    input: &Input<'_>,
    c: &wire::Command,
) -> TransportResult<()> {
    let bad = || error(E::Value, c.payload_offset);
    if c.kind != 26 || c.flags & GLYPH_STYLED != 0 || c.payload_size < 128 {
        return Err(bad());
    }
    let d: wire::PaintedGlyphDraw = input.read(c.payload_offset)?;
    let p: wire::GlyphPaint = input.read(c.payload_offset + 32)?;
    if d.struct_size != 32
        || d.glyph_count == 0
        || d.glyph_count > 1 << 24
        || d.rendering_mode > 1
        || d.reserved0 != 0
        || d.reserved1 != 0
        || d.reserved2 != 0
        || 128 + u64::from(d.glyph_count) * 64 != u64::from(c.payload_size)
        || d.paint_resource_index >= input.header.resource_count
        || !paint(&p)
        || p.coverage_transfer != 0 && d.rendering_mode != 0
    {
        return Err(bad());
    }
    let r = input.resource(d.paint_resource_index)?;
    let valid = if p.kind == 0 {
        r.kind == 8
            && r.payload_size % 256 == 0
            && d.paint_index < r.payload_size / 256
            && p.brush_index == d.paint_index
    } else {
        d.paint_index == 0
            && r.kind == 4
            && r.flags & EXTERNAL_IMAGE != 0
            && r.payload_size == 0
            && r.auxiliary_size == 0
    };
    if !valid {
        return Err(bad());
    }
    Ok(())
}
/// Original common prefix reader; semantic style/paint gates remain separate.
pub(crate) fn glyph_payload(input: &Input<'_>, c: &wire::Command) -> TransportResult<(u32, u32)> {
    let bad = || error(E::Value, c.payload_offset);
    if c.kind == 26 {
        if c.payload_size < 128 {
            return Err(bad());
        }
        let d: wire::PaintedGlyphDraw = input.read(c.payload_offset)?;
        if d.struct_size != 32
            || d.glyph_count == 0
            || 128 + u64::from(d.glyph_count) * 64 != u64::from(c.payload_size)
        {
            return Err(bad());
        }
        return Ok((c.payload_offset + 128, d.glyph_count));
    }
    if c.flags & GLYPH_STYLED == 0 {
        if c.payload_size == 0 || c.payload_size % 64 != 0 {
            return Err(bad());
        }
        return Ok((c.payload_offset, c.payload_size / 64));
    }
    if c.payload_size < 24 {
        return Err(bad());
    }
    let d: wire::GlyphDraw = input.read(c.payload_offset)?;
    if d.struct_size != 24
        || d.glyph_count == 0
        || 24 + u64::from(d.glyph_count) * 64 != u64::from(c.payload_size)
    {
        return Err(bad());
    }
    Ok((c.payload_offset + 24, d.glyph_count))
}
