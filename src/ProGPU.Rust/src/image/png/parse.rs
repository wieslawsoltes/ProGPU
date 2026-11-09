//! Original whole-chunk inventory, CRC and source-order admission.
use super::{Metadata, PngDecodeError as Error, PngDecodeRequirements, layout::Layout};
pub(super) fn read_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes([
        bytes[offset],
        bytes[offset + 1],
        bytes[offset + 2],
        bytes[offset + 3],
    ])
}
fn crc32(mut value: u32, bytes: &[u8]) -> u32 {
    // Original bit recurrence depends on the immediately preceding CRC state.
    for &byte in bytes {
        value ^= u32::from(byte);
        for _ in 0..8 {
            value = (value >> 1) ^ (0xedb8_8320 & 0u32.wrapping_sub(value & 1));
        }
    }
    value
}
pub(super) fn parse(input: &[u8]) -> Result<(Metadata<'_>, Layout), Error> {
    if !input.starts_with(&[0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a]) {
        return Err(Error::InvalidSignature);
    }
    let mut offset = 8;
    let mut compressed_bytes = 0usize;
    let mut header = false;
    let mut palette = false;
    let mut transparency = false;
    let mut data_seen = false;
    let mut data_ended = false;
    let mut end = false;
    let mut metadata = Metadata {
        requirements: PngDecodeRequirements::default(),
        palette: &[],
        transparency: &[],
    };
    let mut layout = None;
    while offset < input.len() {
        if input.len() - offset < 12 {
            return Err(Error::InvalidChunk);
        }
        let length = read_u32(input, offset) as usize;
        let kind = read_u32(input, offset + 4);
        if length > input.len() - offset - 12 {
            return Err(Error::InvalidChunk);
        }
        let data = &input[offset + 8..offset + 8 + length];
        let expected = read_u32(input, offset + 8 + length);
        let actual = crc32(crc32(u32::MAX, &input[offset + 4..offset + 8]), data) ^ u32::MAX;
        if actual != expected {
            return Err(Error::ChecksumMismatch);
        }
        if !header && kind != 0x49484452 {
            return Err(Error::InvalidChunk);
        }
        match kind {
            0x49484452 => {
                if header || length != 13 {
                    return Err(Error::InvalidChunk);
                }
                header = true;
                let width = read_u32(data, 0);
                let height = read_u32(data, 4);
                let bit_depth = data[8];
                let color_type = data[9];
                let interlace_method = data[12];
                let channel_count = match (color_type, bit_depth) {
                    (0, 1 | 2 | 4 | 8 | 16) => 1,
                    (2, 8 | 16) => 3,
                    (3, 1 | 2 | 4 | 8) => 1,
                    (4, 8 | 16) => 2,
                    (6, 8 | 16) => 4,
                    _ => return Err(Error::UnsupportedFormat),
                };
                if width == 0
                    || height == 0
                    || data[10] != 0
                    || data[11] != 0
                    || interlace_method > 1
                {
                    return Err(Error::UnsupportedFormat);
                }
                let rgba_bytes = (width as usize)
                    .checked_mul(height as usize)
                    .and_then(|n| n.checked_mul(4))
                    .ok_or(Error::UnsupportedFormat)?;
                metadata.requirements = PngDecodeRequirements {
                    width,
                    height,
                    rgba_bytes,
                    bit_depth,
                    color_type,
                    channel_count,
                    interlace_method,
                    ..Default::default()
                };
                let resolved = Layout::new(metadata.requirements)?;
                metadata.requirements.filtered_bytes = resolved.filtered_bytes;
                layout = Some(resolved);
            }
            0x504c5445 => {
                let r = metadata.requirements;
                if palette
                    || data_seen
                    || length == 0
                    || length > 768
                    || length % 3 != 0
                    || matches!(r.color_type, 0 | 4)
                    || r.color_type == 3 && length / 3 > (1usize << r.bit_depth)
                {
                    return Err(Error::InvalidChunk);
                }
                palette = true;
                metadata.palette = data;
            }
            0x74524e53 => {
                if transparency || data_seen {
                    return Err(Error::InvalidChunk);
                }
                let valid = match metadata.requirements.color_type {
                    0 => length == 2,
                    2 => length == 6,
                    3 => palette && length != 0 && length <= metadata.palette.len() / 3,
                    _ => false,
                };
                if !valid {
                    return Err(Error::InvalidChunk);
                }
                transparency = true;
                metadata.transparency = data;
            }
            0x49444154 => {
                if data_ended {
                    return Err(Error::InvalidChunk);
                }
                data_seen = true;
                compressed_bytes = compressed_bytes
                    .checked_add(length)
                    .ok_or(Error::UnsupportedFormat)?;
            }
            0x49454e44 => {
                if !data_seen || end || length != 0 {
                    return Err(Error::InvalidChunk);
                }
                end = true;
            }
            _ if (kind >> 24) & 0x20 == 0 => return Err(Error::UnsupportedFormat),
            _ => {}
        }
        if data_seen && kind != 0x49444154 && kind != 0x49454e44 {
            data_ended = true;
        }
        offset += length + 12;
        if end {
            break;
        }
    }
    if !header || !data_seen || !end || offset != input.len() || compressed_bytes == 0 {
        return Err(Error::InvalidChunk);
    }
    if metadata.requirements.color_type == 3
        && (!palette || metadata.transparency.len() > metadata.palette.len() / 3)
    {
        return Err(Error::InvalidChunk);
    }
    metadata.requirements.compressed_bytes = compressed_bytes;
    Ok((metadata, layout.ok_or(Error::InvalidChunk)?))
}
