//! Original outline-driven fallback mark placement after attachment resolution.
use super::*;
use crate::{font::ManagedFont, text::script_actions::managed_modified_combining_class};
#[derive(Clone, Copy)]
struct Extents {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
}
impl ManagedPositionBuffer {
    /// Original mark clusters, ligature components, class stacks and outline
    /// offsets. Uses actual selected managed TrueType bounds and advances.
    pub fn apply_true_type_fallback_marks(&mut self, font: &ManagedFont) -> Result<(), FontError> {
        let mut start = 0;
        for i in 1..self.glyphs.len() {
            if self
                .profile
                .is_mark(self.glyphs[i].code_point)
                .map_err(|_| FontError::InvalidGlyph)?
                || default_ignorable(self.glyphs[i].code_point)
            {
                continue;
            }
            self.fallback_cluster(font, start, i)?;
            start = i;
        }
        self.fallback_cluster(font, start, self.glyphs.len())
    }
    fn fallback_cluster(
        &mut self,
        font: &ManagedFont,
        start: usize,
        end: usize,
    ) -> Result<(), FontError> {
        if end - start < 2 {
            return Ok(());
        }
        let mut base = start;
        while base < end {
            if self
                .profile
                .is_mark(self.glyphs[base].code_point)
                .map_err(|_| FontError::InvalidGlyph)?
            {
                base += 1;
                continue;
            }
            let mut next = base + 1;
            while next < end
                && (self
                    .profile
                    .is_mark(self.glyphs[next].code_point)
                    .map_err(|_| FontError::InvalidGlyph)?
                    || default_ignorable(self.glyphs[next].code_point))
            {
                next += 1;
            }
            self.fallback_base(font, base, next)?;
            base = next;
        }
        Ok(())
    }
    fn fallback_base(
        &mut self,
        font: &ManagedFont,
        base: usize,
        end: usize,
    ) -> Result<(), FontError> {
        let Some(mut bounds) = extents(font, self.glyphs[base].glyph_index)? else {
            return Ok(());
        };
        self.dependency(base, end);
        bounds.y += i32::from(self.glyphs[base].offset_y);
        bounds.x = 0;
        bounds.width = font
            .advance_width(
                self.glyphs[base].glyph_index,
                f32::from(font.metadata().units_per_em),
            )?
            .round_ties_even() as i32;
        let forward = matches!(
            self.direction,
            ManagedPositionDirection::LeftToRight | ManagedPositionDirection::TopToBottom
        );
        let (mut x, mut y) = if forward {
            (
                -i32::from(self.glyphs[base].advance_x),
                -i32::from(self.glyphs[base].advance_y),
            )
        } else {
            (0i32, 0i32)
        };
        let mut last_class = 255;
        let mut last_component = -1;
        let mut class_bounds = bounds;
        let mut component_bounds = bounds;
        let count = i32::from(self.glyphs[base].ligature_component_count);
        for i in base + 1..end {
            let g = self.glyphs[i];
            if g.positioned != 0 {
                continue;
            }
            let class = recategorize(g.code_point, managed_modified_combining_class(g.code_point));
            if class == 0 {
                let sign = if forward { -1 } else { 1 };
                x = x.wrapping_add(sign * i32::from(g.advance_x));
                y = y.wrapping_add(sign * i32::from(g.advance_y));
                continue;
            }
            if count > 1 {
                let component = if g.ligature_component == u8::MAX {
                    count - 1
                } else {
                    i32::from(g.ligature_component).min(count - 1)
                };
                if last_component != component {
                    last_component = component;
                    last_class = 255;
                    component_bounds = bounds;
                    let selected = if self.direction == ManagedPositionDirection::LeftToRight {
                        component
                    } else {
                        count - 1 - component
                    };
                    component_bounds.x = component_bounds
                        .x
                        .wrapping_add(selected.wrapping_mul(component_bounds.width) / count);
                    component_bounds.width /= count;
                }
            }
            if last_class != class {
                last_class = class;
                class_bounds = component_bounds;
            }
            self.fallback_mark(font, i, class, &mut class_bounds)?;
            let g = &mut self.glyphs[i];
            g.advance_x = 0;
            g.advance_y = 0;
            g.offset_x = add_float(g.offset_x, x);
            g.offset_y = add_float(g.offset_y, y);
        }
        Ok(())
    }
    fn fallback_mark(
        &mut self,
        font: &ManagedFont,
        index: usize,
        class: u8,
        base: &mut Extents,
    ) -> Result<(), FontError> {
        let Some(mark) = extents(font, self.glyphs[index].glyph_index)? else {
            return Ok(());
        };
        let x = match class {
            233 | 234 if self.direction == ManagedPositionDirection::LeftToRight => base
                .x
                .wrapping_add(base.width)
                .wrapping_sub(mark.width / 2)
                .wrapping_sub(mark.x),
            233 | 234 if self.direction == ManagedPositionDirection::RightToLeft => {
                base.x.wrapping_sub(mark.width / 2).wrapping_sub(mark.x)
            }
            200 | 218 | 228 => base.x.wrapping_sub(mark.x),
            216 | 222 | 232 => base
                .x
                .wrapping_add(base.width)
                .wrapping_sub(mark.width)
                .wrapping_sub(mark.x),
            _ => base
                .x
                .wrapping_add(base.width.wrapping_sub(mark.width) / 2)
                .wrapping_sub(mark.x),
        };
        let gap = i32::from(font.metadata().units_per_em) / 16;
        let mut y = 0;
        if matches!(class, 233 | 218 | 220 | 222) {
            base.height = base.height.wrapping_sub(gap);
        }
        if matches!(class, 200 | 202 | 218 | 220 | 222 | 233) {
            y = base.y.wrapping_add(base.height).wrapping_sub(mark.y);
            if (gap > 0) == (y > 0) {
                base.height = base.height.wrapping_sub(y);
                y = 0;
            }
            base.height = base.height.wrapping_add(mark.height);
        } else if matches!(class, 228 | 230 | 232 | 234) {
            base.y = base.y.wrapping_add(gap);
            base.height = base.height.wrapping_sub(gap);
            y = above(base, mark, gap);
        } else if matches!(class, 214 | 216) {
            y = above(base, mark, gap);
        }
        self.glyphs[index].offset_x = clamp(x);
        self.glyphs[index].offset_y = clamp(y);
        Ok(())
    }
}
fn extents(font: &ManagedFont, glyph: u16) -> Result<Option<Extents>, FontError> {
    Ok(font.true_type_bounds(glyph)?.map(|b| Extents {
        x: i32::from(b[0]),
        y: i32::from(b[3]),
        width: i32::from(b[2]) - i32::from(b[0]),
        height: i32::from(b[1]) - i32::from(b[3]),
    }))
}
fn above(base: &mut Extents, mark: Extents, gap: i32) -> i32 {
    let mut y = base.y.wrapping_sub(mark.y.wrapping_add(mark.height));
    if (gap > 0) != (y > 0) {
        let correction = y.wrapping_neg() / 2;
        base.y = base.y.wrapping_add(correction);
        base.height = base.height.wrapping_sub(correction);
        y = y.wrapping_add(correction);
    }
    base.y = base.y.wrapping_sub(mark.height);
    base.height = base.height.wrapping_add(mark.height);
    y
}
// The source selects its float AddClamped overload for Int32 offset inputs.
fn add_float(left: i16, right: i32) -> i16 {
    (f32::from(left) + right as f32)
        .round_ties_even()
        .clamp(f32::from(i16::MIN), f32::from(i16::MAX)) as i16
}
fn recategorize(point: u32, mut class: u8) -> u8 {
    if class >= 200 {
        return class;
    }
    if point & !0xff == 0xe00 {
        if class == 0 {
            class = match point {
                0xe31 | 0xe34..=0xe37 | 0xe47 | 0xe4c..=0xe4e => 232,
                0xeb1 | 0xeb4..=0xeb7 | 0xebb | 0xecc | 0xecd => 230,
                0xebc => 220,
                _ => 0,
            };
        } else if point == 0xe3a {
            class = 222;
        }
    }
    match class {
        15..=25 => 220,
        13 => 214,
        10 => 232,
        11 | 14 => 228,
        26..=29 | 31 | 32 | 34..=36 => 230,
        30 | 33 => 220,
        103 | 107 => 232,
        118 | 129 | 132 => 220,
        122 | 130 => 230,
        _ => class,
    }
}
