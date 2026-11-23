//! Original fixed seven-pass Adam7 byte inventory and checked extent arithmetic.
use super::{PngDecodeError as Error, PngDecodeRequirements};
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Pass {
    pub start_x: u32,
    pub start_y: u32,
    pub step_x: u32,
    pub step_y: u32,
    pub width: u32,
    pub height: u32,
    pub row_bytes: usize,
    pub filtered_bytes: usize,
}
#[derive(Clone, Debug, Default)]
pub(super) struct Layout {
    pub passes: [Pass; 7],
    pub count: usize,
    pub filtered_bytes: usize,
    pub bytes_per_pixel: usize,
}
impl Layout {
    pub fn new(r: PngDecodeRequirements) -> Result<Self, Error> {
        let bits = usize::from(r.channel_count)
            .checked_mul(usize::from(r.bit_depth))
            .ok_or(Error::UnsupportedFormat)?;
        if bits == 0 {
            return Err(Error::UnsupportedFormat);
        }
        let mut result = Self {
            bytes_per_pixel: if bits < 8 { 1 } else { (bits + 7) / 8 },
            ..Self::default()
        };
        match r.interlace_method {
            0 => result.append(r.width, r.height, bits, [0, 0, 1, 1])?,
            1 => {
                for pass in [
                    [0, 0, 8, 8],
                    [4, 0, 8, 8],
                    [0, 4, 4, 8],
                    [2, 0, 4, 4],
                    [0, 2, 2, 4],
                    [1, 0, 2, 2],
                    [0, 1, 1, 2],
                ] {
                    result.append(r.width, r.height, bits, pass)?;
                }
            }
            _ => return Err(Error::UnsupportedFormat),
        }
        if result.count == 0 {
            return Err(Error::UnsupportedFormat);
        }
        Ok(result)
    }
    fn append(
        &mut self,
        width: u32,
        height: u32,
        bits: usize,
        [start_x, start_y, step_x, step_y]: [u32; 4],
    ) -> Result<(), Error> {
        let extent = |size, start, step| {
            if size <= start {
                0
            } else {
                1 + (size - 1 - start) / step
            }
        };
        let width = extent(width, start_x, step_x);
        let height = extent(height, start_y, step_y);
        if width == 0 || height == 0 {
            return Ok(());
        }
        let row_bytes = (width as usize)
            .checked_mul(bits)
            .and_then(|n| n.checked_add(7))
            .ok_or(Error::UnsupportedFormat)?
            / 8;
        let filtered_bytes = row_bytes
            .checked_add(1)
            .and_then(|n| n.checked_mul(height as usize))
            .ok_or(Error::UnsupportedFormat)?;
        self.filtered_bytes = self
            .filtered_bytes
            .checked_add(filtered_bytes)
            .ok_or(Error::UnsupportedFormat)?;
        self.passes[self.count] = Pass {
            start_x,
            start_y,
            step_x,
            step_y,
            width,
            height,
            row_bytes,
            filtered_bytes,
        };
        self.count += 1;
        Ok(())
    }
}
