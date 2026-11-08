use super::super::{FontError, bytes};

/// Original encoded CFF1/CFF2 INDEX, borrowing its complete immutable byte owner.
/// Offset entries remain encoded; item access allocates no offset array.
#[derive(Clone, Copy, Debug, Default)]
pub struct Index<'a> {
    pub(super) bytes: &'a [u8],
    pub(super) layout: IndexLayout,
}
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct IndexLayout {
    offsets: usize,
    data: usize,
    end: usize,
    count: u32,
    width: u8,
}
impl IndexLayout {
    pub(super) fn view(self, bytes: &[u8]) -> Index<'_> {
        Index {
            bytes,
            layout: self,
        }
    }
}
impl<'a> Index<'a> {
    /// Parse an original CFF1 INDEX (16-bit item count).
    /// On failure, cursor retains the native parser's already-consumed header bytes.
    pub fn read(bytes: &'a [u8], cursor: &mut usize) -> Result<Self, FontError> {
        Self::read_core(bytes, cursor, false).ok_or(FontError::InvalidFace)
    }
    /// Parse an original CFF2 INDEX (32-bit item count).
    /// All offsets and the final data extent are validated before returning a view.
    pub fn read_cff2(bytes: &'a [u8], cursor: &mut usize) -> Result<Self, FontError> {
        Self::read_core(bytes, cursor, true).ok_or(FontError::InvalidFace)
    }
    fn read_core(data: &'a [u8], cursor: &mut usize, cff2: bool) -> Option<Self> {
        let count = if cff2 {
            bytes::u32(data, *cursor)?
        } else {
            u32::from(bytes::u16(data, *cursor)?)
        };
        *cursor += if cff2 { 4 } else { 2 };
        if count == 0 {
            return Some(Index {
                bytes: data,
                layout: IndexLayout {
                    offsets: *cursor,
                    data: *cursor,
                    end: *cursor,
                    count: 0,
                    width: 0,
                },
            });
        }
        let width = *data.get(*cursor)?;
        *cursor += 1;
        if !(1..=4).contains(&width) || count == u32::MAX {
            return None;
        }
        let size = (count as usize)
            .checked_add(1)?
            .checked_mul(usize::from(width))?;
        bytes::bytes(data, *cursor, size)?;
        let start = cursor.checked_add(size)?;
        let mut result = Index {
            bytes: data,
            layout: IndexLayout {
                offsets: *cursor,
                data: start,
                end: start,
                count,
                width,
            },
        };
        let mut previous = result.offset(0)?;
        if previous != 1 {
            return None;
        }
        for item in 1..=count {
            let next = result.offset(item)?;
            if next < previous {
                return None;
            }
            previous = next;
        }
        let size = (previous - 1) as usize;
        bytes::bytes(data, start, size)?;
        result.layout.end = start + size;
        *cursor = result.layout.end;
        Some(result)
    }
    fn offset(self, item: u32) -> Option<u32> {
        if item > self.layout.count {
            return None;
        }
        let offset = self
            .layout
            .offsets
            .checked_add((item as usize).checked_mul(usize::from(self.layout.width))?)?;
        let mut value = 0;
        for byte in bytes::bytes(self.bytes, offset, usize::from(self.layout.width))? {
            value = (value << 8) | u32::from(*byte);
        }
        Some(value)
    }
    /// Original item count, including empty byte strings.
    pub fn len(self) -> u32 {
        self.layout.count
    }
    /// Whether the original count is zero.
    pub fn is_empty(self) -> bool {
        self.len() == 0
    }
    /// Encoded offset width, or zero for an empty INDEX.
    pub fn offset_size(self) -> u8 {
        self.layout.width
    }
    /// Borrow one original item in O(1), without copying its bytes.
    pub fn get(self, item: u32) -> Result<&'a [u8], FontError> {
        if item >= self.len() {
            return Err(FontError::InvalidArgument);
        }
        let start = self.offset(item).ok_or(FontError::InvalidFace)?;
        let end = self.offset(item + 1).ok_or(FontError::InvalidFace)?;
        if start < 1 || end < start {
            return Err(FontError::InvalidFace);
        }
        let offset = self
            .layout
            .data
            .checked_add((start - 1) as usize)
            .ok_or(FontError::InvalidFace)?;
        let length = (end - start) as usize;
        if offset > self.layout.end || length > self.layout.end - offset {
            return Err(FontError::InvalidFace);
        }
        bytes::bytes(self.bytes, offset, length).ok_or(FontError::InvalidFace)
    }
}
