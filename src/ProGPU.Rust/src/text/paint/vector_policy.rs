//! Shared original Compositor explicit/fallback vector text raster policy.
//! Call only at the actual target; geometry/shaping and placement remain separate.
use super::*;
#[derive(Clone, Copy, Debug)]
pub(crate) struct VectorGlyphRasterInputs {
    pub font_size: f32,
    pub scale_x: f32,
    pub skew_x: f32,
    pub rendering_mode: TextRenderingMode,
    pub coverage_transfer: GlyphCoverageTransfer,
    pub logical_atlas_resolution: bool,
}
fn positive(v: f32) -> f32 {
    if v.is_finite() && v > 0.0 { v } else { 1.0 }
}
pub(crate) fn vector_glyph_raster_policy(
    input: VectorGlyphRasterInputs,
    transform: Matrix4,
    context: TextPaintContext,
) -> std::result::Result<TextPathPolicy, Error> {
    input.coverage_transfer.validate(input.rendering_mode)?;
    if !input.font_size.is_finite() || input.font_size <= 0.0 {
        return Err(Error::InvalidInput);
    }
    let scale_x = if input.scale_x.is_finite() {
        input.scale_x
    } else {
        1.0
    };
    let skew_x = if input.skew_x.is_finite() {
        input.skew_x
    } else {
        0.0
    };
    let dpi = positive(context.dpi_scale) * positive(context.static_zoom);
    let frame = if context.gpu_transforms_active {
        transform * context.camera_view
    } else {
        transform
    };
    let scale = frame.stroke_scales().map_or(1.0, |s| s[0]);
    let gamma = if skew_x.abs() > 0.0001
        || scale_x < 0.0
        || frame.0[0][1].abs() > 0.0001
        || frame.0[1][0].abs() > 0.0001
        || frame.0[0][0] < 0.0
        || frame.0[1][1] < 0.0
    {
        0.875
    } else {
        let pixels = input.font_size.abs() * scale * dpi;
        if pixels.is_finite() && pixels >= 24.0 {
            0.5
        } else {
            0.72
        }
    };
    Ok(TextPathPolicy {
        aliased: input.rendering_mode == TextRenderingMode::Aliased,
        sample_grid: if input.rendering_mode == TextRenderingMode::Aliased {
            4
        } else {
            8
        },
        coverage_gamma: input.coverage_transfer.vector(gamma),
        device_phase_grid: 4,
        quantize_scale: true,
        raster_scale: if input.logical_atlas_resolution {
            positive(context.static_zoom)
        } else {
            dpi
        },
        coverage_transfer: input.coverage_transfer,
    })
}
