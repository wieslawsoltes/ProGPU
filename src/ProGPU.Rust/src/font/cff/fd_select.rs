use super::super::{FontError, bytes};

/// Original CFF FDSelect formats 0, 3 and 4, retaining encoded lookup storage.
#[derive(Clone, Copy, Debug)]
pub struct FdSelect<'a> {
    pub(super) bytes: &'a [u8],
    pub(super) layout: SelectLayout,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct SelectLayout {
    offset: usize,
    glyphs: u32,
    ranges: u32,
    dictionaries: u32,
    format: u8,
}
impl SelectLayout {
    pub(super) fn view(self, bytes: &[u8]) -> FdSelect<'_> {
        FdSelect {
            bytes,
            layout: self,
        }
    }
}
impl<'a> FdSelect<'a> {
    /// Validate the complete original selection inventory and final sentinel.
    pub fn read(
        data: &'a [u8],
        offset: u32,
        glyphs: u32,
        dictionaries: u32,
    ) -> Result<Self, FontError> {
        Self::parse(data, offset as usize, glyphs, dictionaries).ok_or(FontError::InvalidFace)
    }
    pub(crate) fn read_managed(
        data: &'a [u8],
        offset: i32,
        glyphs: u32,
        dictionaries: u32,
    ) -> Result<Self, FontError> {
        let at = usize::try_from(offset).map_err(|_| FontError::InvalidFace)?;
        let result = Self::parse(data, at, glyphs, dictionaries).ok_or(FontError::InvalidFace)?;
        if result.layout.ranges > i32::MAX as u32 {
            return Err(FontError::InvalidFace);
        }
        if result.layout.format == 4 {
            for i in 0..result.layout.ranges {
                if result
                    .range(i)
                    .is_none_or(|(first, _)| first > i32::MAX as u32)
                {
                    return Err(FontError::InvalidFace);
                }
            }
        }
        Ok(result)
    }
    fn parse(data: &'a [u8], offset: usize, glyphs: u32, dictionaries: u32) -> Option<Self> {
        if dictionaries == 0 {
            return None;
        }
        let format = *data.get(offset)?;
        let mut records = offset + 1;
        if format == 0 {
            if bytes::bytes(data, records, glyphs as usize)?
                .iter()
                .any(|b| u32::from(*b) >= dictionaries)
            {
                return None;
            }
            return Some(Self {
                bytes: data,
                layout: SelectLayout {
                    offset: records,
                    glyphs,
                    ranges: glyphs,
                    dictionaries,
                    format,
                },
            });
        }
        let (ranges, width, sentinel) = match format {
            3 => {
                let n = u32::from(bytes::u16(data, records)?);
                records += 2;
                (n, 3usize, 2)
            }
            4 => {
                let n = bytes::u32(data, records)?;
                records += 4;
                (n, 6usize, 4)
            }
            _ => return None,
        };
        if ranges == 0 {
            return None;
        }
        let size = (ranges as usize).checked_mul(width)?;
        bytes::bytes(data, records, size.checked_add(sentinel)?)?;
        let view = Self {
            bytes: data,
            layout: SelectLayout {
                offset: records,
                glyphs,
                ranges,
                dictionaries,
                format,
            },
        };
        let mut previous = 0;
        for i in 0..ranges {
            let (first, dictionary) = view.range(i)?;
            if (i == 0 && first != 0) || (i != 0 && first <= previous) || dictionary >= dictionaries
            {
                return None;
            }
            previous = first;
        }
        let end = if format == 3 {
            u32::from(bytes::u16(data, records + size)?)
        } else {
            bytes::u32(data, records + size)?
        };
        if end != glyphs {
            return None;
        }
        Some(view)
    }
    fn range(self, index: u32) -> Option<(u32, u32)> {
        if index >= self.layout.ranges {
            return None;
        }
        if self.layout.format == 3 {
            let p = self.layout.offset + index as usize * 3;
            Some((
                u32::from(bytes::u16(self.bytes, p)?),
                u32::from(*self.bytes.get(p + 2)?),
            ))
        } else {
            let p = self.layout.offset + index as usize * 6;
            Some((
                bytes::u32(self.bytes, p)?,
                u32::from(bytes::u16(self.bytes, p + 4)?),
            ))
        }
    }
    /// Original format byte.
    pub fn format(self) -> u8 {
        self.layout.format
    }
    /// Original glyph inventory size.
    pub fn glyph_count(self) -> u32 {
        self.layout.glyphs
    }
    /// Original encoded range count (glyph count for format zero).
    pub fn range_count(self) -> u32 {
        self.layout.ranges
    }
    /// Resolve one original font dictionary in O(1) or O(log ranges).
    pub fn dictionary(self, glyph: u32) -> Result<u32, FontError> {
        if glyph >= self.layout.glyphs {
            return Err(FontError::InvalidArgument);
        }
        if self.layout.format == 0 {
            return Ok(u32::from(self.bytes[self.layout.offset + glyph as usize]));
        }
        let (mut low, mut high) = (0, self.layout.ranges);
        while low < high {
            let mid = low + ((high - low) >> 1);
            let (first, _) = self.range(mid).ok_or(FontError::InvalidFace)?;
            if glyph < first {
                high = mid;
            } else {
                low = mid + 1;
            }
        }
        if low == 0 {
            return Err(FontError::InvalidFace);
        }
        let (first, dictionary) = self.range(low - 1).ok_or(FontError::InvalidFace)?;
        if first > glyph || dictionary >= self.layout.dictionaries {
            return Err(FontError::InvalidFace);
        }
        Ok(dictionary)
    }
}
