//! Original in-place filter ordering; independent Up lanes use SIMD.
use super::{PngDecodeError as Error, PngDecodeExecution, layout::Layout};
use wide::u32x4;
fn paeth(left: u8, above: u8, upper_left: u8) -> u8 {
    let [a, b, c] = [left, above, upper_left].map(i32::from);
    let prediction = a + b - c;
    let [da, db, dc] = [
        (prediction - a).abs(),
        (prediction - b).abs(),
        (prediction - c).abs(),
    ];
    if da <= db && da <= dc {
        left
    } else if db <= dc {
        above
    } else {
        upper_left
    }
}
pub(super) fn unfilter(
    layout: &Layout,
    filtered: &mut [u8],
    execution: PngDecodeExecution,
) -> Result<(), Error> {
    if filtered.len() != layout.filtered_bytes {
        return Err(Error::InvalidCompressedData);
    }
    let mut offset = 0;
    for pass in &layout.passes[..layout.count] {
        let data = &mut filtered[offset..offset + pass.filtered_bytes];
        let stride = pass.row_bytes + 1;
        for row in 0..pass.height as usize {
            let start = row * stride;
            let kind = data[start];
            if kind > 4 {
                return Err(Error::InvalidCompressedData);
            }
            if kind == 0 {
                continue;
            }
            let mut column = 0;
            if kind == 2 && row != 0 && execution == PngDecodeExecution::CpuSimd {
                // Original Up has independent byte lanes and wraps modulo 256.
                while column + 4 <= pass.row_bytes {
                    let at = start + 1 + column;
                    let encoded =
                        u32x4::from(std::array::from_fn(|lane| u32::from(data[at + lane])));
                    let above = u32x4::from(std::array::from_fn(|lane| {
                        u32::from(data[at + lane - stride])
                    }));
                    let sum = ((encoded + above) & u32x4::splat(255)).to_array();
                    for lane in 0..4 {
                        data[at + lane] = sum[lane] as u8;
                    }
                    column += 4;
                }
            }
            // Sub, Average and Paeth depend on already reconstructed bytes in
            // this row; preserve the original scalar admission and tie order.
            while column < pass.row_bytes {
                let at = start + 1 + column;
                let left = if column >= layout.bytes_per_pixel {
                    data[at - layout.bytes_per_pixel]
                } else {
                    0
                };
                let above = if row != 0 { data[at - stride] } else { 0 };
                let upper_left = if row != 0 && column >= layout.bytes_per_pixel {
                    data[at - stride - layout.bytes_per_pixel]
                } else {
                    0
                };
                let prediction = match kind {
                    1 => left,
                    2 => above,
                    3 => ((u16::from(left) + u16::from(above)) / 2) as u8,
                    4 => paeth(left, above, upper_left),
                    _ => 0,
                };
                data[at] = data[at].wrapping_add(prediction);
                column += 1;
            }
        }
        offset += pass.filtered_bytes;
    }
    if offset != filtered.len() {
        return Err(Error::InvalidCompressedData);
    }
    Ok(())
}
