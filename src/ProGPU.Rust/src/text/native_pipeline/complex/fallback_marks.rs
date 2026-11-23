//! Original native design-unit fallback marks and font-outline control bounds.
use super::*;
use crate::font::HorizontalAdvanceInstance;
/// Original native fallback extent source; selecting outline bounds does not
/// imply hinted-device metrics or managed font-provider admission.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeFallbackBounds {
    /// Exact original null-scratch glyf-header query, including absent CFF bounds.
    Header,
    /// Original varied TrueType/CFF1/CFF2 source selection and control-point bounds.
    /// Existing owned native decoders allocate temporary paths on a cache miss.
    #[default]
    Outline,
}
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Cached {
    pub generation: u64,
    bounds: Option<[i16; 4]>,
}
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Extents {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}
fn reduce(segments: &[crate::path::Segment]) -> Option<[i16; 4]> {
    let mut min = [f32::INFINITY; 2];
    let mut max = [f32::NEG_INFINITY; 2];
    let mut found = false;
    for s in segments {
        let count = if s.kind == 2 {
            4
        } else if s.kind == 1 {
            3
        } else {
            2
        };
        for &p in &s.points[..count] {
            if !p[0].is_finite() || !p[1].is_finite() {
                continue;
            }
            for axis in 0..2 {
                min[axis] = min[axis].min(p[axis]);
                max[axis] = max[axis].max(p[axis]);
            }
            found = true;
        }
    }
    if !found {
        return None;
    }
    let b = [
        min[0].floor().clamp(-32768.0, 32767.0) as i16,
        min[1].floor().clamp(-32768.0, 32767.0) as i16,
        max[0].ceil().clamp(-32768.0, 32767.0) as i16,
        max[1].ceil().clamp(-32768.0, 32767.0) as i16,
    ];
    (b[2] > b[0] && b[3] > b[1]).then_some(b)
}
fn outline_bounds(
    font: &SfntFace,
    id: u16,
    coordinates: &[i16],
) -> Result<Option<[i16; 4]>, FontError> {
    let count = font.glyph_count().ok_or(FontError::InvalidGlyph)?;
    if id >= count {
        return Err(FontError::InvalidGlyph);
    }
    let has = |t| font.table(Tag::new(t)).is_some();
    if has(*b"glyf") && has(*b"loca") {
        if !coordinates.iter().any(|&c| c != 0) || !has(*b"gvar") {
            return Ok(font.glyph_bounds(id));
        }
        return Ok(reduce(
            font.varied_glyph_outline(id, coordinates)?.segments(),
        ));
    }
    if has(*b"CFF ") {
        return Ok(reduce(font.cff1(count)?.outline(u32::from(id))?.segments()));
    }
    if has(*b"CFF2") {
        return Ok(reduce(
            font.cff2(count)?
                .outline(u32::from(id), coordinates)?
                .segments(),
        ));
    }
    Ok(None)
}
struct Source<'a> {
    font: &'a SfntFace,
    advance: &'a HorizontalAdvanceInstance,
    policy: NativeFallbackBounds,
    cache: &'a mut [Cached],
    generation: u64,
}
impl Source<'_> {
    fn extents(&mut self, id: u16) -> Result<Option<Extents>, FontError> {
        let bounds = if self.policy == NativeFallbackBounds::Header {
            self.font.glyph_bounds(id)
        } else {
            let cached = self
                .cache
                .get_mut(usize::from(id))
                .ok_or(FontError::Capacity)?;
            if cached.generation != self.generation {
                let bounds = outline_bounds(self.font, id, self.advance.coordinates())?;
                *cached = Cached {
                    generation: self.generation,
                    bounds,
                };
            }
            cached.bounds
        };
        Ok(bounds.map(|b| Extents {
            x: i32::from(b[0]),
            y: i32::from(b[3]),
            width: i32::from(b[2]) - i32::from(b[0]),
            height: i32::from(b[1]) - i32::from(b[3]),
        }))
    }
}
fn recategorize(cp: u32, mut c: i32) -> i32 {
    if c >= 200 {
        return c;
    }
    if cp & !0xff == 0xe00 {
        if c == 0 {
            c = match cp {
                0xe31 | 0xe34..=0xe37 | 0xe47 | 0xe4c..=0xe4e => 232,
                0xeb1 | 0xeb4..=0xeb7 | 0xebb | 0xecc | 0xecd => 230,
                0xebc => 220,
                _ => c,
            };
        } else if cp == 0xe3a {
            c = 222;
        }
    }
    match c {
        22 | 15 | 16 | 17 | 23 | 18 | 19 | 20 | 21 | 24 | 25 | 30 | 33 | 118 | 129 | 132 => 220,
        13 => 214,
        10 | 103 | 107 => 232,
        11 | 14 => 228,
        26 | 28 | 29 | 31 | 32 | 27 | 34 | 35 | 36 | 122 | 130 => 230,
        _ => c,
    }
}
fn above(base: &mut Extents, mark: Extents, gap: i32) -> i32 {
    let mut offset = base.y - (mark.y + mark.height);
    if (gap > 0) != (offset > 0) {
        let correction = -offset / 2;
        base.y += correction;
        base.height -= correction;
        offset += correction;
    }
    base.y -= mark.height;
    base.height += mark.height;
    offset
}
fn position(
    source: &mut Source<'_>,
    glyph: &mut ShapingGlyph,
    c: i32,
    base: &mut Extents,
    direction: GposDirection,
    units: i32,
) -> Result<(), FontError> {
    let Some(mark) = source.extents(glyph.glyph_id as u16)? else {
        return Ok(());
    };
    let x = if matches!(c, 233 | 234) && direction == GposDirection::LeftToRight {
        base.x + base.width - mark.width / 2 - mark.x
    } else if matches!(c, 233 | 234) && direction == GposDirection::RightToLeft {
        base.x - mark.width / 2 - mark.x
    } else if matches!(c, 200 | 218 | 228) {
        base.x - mark.x
    } else if matches!(c, 216 | 222 | 232) {
        base.x + base.width - mark.width - mark.x
    } else {
        base.x + (base.width - mark.width) / 2 - mark.x
    };
    let gap = units / 16;
    let mut y = 0;
    if matches!(c, 233 | 218 | 220 | 222) {
        base.height -= gap;
    }
    if matches!(c, 200 | 202 | 218 | 220 | 222 | 233) {
        y = base.y + base.height - mark.y;
        if (gap > 0) == (y > 0) {
            base.height -= y;
            y = 0;
        }
        base.height += mark.height;
    } else if matches!(c, 228 | 230 | 232 | 234) {
        base.y += gap;
        base.height -= gap;
        y = above(base, mark, gap);
    } else if matches!(c, 214 | 216) {
        y = above(base, mark, gap);
    }
    glyph.offset_x = clamped(i64::from(x));
    glyph.offset_y = clamped(i64::from(y));
    Ok(())
}
pub(super) trait Policy {
    fn base(&mut self, index: usize, glyph: u16) -> Result<Option<(Extents, i32)>, FontError>;
    fn prepare_base(
        &self,
        base: &mut Extents,
        glyph: ShapingGlyph,
        width: i32,
    ) -> Result<(), FontError>;
    fn split(
        &self,
        base: &mut Extents,
        component: i32,
        count: u8,
        direction: GposDirection,
    ) -> Result<(), FontError>;
    fn accumulate(
        &self,
        glyph: ShapingGlyph,
        sign: i64,
        x: &mut i64,
        y: &mut i64,
    ) -> Result<(), FontError>;
    fn position(
        &mut self,
        glyph: &mut ShapingGlyph,
        index: usize,
        class: i32,
        base: &mut Extents,
        direction: GposDirection,
        units: i32,
        x: i64,
        y: i64,
    ) -> Result<(), FontError>;
}
impl Policy for Source<'_> {
    fn base(&mut self, _index: usize, glyph: u16) -> Result<Option<(Extents, i32)>, FontError> {
        let Some(base) = self.extents(glyph)? else {
            return Ok(None);
        };
        let width = self.advance.design_advance_width(glyph)?.round_ties_even();
        if !width.is_finite()
            || f64::from(width) < f64::from(i32::MIN)
            || f64::from(width) > f64::from(i32::MAX)
        {
            return Err(FontError::InvalidFace);
        }
        Ok(Some((base, width as i32)))
    }
    fn prepare_base(
        &self,
        base: &mut Extents,
        glyph: ShapingGlyph,
        width: i32,
    ) -> Result<(), FontError> {
        base.y += glyph.offset_y;
        base.x = 0;
        base.width = width;
        Ok(())
    }
    fn split(
        &self,
        base: &mut Extents,
        component: i32,
        count: u8,
        direction: GposDirection,
    ) -> Result<(), FontError> {
        let selected = if direction == GposDirection::LeftToRight {
            component
        } else {
            i32::from(count) - 1 - component
        };
        base.x += selected * base.width / i32::from(count);
        base.width /= i32::from(count);
        Ok(())
    }
    fn accumulate(
        &self,
        glyph: ShapingGlyph,
        sign: i64,
        x: &mut i64,
        y: &mut i64,
    ) -> Result<(), FontError> {
        *x += sign * i64::from(glyph.advance_x);
        *y += sign * i64::from(glyph.advance_y);
        Ok(())
    }
    fn position(
        &mut self,
        glyph: &mut ShapingGlyph,
        _index: usize,
        class: i32,
        base: &mut Extents,
        direction: GposDirection,
        units: i32,
        x: i64,
        y: i64,
    ) -> Result<(), FontError> {
        position(self, glyph, class, base, direction, units)?;
        glyph.advance_x = 0;
        glyph.advance_y = 0;
        glyph.offset_x = clamped(i64::from(glyph.offset_x) + x);
        glyph.offset_y = clamped(i64::from(glyph.offset_y) + y);
        Ok(())
    }
}
fn base_marks(
    source: &mut impl Policy,
    g: &mut [ShapingGlyph],
    direction: GposDirection,
    metadata: &[GposAttachment],
    start: usize,
    end: usize,
    units: i32,
) -> Result<(), FontError> {
    let Some((mut base, width)) = source.base(start, g[start].glyph_id as u16)? else {
        return Ok(());
    };
    mark_dependency(g, start, end);
    source.prepare_base(&mut base, g[start], width)?;
    let forward = matches!(
        direction,
        GposDirection::LeftToRight | GposDirection::TopToBottom
    );
    let mut x = if forward {
        -i64::from(g[start].advance_x)
    } else {
        0
    };
    let mut y = if forward {
        -i64::from(g[start].advance_y)
    } else {
        0
    };
    let mut last_class = 255;
    let mut last_component = -1;
    let mut class_extents = base;
    let mut component_extents = base;
    let count = metadata[start].component_count;
    for index in start + 1..end {
        if metadata[index].positioned != 0 {
            continue;
        }
        let c = recategorize(
            g[index].code_point,
            i32::from(input::modified_class(g[index].code_point)),
        );
        if c == 0 {
            let sign = if forward { -1 } else { 1 };
            source.accumulate(g[index], sign, &mut x, &mut y)?;
            continue;
        }
        if count > 1 {
            let raw = metadata[index].component;
            let component = if raw == 255 {
                i32::from(count) - 1
            } else {
                i32::from(raw.min(count - 1))
            };
            if last_component != component {
                last_component = component;
                last_class = 255;
                component_extents = base;
                source.split(&mut component_extents, component, count, direction)?;
            }
        }
        if last_class != c {
            last_class = c;
            class_extents = component_extents;
        }
        source.position(
            &mut g[index],
            index,
            c,
            &mut class_extents,
            direction,
            units,
            x,
            y,
        )?;
    }
    Ok(())
}
pub(super) fn apply(
    font: &SfntFace,
    advance: &HorizontalAdvanceInstance,
    g: &mut [ShapingGlyph],
    direction: GposDirection,
    metadata: &[GposAttachment],
    policy: NativeFallbackBounds,
    cache: &mut [Cached],
    generation: u64,
) -> Result<(), FontError> {
    let mut source = Source {
        font,
        advance,
        policy,
        cache,
        generation,
    };
    apply_with(font, g, direction, metadata, &mut source)
}
pub(super) fn apply_with(
    font: &SfntFace,
    g: &mut [ShapingGlyph],
    direction: GposDirection,
    metadata: &[GposAttachment],
    source: &mut impl Policy,
) -> Result<(), FontError> {
    if direction == GposDirection::Unspecified || metadata.len() < g.len() {
        return Err(FontError::InvalidArgument);
    }
    if g.iter().any(|g| g.glyph_id > u32::from(u16::MAX)) {
        return Err(FontError::InvalidGlyph);
    }
    let units = font
        .header_metrics()
        .filter(|h| h.units_per_em != 0)
        .ok_or(FontError::InvalidFace)?
        .units_per_em;
    let mut cluster_start = 0;
    for index in 1..=g.len() {
        if index < g.len()
            && (unicode_mark(g[index].code_point) || input::default_ignorable(g[index].code_point))
        {
            continue;
        }
        if index - cluster_start >= 2 {
            let mut base = cluster_start;
            while base < index {
                if unicode_mark(g[base].code_point) {
                    base += 1;
                    continue;
                }
                let mut end = base + 1;
                while end < index
                    && (unicode_mark(g[end].code_point)
                        || input::default_ignorable(g[end].code_point))
                {
                    end += 1;
                }
                base_marks(source, g, direction, metadata, base, end, i32::from(units))?;
                base = end;
            }
        }
        cluster_start = index;
    }
    Ok(())
}
