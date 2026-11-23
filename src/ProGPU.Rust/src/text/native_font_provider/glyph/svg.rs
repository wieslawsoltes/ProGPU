//! Native SVG document lookup and exact single-member gzip framing.
use super::*;
/// Actual selected raw XML/gzip bytes. No UTF-8 validation or XML execution is
/// implied by this source query; first matching range and byte bits survive.
#[derive(Clone, Copy, Debug)]
pub struct NativeSvgGlyphDocument<'a> {
    pub bytes: &'a [u8],
    pub first_glyph: u16,
    pub last_glyph: u16,
    pub gzip_compressed: bool,
}
impl NativeSvgGlyphDocument<'_> {
    /// Original gzip header/trailer size query does not inflate or verify the
    /// data checksum; those belong to decode. Plain documents return byte size.
    pub fn decoded_size(self) -> Result<usize> {
        if self.bytes.is_empty() {
            return Err(FontError::InvalidArgument);
        }
        if !self.gzip_compressed {
            return Ok(self.bytes.len());
        }
        let (_, trailer) = gzip_bounds(self.bytes)?;
        Ok(u32le(self.bytes, trailer + 4).unwrap() as usize)
    }
    /// Caller-owned decode, preserving original raw bytes and gzip checksum /
    /// ISIZE / exact-stream gates. On a gzip error the destination may contain
    /// partial output, as in native; no written count is published on failure.
    pub fn decode(self, output: &mut [u8]) -> Result<usize> {
        if self.bytes.is_empty() {
            return Err(FontError::InvalidArgument);
        }
        if !self.gzip_compressed {
            if output.len() < self.bytes.len() {
                return Err(FontError::Capacity);
            }
            output[..self.bytes.len()].copy_from_slice(self.bytes);
            return Ok(self.bytes.len());
        }
        let (start, trailer) = gzip_bounds(self.bytes)?;
        let written = crate::image::inflate_native_deflate(&self.bytes[start..trailer], output)
            .map_err(|error| {
                if matches!(error, crate::Error::InsufficientBuffer) {
                    FontError::Capacity
                } else {
                    FontError::InvalidGlyph
                }
            })?;
        if crc32(&output[..written]) != u32le(self.bytes, trailer).unwrap()
            || written as u32 != u32le(self.bytes, trailer + 4).unwrap()
        {
            return Err(FontError::InvalidGlyph);
        }
        Ok(written)
    }
}
impl SfntFace {
    pub fn native_svg_glyph_document(&self, glyph: u16) -> Result<NativeSvgGlyphDocument<'_>> {
        let bytes = self
            .table(Tag::new(*b"SVG "))
            .ok_or(FontError::InvalidGlyph)?
            .bytes;
        if bytes.len() < 12 || u16be(bytes, 0) != Some(0) {
            return Err(FontError::InvalidGlyph);
        }
        let list = u32be(bytes, 2).unwrap() as usize;
        if list < 10 {
            return Err(FontError::InvalidFace);
        }
        let count = u16be(bytes, list).ok_or(FontError::InvalidFace)?;
        let records = list.checked_add(2).ok_or(FontError::InvalidFace)?;
        if count == 0 || usize::from(count) > (bytes.len() - records) / 12 {
            return Err(FontError::InvalidFace);
        }
        for index in 0..usize::from(count) {
            let offset = records + index * 12;
            let first_glyph = u16be(bytes, offset).unwrap();
            let last_glyph = u16be(bytes, offset + 2).unwrap();
            if glyph < first_glyph || glyph > last_glyph {
                continue;
            }
            let relative = u32be(bytes, offset + 4).unwrap() as usize;
            let length = u32be(bytes, offset + 8).unwrap() as usize;
            if relative == 0
                || length == 0
                || length > 16 * 1024 * 1024
                || relative > bytes.len() - list
            {
                return Err(FontError::InvalidFace);
            }
            let document = slice(bytes, list + relative, length).ok_or(FontError::InvalidFace)?;
            return Ok(NativeSvgGlyphDocument {
                bytes: document,
                first_glyph,
                last_glyph,
                gzip_compressed: document.starts_with(&[0x1f, 0x8b, 8]),
            });
        }
        Err(FontError::InvalidGlyph)
    }
}
fn u16le(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_le_bytes(
        slice(bytes, offset, 2)?.try_into().ok()?,
    ))
}
fn u32le(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        slice(bytes, offset, 4)?.try_into().ok()?,
    ))
}
fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = u32::MAX;
    // Serial polynomial recurrence preserves the original ordered checksum.
    for &byte in bytes {
        crc ^= u32::from(byte);
        for _ in 0..8 {
            crc = (crc >> 1) ^ (0xedb8_8320 & 0u32.wrapping_sub(crc & 1));
        }
    }
    crc ^ u32::MAX
}
fn gzip_bounds(bytes: &[u8]) -> Result<(usize, usize)> {
    if bytes.len() < 18 || bytes[..3] != [0x1f, 0x8b, 8] || bytes[3] & 0xe0 != 0 {
        return Err(FontError::InvalidGlyph);
    }
    let flags = bytes[3];
    let trailer = bytes.len() - 8;
    let mut offset = 10usize;
    if flags & 4 != 0 {
        if offset + 2 > trailer {
            return Err(FontError::InvalidGlyph);
        }
        let length = usize::from(u16le(bytes, offset).unwrap());
        offset += 2;
        if length > trailer - offset {
            return Err(FontError::InvalidGlyph);
        }
        offset += length;
    }
    for flag in [8, 16] {
        if flags & flag != 0 {
            let count = bytes[offset..trailer]
                .iter()
                .position(|v| *v == 0)
                .ok_or(FontError::InvalidGlyph)?;
            offset += count + 1;
        }
    }
    if flags & 2 != 0 {
        if offset + 2 > trailer || crc32(&bytes[..offset]) as u16 != u16le(bytes, offset).unwrap() {
            return Err(FontError::InvalidGlyph);
        }
        offset += 2;
    }
    if offset >= trailer {
        return Err(FontError::InvalidGlyph);
    }
    Ok((offset, trailer))
}
