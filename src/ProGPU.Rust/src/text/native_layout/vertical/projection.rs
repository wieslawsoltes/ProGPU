//! Original OpenTypeTextShaper.Shape projection arithmetic, including the
//! independent rounded vertical base metrics and unrounded shaping deltas.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeProjectedGlyph {
    pub glyph_id: u32,
    pub code_point: u32,
    pub cluster: i32,
    pub flags: u32,
    pub advance_x: f32,
    pub advance_y: f32,
    pub offset_x: f32,
    pub offset_y: f32,
}
fn direction(d: GposDirection) -> bool {
    matches!(
        d,
        GposDirection::LeftToRight
            | GposDirection::RightToLeft
            | GposDirection::TopToBottom
            | GposDirection::BottomToTop
    )
}
fn round_even(v: f32) -> Result<i32, FontError> {
    if !v.is_finite() {
        return Err(FontError::InvalidArgument);
    }
    let floor = f64::from(v).floor();
    let fraction = f64::from(v) - floor;
    let value = if fraction > 0.5 || fraction == 0.5 && floor.abs() % 2.0 != 0.0 {
        floor + 1.0
    } else {
        floor
    };
    if value < f64::from(i32::MIN) || value > f64::from(i32::MAX) {
        return Err(FontError::InvalidArgument);
    }
    Ok(value as i32)
}
pub(super) fn project(
    font: &SfntFace,
    coordinates: &[i16],
    g: ShapingGlyph,
    scale: f32,
    d: GposDirection,
) -> Result<NativeProjectedGlyph, FontError> {
    if g.glyph_id > u16::MAX as u32 {
        return Err(FontError::InvalidGlyph);
    }
    if !scale.is_finite() || scale <= 0.0 || !direction(d) {
        return Err(FontError::InvalidArgument);
    }
    let mut advance_y = g.advance_y as f32 * scale;
    let mut offset_x = g.offset_x as f32 * scale;
    let mut offset_y = g.offset_y as f32 * scale;
    if vertical(d) && scale != 1.0 {
        let id = g.glyph_id as u16;
        let height = font
            .design_advance_height(id)
            .ok_or(FontError::InvalidFace)?;
        let origin = font
            .design_vertical_origin_y(id)
            .ok_or(FontError::InvalidFace)?;
        let width = font.design_advance_width(id, coordinates)?;
        let base_width = round_even(width)?;
        let scaled_width = round_even(width * scale)?;
        let scaled_height = round_even(height as f32 * scale)?;
        let scaled_origin = round_even(origin as f32 * scale)?;
        let base_advance_y = height.wrapping_neg();
        let scaled_advance_y = scaled_height.wrapping_neg();
        let base_offset_x = -(base_width / 2);
        let scaled_offset_x = -(scaled_width / 2);
        let base_offset_y = origin.wrapping_neg();
        let scaled_offset_y = scaled_origin.wrapping_neg();
        advance_y =
            ((g.advance_y as f32 - base_advance_y as f32) * scale) + scaled_advance_y as f32;
        offset_x = ((g.offset_x as f32 - base_offset_x as f32) * scale) + scaled_offset_x as f32;
        offset_y = ((g.offset_y as f32 - base_offset_y as f32) * scale) + scaled_offset_y as f32;
    }
    Ok(NativeProjectedGlyph {
        glyph_id: g.glyph_id,
        code_point: g.code_point,
        cluster: g.cluster,
        flags: g.flags,
        advance_x: g.advance_x as f32 * scale,
        advance_y: -advance_y,
        offset_x,
        offset_y: -offset_y,
    })
}
/// Project the exact original glyphs at the requested font size. Private staging
/// preserves full publication admission even when owned variation scratch cannot
/// allocate. The original arithmetic and output tails remain unchanged.
pub fn native_project_open_type_into(
    font: &SfntFace,
    coordinates: &[i16],
    glyphs: &[ShapingGlyph],
    font_size: f32,
    d: GposDirection,
    output: &mut [NativeProjectedGlyph],
) -> Result<usize, FontError> {
    if output.len() < glyphs.len() {
        return Err(FontError::Capacity);
    }
    let header = font
        .header_metrics()
        .filter(|h| h.units_per_em != 0)
        .ok_or(FontError::InvalidFace)?;
    if !font_size.is_finite() || font_size <= 0.0 || !direction(d) {
        return Err(FontError::InvalidArgument);
    }
    let scale = font_size / f32::from(header.units_per_em);
    let mut candidate = Vec::new();
    candidate
        .try_reserve_exact(glyphs.len())
        .map_err(|_| FontError::Capacity)?;
    for &g in glyphs {
        candidate.push(project(font, coordinates, g, scale, d)?);
    }
    output[..candidate.len()].copy_from_slice(&candidate);
    Ok(candidate.len())
}
