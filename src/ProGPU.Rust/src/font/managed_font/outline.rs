//! Original TtfFont TrueType/CFF1 outline/cache/variation integration. Raw managed
//! reads retain whole-source bounds, distinct from native per-glyph admission.
//! Recursion retains the original depth-32/cycle checks and skipped components.
use super::*;
use crate::path::Segment;
use metadata::{long, word};

/// One original filled, closed glyph figure, without synthesized closing edges.
#[derive(Clone, Debug)]
pub struct ManagedOutlineFigure {
    /// Original start coordinate, including implicit off-curve midpoint.
    pub start: [f32; 2],
    /// Original line/quadratic/cubic sequence in design coordinates.
    pub segments: Vec<Segment>,
}
/// Immutable selected-font outline and complete original point attachment data.
/// Source bytes survive font/cache retirement; this is unhinted geometry.
#[derive(Clone, Debug)]
pub struct ManagedGlyphOutline {
    source: SfntFace,
    glyph: u16,
    figures: Vec<ManagedOutlineFigure>,
    points: Vec<[f32; 2]>,
}
impl ManagedGlyphOutline {
    /// Exact immutable selected source face.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original glyph slot.
    pub fn glyph_index(&self) -> u16 {
        self.glyph
    }
    /// Original complete figures; each is closed and filled.
    pub fn figures(&self) -> &[ManagedOutlineFigure] {
        &self.figures
    }
    /// Actual original/varied points, including single-point contours.
    pub fn points(&self) -> &[[f32; 2]] {
        &self.points
    }
    /// Original finite-control-point envelope used by PathGeometry.TryGetBounds.
    /// This deliberately is not a tight analytic curve extent.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        let mut bounds = [
            f32::INFINITY,
            f32::INFINITY,
            f32::NEG_INFINITY,
            f32::NEG_INFINITY,
        ];
        let mut any = false;
        let mut visit = |p: [f32; 2]| {
            if p.iter().all(|v| v.is_finite()) {
                bounds[0] = bounds[0].min(p[0]);
                bounds[1] = bounds[1].min(p[1]);
                bounds[2] = bounds[2].max(p[0]);
                bounds[3] = bounds[3].max(p[1]);
                any = true;
            }
        };
        for f in &self.figures {
            visit(f.start);
            for s in &f.segments {
                for &p in &s.points[1..s.kind as usize + 2] {
                    visit(p);
                }
            }
        }
        any.then_some(bounds)
    }
}
#[derive(Debug, Default)]
pub(super) struct OutlineCache {
    glyphs: HashMap<u16, Option<Arc<ManagedGlyphOutline>>>,
}
impl ManagedFont {
    /// Original managed TrueType outline parsing, including selected simple and
    /// component variations and per-instance cached no-ink slots. This explicit
    /// route retains TrueType-only admission; `glyph_outline` also selects CFF1.
    pub fn true_type_outline(
        &self,
        glyph: u16,
    ) -> Result<Option<Arc<ManagedGlyphOutline>>, FontError> {
        if glyph >= self.metadata().glyph_count {
            return Ok(None);
        }
        if !self.metadata().has_true_type_outlines {
            return Err(FontError::InvalidFace);
        }
        let mut cache = self
            .state
            .outlines
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        self.outline_internal(glyph, &mut Vec::new(), &mut cache)
    }
    /// Original outline selection: TrueType first, then the owned managed CFF1
    /// reader. A program requiring the separate legacy fallback returns
    /// UnsupportedOutline; successful empty geometry remains independently None.
    pub fn glyph_outline(&self, glyph: u16) -> Result<Option<Arc<ManagedGlyphOutline>>, FontError> {
        if glyph >= self.metadata().glyph_count {
            return Ok(None);
        }
        if self.metadata().has_true_type_outlines {
            return self.true_type_outline(glyph);
        }
        let mut cache = self
            .state
            .outlines
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some(cached) = cache.glyphs.get(&glyph) {
            return Ok(cached.clone());
        }
        let cff = self.root.cff.get_or_init(|| {
            if self.source().table(Tag::new(*b"CFF ")).is_none() {
                return Ok(None);
            }
            crate::font::cff::ManagedCff::new(self.source().clone(), self.metadata().glyph_count)
                .map(Some)
        });
        let cff = cff.as_ref().map_err(|e| *e)?;
        let outline = match cff {
            Some(cff) => cff.outline(glyph)?.map(|figures| {
                Arc::new(ManagedGlyphOutline {
                    source: self.source().clone(),
                    glyph,
                    figures,
                    points: Vec::new(),
                })
            }),
            None => None,
        };
        cache
            .glyphs
            .try_reserve(1)
            .map_err(|_| FontError::Capacity)?;
        cache.glyphs.insert(glyph, outline.clone());
        Ok(outline)
    }
    /// Original independently cached flipped outline generation. Negation retains
    /// signed-zero behavior; source geometry and point-attachment data stay owned.
    pub fn flipped_outline(
        &self,
        glyph: u16,
    ) -> Result<Option<Arc<ManagedGlyphOutline>>, FontError> {
        let mut cache = self.state.flipped.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(cached) = cache.get(&glyph) {
            return Ok(cached.clone());
        }
        let flipped = self.glyph_outline(glyph)?.map(|outline| {
            let mut out = outline.as_ref().clone();
            for figure in &mut out.figures {
                figure.start[1] = -figure.start[1];
                for segment in &mut figure.segments {
                    for p in &mut segment.points[..segment.kind as usize + 2] {
                        p[1] = -p[1];
                    }
                }
            }
            for point in &mut out.points {
                point[1] = -point[1];
            }
            Arc::new(out)
        });
        cache.try_reserve(1).map_err(|_| FontError::Capacity)?;
        cache.insert(glyph, flipped.clone());
        Ok(flipped)
    }
    fn outline_internal(
        &self,
        glyph: u16,
        ancestors: &mut Vec<u16>,
        cache: &mut OutlineCache,
    ) -> Result<Option<Arc<ManagedGlyphOutline>>, FontError> {
        if glyph >= self.metadata().glyph_count
            || ancestors.len() > 32
            || ancestors.contains(&glyph)
        {
            return Ok(None);
        }
        if let Some(cached) = cache.glyphs.get(&glyph) {
            return Ok(cached.clone());
        }
        ancestors.push(glyph);
        let result = self.parse_outline(glyph, ancestors, cache);
        ancestors.pop();
        let result = result?.map(Arc::new);
        cache
            .glyphs
            .try_reserve(1)
            .map_err(|_| FontError::Capacity)?;
        cache.glyphs.insert(glyph, result.clone());
        Ok(result)
    }
    fn outline_offsets(&self, glyph: u16) -> Result<(u32, u32), FontError> {
        let raw = &self.root.raw;
        let g = u32::from(glyph);
        if raw.loca_format == 0 {
            Ok((
                u32::from(word(self.source(), raw.loca.wrapping_add(g * 2))?) * 2,
                u32::from(word(self.source(), raw.loca.wrapping_add((g + 1) * 2))?) * 2,
            ))
        } else {
            Ok((
                long(self.source(), raw.loca.wrapping_add(g * 4))?,
                long(self.source(), raw.loca.wrapping_add((g + 1) * 4))?,
            ))
        }
    }
    fn parse_outline(
        &self,
        glyph: u16,
        ancestors: &mut Vec<u16>,
        cache: &mut OutlineCache,
    ) -> Result<Option<ManagedGlyphOutline>, FontError> {
        let (start, end) = self.outline_offsets(glyph)?;
        if start == end {
            return Ok(None);
        }
        let at = self.root.raw.glyf.wrapping_add(start);
        let count = word(self.source(), at)? as i16;
        if count < 0 {
            return self.composite_outline(glyph, at, ancestors, cache);
        }
        if count == 0 {
            return Ok(None);
        }
        let mut cursor = at.wrapping_add(10);
        let mut ends = Vec::new();
        ends.try_reserve_exact(count as usize)
            .map_err(|_| FontError::Capacity)?;
        for _ in 0..count {
            ends.push(word(self.source(), cursor)?);
            cursor = cursor.wrapping_add(2);
        }
        cursor = cursor.wrapping_add(2 + u32::from(word(self.source(), cursor)?));
        let total = usize::from(*ends.last().unwrap()) + 1;
        let mut flags = allocated(total, 0u8)?;
        let mut index = 0;
        while index < total {
            let flag = byte(self.source(), cursor)?;
            cursor = cursor.wrapping_add(1);
            flags[index] = flag;
            if flag & 8 != 0 {
                let repeat = usize::from(byte(self.source(), cursor)?);
                cursor = cursor.wrapping_add(1);
                if repeat >= total - index {
                    return Err(FontError::InvalidGlyph);
                }
                flags[index + 1..index + 1 + repeat].fill(flag);
                index += repeat;
            }
            index += 1;
        }
        let mut points = allocated(total, [0.0; 2])?;
        for axis in 0..2 {
            let mut previous = 0.0;
            let short = 2 << axis;
            let same = 16 << axis;
            for (p, &flag) in points.iter_mut().zip(&flags) {
                let delta = if flag & short != 0 {
                    let value = f32::from(byte(self.source(), cursor)?);
                    cursor = cursor.wrapping_add(1);
                    if flag & same != 0 { value } else { -value }
                } else if flag & same != 0 {
                    0.0
                } else {
                    let value = f32::from(word(self.source(), cursor)? as i16);
                    cursor = cursor.wrapping_add(2);
                    value
                };
                previous += delta;
                p[axis] = previous;
            }
        }
        self.apply_simple_variations(glyph, &mut points, &ends)?;
        let mut figures = Vec::new();
        figures
            .try_reserve_exact(count as usize)
            .map_err(|_| FontError::Capacity)?;
        let mut first = 0;
        for &end in &ends {
            let last = usize::from(end) + 1;
            // The original skips negative/single-point contour counts, while
            // keeping all points for later composite point attachment.
            if last > first + 1 {
                let pts = points.get(first..last).ok_or(FontError::InvalidGlyph)?;
                let bits = flags.get(first..last).ok_or(FontError::InvalidGlyph)?;
                figures.push(contour(pts, bits)?);
            }
            first = last;
        }
        Ok(Some(ManagedGlyphOutline {
            source: self.source().clone(),
            glyph,
            figures,
            points,
        }))
    }
    fn composite_outline(
        &self,
        glyph: u16,
        at: u32,
        ancestors: &mut Vec<u16>,
        cache: &mut OutlineCache,
    ) -> Result<Option<ManagedGlyphOutline>, FontError> {
        let mut cursor = at.wrapping_add(10) as usize;
        let data = self.source().data();
        let mut components = Vec::new();
        loop {
            if data.get(cursor..cursor + 4).is_none() {
                return Ok(None);
            }
            let flags = u16::from_be_bytes([data[cursor], data[cursor + 1]]);
            let glyph = u16::from_be_bytes([data[cursor + 2], data[cursor + 3]]);
            cursor += 4;
            let size = if flags & 1 != 0 { 4 } else { 2 };
            let Some(args) = data.get(cursor..cursor + size) else {
                return Ok(None);
            };
            cursor += size;
            let pair = if size == 4 {
                let a = u16::from_be_bytes([args[0], args[1]]);
                let b = u16::from_be_bytes([args[2], args[3]]);
                if flags & 2 != 0 {
                    [i32::from(a as i16), i32::from(b as i16)]
                } else {
                    [i32::from(a), i32::from(b)]
                }
            } else if flags & 2 != 0 {
                [i32::from(args[0] as i8), i32::from(args[1] as i8)]
            } else {
                [i32::from(args[0]), i32::from(args[1])]
            };
            let size = if flags & 8 != 0 {
                2
            } else if flags & 64 != 0 {
                4
            } else if flags & 128 != 0 {
                8
            } else {
                0
            };
            let Some(transform) = data.get(cursor..cursor + size) else {
                return Ok(None);
            };
            cursor += size;
            let f = |i| f32::from(i16::from_be_bytes([transform[i], transform[i + 1]])) / 16384.0;
            let matrix = match size {
                2 => [f(0), 0.0, 0.0, f(0)],
                4 => [f(0), 0.0, 0.0, f(2)],
                8 => [f(0), f(2), f(4), f(6)],
                _ => [1.0, 0.0, 0.0, 1.0],
            };
            components.try_reserve(1).map_err(|_| FontError::Capacity)?;
            components.push((flags, glyph, pair, matrix));
            if flags & 32 == 0 {
                break;
            }
        }
        let deltas = self.component_variations(glyph, components.len())?;
        let mut out = ManagedGlyphOutline {
            source: self.source().clone(),
            glyph,
            figures: Vec::new(),
            points: Vec::new(),
        };
        for (index, (flags, glyph, args, m)) in components.into_iter().enumerate() {
            let Some(component) = self.outline_internal(glyph, ancestors, cache)? else {
                continue;
            };
            let linear = |p: [f32; 2]| [p[0] * m[0] + p[1] * m[1], p[0] * m[2] + p[1] * m[3]];
            let mut translation = if flags & 2 != 0 {
                let mut t = [args[0] as f32, args[1] as f32];
                if let Some(delta) = deltas.get(index) {
                    t[0] += delta[0];
                    t[1] += delta[1];
                }
                if flags & 0x800 != 0 {
                    t = linear(t);
                }
                t
            } else {
                let (Some(a), Some(b)) = (
                    out.points.get(args[0] as usize),
                    component.points.get(args[1] as usize),
                ) else {
                    continue;
                };
                let b = linear(*b);
                [a[0] - b[0], a[1] - b[1]]
            };
            if flags & 4 != 0 {
                translation = translation.map(f32::round_ties_even);
            }
            let transform = |p| {
                let q = linear(p);
                [q[0] + translation[0], q[1] + translation[1]]
            };
            out.figures
                .try_reserve(component.figures.len())
                .map_err(|_| FontError::Capacity)?;
            out.points
                .try_reserve(component.points.len())
                .map_err(|_| FontError::Capacity)?;
            for figure in &component.figures {
                let mut segments =
                    allocated(figure.segments.len(), Segment::line([0.0; 2], [0.0; 2]))?;
                for (result, source) in segments.iter_mut().zip(&figure.segments) {
                    *result = *source;
                    for p in &mut result.points[..source.kind as usize + 2] {
                        *p = transform(*p);
                    }
                }
                out.figures.push(ManagedOutlineFigure {
                    start: transform(figure.start),
                    segments,
                });
            }
            out.points
                .extend(component.points.iter().copied().map(transform));
        }
        Ok((!out.figures.is_empty()).then_some(out))
    }
    /// Explicit TrueType-only bounds entry point retained for existing callers.
    pub fn true_type_bounds(&self, glyph: u16) -> Result<Option<[i16; 4]>, FontError> {
        if glyph >= self.metadata().glyph_count {
            return Ok(None);
        }
        if !self.metadata().has_true_type_outlines {
            return Err(FontError::InvalidFace);
        }
        self.glyph_bounds(glyph)
    }
    /// Original managed bounds: raw static TrueType header or original finite
    /// figure/control envelope for CFF and selected TrueType variations.
    pub fn glyph_bounds(&self, glyph: u16) -> Result<Option<[i16; 4]>, FontError> {
        if glyph >= self.metadata().glyph_count {
            return Ok(None);
        }
        let bounds = if !self.metadata().has_true_type_outlines || self.has_active_variations() {
            let Some(outline) = self.glyph_outline(glyph)? else {
                return Ok(None);
            };
            let Some(b) = outline.bounds() else {
                return Ok(None);
            };
            [
                b[0].floor() as i16,
                b[1].floor() as i16,
                b[2].ceil() as i16,
                b[3].ceil() as i16,
            ]
        } else {
            let (start, end) = self.outline_offsets(glyph)?;
            if start == end {
                return Ok(None);
            }
            let at = self.root.raw.glyf.wrapping_add(start);
            if self.source().data().len() < 10 || at as usize > self.source().data().len() - 10 {
                return Ok(None);
            }
            [
                word(self.source(), at.wrapping_add(2))? as i16,
                word(self.source(), at.wrapping_add(4))? as i16,
                word(self.source(), at.wrapping_add(6))? as i16,
                word(self.source(), at.wrapping_add(8))? as i16,
            ]
        };
        Ok((bounds[2] > bounds[0] && bounds[3] > bounds[1]).then_some(bounds))
    }
}
fn byte(source: &SfntFace, at: u32) -> Result<u8, FontError> {
    source
        .data()
        .get(at as usize)
        .copied()
        .ok_or(FontError::InvalidGlyph)
}
fn allocated<T: Clone>(len: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(len)
        .map_err(|_| FontError::Capacity)?;
    out.resize(len, value);
    Ok(out)
}
fn contour(points: &[[f32; 2]], flags: &[u8]) -> Result<ManagedOutlineFigure, FontError> {
    let count = points.len();
    let on = |i: usize| flags[i] & 1 != 0;
    let midpoint = |a: [f32; 2], b: [f32; 2]| [(a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0];
    let (start, mut index) = if on(0) {
        (points[0], 1)
    } else if on(count - 1) {
        (points[count - 1], 0)
    } else {
        (midpoint(points[0], points[count - 1]), 0)
    };
    let mut current = start;
    let mut segments = Vec::new();
    segments
        .try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    let mut processed = 0;
    while processed < count {
        let i = index % count;
        let next = (index + 1) % count;
        if on(i) {
            segments.push(Segment::line(current, points[i]));
            current = points[i];
            index += 1;
            processed += 1;
        } else {
            let end = if on(next) {
                index += 2;
                processed += 2;
                points[next]
            } else {
                index += 1;
                processed += 1;
                midpoint(points[i], points[next])
            };
            segments.push(Segment::quadratic([current, points[i], end]));
            current = end;
        }
    }
    Ok(ManagedOutlineFigure { start, segments })
}
