//! Immutable owned SFNT/TTC font faces, original metrics and character maps.
//!
//! Direct port of ProGPU's `progpu_native_sfnt.cpp` and standalone-font writer.
//! Simple/composite TrueType decoding preserves source topology and instruction bytes.
//! No font discovery, shaping, hinting, rasterization or GPU work occurs here.
//! Ordinary table lookup preserves the native last-valid-duplicate policy;
//! specialized metadata APIs must independently apply their stricter contracts.

mod bytes;
pub mod cff;
mod composite;
pub mod gvar;
mod item_variation;
mod outline;
mod variations;
pub use composite::{
    CompositeComponent, CompositeGlyph, CompositeRequirements, ExpandedRequirements, GlyphOutline,
    VariedGlyphOutline,
};
pub use item_variation::{DeltaSetIndexMap, ItemVariationStore};
pub use outline::{GlyphKind, GlyphRequirements, OutlinePoint, SimpleGlyph};
use std::sync::Arc;
pub use variations::{VariationAxes, VariationAxis};

/// Exact big-endian four-byte OpenType tag, including spaces and arbitrary bytes.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Tag(pub u32);
impl Tag {
    /// Construct without Unicode conversion or normalization.
    pub const fn new(bytes: [u8; 4]) -> Self {
        Self(u32::from_be_bytes(bytes))
    }
    /// Original four tag bytes.
    pub const fn bytes(self) -> [u8; 4] {
        self.0.to_be_bytes()
    }
}

/// Errors at font parsing, decoding or publication boundaries.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
#[repr(u32)]
pub enum FontError {
    /// The selected face index is outside the container.
    #[error("invalid font argument")]
    InvalidArgument = 1,
    /// Compressed WOFF data requires its separate normalization API.
    #[error("unsupported font container")]
    UnsupportedContainer = 2,
    /// Invalid TTC count or offset inventory.
    #[error("invalid font collection")]
    InvalidCollection = 3,
    /// Missing or invalid face header or standalone inventory.
    #[error("invalid font face")]
    InvalidFace = 4,
    /// The complete declared directory does not fit its owner.
    #[error("truncated font directory")]
    TruncatedDirectory = 5,
    /// Invalid or unavailable glyph record for the selected decoding route.
    #[error("invalid font glyph")]
    InvalidGlyph = 6,
    /// Output cannot be represented or allocated within the supported capacity.
    #[error("font capacity exceeded")]
    Capacity = 7,
}

/// Original directory entry selected by ordinary native table lookup.
#[derive(Clone, Copy, Debug)]
pub struct Table<'a> {
    /// Exact source tag.
    pub tag: Tag,
    /// Original checksum, without recomputation or validation.
    pub checksum: u32,
    /// Borrow tied to the immutable face owner.
    pub bytes: &'a [u8],
}

/// Unscaled original `head` metrics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HeaderMetrics {
    /// Design units per em, without substitution for zero.
    pub units_per_em: u16,
    /// Original signed minX/minY/maxX/maxY.
    pub bounds: [i16; 4],
    /// Original short/long loca selector; other raw values remain observable.
    pub index_to_loc_format: i16,
}

/// Unscaled original `hhea` metrics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HorizontalHeaderMetrics {
    /// Signed ascent.
    pub ascender: i16,
    /// Signed descent.
    pub descender: i16,
    /// Signed line gap.
    pub line_gap: i16,
    /// Maximum original advance.
    pub advance_width_max: u16,
    /// Number of full advance/bearing pairs in hmtx.
    pub number_of_horizontal_metrics: u16,
}

/// Original design-unit glyph advance and side bearing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HorizontalGlyphMetrics {
    /// Last complete advance repeats beyond the full metrics inventory.
    pub advance_width: u16,
    /// Original signed bearing, or the native zero when its slot is unavailable.
    pub left_side_bearing: i16,
}

/// Original glyf record. Empty loca slots succeed with empty bytes and zero fields.
#[derive(Clone, Copy, Debug, Default)]
pub struct GlyphData<'a> {
    /// Signed original contour count; negative counts identify composites.
    pub contour_count: i16,
    /// Original signed minX/minY/maxX/maxY, not reconstructed ink bounds.
    pub bounds: [i16; 4],
    /// Complete original glyph bytes, including instructions.
    pub bytes: &'a [u8],
}

#[derive(Clone, Copy, Debug, Default)]
struct Span {
    offset: usize,
    length: usize,
}
impl Span {
    fn get(self, data: &[u8]) -> &[u8] {
        &data[self.offset..self.offset + self.length]
    }
}

/// One selected face retaining its complete immutable source generation.
///
/// Clones share storage and keep selected tables valid after the original face
/// is dropped. Construction scans directories on the CPU; cached metrics and
/// cmap lookups allocate nothing. This is font data access, not text shaping.
#[derive(Clone, Debug)]
pub struct SfntFace {
    data: Arc<[u8]>,
    face_index: u32,
    face_offset: u32,
    directory: usize,
    table_count: u16,
    metrics: [Span; 4], // head, hhea, hmtx, maxp
    cmap: [Span; 4],    // format 4, 12, 13, 14
    cmap4_ordered: bool,
    symbol: bool,
}

impl SfntFace {
    /// Native SFNT/TTC face count. WOFF/WOFF2 remain separate normalization paths.
    /// Noncollection signatures retain the original generic SFNT admission.
    pub fn face_count(data: &[u8]) -> Result<u32, FontError> {
        if data.len() < 12 {
            return Err(FontError::InvalidFace);
        }
        let signature = &data[..4];
        if signature == b"wOFF" || signature == b"wOF2" {
            return Err(FontError::UnsupportedContainer);
        }
        if signature != b"ttcf" {
            return Ok(1);
        }
        let count = bytes::u32(data, 8).ok_or(FontError::InvalidCollection)?;
        if count == 0 || u64::from(count) > ((data.len() - 12) / 4) as u64 {
            return Err(FontError::InvalidCollection);
        }
        Ok(count)
    }

    /// Select a face and retain its owner. Validates the whole directory before
    /// publication, then selects original available tables and cmap subtables.
    /// Table payload validity remains specific to each query, as in native.
    pub fn new(data: impl Into<Arc<[u8]>>, face_index: u32) -> Result<Self, FontError> {
        let data = data.into();
        if face_index >= Self::face_count(&data)? {
            return Err(FontError::InvalidArgument);
        }
        let face_offset = if &data[..4] == b"ttcf" {
            bytes::u32(&data, 12 + face_index as usize * 4).ok_or(FontError::InvalidCollection)?
        } else {
            0
        };
        let offset = face_offset as usize;
        bytes::bytes(&data, offset, 12).ok_or(FontError::InvalidFace)?;
        let table_count = bytes::u16(&data, offset + 4).ok_or(FontError::InvalidFace)?;
        let directory = offset + 12;
        bytes::bytes(&data, directory, usize::from(table_count) * 16)
            .ok_or(FontError::TruncatedDirectory)?;
        let mut result = Self {
            data,
            face_index,
            face_offset,
            directory,
            table_count,
            metrics: [Span::default(); 4],
            cmap: [Span::default(); 4],
            cmap4_ordered: false,
            symbol: false,
        };
        for (i, tag) in [*b"head", *b"hhea", *b"hmtx", *b"maxp"]
            .into_iter()
            .enumerate()
        {
            if let Some((_, span)) = result.table_span(Tag::new(tag)) {
                result.metrics[i] = span;
            }
        }
        result.select_cmap();
        result.cmap4_ordered = format4_endpoints_are_ordered(result.cmap[0].get(&result.data));
        Ok(result)
    }

    /// Original complete immutable source bytes, including all collection faces.
    pub fn data(&self) -> &[u8] {
        &self.data
    }
    /// Select another face while sharing this exact immutable container owner.
    /// Invalid selection leaves the current face and all borrowed views intact.
    pub fn select_face(&self, face_index: u32) -> Result<Self, FontError> {
        Self::new(self.data.clone(), face_index)
    }
    /// Selected original collection index.
    pub fn face_index(&self) -> u32 {
        self.face_index
    }
    /// Original absolute face-header offset.
    pub fn face_offset(&self) -> u32 {
        self.face_offset
    }
    /// Original directory count, including invalid or duplicate payload records.
    pub fn table_count(&self) -> u16 {
        self.table_count
    }
    /// Whether native symbol-map precedence selected Windows symbol format 4.
    pub fn uses_symbol_character_map(&self) -> bool {
        self.symbol
    }

    fn record(&self, index: usize) -> Option<(Tag, u32, Span)> {
        let offset = self.directory + index * 16;
        let span = Span {
            offset: bytes::u32(&self.data, offset + 8)? as usize,
            length: bytes::u32(&self.data, offset + 12)? as usize,
        };
        bytes::bytes(&self.data, span.offset, span.length)?;
        Some((
            Tag(bytes::u32(&self.data, offset)?),
            bytes::u32(&self.data, offset + 4)?,
            span,
        ))
    }
    fn table_span(&self, tag: Tag) -> Option<(u32, Span)> {
        (0..usize::from(self.table_count)).rev().find_map(|i| {
            let (candidate, checksum, span) = self.record(i)?;
            (candidate == tag).then_some((checksum, span))
        })
    }
    /// Select the last matching directory entry whose complete payload fits.
    /// A later invalid duplicate does not conceal an earlier valid entry.
    pub fn table(&self, tag: Tag) -> Option<Table<'_>> {
        let (checksum, span) = self.table_span(tag)?;
        Some(Table {
            tag,
            checksum,
            bytes: span.get(&self.data),
        })
    }

    fn select_cmap(&mut self) {
        let Some((_, span)) = self.table_span(Tag::new(*b"cmap")) else {
            return;
        };
        let cmap = span.get(&self.data);
        let Some(count) = bytes::u16(cmap, 2) else {
            return;
        };
        let mut symbol = Span::default();
        for i in 0..usize::from(count) {
            let record = 4 + i * 8;
            let Some(entry) = bytes::bytes(cmap, record, 8) else {
                break;
            };
            let platform = bytes::u16(entry, 0).unwrap();
            let encoding = bytes::u16(entry, 2).unwrap();
            let offset = bytes::u32(entry, 4).unwrap() as usize;
            let Some(format) = bytes::u16(cmap, offset) else {
                continue;
            };
            let length = match format {
                4 => bytes::bytes(cmap, offset, 4)
                    .and_then(|header| bytes::u16(header, 2))
                    .map(usize::from),
                12 | 13 => bytes::bytes(cmap, offset, 8)
                    .and_then(|header| bytes::u32(header, 4))
                    .map(|n| n as usize),
                14 => bytes::bytes(cmap, offset, 6)
                    .and_then(|header| bytes::u32(header, 2))
                    .filter(|n| *n >= 10)
                    .map(|n| n as usize),
                _ => None,
            };
            let Some(length) = length.filter(|n| *n > 0) else {
                continue;
            };
            if bytes::bytes(cmap, offset, length).is_none() {
                continue;
            }
            let sub = Span {
                offset: span.offset + offset,
                length,
            };
            let unicode = platform == 0 || (platform == 3 && matches!(encoding, 1 | 10));
            match format {
                12 if unicode => self.cmap[1] = sub,
                13 if unicode => self.cmap[2] = sub,
                4 if unicode => {
                    self.cmap[0] = sub;
                    self.symbol = false;
                }
                4 if platform == 3 && encoding == 0 => symbol = sub,
                14 => self.cmap[3] = sub,
                _ => {}
            }
        }
        if symbol.length != 0 {
            self.cmap[..3].copy_from_slice(&[symbol, Span::default(), Span::default()]);
            self.symbol = true;
        }
    }

    /// Original header metrics; unavailable/truncated data stays absent.
    pub fn header_metrics(&self) -> Option<HeaderMetrics> {
        let table = self.metrics[0].get(&self.data);
        bytes::bytes(table, 0, 52)?;
        Some(HeaderMetrics {
            units_per_em: bytes::u16(table, 18)?,
            bounds: [
                bytes::i16(table, 36)?,
                bytes::i16(table, 38)?,
                bytes::i16(table, 40)?,
                bytes::i16(table, 42)?,
            ],
            index_to_loc_format: bytes::i16(table, 50)?,
        })
    }
    /// Original horizontal header; no scaling, synthetic metrics or line policy.
    pub fn horizontal_header_metrics(&self) -> Option<HorizontalHeaderMetrics> {
        let table = self.metrics[1].get(&self.data);
        bytes::bytes(table, 0, 36)?;
        Some(HorizontalHeaderMetrics {
            ascender: bytes::i16(table, 4)?,
            descender: bytes::i16(table, 6)?,
            line_gap: bytes::i16(table, 8)?,
            advance_width_max: bytes::u16(table, 10)?,
            number_of_horizontal_metrics: bytes::u16(table, 34)?,
        })
    }
    /// Original hmtx lookup, including repeated last advance and absent-bearing zero.
    pub fn horizontal_glyph_metrics(&self, glyph: u16) -> Option<HorizontalGlyphMetrics> {
        let count = self
            .horizontal_header_metrics()?
            .number_of_horizontal_metrics;
        if count == 0 {
            return None;
        }
        let table = self.metrics[2].get(&self.data);
        let advance = usize::from(glyph.min(count - 1)) * 4;
        let bearing = if glyph < count {
            advance + 2
        } else {
            usize::from(count) * 4 + usize::from(glyph - count) * 2
        };
        Some(HorizontalGlyphMetrics {
            advance_width: bytes::u16(table, advance)?,
            left_side_bearing: bytes::i16(table, bearing).unwrap_or(0),
        })
    }
    /// Original maxp glyph count; a present zero remains distinct from absent data.
    pub fn glyph_count(&self) -> Option<u16> {
        bytes::u16(self.metrics[3].get(&self.data), 4)
    }
    /// Original loca/glyf record, preserving empty slots and original instructions.
    pub fn glyph_data(&self, glyph: u16) -> Option<GlyphData<'_>> {
        let header = self.header_metrics()?;
        let loca = self.table(Tag::new(*b"loca"))?.bytes;
        let glyf = self.table(Tag::new(*b"glyf"))?.bytes;
        let (start, end) = match header.index_to_loc_format {
            0 => {
                let offset = usize::from(glyph) * 2;
                (
                    u32::from(bytes::u16(loca, offset)?) * 2,
                    u32::from(bytes::u16(loca, offset + 2)?) * 2,
                )
            }
            1 => {
                let offset = usize::from(glyph) * 4;
                (bytes::u32(loca, offset)?, bytes::u32(loca, offset + 4)?)
            }
            _ => return None,
        };
        let bytes = bytes::bytes(glyf, start as usize, end.checked_sub(start)? as usize)?;
        if bytes.is_empty() {
            return Some(GlyphData::default());
        }
        Some(GlyphData {
            contour_count: bytes::i16(bytes, 0)?,
            bounds: [
                bytes::i16(bytes, 2)?,
                bytes::i16(bytes, 4)?,
                bytes::i16(bytes, 6)?,
                bytes::i16(bytes, 8)?,
            ],
            bytes,
        })
    }

    fn lookup_cmap(&self, code: u32) -> u16 {
        for (index, constant) in [(1, false), (2, true)] {
            if self.cmap[index].length != 0
                && let Some(glyph) = lookup_groups(self.cmap[index].get(&self.data), code, constant)
                && (glyph != 0 || self.cmap[0].length == 0)
            {
                return glyph;
            }
        }
        lookup_format4(self.cmap[0].get(&self.data), code, self.cmap4_ordered).unwrap_or(0)
    }
    /// Original cmap 12/13/4 selection. `None` means no selected base cmap;
    /// `Some(0)` means unmapped. This does not perform shaping or font fallback.
    pub fn glyph_index(&self, code_point: u32) -> Option<u16> {
        if self.cmap[..3].iter().all(|s| s.length == 0) {
            return None;
        }
        let glyph = self.lookup_cmap(code_point);
        if glyph != 0 {
            return Some(glyph);
        }
        Some(if self.symbol && code_point <= 0xff {
            self.lookup_cmap(0xf000 + code_point)
        } else {
            0
        })
    }
    /// Original format-14 nondefault/default UVS mapping. Unsupported or malformed
    /// selectors and zero glyphs remain absent rather than falling back silently.
    pub fn variation_glyph(&self, code: u32, selector: u32) -> Option<u16> {
        let table = self.cmap[3].get(&self.data);
        let count = bytes::u32(table, 6)? as usize;
        if count > (table.len() - 10) / 11 {
            return None;
        }
        let (mut low, mut high) = (0, count);
        while low < high {
            let middle = low + (high - low) / 2;
            let record = 10 + middle * 11;
            match selector.cmp(&bytes::u24(table, record)?) {
                std::cmp::Ordering::Less => high = middle,
                std::cmp::Ordering::Greater => low = middle + 1,
                std::cmp::Ordering::Equal => {
                    let mappings = bytes::u32(table, record + 7)? as usize;
                    if mappings != 0 {
                        let count = bytes::u32(table, mappings)? as usize;
                        if count > (table.len() - mappings - 4) / 5 {
                            return None;
                        }
                        let (mut low, mut high) = (0, count);
                        while low < high {
                            let middle = low + (high - low) / 2;
                            let mapping = mappings + 4 + middle * 5;
                            match code.cmp(&bytes::u24(table, mapping)?) {
                                std::cmp::Ordering::Less => high = middle,
                                std::cmp::Ordering::Greater => low = middle + 1,
                                std::cmp::Ordering::Equal => {
                                    return bytes::u16(table, mapping + 3).filter(|g| *g != 0);
                                }
                            }
                        }
                    }
                    let ranges = bytes::u32(table, record + 3)? as usize;
                    if ranges == 0 {
                        return None;
                    }
                    let count = bytes::u32(table, ranges)? as usize;
                    if count > (table.len() - ranges - 4) / 4 {
                        return None;
                    }
                    for i in 0..count {
                        let start = bytes::u24(table, ranges + 4 + i * 4)?;
                        let end = start + u32::from(table[ranges + 7 + i * 4]);
                        if code < start {
                            return None;
                        }
                        if code <= end {
                            return self.glyph_index(code).filter(|g| *g != 0);
                        }
                    }
                    return None;
                }
            }
        }
        None
    }

    /// Rebuild this selected face as an owned standalone SFNT. Retains the last
    /// valid duplicate of each tag, sorts tags, copies original checksums and
    /// payloads, and uses OTTO when CFF/CFF2 is present. It does not execute fonts
    /// or repair checksums. O(T² + B) time, O(T + B) space, T <= 4096.
    pub fn standalone_font(&self) -> Result<Vec<u8>, FontError> {
        if self.table_count == 0 || self.table_count > 4096 {
            return Err(FontError::InvalidFace);
        }
        let mut records = Vec::new();
        records
            .try_reserve_exact(usize::from(self.table_count))
            .map_err(|_| FontError::Capacity)?;
        for index in 0..usize::from(self.table_count) {
            let Some(record) = self.record(index) else {
                continue;
            };
            let has_later = (index + 1..usize::from(self.table_count))
                .any(|later| self.record(later).is_some_and(|r| r.0 == record.0));
            if !has_later {
                records.push(record);
            }
        }
        if records.is_empty() {
            return Err(FontError::InvalidFace);
        }
        records.sort_unstable_by_key(|r| r.0);
        let mut total = 12 + records.len() * 16;
        for (_, _, span) in &records {
            total = total
                .checked_add(span.length.checked_add(3).ok_or(FontError::InvalidFace)? & !3)
                .ok_or(FontError::InvalidFace)?;
        }
        if total > u32::MAX as usize {
            return Err(FontError::InvalidFace);
        }
        let mut output = Vec::new();
        output
            .try_reserve_exact(total)
            .map_err(|_| FontError::Capacity)?;
        output.resize(total, 0);
        output[..4]
            .copy_from_slice(&self.data[self.face_offset as usize..self.face_offset as usize + 4]);
        if records
            .iter()
            .any(|r| r.0 == Tag::new(*b"CFF ") || r.0 == Tag::new(*b"CFF2"))
        {
            output[..4].copy_from_slice(b"OTTO");
        }
        let count = records.len() as u16;
        let selector = count.ilog2() as u16;
        let power = 1u16 << selector;
        for (offset, value) in [
            (4, count),
            (6, power.wrapping_mul(16)),
            (8, selector),
            (10, count.wrapping_sub(power).wrapping_mul(16)),
        ] {
            output[offset..offset + 2].copy_from_slice(&value.to_be_bytes());
        }
        let mut target = 12 + records.len() * 16;
        for (index, (tag, checksum, span)) in records.into_iter().enumerate() {
            let offset = 12 + index * 16;
            for (i, value) in [tag.0, checksum, target as u32, span.length as u32]
                .into_iter()
                .enumerate()
            {
                output[offset + i * 4..offset + i * 4 + 4].copy_from_slice(&value.to_be_bytes());
            }
            output[target..target + span.length].copy_from_slice(span.get(&self.data));
            target += (span.length + 3) & !3;
        }
        Ok(output)
    }
}

fn lookup_groups(table: &[u8], code: u32, constant: bool) -> Option<u16> {
    let count = bytes::u32(table, 12)? as usize;
    if count > (table.len() - 16) / 12 {
        return None;
    }
    let (mut low, mut high) = (0, count);
    while low < high {
        let middle = low + (high - low) / 2;
        let offset = 16 + middle * 12;
        let start = bytes::u32(table, offset)?;
        let end = bytes::u32(table, offset + 4)?;
        if code < start {
            high = middle;
        } else if code > end {
            low = middle + 1;
        } else {
            let glyph = bytes::u32(table, offset + 8)?;
            let glyph = if constant {
                glyph
            } else {
                glyph.checked_add(code - start)?
            };
            return Some(u16::try_from(glyph).unwrap_or(0));
        }
    }
    Some(0)
}

// A one-time proof on the selected immutable subtable. Duplicate endpoints are
// admitted: lower_bound must still select their first original source slot.
fn format4_endpoints_are_ordered(table: &[u8]) -> bool {
    let Some(count) = bytes::u16(table, 6).map(|n| usize::from(n / 2)) else {
        return false;
    };
    // Short inventories retain the cheaper original sequential route. The
    // search choice changes cost only, never admission or selected source slot.
    if count < 16 {
        return false;
    }
    let Some(ends) = bytes::bytes(table, 14, count * 2) else {
        return false;
    };
    let mut previous = 0;
    for end in ends.chunks_exact(2) {
        let current = u16::from_be_bytes([end[0], end[1]]);
        if current < previous {
            return false;
        }
        previous = current;
    }
    true
}

fn lookup_format4(table: &[u8], code: u32, ordered: bool) -> Option<u16> {
    let code = u16::try_from(code).ok()?;
    bytes::bytes(table, 0, 14)?;
    let count = usize::from(bytes::u16(table, 6)? / 2);
    if count == 0 {
        return None;
    }
    let start = 14 + count * 2 + 2;
    let delta = start + count * 2;
    let range = delta + count * 2;
    bytes::bytes(table, range, count * 2)?;
    let mut index = 0;
    if ordered {
        let mut high = count;
        while index < high {
            let middle = index + (high - index) / 2;
            if bytes::u16(table, 14 + middle * 2)? < code {
                index = middle + 1;
            } else {
                high = middle;
            }
        }
    } else {
        // Unsorted source inventories retain native first-contact behavior;
        // no sorting, skipped slot, or repaired range is admitted.
        while index < count && bytes::u16(table, 14 + index * 2)? < code {
            index += 1;
        }
    }
    if index == count || bytes::u16(table, start + index * 2)? > code {
        return Some(0);
    }
    let delta = bytes::u16(table, delta + index * 2)?;
    let glyph_range = bytes::u16(table, range + index * 2)?;
    if glyph_range == 0 {
        return Some(code.wrapping_add(delta));
    }
    let address = range
        + index * 2
        + usize::from(glyph_range)
        + usize::from(code - bytes::u16(table, start + index * 2)?) * 2;
    let raw = bytes::u16(table, address).unwrap_or(0);
    Some(if raw == 0 { 0 } else { raw.wrapping_add(delta) })
}
