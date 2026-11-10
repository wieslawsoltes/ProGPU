//! Original ordered RLE4/RLE8 commands; run writes use bounded slice operations.
use super::EncodedImageError as E;

pub(super) fn decode(
    encoded: &[u8],
    indices: &mut [u8],
    width: usize,
    height: usize,
    bits: u16,
    bottom_up: bool,
) -> Result<usize, E> {
    let mut offset = 0;
    let mut x = 0usize;
    let mut source_y = 0usize;
    while offset < encoded.len() {
        let command = encoded.get(offset..offset + 2).ok_or(E::RleCommand)?;
        offset += 2;
        let [count, value] = [command[0], command[1]];
        if count != 0 {
            let target = target(
                indices,
                width,
                height,
                bottom_up,
                x,
                source_y,
                usize::from(count),
            )?;
            if bits == 8 {
                target.fill(value);
            } else {
                let mut pairs = target.chunks_exact_mut(2);
                for pair in &mut pairs {
                    pair.copy_from_slice(&[value >> 4, value & 15]);
                }
                if let Some(last) = pairs.into_remainder().first_mut() {
                    *last = value >> 4;
                }
            }
            x += usize::from(count);
            continue;
        }
        match value {
            0 => {
                x = 0;
                source_y += 1;
                if source_y > height {
                    return Err(E::RleRows);
                }
            }
            1 => return Ok(offset),
            2 => {
                let delta = encoded.get(offset..offset + 2).ok_or(E::RleDelta)?;
                offset += 2;
                x = x.checked_add(usize::from(delta[0])).ok_or(E::Capacity)?;
                source_y = source_y
                    .checked_add(usize::from(delta[1]))
                    .ok_or(E::Capacity)?;
                if x > width || source_y > height {
                    return Err(E::RleDeltaBounds);
                }
            }
            _ => {
                let count = usize::from(value);
                let bytes = if bits == 8 { count } else { count.div_ceil(2) };
                let padded = (bytes + 1) & !1;
                let source = encoded.get(offset..offset + padded).ok_or(E::RleAbsolute)?;
                let target = target(indices, width, height, bottom_up, x, source_y, count)?;
                if bits == 8 {
                    target.copy_from_slice(&source[..count]);
                } else {
                    let mut pairs = target.chunks_exact_mut(2);
                    for (pair, byte) in pairs.by_ref().zip(source) {
                        pair.copy_from_slice(&[byte >> 4, byte & 15]);
                    }
                    if let Some(last) = pairs.into_remainder().first_mut() {
                        *last = source[count / 2] >> 4;
                    }
                }
                x += count;
                offset += padded;
            }
        }
    }
    Err(E::RleEnd)
}
fn target(
    indices: &mut [u8],
    width: usize,
    height: usize,
    bottom_up: bool,
    x: usize,
    source_y: usize,
    count: usize,
) -> Result<&mut [u8], E> {
    if source_y >= height || x >= width || x.checked_add(count).is_none_or(|v| v > width) {
        return Err(E::RleRun);
    }
    let y = if bottom_up {
        height - 1 - source_y
    } else {
        source_y
    };
    let offset = y * width + x;
    indices.get_mut(offset..offset + count).ok_or(E::RleRun)
}
