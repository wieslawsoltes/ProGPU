//! Original Canvas text-path raster metadata resolved at the actual target.
use super::*;
use crate::text::paint::{VectorGlyphRasterInputs, vector_glyph_raster_policy};
use crate::vector::PathGeometry;

#[derive(Clone, Debug)]
pub(super) struct CanvasTextPath {
    pub geometry: Arc<PathGeometry>,
    pub brush: Arc<Brush>,
    pub context: TextPaintContext,
    pub inputs: VectorGlyphRasterInputs,
}
impl Scene {
    pub(crate) fn draw_canvas_text_path(
        &mut self,
        geometry: Arc<PathGeometry>,
        brush: Arc<Brush>,
        context: TextPaintContext,
        inputs: VectorGlyphRasterInputs,
    ) -> Result<(), Error> {
        text::validate_context(context)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::CanvasTextPath(Arc::new(CanvasTextPath {
                geometry,
                brush,
                context,
                inputs,
            })));
        Ok(())
    }
    pub(super) fn expand_canvas_text(&self, dpi: f32) -> Result<Option<Self>, Error> {
        if !self
            .commands
            .iter()
            .any(|c| matches!(c, Command::CanvasTextPath(_)))
        {
            return Ok(None);
        }
        let mut candidate = self.clone();
        candidate.commands.clear();
        candidate
            .commands
            .try_reserve(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            let Command::CanvasTextPath(draw) = command else {
                candidate.commands.push(command.clone());
                continue;
            };
            let mut context = draw.context;
            context.dpi_scale = dpi;
            let mut policy = if draw.inputs.font_size > 0.0 {
                vector_glyph_raster_policy(draw.inputs, context.transform, context)?
            } else {
                // Original DrawPath only selects text gamma for positive size.
                TextPathPolicy {
                    aliased: draw.inputs.rendering_mode
                        == crate::text::paint::TextRenderingMode::Aliased,
                    sample_grid: if draw.inputs.rendering_mode
                        == crate::text::paint::TextRenderingMode::Aliased
                    {
                        4
                    } else {
                        8
                    },
                    coverage_gamma: 0.0,
                    device_phase_grid: 64,
                    quantize_scale: false,
                    raster_scale: dpi,
                    coverage_transfer: draw.inputs.coverage_transfer,
                }
            };
            // Canvas records DrawPath with text gamma metadata. It keeps the
            // ordinary CompilePathCommand defaults, unlike direct glyph replay.
            policy.device_phase_grid = 64;
            policy.quantize_scale = false;
            policy.raster_scale = dpi;
            if let Some(command) = text::path_command(VectorGlyphPaint {
                path: draw.geometry.clone(),
                transform: context.transform,
                brush: Some(draw.brush.clone()),
                semantic_opacity: context.semantic_opacity,
                policy,
            })? {
                candidate.commands.push(command);
            }
        }
        Ok(Some(candidate))
    }
}
