//! Original SVG document inventory, first matching range and bounded gzip/UTF-8.
use super::*;
use crate::font::bytes;
use std::io::Read;
const MAX_DOCUMENT: usize = 16 * 1024 * 1024;
impl ManagedFont {
    /// Read the original first matching SVG glyph document, including bounded
    /// gzip expansion and strict UTF-8. This preserves the original XML text; it
    /// does not render SVG or manufacture geometry, brush or shaping metadata.
    /// Missing/unsupported/invalid document payloads return None. A source read
    /// that the managed implementation performs outside its guards remains an
    /// error; this is independent of the later XML/path parser's admission.
    pub fn svg_glyph_document(&self, glyph: u16) -> Result<Option<Arc<str>>, FontError> {
        let Some(table) = self.source().table(Tag::new(*b"SVG ")) else {
            return Ok(None);
        };
        let data = table.bytes;
        if data.len() < 12 || bytes::u16(data, 0) != Some(0) {
            return Ok(None);
        }
        let list = bytes::u32(data, 2).ok_or(FontError::InvalidFace)?;
        if list > i32::MAX as u32 {
            return Ok(None);
        }
        // Preserve the original signed source expression, including overflow
        // reaching its subsequent throwing read instead of inventing no table.
        let list = list as i32;
        if list < 10 || list.wrapping_add(2) > data.len() as i32 {
            return Ok(None);
        }
        let list = list as usize;
        let count = bytes::u16(data, list).ok_or(FontError::InvalidFace)?;
        if count == 0 || list as u64 + 2 + u64::from(count) * 12 > data.len() as u64 {
            return Ok(None);
        }
        for index in 0..usize::from(count) {
            let at = list + 2 + index * 12;
            let first = bytes::u16(data, at).ok_or(FontError::InvalidFace)?;
            let last = bytes::u16(data, at + 2).ok_or(FontError::InvalidFace)?;
            if glyph < first || glyph > last {
                continue;
            }
            let offset = u64::from(bytes::u32(data, at + 4).ok_or(FontError::InvalidFace)?);
            let length = u64::from(bytes::u32(data, at + 8).ok_or(FontError::InvalidFace)?);
            let start = list as u64 + offset;
            if offset == 0
                || length == 0
                || length > MAX_DOCUMENT as u64
                || start < list as u64
                || start + length > data.len() as u64
            {
                return Ok(None);
            }
            let encoded = &data[start as usize..(start + length) as usize];
            if encoded.starts_with(&[0x1f, 0x8b, 0x08]) {
                let Some(decoded) = decompress(encoded)? else {
                    return Ok(None);
                };
                return Ok(std::str::from_utf8(&decoded).ok().map(Arc::from));
            }
            return Ok(std::str::from_utf8(encoded).ok().map(Arc::from));
        }
        Ok(None)
    }
}
fn decompress(encoded: &[u8]) -> Result<Option<Vec<u8>>, FontError> {
    let mut gzip = flate2::read::MultiGzDecoder::new(encoded);
    let mut output = Vec::new();
    let mut buffer = [0; 8192];
    loop {
        let count = match gzip.read(&mut buffer) {
            Ok(count) => count,
            Err(_) => return Ok(None),
        };
        if count == 0 {
            break;
        }
        if output.len() + count > MAX_DOCUMENT {
            return Ok(None);
        }
        output.try_reserve(count).map_err(|_| FontError::Capacity)?;
        output.extend_from_slice(&buffer[..count]);
    }
    Ok((!output.is_empty()).then_some(output))
}
