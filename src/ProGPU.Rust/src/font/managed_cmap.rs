//! Original SfntFontFace nominal mapping and TtfFont variation-selector policy.
use super::{FontError, SfntFace, Span, Tag, bytes, lookup_format4};

/// Immutable current managed character-map policy over one retained source face.
/// Nominal mapping and variation-selector support remain distinct from shaping.
/// Native `SfntFace` mapping keeps its separate original policy.
#[derive(Clone, Debug)]
pub struct ManagedCharacterMap {
    source: SfntFace,
    legacy_page: u16,
    cmap: Span,
}
impl ManagedCharacterMap {
    /// Retain a selected face and its original version-zero OS/2 font page.
    /// The managed source's signed byte-address domain is preserved.
    pub fn new(source: SfntFace) -> Result<Self, FontError> {
        if source.data().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let legacy_page = source.table(Tag::new(*b"OS/2")).map_or(0, |t| {
            if t.bytes.len() >= 64 && bytes::u16(t.bytes, 0) == Some(0) {
                bytes::u16(t.bytes, 62).unwrap() & 0xff00
            } else {
                0
            }
        });
        let cmap = source
            .table_span(Tag::new(*b"cmap"))
            .map_or(Span::default(), |(_, span)| span);
        Ok(Self {
            source,
            legacy_page,
            cmap,
        })
    }
    /// Actual immutable source generation retained by this mapping.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original Microsoft symbol-subtable precedence.
    pub fn uses_symbol_character_map(&self) -> bool {
        self.source.symbol
    }
    fn selected(&self, code: u32) -> Result<u16, FontError> {
        for (index, constant) in [(1, false), (2, true)] {
            if self.source.cmap[index].length != 0
                && let Some(glyph) = groups(
                    self.source.cmap[index].get(self.source.data()),
                    code,
                    constant,
                )?
                && (glyph != 0 || self.source.cmap[0].length == 0)
            {
                return Ok(glyph);
            }
        }
        Ok(lookup_format4(
            self.source.cmap[0].get(self.source.data()),
            code,
            self.source.cmap4_ordered,
        )
        .unwrap_or(0))
    }
    /// Original selected cmap 12/13/4 result and exact symbol-page remapping.
    /// None means no base cmap; Some(0) means unmapped. Original checked group
    /// count overflow remains an error, while ordinary malformed reads miss.
    pub fn glyph_index(&self, code: u32) -> Result<Option<u16>, FontError> {
        if self.source.cmap[..3].iter().all(|s| s.length == 0) {
            return Ok(None);
        }
        let value = self.selected(code)?;
        if value != 0 || !self.source.symbol {
            return Ok(Some(value));
        }
        let remapped = match self.legacy_page {
            0xb200 => legacy(include_bytes!("data/managed-symbol-simplified.bin"), code),
            0xb300 => legacy(include_bytes!("data/managed-symbol-traditional.bin"), code),
            0 if code <= 0xff => Some(0xf000 + code),
            _ => None,
        };
        Ok(Some(match remapped {
            Some(c) => self.selected(c)?,
            None => 0,
        }))
    }
    /// Original ordered format-14 scan, using whole cmap bounds and the first
    /// matching selector. Some(0) preserves an explicit nondefault zero glyph.
    /// Unsupported sequences stay None; default sequences require nonzero nominal
    /// coverage. Declared format-14 length is not a managed admission gate.
    pub fn variation_glyph(&self, code: u32, selector: u32) -> Result<Option<u16>, FontError> {
        self.variation_with(code, selector, || Ok(self.glyph_index(code)?.unwrap_or(0)))
    }
    pub(super) fn variation_with(
        &self,
        code: u32,
        selector: u32,
        nominal: impl FnOnce() -> Result<u16, FontError>,
    ) -> Result<Option<u16>, FontError> {
        let record = variation(self.cmap.get(self.source.data()), code, selector);
        match record {
            Some(Some(g)) => Ok(Some(g)),
            Some(None) => Ok(Some(nominal()?).filter(|g| *g != 0)),
            None => Ok(None),
        }
    }
}
fn legacy(data: &[u8], code: u32) -> Option<u32> {
    let (mut low, mut high) = (0, data.len() / 4);
    while low < high {
        let middle = low + (high - low) / 2;
        let candidate = u32::from(bytes::u16(data, middle * 4)?);
        match candidate.cmp(&code) {
            std::cmp::Ordering::Less => low = middle + 1,
            std::cmp::Ordering::Greater => high = middle,
            std::cmp::Ordering::Equal => return bytes::u16(data, middle * 4 + 2).map(u32::from),
        }
    }
    None
}
fn read(data: &[u8], at: i32, count: usize) -> Option<&[u8]> {
    bytes::bytes(data, usize::try_from(at).ok()?, count)
}
fn groups(data: &[u8], code: u32, constant: bool) -> Result<Option<u16>, FontError> {
    if data.len() < 16 {
        return Ok(None);
    }
    let count = bytes::u32(data, 12).unwrap();
    if constant && u64::from(count) > ((data.len() - 16) / 12) as u64 {
        return Ok(None);
    }
    let mut high = i32::try_from(count).map_err(|_| FontError::InvalidFace)? - 1;
    let mut low = 0i32;
    while low <= high {
        let middle = low + (high - low) / 2;
        let at = 16i32.wrapping_add(middle.wrapping_mul(12));
        let Some(record) = read(data, at, 12) else {
            return Ok(None);
        };
        let start = bytes::u32(record, 0).unwrap();
        let end = bytes::u32(record, 4).unwrap();
        if code >= start && code <= end {
            let first = bytes::u32(record, 8).unwrap();
            let value = if constant {
                first
            } else {
                first.wrapping_add(code - start)
            };
            return Ok(Some(u16::try_from(value).unwrap_or(0)));
        }
        if code < start {
            high = middle - 1;
        } else {
            low = middle + 1;
        }
    }
    Ok(Some(0))
}
// Outer None: unsupported; Some(None): use nominal; Some(Some(g)): explicit g.
fn variation(data: &[u8], code: u32, selector: u32) -> Option<Option<u16>> {
    let count = bytes::u16(data, 2)?;
    for i in 0..i32::from(count) {
        let Some(record) = read(data, 4 + i * 8, 8) else {
            break;
        };
        let offset = bytes::u32(record, 4).unwrap();
        let Ok(offset) = i32::try_from(offset) else {
            continue;
        };
        let Some(header) = read(data, offset, 10) else {
            continue;
        };
        if bytes::u16(header, 0) != Some(14) {
            continue;
        }
        let records = bytes::u32(header, 6).unwrap();
        if records > i32::MAX as u32 / 11 {
            return None;
        }
        for n in 0..records as i32 {
            let record = read(data, offset.wrapping_add(10).wrapping_add(n * 11), 11)?;
            if bytes::u24(record, 0)? != selector {
                continue;
            }
            let mappings = bytes::u32(record, 7)?;
            if mappings != 0 && mappings <= i32::MAX as u32 {
                let at = offset.wrapping_add(mappings as i32);
                let count = bytes::u32(read(data, at, 4)?, 0)?;
                let mut high = if count > i32::MAX as u32 {
                    i32::MAX
                } else {
                    count as i32 - 1
                };
                let mut low = 0i32;
                while low <= high {
                    let middle = low.wrapping_add(high.wrapping_sub(low) >> 1);
                    let entry = read(
                        data,
                        at.wrapping_add(4).wrapping_add(middle.wrapping_mul(5)),
                        5,
                    )?;
                    let candidate = bytes::u24(entry, 0)?;
                    if candidate == code {
                        return Some(Some(bytes::u16(entry, 3)?));
                    }
                    if candidate < code {
                        low = middle.wrapping_add(1);
                    } else {
                        high = middle.wrapping_sub(1);
                    }
                }
            }
            let ranges = bytes::u32(record, 3)?;
            if ranges == 0 || ranges > i32::MAX as u32 {
                return None;
            }
            let at = offset.wrapping_add(ranges as i32);
            let count = bytes::u32(read(data, at, 4)?, 0)?;
            for n in 0..count {
                let entry = read(
                    data,
                    at.wrapping_add(4).wrapping_add((n as i32).wrapping_mul(4)),
                    4,
                )?;
                let start = bytes::u24(entry, 0)?;
                if code < start {
                    break;
                }
                if code <= start + u32::from(entry[3]) {
                    return Some(None);
                }
            }
            return None;
        }
    }
    None
}
