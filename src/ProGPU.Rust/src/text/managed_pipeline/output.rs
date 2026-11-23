//! Original Shape / ShapeDesignUnits output boundaries, including vertical metrics.
use super::*;
use crate::text::ShapingGlyph;

/// Original public scaled glyph output in screen-space coordinates (Y downward).
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ManagedScaledGlyph {
    /// Actual selected glyph slot.
    pub glyph_index: u16,
    /// Original signed UTF-16 cluster.
    pub cluster: i32,
    /// Original source scalar or retained raw code point.
    pub code_point: u32,
    /// Scaled horizontal advance.
    pub advance_x: f32,
    /// Scaled vertical advance, with original output Y inversion.
    pub advance_y: f32,
    /// Original scaled horizontal placement offset.
    pub offset_x: f32,
    /// Original scaled vertical placement offset, Y downward.
    pub offset_y: f32,
    /// Original public/private dependency flag bits.
    pub flags: u32,
}
impl ManagedShapedRun {
    /// Original output scaling. Vertical origins/advances are rounded at the
    /// requested size before the residual GPOS offsets are scaled. Input font
    /// sizes retain source behavior, including zero and nonfinite values.
    /// A failed font query discards the candidate without publishing partial output.
    pub fn scaled(&self, font_size: f32) -> Result<Vec<ManagedScaledGlyph>, FontError> {
        let font = self.source().font();
        let em = f32::from(font.metadata().units_per_em);
        let scale = font_size / em;
        let vertical = matches!(
            self.direction(),
            ManagedShapingDirection::TopToBottom | ManagedShapingDirection::BottomToTop
        );
        let mut result = Vec::new();
        result
            .try_reserve_exact(self.glyphs().len())
            .map_err(|_| FontError::Capacity)?;
        for g in self.glyphs() {
            let mut ay = f32::from(g.advance_y) * scale;
            let mut ox = f32::from(g.offset_x) * scale;
            let mut oy = f32::from(g.offset_y) * scale;
            if vertical && scale != 1.0 {
                let base_ay = (font.advance_height(g.glyph_index, em)?.round_ties_even() as i32)
                    .wrapping_neg();
                let scaled_ay = (font
                    .advance_height(g.glyph_index, font_size)?
                    .round_ties_even() as i32)
                    .wrapping_neg();
                ay = i32::from(g.advance_y).wrapping_sub(base_ay) as f32 * scale + scaled_ay as f32;
                let base_x = (font.advance_width(g.glyph_index, em)?.round_ties_even() as i32 / 2)
                    .wrapping_neg();
                let scaled_x = (font
                    .advance_width(g.glyph_index, font_size)?
                    .round_ties_even() as i32
                    / 2)
                .wrapping_neg();
                ox = i32::from(g.offset_x).wrapping_sub(base_x) as f32 * scale + scaled_x as f32;
                let base_y = (font.vertical_origin_y(g.glyph_index, em)?.round_ties_even() as i32)
                    .wrapping_neg();
                let scaled_y = (font
                    .vertical_origin_y(g.glyph_index, font_size)?
                    .round_ties_even() as i32)
                    .wrapping_neg();
                oy = i32::from(g.offset_y).wrapping_sub(base_y) as f32 * scale + scaled_y as f32;
            }
            result.push(ManagedScaledGlyph {
                glyph_index: g.glyph_index,
                cluster: g.cluster,
                code_point: g.code_point,
                advance_x: f32::from(g.advance_x) * scale,
                advance_y: -ay,
                offset_x: ox,
                offset_y: -oy,
                flags: g.flags,
            });
        }
        Ok(result)
    }
    /// Original ShapeDesignUnits record projection into reusable caller storage.
    /// Clears the destination before capacity admission, as the source writer does.
    pub fn write_design_units(&self, destination: &mut Vec<ShapingGlyph>) -> Result<(), FontError> {
        destination.clear();
        destination
            .try_reserve(self.glyphs().len())
            .map_err(|_| FontError::Capacity)?;
        destination.extend(self.glyphs().iter().map(|g| ShapingGlyph {
            glyph_id: u32::from(g.glyph_index),
            code_point: g.code_point,
            cluster: g.cluster,
            flags: g.flags,
            advance_x: i32::from(g.advance_x),
            advance_y: -i32::from(g.advance_y),
            offset_x: i32::from(g.offset_x),
            offset_y: -i32::from(g.offset_y),
        }));
        Ok(())
    }
}
/// Original public Shape boundary, including its empty-input early return.
/// Nonempty input creates an owned plan; frequent callers can retain a plan and
/// call `shape` / `scaled` separately to reuse selection and immutable font state.
pub fn shape_scaled(
    text: impl Into<Arc<[u16]>>,
    font: ManagedFont,
    font_size: f32,
    options: ManagedShapingOptions,
    preparation: ManagedPreparationOptions,
) -> Result<Vec<ManagedScaledGlyph>, ManagedShapingError> {
    let text = text.into();
    if text.is_empty() {
        return Ok(Vec::new());
    }
    let plan = ManagedShapingPlan::new(font, &text, options)?;
    Ok(plan.shape(text, preparation)?.scaled(font_size)?)
}
/// Original reusable design-unit boundary. Empty source clears output and does
/// not inspect font tables; a failed shape leaves the destination empty.
pub fn shape_design_units(
    text: impl Into<Arc<[u16]>>,
    font: ManagedFont,
    options: ManagedShapingOptions,
    preparation: ManagedPreparationOptions,
    destination: &mut Vec<ShapingGlyph>,
) -> Result<(), ManagedShapingError> {
    destination.clear();
    let text = text.into();
    if text.is_empty() {
        return Ok(());
    }
    let plan = ManagedShapingPlan::new(font, &text, options)?;
    plan.shape(text, preparation)?
        .write_design_units(destination)?;
    Ok(())
}
