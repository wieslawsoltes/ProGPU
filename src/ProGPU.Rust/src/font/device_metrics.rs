//! Original whole-inventory VDMX reader and static managed-font admission.
use super::{FontError, ManagedFont, bytes};

/// One exact font-provided pixel-size record; never scaled or selected by proximity.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FontVerticalDeviceMetrics {
    /// Original pixel em size.
    pub pixels_per_em: u16,
    /// Original signed maximum Y.
    pub maximum_y: i16,
    /// Original signed minimum Y.
    pub minimum_y: i16,
}
impl FontVerticalDeviceMetrics {
    /// Original signed extrema difference, without rounding.
    pub fn height(self) -> i32 {
        i32::from(self.maximum_y) - i32::from(self.minimum_y)
    }
}
/// Validate the complete original VDMX inventory before selecting the first
/// applicable ratio and exact size. Missing coverage is `Ok(None)`; malformed
/// later records still reject a previously matching record. O(records + ratios
/// log groups) time, O(groups) scratch; ordered metadata has dependent cursors.
pub fn read_vertical_device_metrics(
    table: &[u8],
    pixels_per_em: u16,
    ratio_x: u16,
    ratio_y: u16,
) -> Result<Option<FontVerticalDeviceMetrics>, FontError> {
    let bad = FontError::InvalidFace;
    if pixels_per_em == 0 || ratio_x == 0 || ratio_y == 0 {
        return Err(FontError::InvalidArgument);
    }
    let word = |at| bytes::u16(table, at).ok_or(bad);
    let version = word(0)?;
    let groups = usize::from(word(2)?);
    let ratios = usize::from(word(4)?);
    let header_end = 6 + ratios * 6;
    if version > 1 || groups == 0 || ratios == 0 || groups > ratios || header_end > table.len() {
        return Err(bad);
    }
    let mut starts = Vec::new();
    starts
        .try_reserve_exact(groups)
        .map_err(|_| FontError::Capacity)?;
    let mut cursor = header_end;
    for _ in 0..groups {
        let head = bytes::bytes(table, cursor, 4).ok_or(bad)?;
        if cursor > usize::from(u16::MAX) {
            return Err(bad);
        }
        starts.push(cursor);
        let count = usize::from(word(cursor)?);
        let first = u16::from(head[2]);
        let last = u16::from(head[3]);
        if count == 0 || first > last || count > (table.len() - cursor - 4) / 6 {
            return Err(bad);
        }
        let mut previous = 0;
        for index in 0..count {
            let record = cursor + 4 + index * 6;
            let height = word(record)?;
            let maximum = word(record + 2)? as i16;
            let minimum = word(record + 4)? as i16;
            if height == 0
                || height <= previous
                || height < first
                || (if height <= 255 {
                    height > last
                } else {
                    last != 255
                })
                || maximum <= minimum
            {
                return Err(bad);
            }
            previous = height;
        }
        cursor += 4 + count * 6;
    }
    if cursor != table.len() {
        return Err(bad);
    }
    let mut selected = None;
    let mut default_charsets = 0;
    for index in 0..ratios {
        let at = 6 + index * 4;
        let charset = table[at];
        let x = u32::from(table[at + 1]);
        let low = u32::from(table[at + 2]);
        let high = u32::from(table[at + 3]);
        let fallback = x == 0 && low == 0 && high == 0;
        if charset > 1 || (!fallback && (x == 0 || low == 0 || low > high)) {
            return Err(bad);
        }
        let mask = if version == 0 { 1 << charset } else { 3 };
        if default_charsets & mask != 0 {
            return Err(bad);
        }
        if fallback {
            default_charsets |= mask;
        }
        let offset = usize::from(word(6 + ratios * 4 + index * 2)?);
        if starts.binary_search(&offset).is_err() {
            return Err(bad);
        }
        let matches = fallback
            || (u32::from(ratio_y) * x >= low * u32::from(ratio_x)
                && u32::from(ratio_y) * x <= high * u32::from(ratio_x));
        if selected.is_none() && (version == 1 || charset == 1) && matches {
            selected = Some(offset);
        }
    }
    if let Some(selected) = selected {
        for index in 0..usize::from(word(selected)?) {
            let record = selected + 4 + index * 6;
            if word(record)? == pixels_per_em {
                return Ok(Some(FontVerticalDeviceMetrics {
                    pixels_per_em,
                    maximum_y: word(record + 2)? as i16,
                    minimum_y: word(record + 4)? as i16,
                }));
            }
        }
    }
    Ok(None)
}
impl ManagedFont {
    /// Exact static square-pixel source metrics. Raw directory duplicates,
    /// extents and all VDMX records validate before variable/symbol admission.
    pub fn vertical_device_metrics(
        &self,
        pixels_per_em: u16,
    ) -> Result<Option<FontVerticalDeviceMetrics>, FontError> {
        if pixels_per_em == 0 {
            return Err(FontError::InvalidArgument);
        }
        let data = self.source().data();
        let directory = self.source().face_offset() as usize + 12;
        let count = bytes::u16(data, directory - 8).ok_or(FontError::InvalidFace)?;
        let mut table = None;
        let mut variable = false;
        for index in 0..usize::from(count) {
            let entry =
                bytes::bytes(data, directory + index * 16, 16).ok_or(FontError::InvalidFace)?;
            variable |= &entry[..4] == b"fvar";
            if &entry[..4] != b"VDMX" {
                continue;
            }
            if table.is_some() {
                return Err(FontError::InvalidFace);
            }
            let offset = bytes::u32(entry, 8).ok_or(FontError::InvalidFace)? as usize;
            let length = bytes::u32(entry, 12).ok_or(FontError::InvalidFace)? as usize;
            table = Some(bytes::bytes(data, offset, length).ok_or(FontError::InvalidFace)?);
        }
        let Some(table) = table else {
            return Ok(None);
        };
        let candidate = read_vertical_device_metrics(table, pixels_per_em, 1, 1)?;
        if variable
            || !self.metadata().has_true_type_outlines
            || self.character_map().uses_symbol_character_map()
        {
            return Ok(None);
        }
        Ok(candidate)
    }
}
