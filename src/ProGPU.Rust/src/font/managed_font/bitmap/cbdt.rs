//! Original CBLC index formats 1..5 and CBDT image formats 17..19.
use super::*;
#[derive(Clone, Copy, Default)]
struct Metrics {
    size: [u8; 2],
    bearing: [i16; 2],
}
impl Metrics {
    fn read(data: &[u8], at: usize) -> Option<Self> {
        let data = bytes::bytes(data, at, 4)?;
        Some(Self {
            size: [data[1], data[0]],
            bearing: [i16::from(data[2] as i8), i16::from(data[3] as i8)],
        })
    }
    fn valid(self) -> bool {
        !self.size.contains(&0)
    }
}
pub(super) fn select(cblc: &[u8], cbdt: &[u8], glyph: u16, target: f32) -> Option<BitmapRecord> {
    if cblc.len() < 8 || cbdt.len() < 4 || bytes::u16(cblc, 0)? != 3 || bytes::u16(cbdt, 0)? != 3 {
        return None;
    }
    let count = bytes::u32(cblc, 4)?;
    if count == 0 || count > i32::MAX as u32 || 8 + u64::from(count) * 48 > cblc.len() as u64 {
        return None;
    }
    let mut best = None;
    let mut distance = f32::MAX;
    for index in 0..count as usize {
        let at = 8 + index * 48;
        if glyph < bytes::u16(cblc, at + 40)? || glyph > bytes::u16(cblc, at + 42)? {
            continue;
        }
        let ppem = u16::from(if cblc[at + 45] != 0 {
            cblc[at + 45]
        } else {
            cblc[at + 44]
        });
        if ppem == 0 {
            continue;
        }
        if let Some(candidate) = strike(cblc, cbdt, at, glyph, ppem) {
            pick(&mut best, &mut distance, candidate, target);
        }
    }
    best
}
fn strike(cblc: &[u8], cbdt: &[u8], at: usize, glyph: u16, ppem: u16) -> Option<BitmapRecord> {
    let list = bytes::u32(cblc, at)?;
    let size = bytes::u32(cblc, at + 4)?;
    let count = bytes::u32(cblc, at + 8)?;
    if list > i32::MAX as u32 || size > i32::MAX as u32 || count == 0 || count > i32::MAX as u32 {
        return None;
    }
    let end = u64::from(list) + u64::from(size);
    if size < 8 || end > cblc.len() as u64 || u64::from(count) * 8 > u64::from(size) {
        return None;
    }
    let list = list as usize;
    let end = end as usize;
    for index in 0..count as usize {
        let record = list + index * 8;
        let first = bytes::u16(cblc, record)?;
        let last = bytes::u16(cblc, record + 2)?;
        if glyph < first || glyph > last {
            continue;
        }
        let subtable = list as u64 + u64::from(bytes::u32(cblc, record + 4)?);
        if subtable < list as u64 || subtable + 8 > end as u64 {
            return None;
        }
        let subtable = subtable as usize;
        let format = bytes::u16(cblc, subtable + 2)?;
        // First matching subtable is authoritative, including its rejection.
        let (range, metrics) = image_range(cblc, subtable, end, first, last, glyph)?;
        return read_image(cbdt, range, format, ppem, cblc[at + 47], metrics);
    }
    None
}
fn image_range(
    data: &[u8],
    at: usize,
    limit: usize,
    first: u16,
    last: u16,
    glyph: u16,
) -> Option<(Range<u64>, Metrics)> {
    let format = bytes::u16(data, at)?;
    let image = u64::from(bytes::u32(data, at + 4)?);
    let offset = usize::from(glyph - first);
    let count = u64::from(last) - u64::from(first) + 1;
    let mut metrics = Metrics::default();
    let (start, end) = match format {
        1 => {
            if at as u64 + 8 + (count + 1) * 4 > limit as u64 {
                return None;
            }
            (
                u64::from(bytes::u32(data, at + 8 + offset * 4)?),
                u64::from(bytes::u32(data, at + 12 + offset * 4)?),
            )
        }
        2 => {
            if at + 20 > limit {
                return None;
            }
            let size = u64::from(bytes::u32(data, at + 8)?);
            if size == 0 {
                return None;
            }
            metrics = Metrics::read(data, at + 12)?;
            (offset as u64 * size, (offset as u64 + 1) * size)
        }
        3 => {
            if at as u64 + 8 + (count + 1) * 2 > limit as u64 {
                return None;
            }
            (
                u64::from(bytes::u16(data, at + 8 + offset * 2)?),
                u64::from(bytes::u16(data, at + 10 + offset * 2)?),
            )
        }
        4 => {
            if at + 12 > limit {
                return None;
            }
            let count = bytes::u32(data, at + 8)?;
            if count == 0
                || count > i32::MAX as u32
                || at as u64 + 12 + (u64::from(count) + 1) * 4 > limit as u64
            {
                return None;
            }
            let mut selected = None;
            for index in 0..count as usize {
                let pair = at + 12 + index * 4;
                if bytes::u16(data, pair)? == glyph {
                    selected = Some((
                        u64::from(bytes::u16(data, pair + 2)?),
                        u64::from(bytes::u16(data, pair + 6)?),
                    ));
                    break;
                }
            }
            selected?
        }
        5 => {
            if at + 24 > limit {
                return None;
            }
            let size = u64::from(bytes::u32(data, at + 8)?);
            let count = bytes::u32(data, at + 20)?;
            if size == 0
                || count == 0
                || count > i32::MAX as u32
                || at as u64 + 24 + u64::from(count) * 2 > limit as u64
            {
                return None;
            }
            let mut selected = None;
            for index in 0..count as usize {
                if bytes::u16(data, at + 24 + index * 2)? == glyph {
                    selected = Some(index);
                    break;
                }
            }
            let index = selected? as u64;
            metrics = Metrics::read(data, at + 12)?;
            (index * size, (index + 1) * size)
        }
        _ => return None,
    };
    if start >= end {
        return None;
    }
    Some((image.checked_add(start)?..image.checked_add(end)?, metrics))
}
fn read_image(
    data: &[u8],
    range: Range<u64>,
    format: u16,
    ppem: u16,
    flags: u8,
    index_metrics: Metrics,
) -> Option<BitmapRecord> {
    if range.start > i32::MAX as u64
        || range.end > data.len() as u64
        || range.start < 4
        || range.start >= range.end
    {
        return None;
    }
    let start = range.start as usize;
    let end = range.end as usize;
    let (metrics, data_offset, data_length) = match format {
        17 => {
            if flags & 1 == 0 || end - start < 9 {
                return None;
            }
            (
                Metrics::read(data, start)?,
                start + 9,
                bytes::u32(data, start + 5)?,
            )
        }
        18 => {
            if end - start < 12 {
                return None;
            }
            (
                Metrics::read(data, start)?,
                start + 12,
                bytes::u32(data, start + 8)?,
            )
        }
        19 => {
            if !index_metrics.valid() || end - start < 4 {
                return None;
            }
            (index_metrics, start + 4, bytes::u32(data, start)?)
        }
        _ => return None,
    };
    if !metrics.valid()
        || data_length == 0
        || data_offset as u64 + u64::from(data_length) > end as u64
    {
        return None;
    }
    Some(BitmapRecord {
        range: data_offset..data_offset + data_length as usize,
        ppem,
        ppi: 72,
        origin: [0; 2],
        kind: ManagedBitmapGlyph::PNG,
        horizontal: true,
        bearing: metrics.bearing,
    })
}
