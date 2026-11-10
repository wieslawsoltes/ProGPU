//! Ordered original paragraph style ownership over common prepared hinted replay.
use super::*;
use crate::{
    Error,
    image::ImageBrush,
    text::{
        hinted_replay::HintedReplayDraw,
        paint::{GlyphCoverageTransfer, TextHintingMode, TextPaintContext, TextRenderingMode},
    },
    vector::{Brush, BrushKind},
};
/// Original explicit style paint and source domain. The source host supplies
/// brush/image mapping and source ink bounds; neither is inferred from glyph IDs.
#[derive(Clone, Debug)]
pub struct NativeParagraphPaint {
    pub brush: Option<Arc<Brush>>,
    pub image_brush: Option<Arc<ImageBrush>>,
    pub source_ink_bounds: [f32; 4],
    pub rendering_mode: TextRenderingMode,
    pub hinting_mode: TextHintingMode,
    pub coverage_transfer: GlyphCoverageTransfer,
}
impl NativeParagraphPaint {
    /// Exact original finite solid color, with unchanged source coverage defaults.
    pub fn solid(color: [f32; 4], source_ink_bounds: [f32; 4]) -> Result<Self, Error> {
        if color.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            brush: Some(Arc::new(Brush::new(BrushKind::Solid(color)))),
            image_brush: None,
            source_ink_bounds,
            rendering_mode: TextRenderingMode::Grayscale,
            hinting_mode: TextHintingMode::Auto,
            coverage_transfer: GlyphCoverageTransfer::default(),
        })
    }
    fn draw(
        &self,
        geometry: Arc<FontReplayGeometry>,
        range: Range<usize>,
        position: [f32; 2],
    ) -> HintedReplayDraw {
        HintedReplayDraw {
            geometry,
            range,
            position,
            source_ink_bounds: self.source_ink_bounds,
            brush: self.brush.clone(),
            image_brush: self.image_brush.clone(),
            rendering_mode: self.rendering_mode,
            hinting_mode: self.hinting_mode,
            coverage_transfer: self.coverage_transfer,
        }
    }
}
/// Prepare complete visual-order style groups before recorder mutation. Selected
/// views may repeat/reorder occurrences; each keeps its original style/source IDs.
/// Every selected no-ink occurrence participates in exact DPI/origin preflight.
pub fn prepare_native_paragraph_draws(
    geometry: Arc<FontReplayGeometry>,
    paints: &[NativeParagraphPaint],
    position: [f32; 2],
    context: TextPaintContext,
) -> Result<Vec<HintedReplayDraw>, Error> {
    let source = geometry
        .paragraph_source_geometry()
        .ok_or(Error::InvalidInput)?;
    let occurrences = geometry
        .paragraph_occurrences()
        .ok_or(Error::InvalidInput)?;
    if paints.len() != source.records().styles.len() || position.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    for p in paints {
        if let Some(brush) = &p.brush {
            if let BrushKind::Solid(color) = brush.kind {
                if color.iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
            }
        }
        p.draw(geometry.clone(), 0..0, position).validate(context)?;
    }
    let mut draws = Vec::new();
    draws
        .try_reserve(occurrences.len())
        .map_err(|_| Error::Capacity)?;
    let mut start = 0;
    while start < occurrences.len() {
        let style = occurrences[start].style_index as usize;
        let paint = paints.get(style).ok_or(Error::InvalidInput)?;
        let mut end = start + 1;
        while end < occurrences.len() && occurrences[end].style_index as usize == style {
            end += 1;
        }
        let draw = paint.draw(geometry.clone(), start..end, position);
        draw.validate(context)?;
        draws.push(draw);
        start = end;
    }
    Ok(draws)
}
