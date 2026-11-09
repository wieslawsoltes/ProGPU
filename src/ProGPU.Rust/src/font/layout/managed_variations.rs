//! Current managed layout variation store over the actual complete SFNT owner.
//! Header/region/index validation is eager; original delta-row reads remain lazy.
use super::managed_gsub::{fits_at, word};
use super::{FontError, SfntFace, Tag};
use crate::font::item_variation::axis_scalar;
use std::sync::Arc;
#[derive(Clone, Debug)]
struct Subtable {
    items: u16,
    words: u16,
    long: bool,
    indices: i32,
    count: u16,
    deltas: i32,
    row_bytes: i32,
}
#[derive(Clone, Debug)]
struct Layout {
    regions: i32,
    region_count: u16,
    subtables: Vec<Option<Subtable>>,
}
/// Immutable current managed layout variation state for an already-normalized
/// source instance. The complete selected font and exact F2DOT14 coordinates are
/// retained together. This does not perform fvar/avar setting selection or infer
/// that a malformed source font could have produced the supplied active instance.
#[derive(Clone, Debug)]
pub struct ManagedLayoutVariations {
    source: SfntFace,
    coordinates: Arc<[i16]>,
    layout: Option<Layout>,
    active: bool,
}
fn extent(data: &[u8], at: i32, n: usize) -> Result<(), FontError> {
    if fits_at(data, i64::from(at), n) {
        Ok(())
    } else {
        Err(FontError::InvalidFace)
    }
}
fn read(data: &[u8], at: i32) -> Result<u16, FontError> {
    word(data, i64::from(at))
}
fn long(data: &[u8], at: i32) -> Result<i32, FontError> {
    extent(data, at, 4)?;
    Ok(i32::from_be_bytes(
        data[at as usize..at as usize + 4].try_into().unwrap(),
    ))
}
impl ManagedLayoutVariations {
    /// Capture original normalized coordinates and actual font generation.
    /// All-zero coordinates retain the source inactive-instance behavior. Invalid
    /// active metadata rejects; missing/unsupported stores retain a zero delta.
    /// Reads are against the complete source font, as in the original C# owner.
    pub fn from_normalized(source: SfntFace, coordinates: &[i16]) -> Result<Self, FontError> {
        if source.data().len() > i32::MAX as usize || coordinates.len() > u16::MAX as usize {
            return Err(FontError::Capacity);
        }
        let mut owned = Vec::new();
        owned
            .try_reserve_exact(coordinates.len())
            .map_err(|_| FontError::Capacity)?;
        owned.extend_from_slice(coordinates);
        let active = coordinates.iter().any(|&v| v != 0);
        let layout = if active {
            Self::layout(&source, coordinates.len())?
        } else {
            None
        };
        Ok(Self {
            source,
            coordinates: owned.into(),
            layout,
            active,
        })
    }
    /// Actual immutable source owner, independent of a positioning buffer's GDEF.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Exact original normalized axis inventory.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Original nondefault instance state, independent of store availability.
    pub fn is_active(&self) -> bool {
        self.active
    }
    /// Whether the original managed store parser admitted a layout store.
    pub fn has_store(&self) -> bool {
        self.layout.is_some()
    }
    fn layout(source: &SfntFace, axes: usize) -> Result<Option<Layout>, FontError> {
        let Some((_, span)) = source.table_span(Tag::new(*b"GDEF")) else {
            return Ok(None);
        };
        if span.length < 18 {
            return Ok(None);
        }
        let start = span.offset as i32;
        let data = source.data();
        if read(data, start)? != 1 || read(data, start + 2)? < 3 {
            return Ok(None);
        }
        let relative = long(data, start + 14)?;
        if relative <= 0 {
            return Ok(None);
        }
        Self::parse(data, start.wrapping_add(relative), axes)
    }
    fn parse(data: &[u8], start: i32, axes: usize) -> Result<Option<Layout>, FontError> {
        extent(data, start, 8)?;
        if read(data, start)? != 1 {
            return Ok(None);
        }
        let region_relative = long(data, start + 2)?;
        let count = read(data, start + 6)?;
        extent(data, start + 8, usize::from(count) * 4)?;
        let regions = start.wrapping_add(region_relative);
        extent(data, regions, 4)?;
        if usize::from(read(data, regions)?) != axes {
            return Ok(None);
        }
        let region_count = read(data, regions + 2)?;
        let tuples = regions.wrapping_add(4);
        let region_bytes = usize::from(region_count)
            .checked_mul(axes)
            .and_then(|n| n.checked_mul(6))
            .ok_or(FontError::Capacity)?;
        extent(data, tuples, region_bytes)?;
        let mut subtables = Vec::new();
        subtables
            .try_reserve_exact(count as usize)
            .map_err(|_| FontError::Capacity)?;
        for i in 0..count {
            let relative = long(data, start + 8 + i32::from(i) * 4)?;
            if relative == 0 {
                subtables.push(None);
                continue;
            }
            let table = start.wrapping_add(relative);
            extent(data, table, 6)?;
            let items = read(data, table)?;
            let packed = read(data, table + 2)?;
            let count = read(data, table + 4)?;
            let words = packed & 0x7fff;
            if words > count {
                return Ok(None);
            }
            let indices = table + 6;
            extent(data, indices, usize::from(count) * 2)?;
            let long = packed & 0x8000 != 0;
            subtables.push(Some(Subtable {
                items,
                words,
                long,
                indices,
                count,
                deltas: indices + i32::from(count) * 2,
                row_bytes: i32::from(words) * if long { 4 } else { 2 }
                    + i32::from(count - words) * if long { 2 } else { 1 },
            }));
        }
        Ok(Some(Layout {
            regions: tuples,
            region_count,
            subtables,
        }))
    }
    fn scalar(&self, layout: &Layout, region: u16) -> f32 {
        if region >= layout.region_count {
            return 0.0;
        }
        let data = self.source.data();
        let at = layout.regions as usize + usize::from(region) * self.coordinates.len() * 6;
        let mut scalar = 1.0;
        for (i, &coordinate) in self.coordinates.iter().enumerate() {
            let p = at + i * 6;
            let s = |p| f32::from(i16::from_be_bytes([data[p], data[p + 1]])) / 16384.0;
            scalar *= axis_scalar(f32::from(coordinate) / 16384.0, s(p), s(p + 2), s(p + 4));
            if scalar == 0.0 {
                break;
            }
        }
        scalar
    }
    /// Original ordered floating delta, including lazy selected-row failures.
    /// Missing outer/inner entries and unknown regions return/contribute zero.
    /// Each delta is read before testing its region, retaining source error order.
    pub fn delta(&self, outer: u16, inner: u16) -> Result<f32, FontError> {
        let Some(layout) = &self.layout else {
            return Ok(0.0);
        };
        let Some(Some(table)) = layout.subtables.get(usize::from(outer)) else {
            return Ok(0.0);
        };
        if inner >= table.items {
            return Ok(0.0);
        }
        let data = self.source.data();
        let mut at = table
            .deltas
            .wrapping_add(i32::from(inner).wrapping_mul(table.row_bytes));
        let mut sum = 0.0;
        for i in 0..table.count {
            let delta = if i < table.words && table.long {
                let n = long(data, at)?;
                at = at.wrapping_add(4);
                n
            } else if i < table.words || table.long {
                let n = i32::from(read(data, at)? as i16);
                at = at.wrapping_add(2);
                n
            } else {
                extent(data, at, 1)?;
                let n = i32::from(data[at as usize] as i8);
                at = at.wrapping_add(1);
                n
            };
            let region = read(data, table.indices + i32::from(i) * 2)?;
            sum += delta as f32 * self.scalar(layout, region);
        }
        Ok(sum)
    }
}
