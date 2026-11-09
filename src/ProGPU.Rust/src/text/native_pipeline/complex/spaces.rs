//! Original native special-space mapping and design-unit metrics.
use super::*;
fn fallback(cp: u32) -> u8 {
    match cp {
        0x20 | 0xa0 => 18,
        0x2000 | 0x2002 => 2,
        0x2001 | 0x2003 | 0x3000 => 1,
        0x2004 => 3,
        0x2005 => 4,
        0x2006 => 6,
        0x2007 => 19,
        0x2008 => 20,
        0x2009 => 5,
        0x200a => 16,
        0x202f => 21,
        0x205f => 17,
        _ => 0,
    }
}
pub(super) fn map(font: &SfntFace, cp: u32, glyph: u16) -> Result<u16, FontError> {
    if glyph != 0 || fallback(cp) == 0 {
        Ok(glyph)
    } else {
        font.glyph_index(0x20).ok_or(FontError::InvalidFace)
    }
}
pub(super) fn apply(
    font: &SfntFace,
    direction: GposDirection,
    coordinates: &[i16],
    g: &mut ShapingGlyph,
) -> Result<(), FontError> {
    let kind = fallback(g.code_point);
    if kind == 0 {
        return Ok(());
    }
    let original = font
        .glyph_index(g.code_point)
        .ok_or(FontError::InvalidFace)?;
    let space = font.glyph_index(0x20).ok_or(FontError::InvalidFace)?;
    if original != 0 || space == 0 {
        return Ok(());
    }
    let vertical = matches!(
        direction,
        GposDirection::TopToBottom | GposDirection::BottomToTop
    );
    let sign = if vertical { -1 } else { 1 };
    let mut advance = i64::from(if vertical { g.advance_y } else { g.advance_x });
    let width = |id| -> Result<i64, FontError> {
        if vertical {
            Ok(-i64::from(
                font.design_advance_height(id)
                    .ok_or(FontError::InvalidFace)?,
            ))
        } else {
            let v = font.design_advance_width(id, coordinates)?;
            if !v.is_finite() {
                return Err(FontError::InvalidFace);
            }
            Ok(f64::from(v).round_ties_even() as i64)
        }
    };
    if matches!(kind, 1..=6 | 16) {
        let em = i64::from(
            font.header_metrics()
                .ok_or(FontError::InvalidFace)?
                .units_per_em,
        );
        let divisor = i64::from(kind);
        advance = sign * ((em + divisor / 2) / divisor);
    } else if kind == 17 {
        advance = sign
            * (i64::from(
                font.header_metrics()
                    .ok_or(FontError::InvalidFace)?
                    .units_per_em,
            ) * 4
                / 18);
    } else if kind == 19 {
        for cp in 0x30..=0x39 {
            let id = font.glyph_index(cp).ok_or(FontError::InvalidFace)?;
            if id != 0 {
                advance = width(id)?;
                break;
            }
        }
    } else if kind == 20 {
        let mut id = font.glyph_index(0x2e).ok_or(FontError::InvalidFace)?;
        if id == 0 {
            id = font.glyph_index(0x2c).ok_or(FontError::InvalidFace)?;
        }
        if id != 0 {
            advance = width(id)?;
        }
    } else if kind == 21 {
        advance /= 2;
    }
    if vertical {
        g.advance_y = clamped(advance);
    } else {
        g.advance_x = clamped(advance);
    }
    Ok(())
}
