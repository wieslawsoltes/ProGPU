//! Original native raw-directory and selected-SBIX immutable snapshot writer.
//! This is independent of the managed reader's ASCII tag and signed-size policy.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeGlyphResidentRequirements {
    pub sbix_bytes: usize,
    pub font_bytes: usize,
    pub strike_count: u32,
}
struct Record<'a> {
    bytes: &'a [u8],
    origin_x: u16,
    origin_y: u16,
}
fn record(
    sbix: &[u8],
    strike: usize,
    end: usize,
    glyph_count: u16,
    glyph: u16,
    original: u16,
    depth: u32,
) -> Option<Record<'_>> {
    let offsets = (usize::from(glyph_count) + 1) * 4;
    if depth > 16
        || glyph >= glyph_count
        || strike >= end
        || end > sbix.len()
        || strike.checked_add(4 + offsets)? > sbix.len()
    {
        return None;
    }
    let offset = strike + 4 + usize::from(glyph) * 4;
    let start_relative = dword(sbix, offset) as usize;
    let end_relative = dword(sbix, offset + 4) as usize;
    if start_relative >= end_relative
        || start_relative > end - strike
        || end_relative > end - strike
        || end_relative - start_relative < 8
    {
        return None;
    }
    let start = strike + start_relative;
    let last = strike + end_relative;
    if start < strike + 4 + offsets || last > end {
        return None;
    }
    let bytes = &sbix[start..last];
    let origin_x = word(bytes, 0);
    let origin_y = word(bytes, 2);
    let tag = &bytes[4..8];
    if tag == b"dupe" {
        if bytes.len() < 10 {
            return None;
        }
        let duplicate = word(bytes, 8);
        if duplicate == original {
            return None;
        }
        let selected = record(
            sbix,
            strike,
            end,
            glyph_count,
            duplicate,
            original,
            depth + 1,
        )?;
        return Some(Record {
            bytes: selected.bytes,
            origin_x,
            origin_y,
        });
    }
    if tag != b"png " && tag != b"jpg " && tag != b"tiff" {
        return None;
    }
    Some(Record {
        bytes,
        origin_x,
        origin_y,
    })
}
struct Source<'a> {
    sbix: &'a [u8],
    glyph_count: u16,
    glyph: u16,
    strikes: u32,
    bytes: usize,
}
impl<'a> Source<'a> {
    fn inspect(font: &'a SfntFace, glyph: u16) -> Result<Self, FontError> {
        let glyph_count = font
            .glyph_count()
            .filter(|&count| glyph < count)
            .ok_or(FontError::InvalidArgument)?;
        let sbix = font
            .table(Tag::new(*b"sbix"))
            .map(|t| t.bytes)
            .filter(|bytes| bytes.len() >= 8 && word(bytes, 0) == 1)
            .ok_or(FontError::InvalidGlyph)?;
        let strikes = dword(sbix, 4);
        if strikes == 0 || strikes > 4096 || 8 + strikes as usize * 4 > sbix.len() {
            return Err(FontError::InvalidFace);
        }
        let mut bytes = 8 + strikes as usize * 4;
        let offsets = (usize::from(glyph_count) + 1) * 4;
        for index in 0..strikes as usize {
            let start = dword(sbix, 8 + index * 4) as usize;
            let end = if index + 1 < strikes as usize {
                dword(sbix, 12 + index * 4) as usize
            } else {
                sbix.len()
            };
            if start >= end
                || end > sbix.len()
                || start
                    .checked_add(4 + offsets)
                    .is_none_or(|value| value > sbix.len())
            {
                return Err(FontError::InvalidFace);
            }
            let length = record(sbix, start, end, glyph_count, glyph, glyph, 0)
                .map_or(0, |value| value.bytes.len());
            bytes = bytes
                .checked_add(4 + offsets)
                .and_then(|value| value.checked_add(length))
                .ok_or(FontError::InvalidFace)?;
        }
        if bytes > u32::MAX as usize {
            return Err(FontError::InvalidFace);
        }
        Ok(Self {
            sbix,
            glyph_count,
            glyph,
            strikes,
            bytes,
        })
    }
    fn write(&self, output: &mut [u8]) {
        output[..8].copy_from_slice(&self.sbix[..8]);
        let mut target = 8 + self.strikes as usize * 4;
        let record_offset = 4 + (usize::from(self.glyph_count) + 1) * 4;
        for index in 0..self.strikes as usize {
            put32(output, 8 + index * 4, target as u32);
            let start = dword(self.sbix, 8 + index * 4) as usize;
            let end = if index + 1 < self.strikes as usize {
                dword(self.sbix, 12 + index * 4) as usize
            } else {
                self.sbix.len()
            };
            let selected = record(
                self.sbix,
                start,
                end,
                self.glyph_count,
                self.glyph,
                self.glyph,
                0,
            );
            output[target..target + 4].copy_from_slice(&self.sbix[start..start + 4]);
            let strike_size =
                record_offset + selected.as_ref().map_or(0, |value| value.bytes.len());
            for index in 0..=usize::from(self.glyph_count) {
                let offset = if index <= usize::from(self.glyph) {
                    record_offset
                } else {
                    strike_size
                };
                put32(output, target + 4 + index * 4, offset as u32);
            }
            if let Some(value) = selected {
                let at = target + record_offset;
                output[at..at + value.bytes.len()].copy_from_slice(value.bytes);
                put16(output, at, value.origin_x);
                put16(output, at + 2, value.origin_y);
            }
            target += strike_size;
        }
    }
}
fn align(value: usize) -> Result<usize, FontError> {
    value
        .checked_add(3)
        .map(|value| value & !3)
        .ok_or(FontError::InvalidFace)
}
fn font_size(font: &SfntFace, sbix_bytes: usize) -> Result<usize, FontError> {
    let data = font.data();
    let directory = font.face_offset() as usize + 12;
    let count = usize::from(font.table_count());
    if count == 0 || count > 4096 {
        return Err(FontError::InvalidFace);
    }
    let mut size = align(12 + count * 16)?;
    let mut replaced = false;
    for index in 0..count {
        let at = directory + index * 16;
        let entry = data.get(at..at + 16).ok_or(FontError::InvalidFace)?;
        let offset = dword(entry, 8) as usize;
        let length = dword(entry, 12) as usize;
        if offset > data.len() || length > data.len() - offset {
            return Err(FontError::InvalidFace);
        }
        let length = if !replaced && &entry[..4] == b"sbix" {
            replaced = true;
            sbix_bytes
        } else {
            length
        };
        size = size
            .checked_add(align(length)?)
            .ok_or(FontError::InvalidFace)?;
    }
    if !replaced || size > u32::MAX as usize {
        return Err(FontError::InvalidFace);
    }
    Ok(size)
}
fn checksum(input: &[u8]) -> u32 {
    let mut sum = 0u32;
    for bytes in input.chunks(4) {
        let mut value = 0u32;
        for (index, byte) in bytes.iter().enumerate() {
            value |= u32::from(*byte) << (24 - index * 8);
        }
        sum = sum.wrapping_add(value);
    }
    sum
}
impl SfntFace {
    pub fn native_glyph_resident_requirements(
        &self,
        glyph: u16,
    ) -> Result<NativeGlyphResidentRequirements, FontError> {
        let source = Source::inspect(self, glyph)?;
        Ok(NativeGlyphResidentRequirements {
            sbix_bytes: source.bytes,
            font_bytes: font_size(self, source.bytes)?,
            strike_count: source.strikes,
        })
    }
    /// Preserve every strike and every empty/duplicate glyph slot; substitute
    /// only the selected glyph's resolved record and original outer origin.
    pub fn native_glyph_resident_sbix_into(
        &self,
        glyph: u16,
        output: &mut [u8],
    ) -> Result<NativeGlyphResidentRequirements, FontError> {
        let source = Source::inspect(self, glyph)?;
        let requirements = NativeGlyphResidentRequirements {
            sbix_bytes: source.bytes,
            font_bytes: font_size(self, source.bytes)?,
            strike_count: source.strikes,
        };
        if output.len() < source.bytes {
            return Err(FontError::Capacity);
        }
        output[..source.bytes].fill(0);
        source.write(&mut output[..source.bytes]);
        Ok(requirements)
    }
    /// Complete raw directory admission and capacity precede mutation. Original
    /// record/tag/checksum order survives, including duplicate tables; only the
    /// first raw SBIX record receives the selected table's resident replacement.
    pub fn native_glyph_resident_font_into(
        &self,
        glyph: u16,
        output: &mut [u8],
    ) -> Result<NativeGlyphResidentRequirements, FontError> {
        let source = Source::inspect(self, glyph)?;
        let size = font_size(self, source.bytes)?;
        let requirements = NativeGlyphResidentRequirements {
            sbix_bytes: source.bytes,
            font_bytes: size,
            strike_count: source.strikes,
        };
        if output.len() < size {
            return Err(FontError::Capacity);
        }
        output[..size].fill(0);
        let data = self.data();
        let face = self.face_offset() as usize;
        let count = usize::from(self.table_count());
        output[..4].copy_from_slice(&data[face..face + 4]);
        put16(output, 4, count as u16);
        reader::search_parameters(output, count as u16);
        let mut target = align(12 + count * 16)?;
        let mut replaced = false;
        for index in 0..count {
            let source_at = face + 12 + index * 16;
            let target_at = 12 + index * 16;
            let tag = dword(data, source_at);
            let source_offset = dword(data, source_at + 8) as usize;
            let source_length = dword(data, source_at + 12) as usize;
            let resident = !replaced && tag == Tag::new(*b"sbix").0;
            let length = if resident {
                source.bytes
            } else {
                source_length
            };
            put32(output, target_at, tag);
            put32(output, target_at + 8, target as u32);
            put32(output, target_at + 12, length as u32);
            if resident {
                source.write(&mut output[target..target + length]);
                let sum = checksum(&output[target..target + length]);
                put32(output, target_at + 4, sum);
                replaced = true;
            } else {
                put32(output, target_at + 4, dword(data, source_at + 4));
                output[target..target + length]
                    .copy_from_slice(&data[source_offset..source_offset + length]);
            }
            target += align(length)?;
        }
        Ok(requirements)
    }
}
