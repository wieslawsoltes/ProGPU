//! Original signed-64 device mark geometry and atomic signed-32 publication.
use super::super::fallback_marks::{self, Extents, Policy};
use super::*;
fn checked(value: i64) -> Result<i32, FontError> {
    i32::try_from(value).map_err(|_| FontError::InvalidFace)
}
fn add_offsets(
    values: [i64; 2],
    deltas: [i64; 2],
    arithmetic: GposArithmetic,
) -> Result<[i32; 2], FontError> {
    let sums = if arithmetic == GposArithmetic::ScalarReference {
        [
            values[0]
                .checked_add(deltas[0])
                .ok_or(FontError::InvalidFace)?,
            values[1]
                .checked_add(deltas[1])
                .ok_or(FontError::InvalidFace)?,
        ]
    } else {
        let left = wide::i64x2::from(values);
        let right = wide::i64x2::from(deltas);
        let sum = left + right;
        if ((sum ^ left) & !(left ^ right))
            .to_array()
            .iter()
            .any(|&v| v < 0)
        {
            return Err(FontError::InvalidFace);
        }
        sum.to_array()
    };
    Ok([checked(sums[0])?, checked(sums[1])?])
}
struct DevicePolicy<'a> {
    frame: NativeDeviceFrame<'a>,
    mapping: &'a [u32],
    gap: i32,
}
impl DevicePolicy<'_> {
    fn captured(
        &self,
        index: usize,
        glyph: u32,
    ) -> Result<Option<NativeDeviceMarkBounds>, FontError> {
        let bounds = self
            .frame
            .source
            .mark_bounds(index, self.mapping[index], glyph)
            .map_err(|_| FontError::InvalidFace)?;
        if bounds.is_some_and(|b| b.width < 0 || b.negative_height > 0) {
            return Err(FontError::InvalidFace);
        }
        Ok(bounds)
    }
}
impl Policy for DevicePolicy<'_> {
    fn base(&mut self, index: usize, glyph: u16) -> Result<Option<(Extents, i32)>, FontError> {
        Ok(self.captured(index, u32::from(glyph))?.map(|b| {
            (
                Extents {
                    x: b.x_bearing,
                    y: b.y_bearing,
                    width: b.width,
                    height: b.negative_height,
                },
                b.horizontal_advance,
            )
        }))
    }
    fn prepare_base(
        &self,
        base: &mut Extents,
        glyph: ShapingGlyph,
        width: i32,
    ) -> Result<(), FontError> {
        base.y = checked(i64::from(base.y) + i64::from(glyph.offset_y))?;
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
        base.x = checked(
            i64::from(base.x) + i64::from(selected) * i64::from(base.width) / i64::from(count),
        )?;
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
        let next_x = x
            .checked_add(sign * i64::from(glyph.advance_x))
            .ok_or(FontError::InvalidFace)?;
        let next_y = y
            .checked_add(sign * i64::from(glyph.advance_y))
            .ok_or(FontError::InvalidFace)?;
        *x = next_x;
        *y = next_y;
        Ok(())
    }
    fn position(
        &mut self,
        glyph: &mut ShapingGlyph,
        index: usize,
        c: i32,
        base: &mut Extents,
        direction: GposDirection,
        _units: i32,
        x: i64,
        y: i64,
    ) -> Result<(), FontError> {
        let mark = self.captured(index, glyph.glyph_id)?;
        let mut offset_x = i64::from(glyph.offset_x);
        let mut offset_y = i64::from(glyph.offset_y);
        let mut next_y = i64::from(base.y);
        let mut next_height = i64::from(base.height);
        if let Some(mark) = mark {
            let base_x = i64::from(base.x);
            let base_width = i64::from(base.width);
            offset_x = if matches!(c, 233 | 234) && direction == GposDirection::LeftToRight {
                base_x + base_width - i64::from(mark.width / 2) - i64::from(mark.x_bearing)
            } else if matches!(c, 233 | 234) && direction == GposDirection::RightToLeft {
                base_x - i64::from(mark.width / 2) - i64::from(mark.x_bearing)
            } else if matches!(c, 200 | 218 | 228) {
                base_x - i64::from(mark.x_bearing)
            } else if matches!(c, 216 | 222 | 232) {
                base_x + base_width - i64::from(mark.width) - i64::from(mark.x_bearing)
            } else {
                base_x + (base_width - i64::from(mark.width)) / 2 - i64::from(mark.x_bearing)
            };
            offset_y = 0;
            if matches!(c, 233 | 218 | 220 | 222) {
                next_height -= i64::from(self.gap);
            }
            if matches!(c, 200 | 202 | 218 | 220 | 222 | 233) {
                offset_y = next_y + next_height - i64::from(mark.y_bearing);
                if (self.gap > 0) == (offset_y > 0) {
                    next_height -= offset_y;
                    offset_y = 0;
                }
                next_height += i64::from(mark.negative_height);
            } else if matches!(c, 228 | 230 | 232 | 234 | 214 | 216) {
                if matches!(c, 228 | 230 | 232 | 234) {
                    next_y += i64::from(self.gap);
                    next_height -= i64::from(self.gap);
                }
                offset_y = next_y - (i64::from(mark.y_bearing) + i64::from(mark.negative_height));
                if (self.gap > 0) != (offset_y > 0) {
                    let correction = -offset_y / 2;
                    next_y += correction;
                    next_height -= correction;
                    offset_y += correction;
                }
                next_y -= i64::from(mark.negative_height);
                next_height += i64::from(mark.negative_height);
            }
        }
        let candidate_y = checked(next_y)?;
        let candidate_height = checked(next_height)?;
        let offsets = add_offsets([offset_x, offset_y], [x, y], self.frame.arithmetic)?;
        // Original per-mark atomic publication; earlier dependencies/marks remain.
        base.y = candidate_y;
        base.height = candidate_height;
        glyph.offset_x = offsets[0];
        glyph.offset_y = offsets[1];
        glyph.advance_x = 0;
        glyph.advance_y = 0;
        Ok(())
    }
}
/// Position original fallback marks against an actual captured device generation.
/// The shared native walk preserves component identity, already-positioned marks,
/// dependency order and separate source 26.6 arithmetic, including overflow errors.
pub fn apply_native_device_fallback_marks(
    font: &SfntFace,
    coordinates: &[i16],
    glyphs: &mut [ShapingGlyph],
    direction: GposDirection,
    attachments: &[GposAttachment],
    descriptor_mapping: &[u32],
    frame: NativeDeviceFrame<'_>,
) -> Result<(), FontError> {
    frame.validate(font, coordinates, direction)?;
    if attachments.len() < glyphs.len() || descriptor_mapping.len() < glyphs.len() {
        return Err(FontError::InvalidArgument);
    }
    if glyphs.iter().any(|g| g.glyph_id > u32::from(u16::MAX)) {
        return Err(FontError::InvalidGlyph);
    }
    let units = font
        .header_metrics()
        .filter(|h| h.units_per_em != 0)
        .ok_or(FontError::InvalidFace)?
        .units_per_em;
    let projected = frame
        .source
        .project_design([[0, i32::from(units) / 16], [0; 2]])
        .map_err(|_| FontError::InvalidFace)?;
    if projected[0][0] != 0 || projected[1] != [0; 2] {
        return Err(FontError::InvalidFace);
    }
    let mut policy = DevicePolicy {
        frame,
        mapping: descriptor_mapping,
        gap: projected[0][1],
    };
    fallback_marks::apply_with(font, glyphs, direction, attachments, &mut policy)
}
