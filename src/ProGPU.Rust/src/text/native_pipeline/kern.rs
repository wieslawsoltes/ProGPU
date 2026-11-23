//! Original native Microsoft/Apple kern walking, design saturation and explicit
//! checked device projection. Unsupported/truncated legacy records retain the
//! source's permissive stop/skip behavior; GPOS parsing is independently strict.
use super::{FontError, Gdef, GlyphClass, KERN, SfntFace, ShapingGlyph, clamped};
use crate::font::layout::{GposArithmetic, GposDeviceFrame, GposOptions};

fn word(data: &[u8], offset: usize) -> u16 {
    u16::from_be_bytes([data[offset], data[offset + 1]])
}
fn dword(data: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes(data[offset..offset + 4].try_into().unwrap())
}
fn fits(data: &[u8], offset: usize, count: usize) -> bool {
    offset <= data.len() && count <= data.len() - offset
}
fn dependency(glyphs: &mut [ShapingGlyph], left: usize, right: usize) {
    let cluster = glyphs[left..=right]
        .iter()
        .map(|g| g.cluster)
        .min()
        .unwrap();
    for glyph in &mut glyphs[left..=right] {
        if glyph.cluster != cluster {
            glyph.flags |= 3;
        }
    }
}
fn pair(data: &[u8], records: usize, count: u16, left: u16, right: u16) -> i32 {
    let key = (u32::from(left) << 16) | u32::from(right);
    let (mut low, mut high) = (0, count);
    while low < high {
        let middle = low + (high - low) / 2;
        let record = records + usize::from(middle) * 6;
        match key.cmp(&dword(data, record)) {
            std::cmp::Ordering::Less => high = middle,
            std::cmp::Ordering::Greater => low = middle + 1,
            std::cmp::Ordering::Equal => return i32::from(word(data, record + 4) as i16),
        }
    }
    0
}
fn class(data: &[u8], subtable: usize, length: usize, relative: u16, glyph: u16) -> u16 {
    let relative = usize::from(relative);
    if relative > length || length - relative < 4 {
        return 0;
    }
    let table = subtable + relative;
    let first = word(data, table);
    if glyph < first {
        return 0;
    }
    let index = glyph - first;
    let offset = table + 4 + usize::from(index) * 2;
    // Deliberately whole-table bounded, as in the original class helper.
    if index < word(data, table + 2) && fits(data, offset, 2) {
        word(data, offset)
    } else {
        0
    }
}
fn walk(
    font: &SfntFace,
    glyphs: &mut [ShapingGlyph],
    gdef: Option<Gdef<'_>>,
    mut apply: impl FnMut(&mut [ShapingGlyph], usize, usize, i32, bool) -> Result<(), FontError>,
) -> Result<(), FontError> {
    let Some(table) = font.table(KERN) else {
        return Ok(());
    };
    let data = table.bytes;
    let apple = fits(data, 0, 8) && dword(data, 0) == 0x10000;
    let (count, mut subtable) = if apple {
        (dword(data, 4), 8)
    } else if fits(data, 0, 4) && word(data, 0) == 0 {
        (u32::from(word(data, 2)), 4)
    } else {
        return Ok(());
    };
    for _ in 0..count {
        let header = if apple { 8 } else { 6 };
        if !fits(data, subtable, header) {
            break;
        }
        let length = if apple {
            dword(data, subtable) as usize
        } else {
            usize::from(word(data, subtable + 2))
        };
        if length < header || !fits(data, subtable, length) {
            break;
        }
        let format = data[subtable + if apple { 5 } else { 4 }];
        let coverage = data[subtable + if apple { 4 } else { 5 }];
        let horizontal = if apple {
            coverage & 0x80 == 0
        } else {
            coverage & 1 != 0
        };
        let cross = coverage & if apple { 0x40 } else { 4 } != 0;
        let body = subtable + header;
        if horizontal && (format == 0 || format == 2) && fits(data, body, 8) {
            let pairs = word(data, body);
            let records = body + 8;
            let valid_pairs = records <= subtable + length
                && usize::from(pairs) <= (subtable + length - records) / 6;
            if format == 2 || valid_pairs {
                for left in 0..glyphs.len().saturating_sub(1) {
                    let mut right = left + 1;
                    while right < glyphs.len()
                        && gdef.is_some_and(|d| {
                            d.glyph_class(glyphs[right].glyph_id as u16) == GlyphClass::Mark
                        })
                    {
                        right += 1;
                    }
                    if right >= glyphs.len() {
                        break;
                    }
                    let kerning = if format == 0 {
                        pair(
                            data,
                            records,
                            pairs,
                            glyphs[left].glyph_id as u16,
                            glyphs[right].glyph_id as u16,
                        )
                    } else {
                        let l = class(
                            data,
                            subtable,
                            length,
                            word(data, body + 2),
                            glyphs[left].glyph_id as u16,
                        );
                        let r = class(
                            data,
                            subtable,
                            length,
                            word(data, body + 4),
                            glyphs[right].glyph_id as u16,
                        );
                        let offset = usize::from(l) + usize::from(r);
                        if offset < usize::from(word(data, body + 6))
                            || offset > length
                            || length - offset < 2
                        {
                            0
                        } else {
                            i32::from(word(data, subtable + offset) as i16)
                        }
                    };
                    apply(glyphs, left, right, kerning, cross)?;
                }
            }
        }
        subtable += length;
    }
    Ok(())
}
/// Apply the original native design-unit legacy kerning, preserving per-pair
/// signed-16 saturation, ordered subtables, GDEF mark skipping and dependencies.
pub fn apply_native_legacy_kern(
    font: &SfntFace,
    glyphs: &mut [ShapingGlyph],
    gdef: Option<Gdef<'_>>,
) {
    let _ = walk(font, glyphs, gdef, |glyphs, left, right, kerning, cross| {
        if kerning == 0 {
            return Ok(());
        }
        dependency(glyphs, left, right);
        if cross {
            glyphs[right].offset_y =
                clamped(i64::from(glyphs[right].offset_y) + i64::from(kerning));
        } else {
            let first = kerning.div_euclid(2);
            let second = kerning - first;
            glyphs[left].advance_x = clamped(i64::from(glyphs[left].advance_x) + i64::from(first));
            glyphs[right].advance_x =
                clamped(i64::from(glyphs[right].advance_x) + i64::from(second));
            glyphs[right].offset_x = clamped(i64::from(glyphs[right].offset_x) + i64::from(second));
        }
        Ok(())
    });
}
/// Project original legacy pairs into an actual native device generation.
/// Checked SIMD/scalar additions publish complete pairs; earlier pairs remain
/// written on failure. A nonzero raw pair retains dependency even at zero scale.
pub fn apply_native_device_legacy_kern(
    font: &SfntFace,
    glyphs: &mut [ShapingGlyph],
    gdef: Option<Gdef<'_>>,
    frame: GposDeviceFrame<'_>,
) -> Result<(), FontError> {
    frame.validate(GposOptions {
        font: Some(font),
        normalized_coordinates: frame.source.normalized_coordinates(),
        ..Default::default()
    })?;
    if font.data().is_empty() {
        return Err(FontError::InvalidArgument);
    }
    walk(font, glyphs, gdef, |glyphs, left, right, kerning, cross| {
        if kerning == 0 {
            return Ok(());
        }
        let design = if cross { [0, kerning] } else { [kerning, 0] };
        let projected = frame
            .source
            .project_design([design, [0; 2]])
            .map_err(|_| FontError::InvalidArgument)?[0][usize::from(cross)];
        let first = projected.div_euclid(2);
        let second = projected - first;
        let values = [
            glyphs[left].advance_x,
            glyphs[right].advance_x,
            glyphs[right].offset_x,
            glyphs[right].offset_y,
        ];
        let deltas = if cross {
            [0, 0, 0, projected]
        } else {
            [first, second, second, 0]
        };
        let mut adjusted = [0; 4];
        match frame.arithmetic {
            GposArithmetic::ScalarReference => {
                for i in 0..4 {
                    adjusted[i] = values[i]
                        .checked_add(deltas[i])
                        .ok_or(FontError::InvalidArgument)?;
                }
            }
            GposArithmetic::IntrinsicSimd => {
                let a = wide::i32x4::from(values);
                let b = wide::i32x4::from(deltas);
                let sum = a + b;
                let overflow = (sum ^ a) & !(a ^ b);
                if overflow.to_array().iter().any(|&v| v < 0) {
                    return Err(FontError::InvalidArgument);
                }
                adjusted = sum.to_array();
            }
        }
        glyphs[left].advance_x = adjusted[0];
        glyphs[right].advance_x = adjusted[1];
        glyphs[right].offset_x = adjusted[2];
        glyphs[right].offset_y = adjusted[3];
        dependency(glyphs, left, right);
        Ok(())
    })
}
