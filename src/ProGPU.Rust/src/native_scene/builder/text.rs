//! Bulk original native shaped-run bridge; conversion storage remains caller-owned.
use super::*;
use crate::text::{ShapingGlyph, native_layout::NativePositionedTextGlyph};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeShapedTextSceneOptions {
    pub basis_x: wire::Point,
    pub basis_y: wire::Point,
    pub color: wire::Color,
    pub atlas_to_logical_scale: f32,
    pub bold_offset: f32,
    pub italic_skew: f32,
}
impl Default for NativeShapedTextSceneOptions {
    fn default() -> Self {
        Self {
            basis_x: wire::Point { x: 1.0, y: 0.0 },
            basis_y: wire::Point { x: 0.0, y: 1.0 },
            color: wire::Color {
                r: 1.0,
                g: 1.0,
                b: 1.0,
                a: 1.0,
            },
            atlas_to_logical_scale: 1.0,
            bold_offset: 0.0,
            italic_skew: 0.0,
        }
    }
}
impl NativeSceneBuilder {
    /// Convert the actual source occurrences once, then publish one glyph command.
    /// As in the native API, an invalid later occurrence can leave an earlier
    /// prefix in caller scratch; builder records are published only on success.
    pub fn draw_shaped_text_run(
        &mut self,
        glyph_resource_index: u32,
        shaped_glyphs: &[ShapingGlyph],
        positioned_glyphs: &[NativePositionedTextGlyph],
        conversion_scratch: &mut [wire::PositionedGlyph],
        options: &NativeShapedTextSceneOptions,
        bounds: wire::ImageRect,
        glyph_to_outline: &[u32],
        state_resource_index: u32,
        text_style_index: u32,
    ) -> BuildResult<()> {
        if positioned_glyphs.is_empty()
            || conversion_scratch.len() < positioned_glyphs.len()
            || ![
                options.basis_x.x,
                options.basis_x.y,
                options.basis_y.x,
                options.basis_y.y,
                options.atlas_to_logical_scale,
                options.bold_offset,
                options.italic_skew,
            ]
            .into_iter()
            .all(f32::is_finite)
            || !finite_color(options.color)
            || options.atlas_to_logical_scale <= 0.0
        {
            return Err(B::InvalidArgument);
        }
        for (destination, positioned) in conversion_scratch.iter_mut().zip(positioned_glyphs) {
            if (positioned.glyph_index != u32::MAX
                && positioned.glyph_index as usize >= shaped_glyphs.len())
                || !positioned.position.into_iter().all(f32::is_finite)
                || (positioned.glyph_index != u32::MAX
                    && positioned.glyph_id
                        != shaped_glyphs[positioned.glyph_index as usize].glyph_id)
                || (!glyph_to_outline.is_empty()
                    && positioned.glyph_id as usize >= glyph_to_outline.len())
            {
                return Err(B::InvalidArgument);
            }
            *destination = wire::PositionedGlyph {
                outline_index: if glyph_to_outline.is_empty() {
                    positioned.glyph_id
                } else {
                    glyph_to_outline[positioned.glyph_id as usize]
                },
                reserved: 0,
                position: wire::Point {
                    x: positioned.position[0],
                    y: positioned.position[1],
                },
                basis_x: options.basis_x,
                basis_y: options.basis_y,
                color: options.color,
                atlas_to_logical_scale: options.atlas_to_logical_scale,
                bold_offset: options.bold_offset,
                italic_skew: options.italic_skew,
                reserved2: 0.0,
            };
        }
        self.draw_glyph_run(
            glyph_resource_index,
            &conversion_scratch[..positioned_glyphs.len()],
            bounds,
            state_resource_index,
            text_style_index,
            None,
        )
    }
}
