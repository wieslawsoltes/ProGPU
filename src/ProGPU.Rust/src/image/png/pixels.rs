//! Original packed/16-bit samples, palette admission and Adam7 RGBA scatter.
use super::{Metadata, PngDecodeError as Error, PngDecodeExecution, layout::Layout};
use wide::u32x4;
fn sample(row: &[u8], index: usize, depth: u8) -> u16 {
    match depth {
        16 => u16::from_be_bytes([row[index * 2], row[index * 2 + 1]]),
        8 => u16::from(row[index]),
        _ => {
            let bit = index * usize::from(depth);
            let shift = 8 - depth - (bit & 7) as u8;
            u16::from((row[bit / 8] >> shift) & ((1u8 << depth) - 1))
        }
    }
}
fn scale(raw: [u16; 4], depth: u8, execution: PngDecodeExecution) -> [u8; 4] {
    if execution == PngDecodeExecution::CpuSimd {
        let value = u32x4::from(raw.map(u32::from));
        let scaled = match depth {
            8 => value,
            16 => {
                let numerator = value * u32x4::splat(255) + u32x4::splat(32767);
                // Exact division by 65535 for this bounded integer numerator:
                // floor(n/65535) == (n+1+(n>>16))>>16. No float narrowing.
                (numerator + u32x4::splat(1) + (numerator >> 16)) >> 16
            }
            _ => value * u32x4::splat(255 / ((1u32 << depth) - 1)),
        };
        return scaled.to_array().map(|v| v as u8);
    }
    raw.map(|value| {
        if depth == 8 {
            value as u8
        } else {
            let maximum = if depth == 16 {
                65535
            } else {
                (1u32 << depth) - 1
            };
            ((u32::from(value) * 255 + maximum / 2) / maximum) as u8
        }
    })
}
fn transparent(bytes: &[u8], index: usize) -> u16 {
    u16::from_be_bytes([bytes[index * 2], bytes[index * 2 + 1]])
}
pub(super) fn convert(
    metadata: Metadata<'_>,
    layout: &Layout,
    filtered: &[u8],
    output: &mut [u8],
    execution: PngDecodeExecution,
) -> Result<(), Error> {
    let r = metadata.requirements;
    if filtered.len() != layout.filtered_bytes || output.len() != r.rgba_bytes {
        return Err(Error::InvalidCompressedData);
    }
    // As in native, reject every palette index before the first RGBA write.
    if r.color_type == 3 {
        let entries = metadata.palette.len() / 3;
        let mut offset = 0;
        for pass in &layout.passes[..layout.count] {
            let stride = pass.row_bytes + 1;
            for y in 0..pass.height as usize {
                let row = &filtered[offset + y * stride + 1..offset + (y + 1) * stride];
                for x in 0..pass.width as usize {
                    if usize::from(sample(row, x, r.bit_depth)) >= entries {
                        return Err(Error::InvalidCompressedData);
                    }
                }
            }
            offset += pass.filtered_bytes;
        }
    }
    let maximum = if r.bit_depth == 16 {
        65535
    } else {
        (1u16 << r.bit_depth) - 1
    };
    let mut offset = 0;
    for pass in &layout.passes[..layout.count] {
        let stride = pass.row_bytes + 1;
        for row_index in 0..pass.height as usize {
            let row = &filtered[offset + row_index * stride + 1..offset + (row_index + 1) * stride];
            for column in 0..pass.width as usize {
                let first = column * usize::from(r.channel_count);
                let mut raw = [0, 0, 0, maximum];
                let mut zero_alpha = false;
                let rgba = if r.color_type == 3 {
                    let index = usize::from(sample(row, column, r.bit_depth));
                    let base = index * 3;
                    [
                        metadata.palette[base],
                        metadata.palette[base + 1],
                        metadata.palette[base + 2],
                        metadata.transparency.get(index).copied().unwrap_or(255),
                    ]
                } else {
                    match r.color_type {
                        0 => {
                            let gray = sample(row, first, r.bit_depth);
                            raw[..3].fill(gray);
                            zero_alpha = metadata.transparency.len() == 2
                                && transparent(metadata.transparency, 0) == gray;
                        }
                        2 => {
                            for (channel, value) in raw[..3].iter_mut().enumerate() {
                                *value = sample(row, first + channel, r.bit_depth);
                            }
                            zero_alpha = metadata.transparency.len() == 6
                                && (0..3).all(|channel| {
                                    transparent(metadata.transparency, channel) == raw[channel]
                                });
                        }
                        4 => {
                            let gray = sample(row, first, r.bit_depth);
                            raw[..3].fill(gray);
                            raw[3] = sample(row, first + 1, r.bit_depth);
                        }
                        6 => {
                            for (channel, value) in raw.iter_mut().enumerate() {
                                *value = sample(row, first + channel, r.bit_depth);
                            }
                        }
                        _ => return Err(Error::InvalidCompressedData),
                    }
                    let mut rgba = scale(raw, r.bit_depth, execution);
                    if zero_alpha {
                        rgba[3] = 0;
                    }
                    rgba
                };
                let x = pass.start_x + column as u32 * pass.step_x;
                let y = pass.start_y + row_index as u32 * pass.step_y;
                let destination = (y as usize * r.width as usize + x as usize) * 4;
                output[destination..destination + 4].copy_from_slice(&rgba);
            }
        }
        offset += pass.filtered_bytes;
    }
    if offset != filtered.len() {
        return Err(Error::InvalidCompressedData);
    }
    Ok(())
}
