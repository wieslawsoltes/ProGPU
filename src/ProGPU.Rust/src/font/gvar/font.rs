//! Original gvar table/glyph views and complete tuple-header preflight.
use crate::font::{FontError, SfntFace, Span, Tag, bytes, item_variation};

/// Original gvar header, without inventing additional version/flag admission.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Header {
    /// Required original axis inventory count.
    pub axis_count: u16,
    /// Original shared peak-tuple count.
    pub shared_tuple_count: u16,
    /// Original glyph count matching maxp.
    pub glyph_count: u16,
    /// Whether the offsets use unscaled u32 rather than doubled u16 values.
    pub uses_long_offsets: bool,
}
/// Owned original gvar generation. Opening validates directory extents;
/// individual glyph offset pairs and tuple headers remain separately checked.
#[derive(Clone, Debug)]
pub struct Font {
    source: SfntFace,
    table: Span,
    header: Header,
    shared: usize,
    glyphs: usize,
}
/// Original selected glyph's bounded bytes and tuple inventory.
#[derive(Clone, Copy, Debug, Default)]
pub struct GlyphData<'a> {
    /// Exact selected glyph bytes (empty for an absent glyph record).
    pub bytes: &'a [u8],
    /// Original low twelve-bit tuple count.
    pub tuple_count: u16,
    /// Original serialized payload offset.
    pub serialized_data_offset: u16,
    /// Whether a shared packed point list precedes the tuple payloads.
    pub has_shared_point_numbers: bool,
}
/// Original tuple descriptor retaining all raw flag bits.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TupleHeader {
    /// Offset into the generation's flat start/peak/end coordinate inventory.
    pub region_coordinate_offset: u32,
    /// Declared tuple payload size, without inferred packed-data length.
    pub serialized_data_size: u16,
    /// Original embedded/intermediate/private/index bits.
    pub flags: u16,
}
/// Owned fully preflighted header/region generation, retaining its source.
#[derive(Clone, Debug)]
pub struct TupleHeaders {
    source: Font,
    glyph: u16,
    headers: Vec<TupleHeader>,
    coordinates: Vec<i16>,
}
impl TupleHeaders {
    /// Exact source owner.
    pub fn source(&self) -> &Font {
        &self.source
    }
    /// Selected original glyph index.
    pub fn glyph_index(&self) -> u16 {
        self.glyph
    }
    /// Complete original descriptor inventory.
    pub fn headers(&self) -> &[TupleHeader] {
        &self.headers
    }
    /// Flat start/peak/end regions in original tuple order.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
}
impl SfntFace {
    /// Open the selected gvar generation. Missing tables remain `None`; an
    /// invalid present table fails. All declared axes/glyph counts must agree.
    pub fn gvar(&self) -> Result<Option<Font>, FontError> {
        let Some((_, table)) = self.table_span(Tag::new(*b"gvar")) else {
            return Ok(None);
        };
        let data = table.get(&self.data);
        bytes::bytes(data, 0, 20).ok_or(FontError::InvalidFace)?;
        let axes = bytes::u16(data, 4).unwrap();
        let shared_count = bytes::u16(data, 6).unwrap();
        let shared = bytes::u32(data, 8).unwrap() as usize;
        let glyph_count = bytes::u16(data, 12).unwrap();
        let long = bytes::u16(data, 14).unwrap() & 1 != 0;
        let glyphs = bytes::u32(data, 16).unwrap() as usize;
        if self.glyph_count() != Some(glyph_count)
            || self
                .variation_axis_count()
                .map_err(|_| FontError::InvalidFace)?
                != axes
        {
            return Err(FontError::InvalidFace);
        }
        bytes::bytes(
            data,
            20,
            (usize::from(glyph_count) + 1) * if long { 4 } else { 2 },
        )
        .ok_or(FontError::InvalidFace)?;
        if shared_count > 0 && axes == 0 {
            return Err(FontError::InvalidFace);
        }
        let size = usize::from(shared_count)
            .checked_mul(usize::from(axes))
            .and_then(|n| n.checked_mul(2))
            .ok_or(FontError::InvalidFace)?;
        bytes::bytes(data, shared, size).ok_or(FontError::InvalidFace)?;
        if glyphs > data.len() {
            return Err(FontError::InvalidFace);
        }
        Ok(Some(Font {
            source: self.clone(),
            table,
            header: Header {
                axis_count: axes,
                shared_tuple_count: shared_count,
                glyph_count,
                uses_long_offsets: long,
            },
            shared,
            glyphs,
        }))
    }
}
impl Font {
    /// Exact selected source font.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original bounded gvar bytes.
    pub fn data(&self) -> &[u8] {
        self.table.get(&self.source.data)
    }
    /// Original admitted header fields.
    pub fn header(&self) -> Header {
        self.header
    }
    /// Query only the selected original offset pair and bounded record header.
    pub fn glyph_data(&self, glyph: u16) -> Result<GlyphData<'_>, FontError> {
        if glyph >= self.header.glyph_count {
            return Err(FontError::InvalidArgument);
        }
        let start = self.glyph_offset(glyph)?;
        let end = self.glyph_offset(glyph + 1)?;
        if end < start {
            return Err(FontError::InvalidGlyph);
        }
        if start == end {
            return Ok(GlyphData::default());
        }
        let data = &self.data()[start..end];
        bytes::bytes(data, 0, 4).ok_or(FontError::InvalidGlyph)?;
        let flags = bytes::u16(data, 0).unwrap();
        let offset = bytes::u16(data, 2).unwrap();
        if usize::from(offset) > data.len() {
            return Err(FontError::InvalidGlyph);
        }
        Ok(GlyphData {
            bytes: data,
            tuple_count: flags & 0x0fff,
            serialized_data_offset: offset,
            has_shared_point_numbers: flags & 0x8000 != 0,
        })
    }
    fn glyph_offset(&self, glyph: u16) -> Result<usize, FontError> {
        let stride = if self.header.uses_long_offsets { 4 } else { 2 };
        let offset = 20 + usize::from(glyph) * stride;
        let relative = if self.header.uses_long_offsets {
            bytes::u32(self.data(), offset).unwrap() as usize
        } else {
            usize::from(bytes::u16(self.data(), offset).unwrap()) * 2
        };
        if relative > self.data().len() - self.glyphs {
            return Err(FontError::InvalidGlyph);
        }
        Ok(self.glyphs + relative)
    }
    /// Decode one original shared peak tuple after complete output-capacity
    /// validation. Extra output slots remain untouched.
    pub fn shared_tuple_into(&self, index: u16, output: &mut [i16]) -> Result<usize, FontError> {
        if index >= self.header.shared_tuple_count {
            return Err(FontError::InvalidArgument);
        }
        let count = usize::from(self.header.axis_count);
        if output.len() < count {
            return Err(FontError::Capacity);
        }
        let offset = self.shared + usize::from(index) * count * 2;
        for (axis, value) in output[..count].iter_mut().enumerate() {
            *value = bytes::i16(self.data(), offset + axis * 2).unwrap();
        }
        Ok(count)
    }
    /// Preflight every tuple header and serialized size before returning the
    /// original tuple count and flat coordinate count. Payload decoding is separate.
    pub fn tuple_requirements(&self, glyph: u16) -> Result<(u16, u32), FontError> {
        let view = self.glyph_data(glyph)?;
        if view.bytes.is_empty() {
            return Ok((0, 0));
        }
        let coordinates = u32::from(view.tuple_count) * u32::from(self.header.axis_count) * 3;
        let mut cursor = 4;
        let mut serialized = 0usize;
        let tuple_bytes = usize::from(self.header.axis_count) * 2;
        for _ in 0..view.tuple_count {
            bytes::bytes(view.bytes, cursor, 4).ok_or(FontError::InvalidGlyph)?;
            let size = bytes::u16(view.bytes, cursor).unwrap();
            let flags = bytes::u16(view.bytes, cursor + 2).unwrap();
            cursor += 4;
            if flags & 0x8000 != 0 {
                bytes::bytes(view.bytes, cursor, tuple_bytes).ok_or(FontError::InvalidGlyph)?;
                cursor += tuple_bytes;
            } else if flags & 0x0fff >= self.header.shared_tuple_count {
                return Err(FontError::InvalidGlyph);
            }
            if flags & 0x4000 != 0 {
                bytes::bytes(view.bytes, cursor, tuple_bytes * 2).ok_or(FontError::InvalidGlyph)?;
                cursor += tuple_bytes * 2;
            }
            serialized = serialized
                .checked_add(usize::from(size))
                .ok_or(FontError::InvalidGlyph)?;
        }
        let payload = usize::from(view.serialized_data_offset);
        if cursor > payload || serialized > view.bytes.len() - payload {
            return Err(FontError::InvalidGlyph);
        }
        Ok((view.tuple_count, coordinates))
    }
    /// Publish source-owned original tuple descriptors and start/peak/end regions
    /// after complete preflight and fallible allocation. No packed deltas execute.
    pub fn tuple_headers(&self, glyph: u16) -> Result<TupleHeaders, FontError> {
        let (count, coordinates) = self.tuple_requirements(glyph)?;
        let mut headers = Vec::new();
        headers
            .try_reserve_exact(usize::from(count))
            .map_err(|_| FontError::Capacity)?;
        let mut regions = Vec::new();
        regions
            .try_reserve_exact(coordinates as usize)
            .map_err(|_| FontError::Capacity)?;
        regions.resize(coordinates as usize, 0);
        let view = self.glyph_data(glyph)?;
        let axes = usize::from(self.header.axis_count);
        let tuple_bytes = axes * 2;
        let mut cursor = 4;
        for index in 0..usize::from(count) {
            let size = bytes::u16(view.bytes, cursor).unwrap();
            let flags = bytes::u16(view.bytes, cursor + 2).unwrap();
            cursor += 4;
            let base = index * axes * 3;
            let r = &mut regions[base..base + axes * 3];
            if flags & 0x8000 != 0 {
                for axis in 0..axes {
                    r[axes + axis] = bytes::i16(view.bytes, cursor + axis * 2).unwrap();
                }
                cursor += tuple_bytes;
            } else {
                self.shared_tuple_into(flags & 0x0fff, &mut r[axes..axes * 2])?;
            }
            if flags & 0x4000 != 0 {
                for axis in 0..axes {
                    r[axis] = bytes::i16(view.bytes, cursor + axis * 2).unwrap();
                    r[axes * 2 + axis] =
                        bytes::i16(view.bytes, cursor + tuple_bytes + axis * 2).unwrap();
                }
                cursor += tuple_bytes * 2;
            } else {
                for axis in 0..axes {
                    r[axis] = r[axes + axis].min(0);
                    r[axes * 2 + axis] = r[axes + axis].max(0);
                }
            }
            headers.push(TupleHeader {
                region_coordinate_offset: base as u32,
                serialized_data_size: size,
                flags,
            });
        }
        Ok(TupleHeaders {
            source: self.clone(),
            glyph,
            headers,
            coordinates: regions,
        })
    }
}
/// Original tuple scalar. Unlike item-store queries, missing normalized axes
/// stop multiplication at the shorter inventory. Non-triplet region lengths
/// return zero; empty matched inventories retain one.
pub fn tuple_scalar(normalized: &[i16], region: &[i16]) -> f32 {
    if !region.len().is_multiple_of(3) {
        return 0.0;
    }
    let axes = region.len() / 3;
    let mut result = 1.0;
    for axis in 0..normalized.len().min(axes) {
        const SCALE: f32 = 1.0 / 16384.0;
        result *= item_variation::axis_scalar(
            f32::from(normalized[axis]) * SCALE,
            f32::from(region[axis]) * SCALE,
            f32::from(region[axes + axis]) * SCALE,
            f32::from(region[axes * 2 + axis]) * SCALE,
        );
        if result == 0.0 {
            break;
        }
    }
    result
}
