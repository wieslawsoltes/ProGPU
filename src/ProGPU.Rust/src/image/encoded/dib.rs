//! Original ICO DIB layout, palette/bitfield inventory, XOR conversion and AND mask.
use super::{
    EncodedImageError as E, EncodedImageExecution as Execution, EncodedRaster, IconFrame, count,
    le16, le32, rgba_byte_count, zeros,
};
use wide::{f64x4 as F, u32x4 as U};

pub(super) fn decode(
    payload: &[u8],
    icon: IconFrame,
    execution: Execution,
) -> Result<EncodedRaster, E> {
    if payload.len() < 12 {
        return Err(E::BitmapHeader);
    }
    let header = count(le32(payload) as usize)?;
    let core = header == 12;
    if header > payload.len() {
        return Err(E::BitmapHeader);
    }
    if !core && header < 40 {
        return Err(E::UnsupportedHeader);
    }
    let dib_width = if core {
        i32::from(le16(&payload[4..]))
    } else {
        le32(&payload[4..]) as i32
    };
    let dib_height = if core {
        i32::from(le16(&payload[6..]))
    } else {
        le32(&payload[8..]) as i32
    };
    let bits = le16(&payload[if core { 10 } else { 14 }..]);
    let compression = if core { 0 } else { le32(&payload[16..]) };
    let declared_bytes = if core { 0 } else { le32(&payload[20..]) };
    let declared_colors = if core {
        u32::from(icon.color_count)
    } else {
        le32(&payload[32..])
    };
    let uncompressed = compression == 0;
    let bitfields = matches!(compression, 3 | 6);
    let rle = matches!(compression, 1 | 2);
    let supported = if core {
        matches!(bits, 1 | 4 | 8 | 24)
    } else {
        (uncompressed && matches!(bits, 1 | 4 | 8 | 16 | 24 | 32))
            || (bitfields && matches!(bits, 16 | 32))
            || (compression == 1 && bits == 8)
            || (compression == 2 && bits == 4)
    };
    if !supported {
        return Err(E::UnsupportedBitmap);
    }
    if dib_width == i32::MIN || dib_height == i32::MIN {
        return Err(E::Dimensions);
    }
    let width = dib_width.unsigned_abs();
    // The directory height is authoritative even when the DIB height differs.
    let height = if icon.size[1] > 0 {
        icon.size[1]
    } else {
        dib_height.unsigned_abs() / 2
    };
    let size = [width, height];
    if size.contains(&0) {
        return Err(E::Dimensions);
    }
    let width = width as usize;
    let height = height as usize;
    let mut offset = header;
    let mut masks = if bits == 16 && uncompressed {
        [0x7c00, 0x03e0, 0x001f, 0]
    } else {
        [0; 4]
    };
    if bitfields {
        if header >= 52 {
            masks[..3].copy_from_slice(&[
                le32(&payload[40..]),
                le32(&payload[44..]),
                le32(&payload[48..]),
            ]);
            if header >= 56 {
                masks[3] = le32(&payload[52..]);
            }
        } else {
            let channels = if compression == 6 { 4 } else { 3 };
            let bytes = channels * 4;
            let values = payload
                .get(offset..offset.checked_add(bytes).ok_or(E::Capacity)?)
                .ok_or(E::TruncatedMasks)?;
            for (i, mask) in masks.iter_mut().take(channels).enumerate() {
                *mask = le32(&values[i * 4..]);
            }
            offset += bytes;
        }
        validate_masks(bits, compression, masks)?;
    }
    let entry_size = if core { 3 } else { 4 };
    let palette = if bits <= 8 {
        let maximum = 1usize << bits;
        let colors = if declared_colors == 0 {
            maximum
        } else {
            count(declared_colors as usize)?
        };
        if colors == 0 || colors > maximum {
            return Err(E::PaletteSize);
        }
        let bytes = colors * entry_size;
        let palette = payload
            .get(offset..offset.checked_add(bytes).ok_or(E::Capacity)?)
            .ok_or(E::TruncatedPalette)?;
        offset += bytes;
        palette
    } else {
        &[]
    };
    let bottom_up = dib_height > 0;
    let mut indices = None;
    let (row_bytes, xor_bytes) = if rle {
        let available = payload.len() - offset;
        let encoded_count = if declared_bytes == 0 {
            available
        } else {
            count(declared_bytes as usize)?
        };
        if encoded_count > available {
            return Err(E::TruncatedRle);
        }
        let mut output = zeros(count(width.checked_mul(height).ok_or(E::Capacity)?)?)?;
        let consumed = super::rle::decode(
            &payload[offset..offset + encoded_count],
            &mut output,
            width,
            height,
            bits,
            bottom_up,
        )?;
        indices = Some(output);
        (
            0,
            if declared_bytes == 0 {
                consumed
            } else {
                encoded_count
            },
        )
    } else {
        let bit_width = count(
            width
                .checked_mul(usize::from(bits))
                .and_then(|v| v.checked_add(31))
                .ok_or(E::Capacity)?,
        )?;
        let row = count((bit_width / 32).checked_mul(4).ok_or(E::Capacity)?)?;
        (row, count(row.checked_mul(height).ok_or(E::Capacity)?)?)
    };
    if offset
        .checked_add(xor_bytes)
        .is_none_or(|end| end > payload.len())
    {
        return Err(E::TruncatedPixels);
    }
    let mut rgba = zeros(rgba_byte_count(size)?)?;
    let pixel_alpha = (bits == 32 && uncompressed) || masks[3] != 0;
    let mut nonzero_alpha = false;
    for y in 0..height {
        let source_y = if bottom_up { height - 1 - y } else { y };
        let row = &payload[offset + source_y * row_bytes..][..row_bytes];
        let out = &mut rgba[y * width * 4..][..width * 4];
        if bits == 16 || (bits == 32 && bitfields) {
            for (x, pixel) in out.chunks_exact_mut(4).enumerate() {
                let packed = if bits == 16 {
                    u32::from(le16(&row[x * 2..]))
                } else {
                    le32(&row[x * 4..])
                };
                let value = masked_pixel(packed, masks, execution);
                nonzero_alpha |= value[3] != 0;
                pixel.copy_from_slice(&value);
            }
            continue;
        }
        let packed = |x: usize| -> Result<u32, E> {
            if bits <= 8 {
                let index = if let Some(indices) = &indices {
                    indices[y * width + x]
                } else {
                    index(row, x, bits)
                };
                let at = usize::from(index) * entry_size;
                let entry = palette.get(at..at + entry_size).ok_or(E::PaletteIndex)?;
                Ok(u32::from_le_bytes([entry[0], entry[1], entry[2], 255]))
            } else if bits == 24 {
                Ok(u32::from_le_bytes([
                    row[x * 3],
                    row[x * 3 + 1],
                    row[x * 3 + 2],
                    255,
                ]))
            } else {
                Ok(le32(&row[x * 4..]))
            }
        };
        let mut x = 0;
        if execution == Execution::CpuSimd {
            while x + 4 <= width {
                let source = U::from([packed(x)?, packed(x + 1)?, packed(x + 2)?, packed(x + 3)?]);
                let values = swizzle(source).to_array();
                for (i, value) in values.into_iter().enumerate() {
                    nonzero_alpha |= value >> 24 != 0;
                    out[(x + i) * 4..(x + i + 1) * 4].copy_from_slice(&value.to_le_bytes());
                }
                x += 4;
            }
        }
        while x < width {
            let source = packed(x)?.to_le_bytes();
            out[x * 4..x * 4 + 4].copy_from_slice(&[source[2], source[1], source[0], source[3]]);
            nonzero_alpha |= source[3] != 0;
            x += 1;
        }
    }
    if !pixel_alpha || !nonzero_alpha {
        apply_mask(
            payload,
            offset + xor_bytes,
            &mut rgba,
            width,
            height,
            bottom_up,
            execution,
        )?;
    }
    Ok(EncodedRaster { size, rgba })
}
fn index(row: &[u8], x: usize, bits: u16) -> u8 {
    match bits {
        1 => (row[x >> 3] >> (7 - (x & 7))) & 1,
        4 => (row[x >> 1] >> if x & 1 == 0 { 4 } else { 0 }) & 15,
        _ => row[x],
    }
}
fn swizzle(v: U) -> U {
    (v & U::splat(0xff00ff00)) | ((v & U::splat(0xff)) << 16) | ((v >> 16) & U::splat(0xff))
}
fn validate_masks(bits: u16, compression: u32, masks: [u32; 4]) -> Result<(), E> {
    let [red, green, blue, alpha] = masks;
    let valid = if bits == 32 {
        u32::MAX
    } else {
        (1u32 << bits) - 1
    };
    let contiguous = |mask: u32| {
        let normalized = mask.checked_shr(mask.trailing_zeros()).unwrap_or(0);
        normalized & normalized.wrapping_add(1) == 0
    };
    if red == 0
        || green == 0
        || blue == 0
        || (compression == 6 && alpha == 0)
        || ((red | green | blue | alpha) & !valid) != 0
        || masks.iter().any(|&v| v != 0 && !contiguous(v))
        || red & green != 0
        || red & blue != 0
        || red & alpha != 0
        || green & blue != 0
        || green & alpha != 0
        || blue & alpha != 0
    {
        Err(E::InvalidMasks)
    } else {
        Ok(())
    }
}
fn masked_pixel(packed: u32, masks: [u32; 4], execution: Execution) -> [u8; 4] {
    let mut maximum = [1u32; 4];
    let mut values = [0u32; 4];
    for i in 0..4 {
        if masks[i] != 0 {
            let shift = masks[i].trailing_zeros();
            maximum[i] = masks[i] >> shift;
            values[i] = (packed & masks[i]) >> shift;
        }
    }
    let mut result = if execution == Execution::CpuSimd {
        // All integers through value*255 + maximum/2 are below 2^40 and
        // exactly representable in f64. Quotients are <=255; their distance
        // from a different integer is >=1/(2^32-1), well above one f64 ULP.
        // Floor therefore preserves the original unsigned integer division.
        ((F::from(values.map(f64::from)) * F::splat(255.0)
            + F::from(maximum.map(|v| f64::from(v / 2))))
            / F::from(maximum.map(f64::from)))
        .floor()
        .to_array()
        .map(|v| v as u8)
    } else {
        std::array::from_fn(|i| {
            ((u64::from(values[i]) * 255 + u64::from(maximum[i]) / 2) / u64::from(maximum[i])) as u8
        })
    };
    if masks[3] == 0 {
        result[3] = 255;
    }
    result
}
fn apply_mask(
    payload: &[u8],
    offset: usize,
    rgba: &mut [u8],
    width: usize,
    height: usize,
    bottom_up: bool,
    execution: Execution,
) -> Result<(), E> {
    let row_bytes = width
        .checked_add(31)
        .and_then(|v| (v / 32).checked_mul(4))
        .ok_or(E::Capacity)?;
    let complete = row_bytes
        .checked_mul(height)
        .and_then(|v| offset.checked_add(v))
        .filter(|&v| v <= payload.len());
    for y in 0..height {
        let source_y = if bottom_up { height - 1 - y } else { y };
        let mask = complete.map(|_| &payload[offset + source_y * row_bytes..][..row_bytes]);
        let alpha = |x: usize| -> u32 {
            if mask.is_some_and(|row| row[x / 8] & (0x80 >> (x % 8)) != 0) {
                0
            } else {
                255
            }
        };
        let row = &mut rgba[y * width * 4..][..width * 4];
        let mut x = 0;
        if execution == Execution::CpuSimd {
            while x + 4 <= width {
                let pixels = U::from(std::array::from_fn(|i| le32(&row[(x + i) * 4..])));
                let a = U::from(std::array::from_fn(|i| alpha(x + i)));
                let pixels = ((pixels & U::splat(0x00ffffff)) | (a << 24)).to_array();
                for (i, pixel) in pixels.into_iter().enumerate() {
                    row[(x + i) * 4..(x + i + 1) * 4].copy_from_slice(&pixel.to_le_bytes());
                }
                x += 4;
            }
        }
        while x < width {
            row[x * 4 + 3] = alpha(x) as u8;
            x += 1;
        }
    }
    Ok(())
}
