//! Original compact IDs, composite references and long horizontal metrics.
use super::*;
const NO_GLYPH: u16 = u16::MAX;
fn skipped(value: u32) -> bool {
    [
        b"DSIG", b"head", b"maxp", b"loca", b"glyf", b"hhea", b"hmtx", b"cmap", b"GSUB", b"GPOS",
        b"GDEF", b"kern", b"vhea", b"vmtx", b"VORG", b"BASE", b"JSTF", b"MATH", b"COLR", b"CPAL",
        b"post",
    ]
    .iter()
    .any(|bytes| tag(bytes) == value)
}
fn remap(source: &[u8], map: &[u16], policy: Policy) -> Result<Vec<u8>> {
    let mut result = copy(source)?;
    if source.len() < 10 || i16be(source, 0)? >= 0 {
        return Ok(result);
    }
    let mut offset = 10;
    let last_flags = loop {
        slice(source, offset, 4)?;
        let flags = u16be(source, offset)?;
        let glyph = u16be(source, offset + 2)?;
        let compact = *map
            .get(usize::from(glyph))
            .filter(|&&v| v != NO_GLYPH)
            .ok_or(INVALID)?;
        put16(&mut result, offset + 2, compact)?;
        offset = glyphs::component_end(
            offset,
            flags,
            if policy == Policy::Managed {
                usize::MAX
            } else {
                source.len()
            },
        )?;
        if flags & 0x20 == 0 {
            break flags;
        }
    };
    // Only this compact producer validates the final component's instruction
    // extent; the preserving composite-closure route intentionally does not.
    if last_flags & 0x100 != 0 {
        let length = usize::from(u16be(source, offset)?);
        slice(source, offset + 2, length)?;
    }
    Ok(result)
}
fn horizontal(hhea: &[u8], hmtx: &[u8], order: &[u16]) -> Result<Vec<u8>> {
    let count = u16be(hhea, 34)?;
    if count == 0 {
        return Err(INVALID);
    }
    let mut output = filled(order.len() * 4, 0)?;
    for (index, &glyph) in order.iter().enumerate() {
        let advance_offset = usize::from(if glyph < count { glyph } else { count - 1 }) * 4;
        let bearing_offset = if glyph < count {
            advance_offset + 2
        } else {
            usize::from(count) * 4 + usize::from(glyph - count) * 2
        };
        let advance = u16be(hmtx, advance_offset)?;
        let bearing = bytes::i16(hmtx, bearing_offset).unwrap_or(0);
        put16(&mut output, index * 4, advance)?;
        put16(&mut output, index * 4 + 2, bearing as u16)?;
    }
    Ok(output)
}
pub(super) fn build(
    data: &[u8],
    directory: usize,
    requested: &[u16],
    policy: Policy,
) -> Result<NativeSfntSubset> {
    let mut face = tables::parse(data, directory, policy)?;
    let head = face.find(tag(b"head")).ok_or(INVALID)?;
    let maxp = face.find(tag(b"maxp")).ok_or(INVALID)?;
    let loca = face.find(tag(b"loca")).ok_or(INVALID)?;
    let source_glyf = face.find(tag(b"glyf")).ok_or(INVALID)?;
    if head.len() < 54 || maxp.len() < 6 {
        return Err(INVALID);
    }
    let count = u16be(maxp, 4)?;
    if count == 0 {
        return Err(INVALID);
    }
    let source_offsets = glyphs::read_loca(loca, count, i16be(head, 50)?)?;
    let included = glyphs::include(source_glyf, &source_offsets, count, requested, policy)?;
    let mut order = reserved(included.iter().filter(|&&v| v).count())?;
    for (glyph, &keep) in included.iter().enumerate() {
        if keep {
            order.push(glyph as u16);
        }
    }
    let mut source_to_subset = filled(usize::from(count), NO_GLYPH)?;
    let mut glyph_map = reserved(order.len())?;
    for (subset, &source) in order.iter().enumerate() {
        let compact = subset as u16;
        source_to_subset[usize::from(source)] = compact;
        glyph_map.push(NativeSfntGlyphRemap {
            source_glyph_id: source,
            subset_glyph_id: compact,
        });
    }
    let mut offsets = reserved(order.len() + 1)?;
    let mut glyf = Vec::new();
    for &source in &order {
        offsets.push(u32::try_from(glyf.len()).map_err(|_| INVALID)?);
        let start = source_offsets[usize::from(source)] as usize;
        let end = source_offsets[usize::from(source) + 1] as usize;
        if policy == Policy::Managed && start == end {
            continue;
        }
        let source = slice(source_glyf, start, end.checked_sub(start).ok_or(INVALID)?)?;
        let remapped = remap(source, &source_to_subset, policy)?;
        glyphs::append(&mut glyf, &remapped)?;
    }
    offsets.push(u32::try_from(glyf.len()).map_err(|_| INVALID)?);
    let loca = glyphs::make_loca(&offsets)?;
    let mut head = copy(head)?;
    put32(&mut head, 8, 0)?;
    put16(&mut head, 50, 1)?;
    let mut maxp = copy(maxp)?;
    put16(&mut maxp, 4, order.len() as u16)?;
    let metrics =
        if let (Some(hhea), Some(hmtx)) = (face.find(tag(b"hhea")), face.find(tag(b"hmtx"))) {
            if hhea.len() < 36 {
                return Err(INVALID);
            }
            let compact_hmtx = horizontal(hhea, hmtx, &order)?;
            let mut compact_hhea = copy(hhea)?;
            put16(&mut compact_hhea, 34, order.len() as u16)?;
            Some((compact_hhea, compact_hmtx))
        } else {
            None
        };
    face.tables.retain(|table| !skipped(table.tag));
    face.tables.try_reserve(6).map_err(|_| INVALID)?;
    face.tables.extend([
        Table::owned(tag(b"head"), head),
        Table::owned(tag(b"maxp"), maxp),
        Table::owned(tag(b"loca"), loca),
        Table::owned(tag(b"glyf"), glyf),
    ]);
    if let Some((hhea, hmtx)) = metrics {
        face.tables.extend([
            Table::owned(tag(b"hhea"), hhea),
            Table::owned(tag(b"hmtx"), hmtx),
        ]);
    }
    Ok(NativeSfntSubset {
        font: tables::build(face)?,
        glyph_map,
    })
}
