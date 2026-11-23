//! Original SfntFontFace.TryGetGlyphIndexFromCmap, independently owned metadata.
use super::{FontError, Span, bytes, format4_endpoints_are_ordered, lookup_format4};
use std::sync::Arc;
/// Compact source cmap ownership used before loading a complete font face.
/// Microsoft symbol precedence is preserved; full-font PUA remapping is absent.
#[derive(Clone, Debug)]
pub struct MetadataCharacterMap {
    data: Arc<[u8]>,
    selected: [Span; 3],
    ordered: bool,
}
impl MetadataCharacterMap {
    pub fn new(data: impl Into<Arc<[u8]>>) -> Result<Self, FontError> {
        let data = data.into();
        if data.len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let mut selected = [Span::default(); 3];
        let mut symbol = Span::default();
        for index in 0..usize::from(bytes::u16(&data, 2).unwrap_or(0)) {
            let Some(record) = bytes::bytes(&data, 4 + index * 8, 8) else {
                break;
            };
            let platform = bytes::u16(record, 0).unwrap();
            let encoding = bytes::u16(record, 2).unwrap();
            let offset = bytes::u32(record, 4).unwrap() as usize;
            let Some(format) = bytes::u16(&data, offset) else {
                continue;
            };
            let length = match format {
                4 => bytes::u16(&data, offset + 2).map(usize::from),
                12 | 13 => bytes::u32(&data, offset + 4).map(|v| v as usize),
                _ => None,
            };
            let Some(length) = length.filter(|v| *v != 0) else {
                continue;
            };
            if bytes::bytes(&data, offset, length).is_none() {
                continue;
            }
            let span = Span { offset, length };
            let unicode = platform == 0 || (platform == 3 && matches!(encoding, 1 | 10));
            match format {
                4 if unicode => selected[0] = span,
                12 if unicode => selected[1] = span,
                13 if unicode => selected[2] = span,
                4 if platform == 3 && encoding == 0 => symbol = span,
                _ => {}
            }
        }
        if symbol.length != 0 {
            selected = [symbol, Span::default(), Span::default()];
        }
        let ordered = format4_endpoints_are_ordered(selected[0].get(&data));
        Ok(Self {
            data,
            selected,
            ordered,
        })
    }
    pub fn data(&self) -> &[u8] {
        &self.data
    }
    pub fn glyph_index(&self, codepoint: u32) -> Result<u16, FontError> {
        for (index, constant) in [(1, false), (2, true)] {
            if self.selected[index].length != 0 {
                if let Some(glyph) = super::managed_cmap::groups(
                    self.selected[index].get(&self.data),
                    codepoint,
                    constant,
                )? {
                    if glyph != 0 || self.selected[0].length == 0 {
                        return Ok(glyph);
                    }
                }
            }
        }
        Ok(lookup_format4(self.selected[0].get(&self.data), codepoint, self.ordered).unwrap_or(0))
    }
}
