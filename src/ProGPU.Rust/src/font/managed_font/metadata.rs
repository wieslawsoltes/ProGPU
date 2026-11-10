use super::*;
use crate::font::bytes;
#[derive(Debug, Default)]
pub(super) struct Raw {
    pub loca_format: i16,
    pub loca: u32,
    pub glyf: u32,
    pub horizontal_count: u16,
    pub horizontal: u32,
    pub vertical_count: u16,
    pub vertical: u32,
    pub cpal: u32,
    pub palette_entries: u16,
    pub palettes: u16,
    pub colors: u16,
    pub color_records: u32,
    pub colr_base_count: u16,
    pub colr_base_records: u32,
    pub colr_layer_records: u32,
}
pub(super) fn word(source: &SfntFace, at: u32) -> Result<u16, FontError> {
    if at > i32::MAX as u32 {
        return Err(FontError::InvalidFace);
    }
    bytes::u16(source.data(), at as usize).ok_or(FontError::InvalidFace)
}
pub(super) fn long(source: &SfntFace, at: u32) -> Result<u32, FontError> {
    if at > i32::MAX as u32 {
        return Err(FontError::InvalidFace);
    }
    bytes::u32(source.data(), at as usize).ok_or(FontError::InvalidFace)
}
pub(super) fn span(source: &SfntFace, tag: [u8; 4]) -> Option<(u32, u32)> {
    source
        .table_span(Tag::new(tag))
        .map(|(_, s)| (s.offset as u32, s.length as u32))
}
fn name(source: &SfntFace, ids: &[u16]) -> Option<Arc<str>> {
    ids.iter().find_map(|&id| source.managed_name(id))
}
pub(super) fn color(source: &SfntFace, at: u32) -> Result<[f32; 4], FontError> {
    let at = at as i32;
    let b = usize::try_from(at)
        .ok()
        .and_then(|n| bytes::bytes(source.data(), n, 4))
        .ok_or(FontError::InvalidFace)?;
    Ok([
        f32::from(b[2]) / 255.0,
        f32::from(b[1]) / 255.0,
        f32::from(b[0]) / 255.0,
        f32::from(b[3]) / 255.0,
    ])
}
pub(super) fn parse(source: &SfntFace) -> Result<(ManagedFontMetadata, Raw), FontError> {
    let family = name(source, &[16, 1]).unwrap_or_else(|| "".into());
    let subfamily = name(source, &[17, 2]).unwrap_or_else(|| "".into());
    let full_name = name(source, &[4]).unwrap_or_else(|| family.clone());
    let postscript_name = name(source, &[6]).unwrap_or_else(|| "".into());
    let head = span(source, *b"head")
        .or_else(|| span(source, *b"bhed"))
        .ok_or(FontError::InvalidFace)?
        .0;
    if span(source, *b"cmap").is_none() {
        return Err(FontError::InvalidFace);
    }
    let exists = |tag| span(source, tag).is_some();
    let mut raw = Raw::default();
    let mut out = ManagedFontMetadata {
        family,
        subfamily,
        full_name,
        postscript_name,
        weight_class: 400,
        width_class: 5,
        italic: false,
        has_true_type_outlines: exists(*b"loca") && exists(*b"glyf"),
        has_cff_outlines: exists(*b"CFF "),
        has_bitmap_glyphs: exists(*b"sbix")
            || (exists(*b"CBLC") && exists(*b"CBDT"))
            || (exists(*b"bloc") && exists(*b"bdat")),
        has_color_glyphs: (exists(*b"COLR") && exists(*b"CPAL")) || exists(*b"SVG "),
        fixed_pitch: false,
        units_per_em: word(source, head.wrapping_add(18))?,
        bounds: [0; 4],
        ascender: 0,
        descender: 0,
        line_gap: 0,
        underline_position: None,
        underline_thickness: None,
        strikeout_position: None,
        strikeout_thickness: None,
        x_height: None,
        cap_height: None,
        glyph_count: 0,
        palette: Arc::from([]),
    };
    if out.has_true_type_outlines {
        raw.loca = span(source, *b"loca").unwrap().0;
        raw.glyf = span(source, *b"glyf").unwrap().0;
    }
    for (i, v) in out.bounds.iter_mut().enumerate() {
        *v = word(source, head.wrapping_add(36 + i as u32 * 2))? as i16;
    }
    raw.loca_format = word(source, head.wrapping_add(50))? as i16;
    if let Some((at, _)) = span(source, *b"hhea") {
        out.ascender = word(source, at.wrapping_add(4))? as i16;
        out.descender = word(source, at.wrapping_add(6))? as i16;
        out.line_gap = word(source, at.wrapping_add(8))? as i16;
        raw.horizontal_count = word(source, at.wrapping_add(34))?;
        raw.horizontal = span(source, *b"hmtx").map_or(0, |s| s.0);
    }
    if let Some((at, len)) = span(source, *b"vhea")
        && len >= 36
    {
        raw.vertical_count = word(source, at.wrapping_add(34))?;
        raw.vertical = span(source, *b"vmtx").map_or(0, |s| s.0);
    }
    if let Some((at, _)) = span(source, *b"maxp") {
        out.glyph_count = word(source, at.wrapping_add(4))?;
    }
    if let Some(t) = source.table(Tag::new(*b"OS/2")) {
        let b = t.bytes;
        if b.len() >= 8 {
            out.weight_class = bytes::u16(b, 4).unwrap();
            out.width_class = bytes::u16(b, 6).unwrap();
        }
        if b.len() >= 64 {
            out.italic = bytes::u16(b, 62).unwrap() & 0x0201 != 0;
        }
        if b.len() >= 30 {
            out.strikeout_thickness = Some(bytes::u16(b, 26).unwrap() as i16);
            out.strikeout_position = Some(bytes::u16(b, 28).unwrap() as i16);
        }
        if b.len() >= 90 && bytes::u16(b, 0).unwrap() >= 2 {
            out.x_height = Some(bytes::u16(b, 86).unwrap() as i16);
            out.cap_height = Some(bytes::u16(b, 88).unwrap() as i16);
        }
    }
    if !out.italic
        && let Some(t) = source
            .table(Tag::new(*b"head"))
            .or_else(|| source.table(Tag::new(*b"bhed")))
        && t.bytes.len() >= 46
    {
        out.italic = bytes::u16(t.bytes, 44).unwrap() & 2 != 0;
    }
    if let Some(t) = source.table(Tag::new(*b"post"))
        && t.bytes.len() >= 12
    {
        out.underline_position = Some(bytes::u16(t.bytes, 8).unwrap() as i16);
        out.underline_thickness = Some(bytes::u16(t.bytes, 10).unwrap() as i16);
        if t.bytes.len() >= 16 {
            out.fixed_pitch = bytes::u32(t.bytes, 12).unwrap() != 0;
        }
    }
    // Retain every original constructor read even before color-layer execution
    // is requested. These headers use whole-font bounds, not declared-table bounds.
    if let Some((at, _)) = span(source, *b"COLR")
        && word(source, at)? <= 1
    {
        raw.colr_base_count = word(source, at.wrapping_add(2))?;
        raw.colr_base_records = at.wrapping_add(long(source, at.wrapping_add(4))?);
        raw.colr_layer_records = at.wrapping_add(long(source, at.wrapping_add(8))?);
        word(source, at.wrapping_add(12))?;
    }
    if let Some((at, _)) = span(source, *b"CPAL") {
        raw.cpal = at;
        if word(source, at)? <= 1 {
            raw.palette_entries = word(source, at.wrapping_add(2))?;
            raw.palettes = word(source, at.wrapping_add(4))?;
            raw.colors = word(source, at.wrapping_add(6))?;
            raw.color_records = at.wrapping_add(long(source, at.wrapping_add(8))?);
            let mut palette = Vec::new();
            palette
                .try_reserve_exact(usize::from(raw.palette_entries))
                .map_err(|_| FontError::Capacity)?;
            let first = u32::from(word(source, at.wrapping_add(12))?);
            for i in 0..u32::from(raw.palette_entries) {
                palette.push(color(
                    source,
                    raw.color_records.wrapping_add((first + i) * 4),
                )?);
            }
            out.palette = palette.into();
        }
    }
    Ok((out, raw))
}
pub(super) fn varied(
    base: &ManagedFontMetadata,
    instance: &ManagedVariationInstance,
) -> Result<ManagedFontMetadata, FontError> {
    let mut out = base.clone();
    let user = |tag: [u8; 4]| instance.user_coordinate(&tag.map(u16::from));
    if let Some(v) = user(*b"wght") {
        out.weight_class = (v.round_ties_even() as i32).clamp(1, 1000) as u16;
    }
    if let Some(v) = user(*b"ital") {
        out.italic = v >= 0.5;
    }
    if let Some(v) = user(*b"slnt") {
        out.italic |= v.abs() > 0.01;
    }
    let delta = |tag: [u8; 4]| instance.metric_delta(&tag.map(u16::from));
    let add = |v: i16, d: f32| {
        (f32::from(v) + d)
            .round_ties_even()
            .clamp(f32::from(i16::MIN), f32::from(i16::MAX)) as i16
    };
    out.ascender = add(out.ascender, delta(*b"hasc")?);
    out.descender = add(out.descender, delta(*b"hdsc")?);
    out.line_gap = add(out.line_gap, delta(*b"hlgp")?);
    for (v, tag) in [
        (&mut out.x_height, *b"xhgt"),
        (&mut out.cap_height, *b"cpht"),
        (&mut out.underline_position, *b"undo"),
        (&mut out.underline_thickness, *b"unds"),
        (&mut out.strikeout_position, *b"stro"),
        (&mut out.strikeout_thickness, *b"strs"),
    ] {
        let d = delta(tag)?;
        *v = v.map(|v| add(v, d));
    }
    Ok(out)
}
