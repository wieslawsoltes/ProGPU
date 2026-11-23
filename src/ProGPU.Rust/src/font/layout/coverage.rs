use super::{FontError, fits, u16_at};

/// Borrowed validated original coverage, including admitted duplicate format-1
/// glyph IDs. The original inclusive binary-search midpoint determines the
/// returned index for duplicates; this is not a lower-bound search.
#[derive(Clone, Copy, Debug)]
pub struct Coverage<'a> {
    data: &'a [u8],
    offset: usize,
    count: u16,
    format: u16,
}
impl<'a> Coverage<'a> {
    // Only for an immutable inventory already checked by parse (GDEF mark sets).
    pub(super) fn validated(data: &'a [u8], offset: usize) -> Self {
        Self {
            data,
            offset,
            count: u16_at(data, offset + 2),
            format: u16_at(data, offset),
        }
    }
    /// Validate the complete original coverage inventory at a table-relative offset.
    pub fn parse(data: &'a [u8], offset: usize) -> Result<Self, FontError> {
        if !fits(data, offset, 4) {
            return Err(FontError::InvalidFace);
        }
        let format = u16_at(data, offset);
        let count = u16_at(data, offset + 2);
        let payload = offset + 4;
        match format {
            1 => {
                if !fits(data, payload, usize::from(count) * 2) {
                    return Err(FontError::InvalidFace);
                }
                let mut previous = 0;
                for index in 0..usize::from(count) {
                    let glyph = u16_at(data, payload + index * 2);
                    if index != 0 && glyph < previous {
                        return Err(FontError::InvalidFace);
                    }
                    previous = glyph;
                }
            }
            2 => {
                if !fits(data, payload, usize::from(count) * 6) {
                    return Err(FontError::InvalidFace);
                }
                let (mut expected, mut previous_end) = (0u32, 0);
                for index in 0..usize::from(count) {
                    let range = payload + index * 6;
                    let start = u16_at(data, range);
                    let end = u16_at(data, range + 2);
                    let coverage = u16_at(data, range + 4);
                    if start > end
                        || (index != 0 && start <= previous_end)
                        || u32::from(coverage) != expected
                    {
                        return Err(FontError::InvalidFace);
                    }
                    expected += u32::from(end) - u32::from(start) + 1;
                    if expected > 0x10000 {
                        return Err(FontError::InvalidFace);
                    }
                    previous_end = end;
                }
            }
            _ => return Err(FontError::InvalidFace),
        }
        Ok(Self {
            data,
            offset,
            count,
            format,
        })
    }
    /// Exact original coverage index, or absence. O(log C), no allocation.
    pub fn find(&self, glyph: u16) -> Option<u16> {
        let (mut low, mut high) = (0i32, i32::from(self.count) - 1);
        while low <= high {
            let middle = (low + high) >> 1;
            let offset = self.offset + 4 + middle as usize * if self.format == 1 { 2 } else { 6 };
            let start = u16_at(self.data, offset);
            let end = if self.format == 1 {
                start
            } else {
                u16_at(self.data, offset + 2)
            };
            if glyph < start {
                high = middle - 1;
            } else if glyph > end {
                low = middle + 1;
            } else {
                return Some(if self.format == 1 {
                    middle as u16
                } else {
                    u16_at(self.data, offset + 4) + (glyph - start)
                });
            }
        }
        None
    }
}

#[derive(Clone, Copy, Debug)]
pub(super) struct ClassMeta {
    offset: usize,
    count: u16,
    start: u16,
    format: u16,
}
/// Borrowed original class definition. Missing glyphs have class zero; class
/// values themselves are retained raw, independent of GDEF interpretation.
#[derive(Clone, Copy, Debug)]
pub struct ClassDefinition<'a> {
    pub(super) data: &'a [u8],
    pub(super) meta: ClassMeta,
}
impl<'a> ClassDefinition<'a> {
    /// Validate exact format-1 bounds or sorted non-overlapping format-2 ranges.
    pub fn parse(data: &'a [u8], offset: usize) -> Result<Self, FontError> {
        if !fits(data, offset, 4) {
            return Err(FontError::InvalidFace);
        }
        let format = u16_at(data, offset);
        let (count, start) = match format {
            1 => {
                if !fits(data, offset, 6) {
                    return Err(FontError::InvalidFace);
                }
                let start = u16_at(data, offset + 2);
                let count = u16_at(data, offset + 4);
                if u32::from(start) + u32::from(count) > 0x10000
                    || !fits(data, offset + 6, usize::from(count) * 2)
                {
                    return Err(FontError::InvalidFace);
                }
                (count, start)
            }
            2 => {
                let count = u16_at(data, offset + 2);
                if !fits(data, offset + 4, usize::from(count) * 6) {
                    return Err(FontError::InvalidFace);
                }
                let mut previous_end = 0;
                for index in 0..usize::from(count) {
                    let range = offset + 4 + index * 6;
                    let start = u16_at(data, range);
                    let end = u16_at(data, range + 2);
                    if start > end || (index != 0 && start <= previous_end) {
                        return Err(FontError::InvalidFace);
                    }
                    previous_end = end;
                }
                (count, 0)
            }
            _ => return Err(FontError::InvalidFace),
        };
        Ok(Self {
            data,
            meta: ClassMeta {
                offset,
                count,
                start,
                format,
            },
        })
    }
    /// Original class value. O(1) for format 1, O(log R) for format 2.
    pub fn get(&self, glyph: u16) -> u16 {
        let m = self.meta;
        if m.format == 1 {
            let index = u32::from(glyph).wrapping_sub(u32::from(m.start));
            return if index < u32::from(m.count) {
                u16_at(self.data, m.offset + 6 + index as usize * 2)
            } else {
                0
            };
        }
        let (mut low, mut high) = (0, usize::from(m.count));
        while low < high {
            let middle = low + (high - low) / 2;
            let offset = m.offset + 4 + middle * 6;
            let start = u16_at(self.data, offset);
            let end = u16_at(self.data, offset + 2);
            if glyph < start {
                high = middle;
            } else if glyph > end {
                low = middle + 1;
            } else {
                return u16_at(self.data, offset + 4);
            }
        }
        0
    }
}
