//! Allocation-free original item variation stores and delta-index maps.
//! Direct port of `progpu_native_item_variation_store.cpp`.

use super::{FontError, bytes};

/// Validated borrowed item variation store. Private layout fields prevent
/// constructing a view over unchecked data. The borrow keeps its source alive.
/// Parsing validates every subtable, including unselected rows and regions.
#[derive(Clone, Copy, Debug)]
pub struct ItemVariationStore<'a> {
    data: &'a [u8],
    offset: usize,
    regions: usize,
    axes: u16,
    region_count: u16,
    subtables: u16,
}

impl<'a> ItemVariationStore<'a> {
    /// Parse a format-one store at an arbitrary byte offset. The declared axis
    /// count must exactly match the expected count, including zero.
    pub fn parse(data: &'a [u8], offset: usize, expected_axes: u16) -> Result<Self, FontError> {
        extent(data, offset, 8)?;
        if bytes::u16(data, offset) != Some(1) {
            return Err(FontError::InvalidFace);
        }
        let relative = bytes::u32(data, offset + 2).unwrap() as usize;
        let subtables = bytes::u16(data, offset + 6).unwrap();
        extent(data, offset + 8, usize::from(subtables) * 4)?;
        if relative > data.len() - offset {
            return Err(FontError::InvalidFace);
        }
        let regions = offset + relative;
        extent(data, regions, 4)?;
        let axes = bytes::u16(data, regions).unwrap();
        let region_count = bytes::u16(data, regions + 2).unwrap();
        if axes != expected_axes {
            return Err(FontError::InvalidFace);
        }
        let region_bytes = usize::from(region_count)
            .checked_mul(usize::from(axes))
            .and_then(|n| n.checked_mul(6))
            .ok_or(FontError::InvalidFace)?;
        extent(data, regions + 4, region_bytes)?;
        let store = Self {
            data,
            offset,
            regions,
            axes,
            region_count,
            subtables,
        };
        for table in 0..subtables {
            let relative = bytes::u32(data, offset + 8 + usize::from(table) * 4).unwrap() as usize;
            if relative == 0 {
                continue;
            }
            if relative > data.len() - offset {
                return Err(FontError::InvalidFace);
            }
            let start = offset + relative;
            extent(data, start, 6)?;
            let items = usize::from(bytes::u16(data, start).unwrap());
            let packed = bytes::u16(data, start + 2).unwrap();
            let indices = usize::from(bytes::u16(data, start + 4).unwrap());
            let words = usize::from(packed & 0x7fff);
            if words > indices {
                return Err(FontError::InvalidFace);
            }
            extent(data, start + 6, indices * 2)?;
            let long = packed & 0x8000 != 0;
            let row_bytes =
                words * if long { 4 } else { 2 } + (indices - words) * if long { 2 } else { 1 };
            let delta_bytes = items.checked_mul(row_bytes).ok_or(FontError::InvalidFace)?;
            extent(data, start + 6 + indices * 2, delta_bytes)?;
        }
        Ok(store)
    }

    /// Original axis count required by normalized coordinate queries.
    pub fn axis_count(self) -> u16 {
        self.axes
    }
    /// Original full region inventory count.
    pub fn region_count(self) -> u16 {
        self.region_count
    }
    /// Original subtable count, including absent zero-offset entries.
    pub fn subtable_count(self) -> u16 {
        self.subtables
    }

    /// Original region scalar using signed F2DOT14 coordinates. Extra coordinates
    /// are ignored; too few return `Capacity`, the native insufficient-buffer error.
    /// Original invalid/neutral region tuples retain their unit scalar policy.
    pub fn region_scalar(self, coordinates: &[i16], region: u16) -> Result<f32, FontError> {
        if coordinates.len() < usize::from(self.axes) {
            return Err(FontError::Capacity);
        }
        if region >= self.region_count {
            return Err(FontError::InvalidArgument);
        }
        Ok(self.scalar(coordinates, region))
    }

    /// Accumulate original deltas in source order. Missing outer/inner entries
    /// return zero; out-of-range region references are skipped, as in native.
    pub fn delta(self, coordinates: &[i16], outer: u16, inner: u16) -> Result<f32, FontError> {
        if coordinates.len() < usize::from(self.axes) {
            return Err(FontError::Capacity);
        }
        Ok(self.delta_with(outer, inner, |region| self.scalar(coordinates, region)))
    }

    /// Reuse a caller's complete region scalar inventory. Raw scalar values,
    /// including nonfinite values, retain the original arithmetic contract.
    pub fn delta_with_scalars(
        self,
        scalars: &[f32],
        outer: u16,
        inner: u16,
    ) -> Result<f32, FontError> {
        if scalars.len() < usize::from(self.region_count) {
            return Err(FontError::Capacity);
        }
        Ok(self.delta_with(outer, inner, |region| scalars[usize::from(region)]))
    }

    /// Number of selected CFF2 blend regions. This distinct route requires zero
    /// item/word counts and validates every selected region index before return.
    pub fn blend_region_count(self, outer: u16) -> Result<u16, FontError> {
        let table = self.subtable(outer).ok_or(FontError::InvalidFace)?;
        if bytes::u16(self.data, table) != Some(0) || bytes::u16(self.data, table + 2) != Some(0) {
            return Err(FontError::InvalidFace);
        }
        let count = bytes::u16(self.data, table + 4).unwrap();
        for position in 0..count {
            if bytes::u16(self.data, table + 6 + usize::from(position) * 2).unwrap()
                >= self.region_count
            {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(count)
    }

    /// Scalar at an original selected CFF2 region position. Whole selection
    /// validation precedes the position query, preserving native rejection order.
    pub fn blend_region_scalar(
        self,
        coordinates: &[i16],
        outer: u16,
        position: u16,
    ) -> Result<f32, FontError> {
        if coordinates.len() < usize::from(self.axes) {
            return Err(FontError::Capacity);
        }
        if position >= self.blend_region_count(outer)? {
            return Err(FontError::InvalidArgument);
        }
        let table = self.subtable(outer).unwrap();
        let region = bytes::u16(self.data, table + 6 + usize::from(position) * 2).unwrap();
        Ok(self.scalar(coordinates, region))
    }

    fn subtable(self, outer: u16) -> Option<usize> {
        if outer >= self.subtables {
            return None;
        }
        let relative =
            bytes::u32(self.data, self.offset + 8 + usize::from(outer) * 4).unwrap() as usize;
        if relative == 0 {
            None
        } else {
            Some(self.offset + relative)
        }
    }
    fn scalar(self, coordinates: &[i16], region: u16) -> f32 {
        let start = self.regions + 4 + usize::from(region) * usize::from(self.axes) * 6;
        const SCALE: f32 = 1.0 / 16384.0;
        let mut scalar = 1.0;
        for (axis, coordinate) in coordinates[..usize::from(self.axes)].iter().enumerate() {
            let offset = start + axis * 6;
            scalar *= axis_scalar(
                f32::from(*coordinate) * SCALE,
                f32::from(bytes::i16(self.data, offset).unwrap()) * SCALE,
                f32::from(bytes::i16(self.data, offset + 2).unwrap()) * SCALE,
                f32::from(bytes::i16(self.data, offset + 4).unwrap()) * SCALE,
            );
            if scalar == 0.0 {
                break;
            }
        }
        scalar
    }
    fn delta_with(self, outer: u16, inner: u16, mut resolve: impl FnMut(u16) -> f32) -> f32 {
        let Some(table) = self.subtable(outer) else {
            return 0.0;
        };
        let items = bytes::u16(self.data, table).unwrap();
        if inner >= items {
            return 0.0;
        }
        let packed = bytes::u16(self.data, table + 2).unwrap();
        let count = usize::from(bytes::u16(self.data, table + 4).unwrap());
        let words = usize::from(packed & 0x7fff);
        let long = packed & 0x8000 != 0;
        let row_bytes =
            words * if long { 4 } else { 2 } + (count - words) * if long { 2 } else { 1 };
        let indices = table + 6;
        let mut offset = indices + count * 2 + usize::from(inner) * row_bytes;
        let mut sum = 0.0;
        for position in 0..count {
            let delta = if position < words && long {
                let v = bytes::u32(self.data, offset).unwrap() as i32;
                offset += 4;
                v
            } else if position < words || long {
                let v = i32::from(bytes::i16(self.data, offset).unwrap());
                offset += 2;
                v
            } else {
                let v = i32::from(self.data[offset] as i8);
                offset += 1;
                v
            };
            let region = bytes::u16(self.data, indices + position * 2).unwrap();
            if region >= self.region_count {
                continue;
            }
            sum += delta as f32 * resolve(region);
        }
        sum
    }
}

/// Validated borrowed format-zero/one delta-set index map, with no decoded
/// allocation. Unknown entry-format bits retain the original native behavior.
#[derive(Clone, Copy, Debug)]
pub struct DeltaSetIndexMap<'a> {
    entries: &'a [u8],
    count: u32,
    size: u8,
    bits: u8,
}
impl<'a> DeltaSetIndexMap<'a> {
    /// Validate the complete encoded map at an arbitrary byte offset.
    pub fn parse(data: &'a [u8], offset: usize) -> Result<Self, FontError> {
        extent(data, offset, 4)?;
        let (count, start) = match data[offset] {
            0 => (u32::from(bytes::u16(data, offset + 2).unwrap()), offset + 4),
            1 => (
                bytes::u32(data, offset + 2).ok_or(FontError::InvalidFace)?,
                offset + 6,
            ),
            _ => return Err(FontError::InvalidFace),
        };
        let format = data[offset + 1];
        let size = ((format & 0x30) >> 4) + 1;
        let bits = (format & 0x0f) + 1;
        let length = (count as usize)
            .checked_mul(usize::from(size))
            .ok_or(FontError::InvalidFace)?;
        let entries = bytes::bytes(data, start, length).ok_or(FontError::InvalidFace)?;
        Ok(Self {
            entries,
            count,
            size,
            bits,
        })
    }
    /// Original entry count, including an explicitly empty map.
    pub fn len(self) -> u32 {
        self.count
    }
    /// Whether this map uses the original implicit outer-zero mapping.
    pub fn is_empty(self) -> bool {
        self.count == 0
    }
    /// Resolve outer/inner indices. Past-end queries reuse the last entry;
    /// empty maps clamp the item index to u16. Original narrowing is retained.
    pub fn get(self, item: u32) -> (u16, u16) {
        if self.count == 0 {
            return (0, item.min(u32::from(u16::MAX)) as u16);
        }
        let offset = item.min(self.count - 1) as usize * usize::from(self.size);
        let mut value = 0u32;
        for byte in &self.entries[offset..offset + usize::from(self.size)] {
            value = (value << 8) | u32::from(*byte);
        }
        (
            (value >> self.bits) as u16,
            (value & ((1u32 << self.bits) - 1)) as u16,
        )
    }
}

fn extent(data: &[u8], offset: usize, length: usize) -> Result<(), FontError> {
    bytes::bytes(data, offset, length)
        .map(|_| ())
        .ok_or(FontError::InvalidFace)
}
fn axis_scalar(coordinate: f32, start: f32, peak: f32, end: f32) -> f32 {
    if start > peak || peak > end || (start < 0.0 && end > 0.0 && peak != 0.0) || peak == 0.0 {
        return 1.0;
    }
    if coordinate < start || coordinate > end {
        return 0.0;
    }
    if coordinate == peak {
        return 1.0;
    }
    if coordinate < peak {
        if peak == start {
            1.0
        } else {
            (coordinate - start) / (peak - start)
        }
    } else if end == peak {
        1.0
    } else {
        (end - coordinate) / (end - peak)
    }
}
