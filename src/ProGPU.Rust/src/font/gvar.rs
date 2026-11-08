//! Original TrueType variation data primitives. No hinting or rasterization.
//! Direct ports of ProGPU's gvar packed-data and contour interpolation sources.
use super::{FontError, bytes};
mod font;
pub use font::{Font, GlyphData, Header, TupleHeader, TupleHeaders, tuple_scalar};

/// Validated packed point numbers, borrowing their exact encoded prefix.
/// Parsing and iteration allocate nothing; duplicate point indices remain intact.
#[derive(Clone, Copy, Debug)]
pub struct PackedPoints<'a> {
    data: &'a [u8],
    count: u32,
    all: bool,
    start: usize,
}
impl<'a> PackedPoints<'a> {
    /// Preflight every run and accumulated point before publishing a view.
    pub fn parse(data: &'a [u8]) -> Result<Self, FontError> {
        let first = *data.first().ok_or(FontError::InvalidGlyph)?;
        if first == 0 {
            return Ok(Self {
                data: &data[..1],
                count: 0,
                all: true,
                start: 1,
            });
        }
        let mut offset = 1;
        let count = if first & 0x80 != 0 {
            let second = *data.get(offset).ok_or(FontError::InvalidGlyph)?;
            offset += 1;
            (u32::from(first & 0x7f) << 8) | u32::from(second)
        } else {
            u32::from(first)
        };
        let start = offset;
        let mut written = 0;
        let mut current = 0u32;
        while written < count {
            let control = *data.get(offset).ok_or(FontError::InvalidGlyph)?;
            offset += 1;
            let run = u32::from(control & 0x7f) + 1;
            let words = control & 0x80 != 0;
            if run > count - written {
                return Err(FontError::InvalidGlyph);
            }
            let size = if words { 2 } else { 1 };
            bytes::bytes(data, offset, run as usize * size).ok_or(FontError::InvalidGlyph)?;
            for i in 0..run as usize {
                let delta = if words {
                    u32::from(bytes::u16(data, offset + i * 2).unwrap())
                } else {
                    u32::from(data[offset + i])
                };
                current = current.checked_add(delta).ok_or(FontError::InvalidGlyph)?;
            }
            offset += run as usize * size;
            written += run;
        }
        Ok(Self {
            data: &data[..offset],
            count,
            all: false,
            start,
        })
    }
    /// Original explicit point count, zero for the all-points marker.
    pub fn len(self) -> u32 {
        self.count
    }
    /// Whether there are no explicit point numbers (separate from all-points).
    pub fn is_empty(self) -> bool {
        self.count == 0
    }
    /// Original single-zero all-points marker. A two-byte zero count stays false.
    pub fn all_points(self) -> bool {
        self.all
    }
    /// Length of the validated encoded prefix; following payload is untouched.
    pub fn bytes_consumed(self) -> usize {
        self.data.len()
    }
    /// Iterate original cumulative unsigned point numbers without allocating.
    pub fn iter(self) -> PointIter<'a> {
        PointIter {
            data: self.data,
            cursor: self.start,
            remaining: self.count as usize,
            run: 0,
            words: false,
            current: 0,
        }
    }
    /// Write only after checking full output capacity; the extra output tail stays intact.
    pub fn decode_into(self, output: &mut [u32]) -> Result<usize, FontError> {
        if output.len() < self.count as usize {
            return Err(FontError::Capacity);
        }
        for (slot, value) in output.iter_mut().zip(self.iter()) {
            *slot = value;
        }
        Ok(self.count as usize)
    }
}
/// Allocation-free iterator over a preflighted packed point inventory.
#[derive(Clone, Debug)]
pub struct PointIter<'a> {
    data: &'a [u8],
    cursor: usize,
    remaining: usize,
    run: usize,
    words: bool,
    current: u32,
}
impl Iterator for PointIter<'_> {
    type Item = u32;
    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 {
            return None;
        }
        if self.run == 0 {
            let control = self.data[self.cursor];
            self.cursor += 1;
            self.run = usize::from(control & 0x7f) + 1;
            self.words = control & 0x80 != 0;
        }
        let delta = if self.words {
            let v = bytes::u16(self.data, self.cursor).unwrap();
            self.cursor += 2;
            u32::from(v)
        } else {
            let v = self.data[self.cursor];
            self.cursor += 1;
            u32::from(v)
        };
        self.current += delta;
        self.run -= 1;
        self.remaining -= 1;
        Some(self.current)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}
impl ExactSizeIterator for PointIter<'_> {}
impl std::iter::FusedIterator for PointIter<'_> {}

/// Validated packed signed deltas, preserving zero-run precedence over word runs.
#[derive(Clone, Copy, Debug)]
pub struct PackedDeltas<'a> {
    data: &'a [u8],
    count: u32,
}
impl<'a> PackedDeltas<'a> {
    /// Preflight exactly the requested delta count, ignoring any trailing payload.
    pub fn parse(data: &'a [u8], count: u32) -> Result<Self, FontError> {
        let mut offset = 0;
        let mut written = 0;
        while written < count {
            let control = *data.get(offset).ok_or(FontError::InvalidGlyph)?;
            offset += 1;
            let run = u32::from(control & 0x3f) + 1;
            if run > count - written {
                return Err(FontError::InvalidGlyph);
            }
            let size = if control & 0x80 != 0 {
                0
            } else if control & 0x40 != 0 {
                2
            } else {
                1
            };
            bytes::bytes(data, offset, run as usize * size).ok_or(FontError::InvalidGlyph)?;
            offset += run as usize * size;
            written += run;
        }
        Ok(Self {
            data: &data[..offset],
            count,
        })
    }
    /// Validated requested count.
    pub fn len(self) -> u32 {
        self.count
    }
    /// Whether no encoded bytes or output values are needed.
    pub fn is_empty(self) -> bool {
        self.count == 0
    }
    /// Complete consumed prefix length.
    pub fn bytes_consumed(self) -> usize {
        self.data.len()
    }
    /// Iterate original signed/zero values without allocating.
    pub fn iter(self) -> DeltaIter<'a> {
        DeltaIter {
            data: self.data,
            cursor: 0,
            remaining: self.count as usize,
            run: 0,
            control: 0,
        }
    }
    /// Decode atomically with respect to capacity rejection, preserving the output tail.
    pub fn decode_into(self, output: &mut [i16]) -> Result<usize, FontError> {
        if output.len() < self.count as usize {
            return Err(FontError::Capacity);
        }
        for (slot, value) in output.iter_mut().zip(self.iter()) {
            *slot = value;
        }
        Ok(self.count as usize)
    }
}
/// Allocation-free iterator over validated packed signed deltas.
#[derive(Clone, Debug)]
pub struct DeltaIter<'a> {
    data: &'a [u8],
    cursor: usize,
    remaining: usize,
    run: usize,
    control: u8,
}
impl Iterator for DeltaIter<'_> {
    type Item = i16;
    fn next(&mut self) -> Option<i16> {
        if self.remaining == 0 {
            return None;
        }
        if self.run == 0 {
            self.control = self.data[self.cursor];
            self.cursor += 1;
            self.run = usize::from(self.control & 0x3f) + 1;
        }
        let value = if self.control & 0x80 != 0 {
            0
        } else if self.control & 0x40 != 0 {
            let v = bytes::i16(self.data, self.cursor).unwrap();
            self.cursor += 2;
            v
        } else {
            let v = i16::from(self.data[self.cursor] as i8);
            self.cursor += 1;
            v
        };
        self.run -= 1;
        self.remaining -= 1;
        Some(value)
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}
impl ExactSizeIterator for DeltaIter<'_> {}
impl std::iter::FusedIterator for DeltaIter<'_> {}

/// Infer original untouched deltas using the bounded circular contour traversal.
/// All capacities and the complete contour inventory are validated before any
/// write. Nonzero touched bytes are retained; untouched contours and output
/// tails stay unchanged. This is variation interpolation, not a hinting policy.
pub fn infer_untouched(
    points: &[[f32; 2]],
    ends: &[u16],
    x: &mut [f32],
    y: &mut [f32],
    touched: &[u8],
) -> Result<(), FontError> {
    if x.len() < points.len() || y.len() < points.len() || touched.len() < points.len() {
        return Err(FontError::Capacity);
    }
    let mut start = 0;
    for &end in ends {
        let end = usize::from(end);
        if end < start || end >= points.len() {
            return Err(FontError::InvalidGlyph);
        }
        start = end + 1;
    }
    if (!points.is_empty() && (ends.is_empty() || start != points.len()))
        || (points.is_empty() && !ends.is_empty())
    {
        return Err(FontError::InvalidGlyph);
    }
    start = 0;
    for &end in ends {
        let end = usize::from(end);
        interpolate_axis(points, x, touched, start, end, 0);
        interpolate_axis(points, y, touched, start, end, 1);
        start = end + 1;
    }
    Ok(())
}
fn interpolate_axis(
    points: &[[f32; 2]],
    deltas: &mut [f32],
    touched: &[u8],
    start: usize,
    end: usize,
    axis: usize,
) {
    let next = |i| if i == end { start } else { i + 1 };
    let mut first = start;
    while first <= end && touched[first] == 0 {
        first += 1;
    }
    if first > end {
        return;
    }
    let mut second = next(first);
    while second != first && touched[second] == 0 {
        second = next(second);
    }
    if second == first {
        let value = deltas[first];
        deltas[start..=end].fill(value);
        return;
    }
    let mut current = first;
    loop {
        let mut following = next(current);
        while touched[following] == 0 {
            following = next(following);
        }
        let mut point = next(current);
        while point != following {
            deltas[point] = interpolate(
                points[point][axis],
                points[current][axis],
                points[following][axis],
                deltas[current],
                deltas[following],
            );
            point = next(point);
        }
        current = following;
        if current == first {
            break;
        }
    }
}
fn interpolate(target: f32, mut first: f32, mut second: f32, mut a: f32, mut b: f32) -> f32 {
    if first == second {
        return if target <= first {
            if a < b { a } else { b }
        } else if a > b {
            a
        } else {
            b
        };
    }
    if first > second {
        std::mem::swap(&mut first, &mut second);
        std::mem::swap(&mut a, &mut b);
    }
    if target <= first {
        return a;
    }
    if target >= second {
        return b;
    }
    let ratio = (target - first) / (second - first);
    a + ratio * (b - a)
}
