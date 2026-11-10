//! Checked big-endian reads shared by the owned font parsers.

pub(super) fn bytes(data: &[u8], offset: usize, length: usize) -> Option<&[u8]> {
    data.get(offset..offset.checked_add(length)?)
}
pub(super) fn u16(data: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_be_bytes(bytes(data, offset, 2)?.try_into().ok()?))
}
pub(super) fn i16(data: &[u8], offset: usize) -> Option<i16> {
    Some(u16(data, offset)? as i16)
}
pub(super) fn u24(data: &[u8], offset: usize) -> Option<u32> {
    let b = bytes(data, offset, 3)?;
    Some((u32::from(b[0]) << 16) | (u32::from(b[1]) << 8) | u32::from(b[2]))
}
pub(super) fn u32(data: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_be_bytes(bytes(data, offset, 4)?.try_into().ok()?))
}
