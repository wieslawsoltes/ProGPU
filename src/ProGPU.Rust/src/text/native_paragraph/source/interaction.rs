//! Original double advance interaction; draw offsets do not become caret advances.
use super::*;
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceClusterBox {
    pub input_start: i32,
    pub input_end: i32,
    pub line_index: u32,
    pub bidi_level: i8,
    pub reserved: [u8; 3],
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceCaretStop {
    pub input_position: i32,
    pub line_index: u32,
    pub x: f64,
    pub y: f64,
    pub height: f64,
    pub bidi_level: i8,
    pub trailing: u8,
    pub reserved: [u8; 2],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceRectangle {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceHit {
    pub input_position: i32,
    pub line_index: u32,
    pub bounds: NativeSourceRectangle,
    pub bidi_level: i8,
    pub trailing: bool,
    pub inside: bool,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeSourceInteraction<'a> {
    pub(super) boxes: &'a [NativeSourceClusterBox],
    pub(super) carets: &'a [NativeSourceCaretStop],
    pub(super) lines: &'a [NativeSourceLineMetrics],
}
fn min(a: f64, b: f64) -> f64 {
    if b < a { b } else { a }
}
fn max(a: f64, b: f64) -> f64 {
    if a < b { b } else { a }
}
fn valid_box(b: NativeSourceClusterBox) -> bool {
    [b.x, b.y, b.width, b.height, b.x + b.width, b.y + b.height]
        .iter()
        .all(|v| v.is_finite())
        && b.width >= 0.0
        && b.height >= 0.0
        && b.input_end > b.input_start
        && (0..=125).contains(&b.bidi_level)
}
fn valid_caret(c: NativeSourceCaretStop) -> bool {
    [c.x, c.y, c.height].iter().all(|v| v.is_finite())
        && c.height >= 0.0
        && (0..=125).contains(&c.bidi_level)
        && c.trailing <= 1
}
fn requirements(
    glyphs: &[NativeSourceGlyphPosition],
    lines: &[NativeSourceLineMetrics],
    ends: &[i32],
    levels: &[i8],
) -> Result<usize, NativeSourceError> {
    require(
        ends.len() == glyphs.len()
            && levels.len() == glyphs.len()
            && glyphs.len() <= u32::MAX as usize / 2
            && lines.len() <= u32::MAX as usize,
    )?;
    let mut expected = 0;
    let mut top = 0.0;
    let mut clusters = 0;
    for line in lines {
        let bottom = top + line.height;
        require(
            line.baseline_y.is_finite()
                && line.height.is_finite()
                && line.height >= 0.0
                && bottom.is_finite()
                && bottom <= f64::from(f32::MAX)
                && line.baseline_y >= top
                && line.baseline_y <= bottom
                && line.glyph_start as usize == expected
                && line.glyph_count as usize <= glyphs.len() - expected
                && line.origin_x.is_finite(),
        )?;
        let end = expected + line.glyph_count as usize;
        let mut pen = line.origin_x;
        for i in expected..end {
            let g = glyphs[i];
            require(
                [g.x, g.y, g.advance_x, g.advance_y, g.x + g.advance_x]
                    .iter()
                    .all(|v| v.is_finite())
                    && ends[i] > g.cluster
                    && (0..=125).contains(&levels[i]),
            )?;
            pen += g.advance_x;
            require(pen.is_finite())?;
            if i == expected || glyphs[i - 1].cluster != g.cluster {
                clusters += 1;
            }
        }
        expected = end;
        top = bottom;
    }
    require(expected == glyphs.len())?;
    Ok(clusters)
}
fn emit(
    glyphs: &[NativeSourceGlyphPosition],
    lines: &[NativeSourceLineMetrics],
    ends: &[i32],
    levels: &[i8],
    mut emit_box: impl FnMut(NativeSourceClusterBox) -> Result<(), NativeSourceError>,
    mut emit_caret: impl FnMut(NativeSourceCaretStop) -> Result<(), NativeSourceError>,
) -> Result<(), NativeSourceError> {
    let mut top = 0.0;
    let mut previous = None;
    for (line_index, line) in lines.iter().enumerate() {
        let mut pen = line.origin_x;
        let mut i = line.glyph_start as usize;
        let end = i + line.glyph_count as usize;
        while i < end {
            let first = i;
            let cluster = glyphs[i].cluster;
            let (mut left, mut right) = (pen, pen);
            let mut cluster_end = ends[i];
            loop {
                let next = pen + glyphs[i].advance_x;
                left = min(left, min(pen, next));
                right = max(right, max(pen, next));
                pen = next;
                cluster_end = cluster_end.max(ends[i]);
                i += 1;
                if i == end || glyphs[i].cluster != cluster {
                    break;
                }
            }
            let b = NativeSourceClusterBox {
                input_start: cluster,
                input_end: cluster_end,
                line_index: line_index as u32,
                bidi_level: levels[first],
                reserved: [0; 3],
                x: left,
                y: top,
                width: max(0.0, right - left),
                height: line.height,
            };
            emit_box(b)?;
            let rtl = b.bidi_level & 1 != 0;
            let leading = NativeSourceCaretStop {
                input_position: if rtl { b.input_end } else { b.input_start },
                line_index: b.line_index,
                x: b.x,
                y: b.y,
                height: b.height,
                bidi_level: b.bidi_level,
                trailing: u8::from(rtl),
                reserved: [0; 2],
            };
            let trailing = NativeSourceCaretStop {
                input_position: if rtl { b.input_start } else { b.input_end },
                x: b.x + b.width,
                trailing: u8::from(!rtl),
                ..leading
            };
            for c in [leading, trailing] {
                if previous.is_some_and(|p: NativeSourceCaretStop| {
                    p.input_position == c.input_position
                        && p.trailing == c.trailing
                        && (p.x - c.x).abs() < f64::from(0.0001f32)
                        && (p.y - c.y).abs() < f64::from(0.0001f32)
                }) {
                    continue;
                }
                emit_caret(c)?;
                previous = Some(c);
            }
        }
        top += line.height;
    }
    Ok(())
}
pub(super) fn build(
    glyphs: &[NativeSourceGlyphPosition],
    lines: &[NativeSourceLineMetrics],
    ends: &[i32],
    levels: &[i8],
) -> Result<(Vec<NativeSourceClusterBox>, Vec<NativeSourceCaretStop>), NativeSourceError> {
    let count = requirements(glyphs, lines, ends, levels)?;
    let mut boxes = reserve(count)?;
    let mut carets = reserve(count * 2)?;
    emit(
        glyphs,
        lines,
        ends,
        levels,
        |b| {
            boxes.push(b);
            Ok(())
        },
        |c| {
            carets.push(c);
            Ok(())
        },
    )?;
    Ok((boxes, carets))
}
/// Shared original emitter compares every retained field without extra arrays.
pub(super) fn admit_records(
    glyphs: &[NativeSourceGlyphPosition],
    lines: &[NativeSourceLineMetrics],
    ends: &[i32],
    levels: &[i8],
    boxes: &[NativeSourceClusterBox],
    carets: &[NativeSourceCaretStop],
) -> Result<(), NativeSourceError> {
    let count = requirements(glyphs, lines, ends, levels)?;
    require(boxes.len() == count && carets.len() <= count * 2)?;
    let (mut box_index, mut caret_index) = (0, 0);
    let exact = |a: f64, b: f64| a.to_bits() == b.to_bits();
    emit(
        glyphs,
        lines,
        ends,
        levels,
        |a| {
            let b = boxes
                .get(box_index)
                .ok_or(NativeSourceError::InvalidArgument)?;
            box_index += 1;
            require(
                a.input_start == b.input_start
                    && a.input_end == b.input_end
                    && a.line_index == b.line_index
                    && a.bidi_level == b.bidi_level
                    && a.reserved == b.reserved
                    && exact(a.x, b.x)
                    && exact(a.y, b.y)
                    && exact(a.width, b.width)
                    && exact(a.height, b.height),
            )
        },
        |a| {
            let b = carets
                .get(caret_index)
                .ok_or(NativeSourceError::InvalidArgument)?;
            caret_index += 1;
            require(
                a.input_position == b.input_position
                    && a.line_index == b.line_index
                    && a.bidi_level == b.bidi_level
                    && a.trailing == b.trailing
                    && a.reserved == b.reserved
                    && exact(a.x, b.x)
                    && exact(a.y, b.y)
                    && exact(a.height, b.height),
            )
        },
    )?;
    require(box_index == boxes.len() && caret_index == carets.len())
}
impl<'a> NativeSourceInteraction<'a> {
    pub fn boxes(self) -> &'a [NativeSourceClusterBox] {
        self.boxes
    }
    pub fn carets(self) -> &'a [NativeSourceCaretStop] {
        self.carets
    }
    pub fn line(self, index: u32) -> Result<Self, FontError> {
        if index as usize >= self.lines.len() {
            return Err(FontError::InvalidArgument);
        }
        let a = self.boxes.partition_point(|b| b.line_index < index);
        let b = self.boxes.partition_point(|b| b.line_index <= index);
        let c = self.carets.partition_point(|c| c.line_index < index);
        let d = self.carets.partition_point(|c| c.line_index <= index);
        Ok(Self {
            boxes: &self.boxes[a..b],
            carets: &self.carets[c..d],
            lines: self.lines,
        })
    }
    pub fn hit_test_line(self, index: u32, x: f64) -> Result<NativeSourceHit, FontError> {
        let row = self.line(index)?;
        row.hit_test(x, self.lines[index as usize].top)
    }
    pub fn hit_test(self, x: f64, y: f64) -> Result<NativeSourceHit, FontError> {
        if self.boxes.is_empty() || !x.is_finite() || !y.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let mut best = None;
        let mut distance = f64::INFINITY;
        let mut inside = false;
        for &b in self.boxes {
            if !valid_box(b) {
                return Err(FontError::InvalidArgument);
            }
            let right = b.x + b.width;
            let bottom = b.y + b.height;
            let dx = if x < b.x {
                b.x - x
            } else if x > right {
                x - right
            } else {
                0.0
            };
            let dy = if y < b.y {
                b.y - y
            } else if y > bottom {
                y - bottom
            } else {
                0.0
            };
            let d = dx * dx + dy * dy;
            if d >= distance {
                continue;
            }
            distance = d;
            best = Some(b);
            inside = dx == 0.0 && dy == 0.0;
        }
        let b = best.ok_or(FontError::InvalidArgument)?;
        let visual = x >= b.x + b.width * 0.5;
        let trailing = if b.bidi_level & 1 != 0 {
            !visual
        } else {
            visual
        };
        Ok(NativeSourceHit {
            input_position: if trailing { b.input_end } else { b.input_start },
            line_index: b.line_index,
            bounds: NativeSourceRectangle {
                x: b.x,
                y: b.y,
                width: b.width,
                height: b.height,
            },
            bidi_level: b.bidi_level,
            trailing,
            inside,
        })
    }
    fn caret_index(self, input: i32, trailing: bool, weighted: bool) -> Result<usize, FontError> {
        if self.carets.is_empty() {
            return Err(FontError::InvalidArgument);
        }
        let (mut best, mut distance) = (0, i64::MAX);
        for (i, &c) in self.carets.iter().enumerate() {
            if !valid_caret(c) {
                return Err(FontError::InvalidArgument);
            }
            let d = (i64::from(c.input_position) - i64::from(input)).abs();
            let d = if weighted {
                d * 4 + i64::from((c.trailing != 0) != trailing)
            } else {
                d
            };
            if d < distance
                || !weighted
                    && d == distance
                    && (c.trailing != 0) == trailing
                    && (self.carets[best].trailing != 0) != trailing
            {
                best = i;
                distance = d;
            }
        }
        Ok(best)
    }
    pub fn caret(self, input: i32, trailing: bool) -> Result<NativeSourceCaretStop, FontError> {
        Ok(self.carets[self.caret_index(input, trailing, false)?])
    }
    pub fn move_caret(
        self,
        input: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<NativeSourceCaretStop, FontError> {
        let mut i = self.caret_index(input, trailing, true)?;
        if direction < 0 && i != 0 {
            i -= 1;
        } else if direction > 0 && i + 1 < self.carets.len() {
            i += 1;
        }
        Ok(self.carets[i])
    }
    /// Preflight the whole original selection before modifying caller storage.
    pub fn selection_into(
        self,
        start: i32,
        end: i32,
        out: &mut [NativeSourceRectangle],
    ) -> Result<usize, FontError> {
        if end < start {
            return Err(FontError::InvalidArgument);
        }
        let mut count = 0;
        let mut previous = u32::MAX;
        let mut right = 0.0;
        for &b in self.boxes {
            if !valid_box(b) {
                return Err(FontError::InvalidArgument);
            }
            if b.input_end <= start || b.input_start >= end {
                continue;
            }
            if count == 0 || b.line_index != previous || b.x > right + 0.5 {
                count += 1;
                right = b.x + b.width;
            } else {
                right = max(right, b.x + b.width);
            }
            previous = b.line_index;
        }
        if out.len() < count {
            return Err(FontError::Capacity);
        }
        count = 0;
        previous = u32::MAX;
        for &b in self.boxes {
            if b.input_end <= start || b.input_start >= end {
                continue;
            }
            if count != 0
                && b.line_index == previous
                && b.x <= out[count - 1].x + out[count - 1].width + 0.5
            {
                let r = &mut out[count - 1];
                let far = max(r.x + r.width, b.x + b.width);
                r.x = min(r.x, b.x);
                r.width = far - r.x;
            } else {
                out[count] = NativeSourceRectangle {
                    x: b.x,
                    y: b.y,
                    width: b.width,
                    height: b.height,
                };
                count += 1;
            }
            previous = b.line_index;
        }
        Ok(count)
    }
}
