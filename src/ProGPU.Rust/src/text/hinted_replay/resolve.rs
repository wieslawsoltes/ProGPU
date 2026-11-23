//! Original ordered physical atlas occurrences and shared solid text styles.
use super::*;
use crate::{text::glyph::GlyphAtlas, vector::BrushKind};

/// Completed original atlas instances; atlas submission remains caller-owned.
/// The geometry lease retains original captured bytes beyond provider retirement.
#[derive(Debug)]
pub struct ResolvedHintedReplay {
    /// Original per-occurrence order, omitting only no-ink/clip-rejected slots.
    pub instances: Vec<GlyphInstance>,
    /// Exact shared text styles referenced by the resulting instances.
    pub styles: Vec<GpuTextStyle>,
    /// Original direct spatial paint, absent for the shared solid-style route.
    pub material: Option<HintedMaterialPaint>,
    source: Arc<FontReplayGeometry>,
}
impl ResolvedHintedReplay {
    /// Actual retained captured source generation, including no-ink descriptors.
    pub fn source(&self) -> &Arc<FontReplayGeometry> {
        &self.source
    }
}
impl HintedReplayDraw {
    /// Check source paint admission before atlas work. Spatial brushes retain the
    /// separate original per-occurrence hinted-material shader route.
    pub fn validate_atlas_paint(&self, context: TextPaintContext) -> Result<(), Error> {
        self.validate(context)?;
        if let Some(brush) = &self.brush {
            match brush.kind {
                BrushKind::Solid(color)
                    if color.iter().all(|v| v.is_finite()) && brush.opacity.is_finite() =>
                {
                    ()
                }
                BrushKind::Solid(_) => return Err(Error::InvalidInput),
                _ if !brush.opacity.is_finite() => return Err(Error::InvalidInput),
                _ => (),
            }
        }
        let spatial = self.image_brush.is_some()
            || self
                .brush
                .as_ref()
                .is_some_and(|brush| !matches!(brush.kind, BrushKind::Solid(_)));
        if spatial && context.use_mvp {
            return Err(Error::UnsupportedExecution);
        }
        if self.image_brush.is_some() && !self.range.is_empty() && self.ink_bounds()?.is_some() {
            self.texture_paint(context)?;
        }
        Ok(())
    }

    /// Prepare original solid or spatial replay in an active atlas batch. Whole selection,
    /// exact target DPI, source domain and no-ink origins preflight first. The
    /// caller flushes before submission and retains actual atlas residency.
    pub async fn resolve_pending(
        &self,
        atlas: &mut GlyphAtlas,
        context: TextPaintContext,
    ) -> Result<ResolvedHintedReplay, Error> {
        self.validate_atlas_paint(context)?;
        if !atlas.is_batch_active() {
            return Err(Error::InvalidInput);
        }
        let mut result = ResolvedHintedReplay {
            instances: Vec::new(),
            styles: Vec::new(),
            material: None,
            source: self.geometry.clone(),
        };
        if self.brush.is_none() && self.image_brush.is_none() {
            return Ok(result);
        }
        if self.range.is_empty() || self.ink_bounds()?.is_none() {
            return Ok(result);
        }
        let solid = self
            .brush
            .as_ref()
            .is_some_and(|brush| matches!(brush.kind, BrushKind::Solid(_)));
        let mut color = self
            .brush
            .as_ref()
            .map_or([1.0; 4], |brush| match brush.kind {
                BrushKind::Solid(color) => color,
                _ => [1.0; 4],
            });
        let mut effective_color = color;
        if solid {
            effective_color[3] *=
                self.brush.as_ref().map_or(1.0, |brush| brush.opacity) * context.semantic_opacity;
            if context.use_mvp {
                color = effective_color;
            }
        }
        let translation = identity_translation(context.transform)?;
        if !solid {
            if context.use_mvp {
                return Err(Error::UnsupportedExecution);
            }
            result.material = if self.image_brush.is_some() {
                self.texture_paint(context)?
            } else {
                Some(HintedMaterialPaint {
                    brush: self.brush.clone(),
                    texture: None,
                    semantic_opacity: context.semantic_opacity,
                    record: GpuHintedGlyphPaint {
                        source_offset_opacity: [translation[0], translation[1], 0.0, 0.0],
                        ..Default::default()
                    },
                })
            };
        }
        if !effective_color.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        result
            .instances
            .try_reserve_exact(self.range.len())
            .map_err(|_| Error::Capacity)?;
        if solid && !context.use_mvp {
            result
                .styles
                .try_reserve_exact(1)
                .map_err(|_| Error::Capacity)?;
            result.styles.push(GpuTextStyle {
                color: effective_color,
                rendering_mode: self.rendering_mode as u32,
                padding: [0; 3],
            });
        }
        let translation = identity_translation(context.transform)?;
        let inverse_dpi = 1.0 / self.geometry.dpi_scale();
        let mode = if context.use_mvp || !solid {
            self.rendering_mode
        } else {
            TextRenderingMode::Grayscale
        };
        let flags = match (mode, context.use_mvp) {
            (TextRenderingMode::Aliased, true) => -2.0,
            (TextRenderingMode::Aliased, false) => -1.0,
            (TextRenderingMode::Grayscale, true) => 1.0,
            (TextRenderingMode::Grayscale, false) => 0.0,
            _ => return Err(Error::UnsupportedExecution),
        } + 16.0 * self.coverage_transfer.encoding() as f32;
        for placement in self.selected()? {
            if placement.outline_index == u32::MAX {
                continue;
            }
            let position = self.raster_origin(placement.position, translation)?;
            let bounds = hinted_raster_bounds(self.record(placement.outline_index)?)?;
            if let Some(clip) = context.clip {
                let left = position[0] + bounds.origin[0] as f32 * inverse_dpi;
                let top = position[1] + bounds.origin[1] as f32 * inverse_dpi;
                if left + bounds.size[0] as f32 * inverse_dpi <= clip[0]
                    || left >= clip[0] + clip[2]
                    || top + bounds.size[1] as f32 * inverse_dpi <= clip[1]
                    || top >= clip[1] + clip[3]
                {
                    continue;
                }
            }
            let info = atlas
                .get_or_create_hinted_glyph(&self.geometry, placement.outline_index)
                .await?;
            result.instances.push(GlyphInstance {
                snapped_logical_position: position,
                basis_x: [1.0, 0.0],
                basis_y: [0.0, 1.0],
                bearing_size: [
                    info.bearing[0],
                    info.bearing[1],
                    info.size[0] as f32,
                    info.size[1] as f32,
                ],
                texture_coordinates: [
                    info.origin[0] as f32,
                    info.origin[1] as f32,
                    (info.origin[0] + info.size[0]) as f32,
                    (info.origin[1] + info.size[1]) as f32,
                ],
                color,
                scale_bold_italic_flags: [1.0, 0.0, 0.0, flags],
                brush_index: if context.use_mvp || !solid { -1.0 } else { 0.0 },
                padding: 0.0,
            });
        }
        Ok(result)
    }
}
