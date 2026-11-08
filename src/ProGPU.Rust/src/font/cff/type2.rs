//! Original bounded CFF1/CFF2 Type 2 evaluator; no external font engine.
use super::{Font, Font2, Index};
use crate::{
    font::{FontError, ItemVariationStore, bytes},
    path::Segment,
};

/// Optional source-owned charstring-to-design mapping, applied before float narrowing.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct OutlineTransform(pub [f64; 6]);
impl Default for OutlineTransform {
    fn default() -> Self {
        Self([1.0, 0.0, 0.0, 1.0, 0.0, 0.0])
    }
}
/// Owned unhinted CFF1 path generation retaining its exact selected source.
#[derive(Clone, Debug)]
pub struct Outline {
    source: Font,
    glyph: u32,
    segments: Vec<Segment>,
}
impl Outline {
    /// Original CFF face owner, retained after the caller's handle is dropped.
    pub fn source(&self) -> &Font {
        &self.source
    }
    /// Original charstring slot.
    pub fn glyph_index(&self) -> u32 {
        self.glyph
    }
    /// Original closed line/cubic path records.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
}
impl Font {
    /// Measure the original raw unhinted Type 2 output without allocating segments.
    pub fn outline_segment_count(&self, glyph: u32) -> Result<u32, FontError> {
        self.evaluate(glyph, None, None)
    }
    /// Atomically evaluate original raw charstring coordinates into owned paths.
    pub fn outline(&self, glyph: u32) -> Result<Outline, FontError> {
        self.build_outline(glyph, None)
    }
    /// Apply an explicit original double mapping before narrowing each path point.
    pub fn outline_transformed(
        &self,
        glyph: u32,
        transform: OutlineTransform,
    ) -> Result<Outline, FontError> {
        self.build_outline(glyph, Some(transform))
    }
    fn build_outline(
        &self,
        glyph: u32,
        transform: Option<OutlineTransform>,
    ) -> Result<Outline, FontError> {
        let count = self.evaluate(glyph, transform, None)?;
        let mut segments = Vec::new();
        segments
            .try_reserve_exact(count as usize)
            .map_err(|_| FontError::Capacity)?;
        let written = self.evaluate(glyph, transform, Some((&mut segments, count)))?;
        if written != count {
            return Err(FontError::InvalidGlyph);
        }
        Ok(Outline {
            source: self.clone(),
            glyph,
            segments,
        })
    }
    fn evaluate(
        &self,
        glyph: u32,
        transform: Option<OutlineTransform>,
        output: Option<(&mut Vec<Segment>, u32)>,
    ) -> Result<u32, FontError> {
        if transform.is_some_and(|m| m.0.iter().any(|v| !v.is_finite())) {
            return Err(FontError::InvalidArgument);
        }
        let program = self.char_strings().get(glyph)?;
        let local = self.local_subroutines(glyph)?;
        let mut e = Evaluator {
            writer: Writer {
                output,
                transform,
                count: 0,
                start: [0.0; 2],
                current: [0.0; 2],
                active: false,
            },
            global: self.global_subroutines(),
            local,
            operands: [0.0; 513],
            len: 0,
            transient: [0.0; 32],
            stems: 0,
            width_seen: false,
            p: [0.0; 2],
            random: glyph.wrapping_add(1),
            variation: None,
        };
        if !matches!(e.execute(program, 0, false), Some(Flow::End)) {
            return Err(FontError::InvalidGlyph);
        }
        e.writer.close().ok_or(FontError::InvalidGlyph)?;
        Ok(e.writer.count)
    }
}
/// Owned CFF2 outline retaining the source and exact requested coordinates.
#[derive(Clone, Debug)]
pub struct Outline2 {
    source: Font2,
    glyph: u32,
    coordinates: Vec<i16>,
    segments: Vec<Segment>,
}
impl Outline2 {
    /// Original selected CFF2 generation.
    pub fn source(&self) -> &Font2 {
        &self.source
    }
    /// Original charstring index.
    pub fn glyph_index(&self) -> u32 {
        self.glyph
    }
    /// Exact supplied normalized coordinates, including unused extra entries.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Original closed line/cubic path records.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
}
impl Font2 {
    /// Measure a CFF2 outline with original F2DOT14 coordinates, without output allocation.
    pub fn outline_segment_count(&self, glyph: u32, coordinates: &[i16]) -> Result<u32, FontError> {
        self.evaluate(glyph, coordinates, None)
    }
    /// Execute and publish an owned unhinted CFF2 outline after complete preflight.
    pub fn outline(&self, glyph: u32, coordinates: &[i16]) -> Result<Outline2, FontError> {
        let count = self.evaluate(glyph, coordinates, None)?;
        let mut segments = Vec::new();
        segments
            .try_reserve_exact(count as usize)
            .map_err(|_| FontError::Capacity)?;
        let mut owned_coordinates = Vec::new();
        owned_coordinates
            .try_reserve_exact(coordinates.len())
            .map_err(|_| FontError::Capacity)?;
        owned_coordinates.extend_from_slice(coordinates);
        if self.evaluate(glyph, &owned_coordinates, Some((&mut segments, count)))? != count {
            return Err(FontError::InvalidGlyph);
        }
        Ok(Outline2 {
            source: self.clone(),
            glyph,
            coordinates: owned_coordinates,
            segments,
        })
    }
    fn evaluate(
        &self,
        glyph: u32,
        coordinates: &[i16],
        output: Option<(&mut Vec<Segment>, u32)>,
    ) -> Result<u32, FontError> {
        if glyph >= self.char_strings().len() {
            return Err(FontError::InvalidArgument);
        }
        if coordinates.len() < usize::from(self.axis_count()) {
            return Err(FontError::Capacity);
        }
        let program = self.char_strings().get(glyph)?;
        if program.len() > 65535 {
            return Err(FontError::InvalidGlyph);
        }
        let private = self.glyph_private(glyph)?;
        let mut e = Evaluator {
            writer: Writer {
                output,
                transform: None,
                count: 0,
                start: [0.0; 2],
                current: [0.0; 2],
                active: false,
            },
            global: self.global_subroutines(),
            local: private.local_subroutines,
            operands: [0.0; 513],
            len: 0,
            transient: [0.0; 32],
            stems: 0,
            width_seen: true,
            p: [0.0; 2],
            random: glyph.wrapping_add(1),
            variation: Some(Variation {
                store: self.variation_store(),
                coordinates,
                active: private.initial_vsindex,
                vsindex_seen: false,
                blend_seen: false,
                scalars: [0.0; 512],
                count: None,
            }),
        };
        if !matches!(e.execute(program, 0, false), Some(Flow::End)) {
            return Err(FontError::InvalidGlyph);
        }
        e.writer.close().ok_or(FontError::InvalidGlyph)?;
        Ok(e.writer.count)
    }
}

struct Variation<'a> {
    store: Option<ItemVariationStore<'a>>,
    coordinates: &'a [i16],
    active: u16,
    vsindex_seen: bool,
    blend_seen: bool,
    scalars: [f32; 512],
    count: Option<usize>,
}
impl Variation<'_> {
    fn ensure(&mut self) -> Option<usize> {
        if let Some(count) = self.count {
            return Some(count);
        }
        let store = self.store?;
        if self.coordinates.len() < usize::from(store.axis_count()) {
            return None;
        }
        let count = usize::from(store.blend_region_count(self.active).ok()?);
        if count > self.scalars.len() {
            return None;
        }
        for region in 0..count {
            self.scalars[region] = store
                .blend_region_scalar(self.coordinates, self.active, region as u16)
                .ok()?;
        }
        self.count = Some(count);
        Some(count)
    }
}
struct Writer<'a> {
    output: Option<(&'a mut Vec<Segment>, u32)>,
    transform: Option<OutlineTransform>,
    count: u32,
    start: [f64; 2],
    current: [f64; 2],
    active: bool,
}
impl Writer<'_> {
    fn point(&self, p: [f64; 2]) -> [f32; 2] {
        let q = if let Some(OutlineTransform(m)) = self.transform {
            [
                (p[0] * m[0] + p[1] * m[2]) + m[4],
                (p[0] * m[1] + p[1] * m[3]) + m[5],
            ]
        } else {
            p
        };
        [q[0] as f32, q[1] as f32]
    }
    fn valid(&self, p: [f64; 2]) -> Option<()> {
        if p.iter().any(|v| !v.is_finite()) {
            return None;
        }
        let q = if let Some(OutlineTransform(m)) = self.transform {
            [
                (p[0] * m[0] + p[1] * m[2]) + m[4],
                (p[0] * m[1] + p[1] * m[3]) + m[5],
            ]
        } else {
            p
        };
        q.iter()
            .all(|v| v.is_finite() && *v >= -f64::from(f32::MAX) && *v <= f64::from(f32::MAX))
            .then_some(())
    }
    fn emit(&mut self, s: Segment) -> Option<()> {
        if self.count == u32::MAX {
            return None;
        }
        if let Some((out, limit)) = &mut self.output {
            if self.count >= *limit {
                return None;
            }
            out.push(s);
        }
        self.count += 1;
        Some(())
    }
    fn close(&mut self) -> Option<()> {
        if self.active && self.current != self.start {
            self.emit(Segment::line(
                self.point(self.current),
                self.point(self.start),
            ))?;
        }
        self.active = false;
        Some(())
    }
    fn move_to(&mut self, p: [f64; 2]) -> Option<()> {
        self.valid(p)?;
        self.close()?;
        self.start = p;
        self.current = p;
        self.active = true;
        Some(())
    }
    fn begin(&mut self, p: [f64; 2]) {
        if !self.active {
            self.start = p;
            self.current = p;
            self.active = true;
        }
    }
    fn line(&mut self, p: [f64; 2], q: [f64; 2]) -> Option<()> {
        self.valid(p)?;
        self.valid(q)?;
        self.begin(p);
        self.emit(Segment::line(self.point(p), self.point(q)))?;
        self.current = q;
        Some(())
    }
    fn curve(&mut self, p: [[f64; 2]; 4]) -> Option<()> {
        for v in p {
            self.valid(v)?;
        }
        self.begin(p[0]);
        self.emit(Segment::cubic(p.map(|v| self.point(v))))?;
        self.current = p[3];
        Some(())
    }
}
#[derive(Clone, Copy)]
enum Flow {
    Return,
    End,
}
struct Evaluator<'a, 'b> {
    writer: Writer<'b>,
    global: Index<'a>,
    local: Index<'a>,
    operands: [f64; 513],
    len: usize,
    transient: [f64; 32],
    stems: u32,
    width_seen: bool,
    p: [f64; 2],
    random: u32,
    variation: Option<Variation<'a>>,
}
impl Evaluator<'_, '_> {
    fn push(&mut self, v: f64) -> Option<()> {
        if self.len >= self.operands.len() || !v.is_finite() {
            return None;
        }
        self.operands[self.len] = v;
        self.len += 1;
        Some(())
    }
    fn pop(&mut self) -> Option<f64> {
        if self.len == 0 {
            return None;
        }
        self.len -= 1;
        Some(self.operands[self.len])
    }
    fn remove_first(&mut self) {
        self.operands.copy_within(1..self.len, 0);
        self.len -= 1;
    }
    fn width(&mut self, n: usize) -> Option<()> {
        if !self.width_seen {
            if self.len == n + 1 {
                self.remove_first();
            }
            self.width_seen = true;
        }
        (self.len == n).then_some(())
    }
    fn stems(&mut self) -> Option<()> {
        if !self.width_seen && self.len & 1 != 0 {
            self.remove_first();
            self.width_seen = true;
        }
        if self.len & 1 != 0 {
            return None;
        }
        self.stems += (self.len >> 1) as u32;
        self.len = 0;
        (self.stems <= 96).then_some(())
    }
    fn line(&mut self, dx: f64, dy: f64) -> Option<()> {
        let p = self.p;
        self.p[0] += dx;
        self.p[1] += dy;
        self.writer.line(p, self.p)
    }
    fn curve(&mut self, a: [f64; 6]) -> Option<()> {
        let p = self.p;
        let q = [p[0] + a[0], p[1] + a[1]];
        let r = [q[0] + a[2], q[1] + a[3]];
        self.p = [r[0] + a[4], r[1] + a[5]];
        self.writer.curve([p, q, r, self.p])
    }
    fn curve_at(&mut self, i: usize) -> Option<()> {
        self.curve(self.operands[i..i + 6].try_into().ok()?)
    }
    fn execute(&mut self, program: &[u8], depth: u32, sub: bool) -> Option<Flow> {
        if depth > 10 {
            return None;
        }
        let mut cursor = 0;
        while cursor < program.len() {
            let op = program[cursor];
            cursor += 1;
            if let Some(v) = operand(program, &mut cursor, op) {
                self.push(v)?;
                continue;
            }
            match op {
                1 | 3 | 18 | 23 => self.stems()?,
                19 | 20 => {
                    self.stems()?;
                    let n = ((self.stems + 7) >> 3) as usize;
                    bytes::bytes(program, cursor, n)?;
                    cursor += n;
                }
                4 | 21 | 22 => {
                    self.width(if op == 21 { 2 } else { 1 })?;
                    if op == 4 {
                        self.p[1] += self.operands[0];
                    } else {
                        self.p[0] += self.operands[0];
                        if op == 21 {
                            self.p[1] += self.operands[1];
                        }
                    }
                    self.writer.move_to(self.p)?;
                    self.len = 0;
                }
                5 => {
                    if self.len < 2 || self.len & 1 != 0 {
                        return None;
                    }
                    for i in (0..self.len).step_by(2) {
                        self.line(self.operands[i], self.operands[i + 1])?;
                    }
                    self.len = 0;
                }
                6 | 7 => {
                    if self.len == 0 {
                        return None;
                    }
                    let mut horizontal = op == 6;
                    for i in 0..self.len {
                        let start = self.p;
                        if horizontal {
                            self.p[0] += self.operands[i];
                        } else {
                            self.p[1] += self.operands[i];
                        }
                        self.writer.line(start, self.p)?;
                        horizontal = !horizontal;
                    }
                    self.len = 0;
                }
                8 => {
                    if self.len == 0 || !self.len.is_multiple_of(6) {
                        return None;
                    }
                    for i in (0..self.len).step_by(6) {
                        self.curve_at(i)?;
                    }
                    self.len = 0;
                }
                10 | 29 => {
                    let requested = i32_value(self.pop()?)?;
                    let subs = if op == 10 { self.local } else { self.global };
                    let bias = if subs.len() < 1240 {
                        107
                    } else if subs.len() < 33900 {
                        1131
                    } else {
                        32768
                    };
                    let index = i64::from(requested) + bias;
                    if index < 0 || index >= i64::from(subs.len()) {
                        return None;
                    }
                    match self.execute(subs.get(index as u32).ok()?, depth + 1, true)? {
                        Flow::End => return Some(Flow::End),
                        Flow::Return => {}
                    }
                }
                11 => return (sub && self.variation.is_none()).then_some(Flow::Return),
                12 => {
                    let escaped = *program.get(cursor)?;
                    cursor += 1;
                    self.escaped(escaped)?;
                }
                14 => {
                    if self.variation.is_some() {
                        return None;
                    }
                    let expected = if self.len == 1 || self.len == 5 {
                        self.len - 1
                    } else {
                        self.len
                    };
                    self.width(expected)?;
                    return (self.len == 0).then_some(Flow::End);
                }
                15 => self.select_variation()?,
                16 => self.blend_variation()?,
                24 => {
                    if self.len < 8 || !(self.len - 2).is_multiple_of(6) {
                        return None;
                    }
                    let limit = self.len - 2;
                    for i in (0..limit).step_by(6) {
                        self.curve_at(i)?;
                    }
                    self.line(self.operands[limit], self.operands[limit + 1])?;
                    self.len = 0;
                }
                25 => {
                    if self.len < 8 || !(self.len - 6).is_multiple_of(2) {
                        return None;
                    }
                    let limit = self.len - 6;
                    for i in (0..limit).step_by(2) {
                        self.line(self.operands[i], self.operands[i + 1])?;
                    }
                    self.curve_at(limit)?;
                    self.len = 0;
                }
                26 | 27 => self.parallel_curve(op == 27)?,
                30 | 31 => self.alternating_curve(op == 31)?,
                _ => return None,
            }
        }
        if self.variation.is_some() && !sub {
            Some(Flow::End)
        } else {
            sub.then_some(Flow::Return)
        }
    }
    fn select_variation(&mut self) -> Option<()> {
        let v = self.variation.as_mut()?;
        if v.vsindex_seen || v.blend_seen || self.len != 1 {
            return None;
        }
        let value = self.operands[0];
        if value != value.trunc() || !(0.0..=65535.0).contains(&value) || v.store.is_none() {
            return None;
        }
        self.len = 0;
        v.active = value as u16;
        v.count = None;
        v.vsindex_seen = true;
        v.ensure()?;
        Some(())
    }
    fn blend_variation(&mut self) -> Option<()> {
        if self.len == 0 {
            return None;
        }
        let v = self.variation.as_mut()?;
        let scalar_count = v.ensure()?;
        let value = self.operands[self.len - 1];
        let n = i32_value(value)?;
        if n <= 0 || value != f64::from(n) {
            return None;
        }
        let n = n as usize;
        let consumed = n.checked_mul(scalar_count + 1)?.checked_add(1)?;
        let start = self.len.checked_sub(consumed)?;
        let deltas = start + n;
        for i in 0..n {
            let mut blended = self.operands[start + i];
            for region in 0..scalar_count {
                blended += self.operands[deltas + i * scalar_count + region]
                    * f64::from(v.scalars[region]);
            }
            if !blended.is_finite() {
                return None;
            }
            self.operands[start + i] = blended;
        }
        self.len = start + n;
        v.blend_seen = true;
        Some(())
    }
    fn parallel_curve(&mut self, horizontal: bool) -> Option<()> {
        if self.len < 4 {
            return None;
        }
        let mut i = 0;
        let mut cross = 0.0;
        if self.len & 1 != 0 {
            cross = self.operands[0];
            i = 1;
        }
        while i <= self.len - 4 {
            let a = &self.operands[i..i + 4];
            let args = if horizontal {
                [a[0], cross, a[1], a[2], a[3], 0.0]
            } else {
                [cross, a[0], a[1], a[2], 0.0, a[3]]
            };
            self.curve(args)?;
            cross = 0.0;
            i += 4;
        }
        let valid = i == self.len;
        self.len = 0;
        valid.then_some(())
    }
    fn alternating_curve(&mut self, mut horizontal: bool) -> Option<()> {
        if self.len < 4 {
            return None;
        }
        let mut i = 0;
        while i <= self.len - 4 {
            let extra = self.len - i == 5;
            let a = &self.operands[i..];
            let delta = if extra { a[4] } else { 0.0 };
            let args = if horizontal {
                [a[0], 0.0, a[1], a[2], delta, a[3]]
            } else {
                [0.0, a[0], a[1], a[2], a[3], delta]
            };
            self.curve(args)?;
            i += if extra { 5 } else { 4 };
            horizontal = !horizontal;
        }
        let valid = i == self.len;
        self.len = 0;
        valid.then_some(())
    }
    fn escaped(&mut self, op: u8) -> Option<()> {
        if self.variation.is_some() && !(34..=37).contains(&op) {
            return None;
        }
        match op {
            0 => self.len = 0,
            3 | 4 | 10 | 11 | 12 | 15 | 24 => {
                let r = self.pop()?;
                let l = self.pop()?;
                self.push(match op {
                    3 => f64::from(l != 0.0 && r != 0.0),
                    4 => f64::from(l != 0.0 || r != 0.0),
                    10 => l + r,
                    11 => l - r,
                    12 => {
                        if r == 0.0 {
                            0.0
                        } else {
                            l / r
                        }
                    }
                    15 => f64::from(l == r),
                    _ => l * r,
                })?;
            }
            5 | 9 | 14 | 26 => {
                let v = self.pop()?;
                self.push(match op {
                    5 => f64::from(v == 0.0),
                    9 => v.abs(),
                    14 => -v,
                    _ => {
                        if v > 0.0 {
                            v.sqrt()
                        } else {
                            0.0
                        }
                    }
                })?;
            }
            18 => {
                self.pop()?;
            }
            20 => {
                let encoded = self.pop()?;
                let value = self.pop()?;
                let i = i32_value(encoded)?;
                if i < 0 || i as usize >= self.transient.len() {
                    return None;
                }
                self.transient[i as usize] = value;
            }
            21 => {
                let i = i32_value(self.pop()?)?;
                if i < 0 || i as usize >= self.transient.len() {
                    return None;
                }
                self.push(self.transient[i as usize])?;
            }
            22 => {
                if self.len < 4 {
                    return None;
                }
                let l2 = self.pop()?;
                let l1 = self.pop()?;
                let v2 = self.pop()?;
                let v1 = self.pop()?;
                self.push(if l1 <= l2 { v1 } else { v2 })?;
            }
            23 => {
                self.random ^= self.random << 13;
                self.random ^= self.random >> 17;
                self.random ^= self.random << 5;
                self.push((f64::from(self.random) + 1.0) / (f64::from(u32::MAX) + 2.0))?;
            }
            27 => {
                if self.len == 0 {
                    return None;
                }
                self.push(self.operands[self.len - 1])?;
            }
            28 => {
                if self.len < 2 {
                    return None;
                }
                self.operands.swap(self.len - 1, self.len - 2);
            }
            29 => {
                let encoded = self.pop()?;
                if self.len == 0 {
                    return None;
                }
                let i = i32_value(encoded)?.clamp(0, (self.len - 1) as i32) as usize;
                self.push(self.operands[self.len - 1 - i])?;
            }
            30 => {
                let shift = i32_value(self.pop()?)?;
                let count = i32_value(self.pop()?)?;
                if count < 0 || count as usize > self.len {
                    return None;
                }
                if count >= 2 {
                    let shift = shift.rem_euclid(count) as usize;
                    let start = self.len - count as usize;
                    self.operands[start..self.len].rotate_right(shift);
                }
            }
            34..=37 => self.flex(op)?,
            _ => return None,
        }
        Some(())
    }
    fn flex(&mut self, op: u8) -> Option<()> {
        let expected = match op {
            34 => 7,
            35 => 13,
            36 => 9,
            _ => 11,
        };
        if self.len != expected {
            return None;
        }
        let a: [f64; 13] = self.operands[..13].try_into().ok()?;
        match op {
            34 => {
                self.curve([a[0], 0.0, a[1], a[2], a[3], 0.0])?;
                self.curve([a[4], 0.0, a[5], -a[2], a[6], 0.0])?;
            }
            35 => {
                self.curve(a[..6].try_into().ok()?)?;
                self.curve(a[6..12].try_into().ok()?)?;
            }
            36 => {
                let y = -(a[1] + a[3] + a[7]);
                self.curve([a[0], a[1], a[2], a[3], a[4], 0.0])?;
                self.curve([a[5], 0.0, a[6], a[7], a[8], y])?;
            }
            _ => {
                let x = a[0] + a[2] + a[4] + a[6] + a[8];
                let y = a[1] + a[3] + a[5] + a[7] + a[9];
                let dx = if x.abs() > y.abs() { a[10] } else { -x };
                let dy = if x.abs() > y.abs() { -y } else { a[10] };
                self.curve(a[..6].try_into().ok()?)?;
                self.curve([a[6], a[7], a[8], a[9], dx, dy])?;
            }
        }
        self.len = 0;
        Some(())
    }
}
fn i32_value(v: f64) -> Option<i32> {
    (v.is_finite() && v >= f64::from(i32::MIN) && v <= f64::from(i32::MAX)).then_some(v as i32)
}
fn operand(data: &[u8], cursor: &mut usize, first: u8) -> Option<f64> {
    match first {
        28 | 32..=254 => super::read_number(data, cursor, first),
        255 => {
            let value = bytes::u32(data, *cursor)? as i32;
            *cursor += 4;
            Some(f64::from(value) / 65536.0)
        }
        _ => None,
    }
}
