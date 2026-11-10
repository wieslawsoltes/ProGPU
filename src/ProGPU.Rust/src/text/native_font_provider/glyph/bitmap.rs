//! Native sbix duplicate/strike selection and CBLC1..5/CBDT17..19 lookup.
use super::*;
/// Exact encoded payload and independent sbix origins / CBDT bearings. This
/// does not decode pixels or infer image dimensions from the strike metadata.
#[derive(Clone, Copy, Debug)]
pub struct NativeBitmapGlyphData<'a> {
    pub bytes: &'a [u8],
    pub graphic_type: Tag,
    pub pixels_per_em: u16,
    pub pixels_per_inch: u16,
    pub origin_offset_x: i16,
    pub origin_offset_y: i16,
    pub uses_horizontal_metrics: bool,
    pub bearing_x: i16,
    pub bearing_y: i16,
}
impl SfntFace {
    pub fn native_sbix_glyph(
        &self,
        glyph: u16,
        target_pixels_per_em: f32,
    ) -> Result<NativeBitmapGlyphData<'_>> {
        let glyphs = glyph_count(self, glyph)?;
        let bytes = self
            .table(Tag::new(*b"sbix"))
            .ok_or(FontError::InvalidGlyph)?
            .bytes;
        if bytes.len() < 12 || u16be(bytes, 0) != Some(1) {
            return Err(FontError::InvalidGlyph);
        }
        let strikes = u32be(bytes, 4).unwrap() as usize;
        if strikes == 0 || strikes > (bytes.len() - 8) / 4 {
            return Err(FontError::InvalidFace);
        }
        let mut best = None;
        let mut distance = 0.0;
        for strike in 0..strikes {
            let start = u32be(bytes, 8 + strike * 4).unwrap() as usize;
            let end = if strike + 1 < strikes {
                u32be(bytes, 8 + (strike + 1) * 4).unwrap() as usize
            } else {
                bytes.len()
            };
            if let Some(candidate) = sbix_strike(bytes, start, end, glyphs, glyph) {
                select(&mut best, &mut distance, candidate, target_pixels_per_em);
            }
        }
        best.ok_or(FontError::InvalidGlyph)
    }
    pub fn native_cbdt_glyph(
        &self,
        glyph: u16,
        target_pixels_per_em: f32,
    ) -> Result<NativeBitmapGlyphData<'_>> {
        glyph_count(self, glyph)?;
        let index = self
            .table(Tag::new(*b"CBLC"))
            .ok_or(FontError::InvalidGlyph)?
            .bytes;
        let data = self
            .table(Tag::new(*b"CBDT"))
            .ok_or(FontError::InvalidGlyph)?
            .bytes;
        if index.len() < 8
            || data.len() < 4
            || u16be(index, 0) != Some(3)
            || u16be(data, 0) != Some(3)
        {
            return Err(FontError::InvalidGlyph);
        }
        let strikes = u32be(index, 4).unwrap() as usize;
        if strikes == 0 || strikes > (index.len() - 8) / 48 {
            return Err(FontError::InvalidFace);
        }
        let mut best = None;
        let mut distance = 0.0;
        for strike in 0..strikes {
            let offset = 8 + strike * 48;
            let first = u16be(index, offset + 40).unwrap();
            let last = u16be(index, offset + 42).unwrap();
            if glyph < first || glyph > last {
                continue;
            }
            let ppem = if index[offset + 45] != 0 {
                index[offset + 45]
            } else {
                index[offset + 44]
            };
            if ppem == 0 {
                continue;
            }
            if let Some(candidate) = cbdt_strike(index, data, offset, glyph, u16::from(ppem)) {
                select(&mut best, &mut distance, candidate, target_pixels_per_em);
            }
        }
        best.ok_or(FontError::InvalidGlyph)
    }
}
fn select<'a>(
    best: &mut Option<NativeBitmapGlyphData<'a>>,
    best_distance: &mut f32,
    candidate: NativeBitmapGlyphData<'a>,
    target: f32,
) {
    let distance = (f32::from(candidate.pixels_per_em) - target).abs();
    if best.is_none_or(|old| {
        distance < *best_distance
            || (distance == *best_distance && candidate.pixels_per_em > old.pixels_per_em)
    }) {
        *best = Some(candidate);
        *best_distance = distance;
    }
}
fn sbix_strike(
    bytes: &[u8],
    start: usize,
    end: usize,
    glyphs: u16,
    original: u16,
) -> Option<NativeBitmapGlyphData<'_>> {
    if start >= end || end > bytes.len() {
        return None;
    }
    slice(bytes, start, 4 + (usize::from(glyphs) + 1) * 4)?;
    let pixels_per_em = u16be(bytes, start)?;
    let pixels_per_inch = u16be(bytes, start + 2)?;
    let offsets = start + 4;
    let mut glyph = original;
    let mut origin = [0; 2];
    for depth in 0..=16 {
        if glyph >= glyphs {
            return None;
        }
        let record = offsets.checked_add(usize::from(glyph) * 4)?;
        let first = u32be(bytes, record)? as usize;
        let last = u32be(bytes, record.checked_add(4)?)? as usize;
        if first >= last || first > end - start || last > end - start {
            return None;
        }
        let first = start + first;
        let last = start + last;
        if first < offsets || last > end || last - first < 8 {
            return None;
        }
        if depth == 0 {
            origin = [u16be(bytes, first)? as i16, u16be(bytes, first + 2)? as i16];
        }
        let graphic_type = Tag(u32be(bytes, first + 4)?);
        if graphic_type == Tag::new(*b"dupe") {
            if last - first < 10 {
                return None;
            }
            glyph = u16be(bytes, first + 8)?;
            if glyph == original {
                return None;
            }
            continue;
        }
        if ![Tag::new(*b"png "), Tag::new(*b"jpg "), Tag::new(*b"tiff")].contains(&graphic_type) {
            return None;
        }
        return Some(NativeBitmapGlyphData {
            bytes: &bytes[first + 8..last],
            graphic_type,
            pixels_per_em,
            pixels_per_inch,
            origin_offset_x: origin[0],
            origin_offset_y: origin[1],
            uses_horizontal_metrics: false,
            bearing_x: 0,
            bearing_y: 0,
        });
    }
    None
}
#[derive(Clone, Copy, Default)]
struct Metrics {
    width: u8,
    height: u8,
    x: i8,
    y: i8,
}
impl Metrics {
    fn read(bytes: &[u8], offset: usize) -> Option<Self> {
        let value = slice(bytes, offset, 4)?;
        Some(Self {
            width: value[1],
            height: value[0],
            x: value[2] as i8,
            y: value[3] as i8,
        })
    }
    fn valid(self) -> bool {
        self.width != 0 && self.height != 0
    }
}
struct ImageRange {
    start: u64,
    end: u64,
    metrics: Metrics,
}
fn cbdt_strike<'a>(
    index: &[u8],
    data: &'a [u8],
    strike: usize,
    glyph: u16,
    ppem: u16,
) -> Option<NativeBitmapGlyphData<'a>> {
    let start = u32be(index, strike)? as usize;
    let size = u32be(index, strike + 4)? as usize;
    let count = u32be(index, strike + 8)? as usize;
    if size < 8
        || start > index.len()
        || size > index.len() - start
        || count == 0
        || count > size / 8
    {
        return None;
    }
    let end = start + size;
    for i in 0..count {
        let record = start + i * 8;
        let first = u16be(index, record)?;
        let last = u16be(index, record + 2)?;
        if glyph < first || glyph > last {
            continue;
        }
        let relative = u32be(index, record + 4)? as usize;
        if relative > size || size - relative < 8 {
            return None;
        }
        let table = start + relative;
        let range = image_range(index, table, end, first, last, glyph)?;
        return cbdt_image(
            data,
            range,
            u16be(index, table + 2)?,
            ppem,
            index[strike + 47],
        );
    }
    None
}
fn image_range(
    index: &[u8],
    start: usize,
    limit: usize,
    first: u16,
    last: u16,
    glyph: u16,
) -> Option<ImageRange> {
    if first > last
        || glyph < first
        || glyph > last
        || limit > index.len()
        || start > limit
        || limit - start < 8
    {
        return None;
    }
    let format = u16be(index, start)?;
    let base = u64::from(u32be(index, start + 4)?);
    let slot = usize::from(glyph - first);
    let count = usize::from(last - first) + 1;
    let mut metrics = Metrics::default();
    let (first_byte, last_byte) = match format {
        1 | 3 => {
            let width = if format == 1 { 4 } else { 2 };
            if count + 1 > (limit - start - 8) / width {
                return None;
            }
            let offset = start + 8 + slot * width;
            if format == 1 {
                (
                    u64::from(u32be(index, offset)?),
                    u64::from(u32be(index, offset + 4)?),
                )
            } else {
                (
                    u64::from(u16be(index, offset)?),
                    u64::from(u16be(index, offset + 2)?),
                )
            }
        }
        2 => {
            if limit - start < 20 {
                return None;
            }
            let size = u64::from(u32be(index, start + 8)?);
            if size == 0 {
                return None;
            }
            metrics = Metrics::read(index, start + 12)?;
            let first = slot as u64 * size;
            (first, first + size)
        }
        4 => {
            if limit - start < 12 {
                return None;
            }
            let count = u32be(index, start + 8)?;
            if count == 0 || u64::from(count) + 1 > ((limit - start - 12) / 4) as u64 {
                return None;
            }
            let pairs = start + 12;
            let mut found = None;
            for i in 0..count as usize {
                let pair = pairs + i * 4;
                if u16be(index, pair)? == glyph {
                    found = Some((
                        u64::from(u16be(index, pair + 2)?),
                        u64::from(u16be(index, pair + 6)?),
                    ));
                    break;
                }
            }
            found?
        }
        5 => {
            if limit - start < 24 {
                return None;
            }
            let size = u64::from(u32be(index, start + 8)?);
            let count = u32be(index, start + 20)? as usize;
            if size == 0 || count == 0 || count > (limit - start - 24) / 2 {
                return None;
            }
            let mut found = None;
            for i in 0..count {
                if u16be(index, start + 24 + i * 2)? == glyph {
                    found = Some(i);
                    break;
                }
            }
            metrics = Metrics::read(index, start + 12)?;
            let first = found? as u64 * size;
            (first, first.checked_add(size)?)
        }
        _ => return None,
    };
    if first_byte >= last_byte {
        return None;
    }
    Some(ImageRange {
        start: base.checked_add(first_byte)?,
        end: base.checked_add(last_byte)?,
        metrics,
    })
}
fn cbdt_image(
    bytes: &[u8],
    range: ImageRange,
    format: u16,
    ppem: u16,
    flags: u8,
) -> Option<NativeBitmapGlyphData<'_>> {
    if range.start < 4 || range.start >= range.end || range.end > bytes.len() as u64 {
        return None;
    }
    let start = usize::try_from(range.start).ok()?;
    let end = usize::try_from(range.end).ok()?;
    let (metrics, data, length) = match format {
        17 => {
            if flags & 1 == 0 || end - start < 9 {
                return None;
            }
            (
                Metrics::read(bytes, start)?,
                start + 9,
                u32be(bytes, start + 5)? as usize,
            )
        }
        18 => {
            if end - start < 12 {
                return None;
            }
            (
                Metrics::read(bytes, start)?,
                start + 12,
                u32be(bytes, start + 8)? as usize,
            )
        }
        19 => {
            if !range.metrics.valid() || end - start < 4 {
                return None;
            }
            (range.metrics, start + 4, u32be(bytes, start)? as usize)
        }
        _ => return None,
    };
    if !metrics.valid() || length == 0 || data > end || length > end - data {
        return None;
    }
    Some(NativeBitmapGlyphData {
        bytes: &bytes[data..data + length],
        graphic_type: Tag::new(*b"png "),
        pixels_per_em: ppem,
        pixels_per_inch: 72,
        origin_offset_x: 0,
        origin_offset_y: 0,
        uses_horizontal_metrics: true,
        bearing_x: i16::from(metrics.x),
        bearing_y: i16::from(metrics.y),
    })
}
