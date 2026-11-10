//! Original ordered sbix strikes and depth-bounded duplicate resolution.
use super::*;
pub(super) fn select(
    data: &[u8],
    glyph_count: u16,
    glyph: u16,
    target: f32,
) -> Option<BitmapRecord> {
    if data.len() < 12 || bytes::u16(data, 0)? != 1 {
        return None;
    }
    let count = bytes::u32(data, 4)?;
    if count == 0 || count > i32::MAX as u32 || 8 + u64::from(count) * 4 > data.len() as u64 {
        return None;
    }
    let mut best = None;
    let mut distance = f32::MAX;
    for index in 0..count as usize {
        let start = bytes::u32(data, 8 + index * 4)? as usize;
        let end = if index + 1 < count as usize {
            bytes::u32(data, 12 + index * 4)? as usize
        } else {
            data.len()
        };
        if start > i32::MAX as usize
            || end > data.len()
            || start >= end
            || start as u64 + 4 + (u64::from(glyph_count) + 1) * 4 > end as u64
        {
            continue;
        }
        if let Some(candidate) = resolve(data, start, end, glyph_count, glyph, glyph, 0) {
            pick(&mut best, &mut distance, candidate, target);
        }
    }
    best
}
fn resolve(
    data: &[u8],
    strike: usize,
    end: usize,
    count: u16,
    glyph: u16,
    original: u16,
    depth: u8,
) -> Option<BitmapRecord> {
    if depth > 16 || glyph >= count {
        return None;
    }
    let offsets = strike + 4;
    let start_relative = bytes::u32(data, offsets + usize::from(glyph) * 4)?;
    let end_relative = bytes::u32(data, offsets + (usize::from(glyph) + 1) * 4)?;
    if start_relative >= end_relative
        || start_relative > i32::MAX as u32
        || end_relative > i32::MAX as u32
    {
        return None;
    }
    let start = strike.checked_add(start_relative as usize)?;
    let next = strike.checked_add(end_relative as usize)?;
    if start < offsets || next > end || next - start < 8 {
        return None;
    }
    let origin = [bytes::i16(data, start)?, bytes::i16(data, start + 2)?];
    let kind = bytes::u32(data, start + 4)?;
    if kind == 0x64757065 {
        if next - start < 10 {
            return None;
        }
        let duplicate = bytes::u16(data, start + 8)?;
        if duplicate == original {
            return None;
        }
        let mut resolved = resolve(data, strike, end, count, duplicate, original, depth + 1)?;
        resolved.origin = origin;
        return Some(resolved);
    }
    if !matches!(
        kind,
        ManagedBitmapGlyph::PNG | ManagedBitmapGlyph::JPEG | ManagedBitmapGlyph::TIFF
    ) {
        return None;
    }
    Some(BitmapRecord {
        range: start + 8..next,
        ppem: bytes::u16(data, strike)?,
        ppi: bytes::u16(data, strike + 2)?,
        origin,
        kind,
        horizontal: false,
        bearing: [0; 2],
    })
}
