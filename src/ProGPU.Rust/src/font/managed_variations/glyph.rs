use super::parse::{extent, positive_long, table, word};
use super::*;
use crate::font::item_variation::axis_scalar;
#[derive(Debug)]
pub(super) struct GlyphMetadata {
    pub(super) offsets: Vec<i32>,
    base: i32,
    shared: Vec<Vec<f32>>,
}
#[derive(Clone, Debug)]
pub(super) enum GlyphCache {
    Unparsed,
    Parsed(Option<Arc<GlyphVariation>>),
}
#[derive(Debug)]
pub(super) struct GlyphVariation {
    tuples: Vec<Tuple>,
}
#[derive(Debug)]
struct Tuple {
    start: Vec<f32>,
    peak: Vec<f32>,
    end: Vec<f32>,
    points: Option<Arc<[i32]>>,
    x: Vec<i16>,
    y: Vec<i16>,
}
fn tuple(data: &[u8], at: i32, end: i32, axes: usize) -> Result<Vec<f32>, FontError> {
    extent(data, at, (axes as i32) * 2, end)?;
    let mut out = Vec::new();
    out.try_reserve_exact(axes)
        .map_err(|_| FontError::Capacity)?;
    for i in 0..axes {
        out.push(f32::from(word(data, at + i as i32 * 2)? as i16) / 16384.0)
    }
    Ok(out)
}
impl GlyphMetadata {
    pub(super) fn parse(source: &SfntFace, axes: usize) -> Result<Option<Self>, FontError> {
        let Some((start, end)) = table(source, *b"gvar") else {
            return Ok(None);
        };
        let data = source.data();
        extent(data, start, 20, end)?;
        let axis_count = usize::from(word(data, start + 4)?);
        let shared_count = word(data, start + 6)?;
        let shared_offset = positive_long(data, start + 8)?;
        let glyph_count = word(data, start + 12)?;
        let flags = word(data, start + 14)?;
        let data_offset = positive_long(data, start + 16)?;
        let source_count = if let Some((at, _)) = table(source, *b"maxp") {
            word(data, at.wrapping_add(4))?
        } else {
            0
        };
        if axis_count != axes || glyph_count != source_count {
            return Ok(None);
        }
        let mut offsets = Vec::new();
        offsets
            .try_reserve_exact(usize::from(glyph_count) + 1)
            .map_err(|_| FontError::Capacity)?;
        for i in 0..=glyph_count {
            let at = (start + 20).wrapping_add(i32::from(i) * if flags & 1 != 0 { 4 } else { 2 });
            if flags & 1 != 0 {
                extent(data, at, 4, end)?;
                offsets.push(positive_long(data, at)?);
            } else {
                extent(data, at, 2, end)?;
                offsets.push(i32::from(word(data, at)?) * 2);
            }
        }
        let mut shared = Vec::new();
        shared
            .try_reserve_exact(usize::from(shared_count))
            .map_err(|_| FontError::Capacity)?;
        let mut at = start.wrapping_add(shared_offset);
        for _ in 0..shared_count {
            shared.push(tuple(data, at, end, axes)?);
            at = at.wrapping_add(axes as i32 * 2);
        }
        let base = start.wrapping_add(data_offset);
        Ok((base >= start && base <= end).then_some(Self {
            offsets,
            base,
            shared,
        }))
    }
    fn glyph(
        &self,
        data: &[u8],
        start: i32,
        end: i32,
        items: usize,
        axes: usize,
    ) -> Result<Option<GlyphVariation>, FontError> {
        extent(data, start, 4, end)?;
        let count_flags = word(data, start)?;
        let count = count_flags & 0xfff;
        let data_offset = word(data, start + 2)?;
        let mut at = start + 4;
        let mut headers = Vec::new();
        headers
            .try_reserve_exact(usize::from(count))
            .map_err(|_| FontError::Capacity)?;
        for _ in 0..count {
            extent(data, at, 4, end)?;
            let size = word(data, at)?;
            let flags = word(data, at + 2)?;
            at += 4;
            let peak = if flags & 0x8000 != 0 {
                let t = tuple(data, at, end, axes)?;
                at += axes as i32 * 2;
                t
            } else {
                let Some(t) = self.shared.get(usize::from(flags & 0xfff)) else {
                    return Ok(None);
                };
                t.clone()
            };
            let (low, high) = if flags & 0x4000 != 0 {
                let a = tuple(data, at, end, axes)?;
                at += axes as i32 * 2;
                let b = tuple(data, at, end, axes)?;
                at += axes as i32 * 2;
                (a, b)
            } else {
                (
                    peak.iter().map(|&v| v.min(0.0)).collect(),
                    peak.iter().map(|&v| v.max(0.0)).collect(),
                )
            };
            headers.push((size, flags, low, peak, high));
        }
        let mut at = start.wrapping_add(i32::from(data_offset));
        let shared_points = if count_flags & 0x8000 != 0 {
            points(data, &mut at, end)?
        } else {
            None
        };
        let mut tuples = Vec::new();
        tuples
            .try_reserve_exact(usize::from(count))
            .map_err(|_| FontError::Capacity)?;
        for (size, flags, start, peak, high) in headers {
            let tuple_end = at
                .checked_add(i32::from(size))
                .ok_or(FontError::InvalidFace)?;
            if tuple_end > end {
                return Ok(None);
            }
            let points = if flags & 0x2000 != 0 {
                points(data, &mut at, tuple_end)?
            } else {
                shared_points.clone()
            };
            let count = points.as_ref().map_or(items, |p| p.len());
            let x = deltas(data, &mut at, tuple_end, count)?;
            let y = deltas(data, &mut at, tuple_end, count)?;
            at = tuple_end;
            tuples.push(Tuple {
                start,
                peak,
                end: high,
                points,
                x,
                y,
            });
        }
        Ok(Some(GlyphVariation { tuples }))
    }
}
fn byte(data: &[u8], at: &mut i32, end: i32) -> Result<u8, FontError> {
    extent(data, *at, 1, end)?;
    let b = data[*at as usize];
    *at += 1;
    Ok(b)
}
fn points(data: &[u8], at: &mut i32, end: i32) -> Result<Option<Arc<[i32]>>, FontError> {
    let first = byte(data, at, end)?;
    if first == 0 {
        return Ok(None);
    }
    let count = if first & 0x80 != 0 {
        (usize::from(first & 127) << 8) | usize::from(byte(data, at, end)?)
    } else {
        usize::from(first)
    };
    let mut out = Vec::new();
    out.try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    let mut current = 0i32;
    while out.len() < count {
        let control = byte(data, at, end)?;
        let run = usize::from(control & 127) + 1;
        if out.len() + run > count {
            return Err(FontError::InvalidFace);
        }
        for _ in 0..run {
            let delta = if control & 128 != 0 {
                extent(data, *at, 2, end)?;
                let n = word(data, *at)?;
                *at += 2;
                i32::from(n)
            } else {
                i32::from(byte(data, at, end)?)
            };
            current = current.wrapping_add(delta);
            out.push(current);
        }
    }
    Ok(Some(out.into()))
}
fn deltas(data: &[u8], at: &mut i32, end: i32, count: usize) -> Result<Vec<i16>, FontError> {
    let mut out = allocate(count, 0)?;
    let mut written = 0;
    while written < count {
        let control = byte(data, at, end)?;
        let run = usize::from(control & 63) + 1;
        if written + run > count {
            return Err(FontError::InvalidFace);
        }
        if control & 128 != 0 {
            written += run;
            continue;
        }
        for _ in 0..run {
            out[written] = if control & 64 != 0 {
                extent(data, *at, 2, end)?;
                let n = word(data, *at)? as i16;
                *at += 2;
                n
            } else {
                i16::from(byte(data, at, end)? as i8)
            };
            written += 1;
        }
    }
    Ok(out)
}
fn scalar(coordinates: &[i16], t: &Tuple) -> f32 {
    let mut s = 1.0;
    for (i, &c) in coordinates.iter().enumerate().take(t.peak.len()) {
        s *= axis_scalar(f32::from(c) / 16384.0, t.start[i], t.peak[i], t.end[i]);
        if s == 0.0 {
            break;
        }
    }
    s
}
impl ManagedVariationInstance {
    fn glyph(&self, glyph: u16, items: usize) -> Result<Option<Arc<GlyphVariation>>, FontError> {
        let Some(meta) = &self.data.0.glyphs else {
            return Ok(None);
        };
        let mut cache = self
            .data
            .0
            .glyph_cache
            .lock()
            .map_err(|_| FontError::Capacity)?;
        let Some(slot) = cache.get_mut(usize::from(glyph)) else {
            return Ok(None);
        };
        if let GlyphCache::Parsed(value) = slot {
            return Ok(value.clone());
        }
        // Original parsed flag precedes parsing. A source failure is observed
        // once; later queries retain the cached null, even with another item count.
        *slot = GlyphCache::Parsed(None);
        let start = meta.offsets[usize::from(glyph)];
        let end = meta.offsets[usize::from(glyph) + 1];
        if start == end {
            return Ok(None);
        }
        let value = meta
            .glyph(
                self.data.source().data(),
                meta.base.wrapping_add(start),
                meta.base.wrapping_add(end),
                items,
                self.coordinates.len(),
            )?
            .map(Arc::new);
        *slot = GlyphCache::Parsed(value.clone());
        Ok(value)
    }
    /// Original cached gvar right-minus-left phantom delta. HVAR presence keeps
    /// this route disabled, and fewer than four supplied items returns zero.
    pub fn phantom_advance_delta(&self, glyph: u16, items: usize) -> Result<f32, FontError> {
        if !self.data.uses_glyph_phantom_advance() || items < 4 {
            return Ok(0.0);
        }
        if items > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let Some(g) = self.glyph(glyph, items)? else {
            return Ok(0.0);
        };
        let left = items - 4;
        let right = left + 1;
        let mut a = 0.0;
        let mut b = 0.0;
        for t in &g.tuples {
            let s = scalar(&self.coordinates, t);
            if s == 0.0 {
                continue;
            }
            if let Some(points) = &t.points {
                for (i, &p) in points.iter().enumerate() {
                    if p as usize == left {
                        a += f32::from(t.x[i]) * s
                    } else if p as usize == right {
                        b += f32::from(t.x[i]) * s
                    }
                }
            } else if right < t.x.len() {
                a += f32::from(t.x[left]) * s;
                b += f32::from(t.x[right]) * s;
            }
        }
        Ok(b - a)
    }
    /// Apply original ordered gvar tuples directly to caller coordinates. IUP
    /// sees coordinates updated by prior tuples, as in the original C# method.
    /// Earlier tuple writes survive a later source/contour failure.
    pub fn apply_simple_deltas(
        &self,
        glyph: u16,
        coordinates: &mut [[f32; 2]],
        contours: &[u16],
    ) -> Result<(), FontError> {
        let items = coordinates
            .len()
            .checked_add(4)
            .filter(|&n| n <= i32::MAX as usize)
            .ok_or(FontError::Capacity)?;
        let Some(g) = self.glyph(glyph, items)? else {
            return Ok(());
        };
        let mut x = allocate(coordinates.len(), 0.0)?;
        let mut y = allocate(coordinates.len(), 0.0)?;
        let mut touched = allocate(coordinates.len(), false)?;
        for t in &g.tuples {
            let s = scalar(&self.coordinates, t);
            if s == 0.0 {
                continue;
            }
            x.fill(0.0);
            y.fill(0.0);
            touched.fill(false);
            if let Some(points) = &t.points {
                for (i, &p) in points.iter().enumerate() {
                    if let Some(v) = touched.get_mut(p as usize) {
                        x[p as usize] += f32::from(t.x[i]);
                        y[p as usize] += f32::from(t.y[i]);
                        *v = true;
                    }
                }
                infer(coordinates, contours, &mut x, &mut y, &touched)?;
            } else {
                for i in 0..coordinates.len().min(t.x.len()) {
                    x[i] = f32::from(t.x[i]);
                    y[i] = f32::from(t.y[i]);
                    touched[i] = true;
                }
            }
            for (i, p) in coordinates.iter_mut().enumerate() {
                p[0] += x[i] * s;
                p[1] += y[i] * s;
            }
        }
        Ok(())
    }
    /// Original component deltas from the shared first-query gvar generation.
    /// Missing glyph variation returns an empty vector, distinct from zero deltas.
    pub fn composite_deltas(&self, glyph: u16, count: usize) -> Result<Vec<[f32; 2]>, FontError> {
        let items = count
            .checked_add(4)
            .filter(|&n| n <= i32::MAX as usize)
            .ok_or(FontError::Capacity)?;
        let Some(g) = self.glyph(glyph, items)? else {
            return Ok(Vec::new());
        };
        let mut out = allocate(count, [0.0; 2])?;
        for t in &g.tuples {
            let s = scalar(&self.coordinates, t);
            if s == 0.0 {
                continue;
            }
            if let Some(points) = &t.points {
                for (i, &p) in points.iter().enumerate() {
                    if let Some(v) = out.get_mut(p as usize) {
                        v[0] += f32::from(t.x[i]) * s;
                        v[1] += f32::from(t.y[i]) * s;
                    }
                }
            } else {
                for (i, v) in out.iter_mut().enumerate().take(t.x.len()) {
                    v[0] += f32::from(t.x[i]) * s;
                    v[1] += f32::from(t.y[i]) * s;
                }
            }
        }
        Ok(out)
    }
}
fn interpolate(target: f32, mut first: f32, mut second: f32, mut a: f32, mut b: f32) -> f32 {
    if first == second {
        return if target <= first { a.min(b) } else { a.max(b) };
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
fn infer(
    points: &[[f32; 2]],
    ends: &[u16],
    x: &mut [f32],
    y: &mut [f32],
    touched: &[bool],
) -> Result<(), FontError> {
    let mut start = 0;
    for &end in ends {
        let end = usize::from(end);
        let mut indices = Vec::new();
        for p in start..=end {
            if *touched.get(p).ok_or(FontError::InvalidFace)? {
                indices.try_reserve(1).map_err(|_| FontError::Capacity)?;
                indices.push(p);
            }
        }
        if indices.len() == 1 {
            let source = indices[0];
            for p in start..=end {
                x[p] = x[source];
                y[p] = y[source];
            }
        } else if indices.len() > 1 {
            for i in 0..indices.len() {
                let first = indices[i];
                let second = indices[(i + 1) % indices.len()];
                let mut current = first;
                loop {
                    current = if current == end { start } else { current + 1 };
                    if current == second {
                        break;
                    }
                    x[current] = interpolate(
                        points[current][0],
                        points[first][0],
                        points[second][0],
                        x[first],
                        x[second],
                    );
                    y[current] = interpolate(
                        points[current][1],
                        points[first][1],
                        points[second][1],
                        y[first],
                        y[second],
                    );
                }
            }
        }
        start = end + 1;
    }
    Ok(())
}
