use super::*;
use crate::{
    path::Path,
    text::{
        native_atlas::NativeAtlasPaint,
        native_design_geometry::map_native_design_outline,
        paint::{TextPaintContext, VectorGlyphRasterInputs, vector_glyph_raster_policy},
    },
    vector::{Brush, BrushKind, Matrix4},
};
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(super) struct PathKey {
    owner: usize,
    layer: usize,
    values: [u32; 4],
}
fn key(value: f32) -> u32 {
    if value == 0.0 { 0 } else { value.to_bits() }
}
pub(super) fn translation(position: [f32; 2]) -> Matrix4 {
    let mut result = Matrix4::IDENTITY;
    result.0[3][0] = position[0];
    result.0[3][1] = position[1];
    result
}
pub(super) fn colr(
    resolver: &mut NativeColorResolver,
    glyph: &Arc<NativeColorGlyph>,
    slot: u32,
    paint: NativeAtlasPaint,
    em: f32,
    position: [f32; 2],
    context: TextPaintContext,
) -> Result<Vec<NativeColorVectorPath>> {
    let NativeColorPresentation::Colr(layers) = glyph.presentation() else {
        return Err(Error::InvalidInput.into());
    };
    let phase = position.map(|v| {
        let value = ((v - v.floor()) * 128.0).round_ties_even() / 128.0;
        if value >= 1.0 { 0.0 } else { value }
    });
    let italic = if paint.italic { 0.22 } else { 0.0 };
    let policy = vector_glyph_raster_policy(
        VectorGlyphRasterInputs {
            font_size: paint.font_size,
            scale_x: 1.0,
            skew_x: 0.0,
            rendering_mode: paint.rendering_mode,
            coverage_transfer: paint.coverage_transfer,
            logical_atlas_resolution: false,
        },
        context.transform,
        context,
    )?;
    let transform =
        translation([position[0] - phase[0], position[1] - phase[1]]) * context.transform;
    let mut output = reserve(layers.len())?;
    for (index, layer) in layers.iter().enumerate() {
        if !layer.outline.has_ink() {
            continue;
        }
        let key = PathKey {
            owner: Arc::as_ptr(glyph) as usize,
            layer: index,
            values: [em, phase[0], phase[1], italic].map(key),
        };
        let mut path: Path = if let Some((_, path)) = resolver.paths.get(&key) {
            path.clone()
        } else {
            let path = map_native_design_outline(&layer.outline, em, phase, italic, 1.0)?;
            if resolver.paths.len() >= 4096 {
                resolver.paths.clear();
            }
            resolver.paths.try_reserve(1).map_err(|_| Error::Capacity)?;
            resolver.paths.insert(key, (glyph.clone(), path.clone()));
            path
        };
        path.retain_source_owner(glyph.clone());
        let color = layer.record.color;
        let color = [color.red, color.green, color.blue, color.alpha].map(|v| f32::from(v) / 255.0);
        output.push(NativeColorVectorPath {
            path,
            transform,
            brush: Arc::new(Brush::new(BrushKind::Solid(color))),
            semantic_opacity: context.semantic_opacity,
            policy,
            source_slot: slot,
            source: glyph.clone(),
        });
    }
    Ok(output)
}
