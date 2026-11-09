//! Ordered same-device atlas realization. The owning renderer controls the outer
//! batch lifetime; no destructor submits, polls or destroys an in-flight texture.
use super::*;
use crate::text::glyph::{
    GlyphAtlas, GlyphAtlasResidency, GlyphAtlasStatistics, GlyphColorTexture, GlyphCoverageTexture,
    GlyphInfo,
};

/// Complete realized painter-order command. Vector policies still require the
/// source text PathAtlas consumer; ordinary path defaults must not replace them.
#[derive(Clone, Debug)]
pub enum ResolvedTextCommand {
    /// Original instance sampling actual atlas coverage.
    Glyph(GlyphInstance),
    /// Original positioned outline/color-layer draw.
    Vector(VectorGlyphPaint),
}
/// Submitted atlas coverage, exact text-style page and independent vector draws.
/// The host validates residency before replay and recompiles after invalidation.
pub struct ResolvedTextPaint {
    commands: Vec<ResolvedTextCommand>,
    styles: Vec<GpuTextStyle>,
    residency: GlyphAtlasResidency,
    statistics: GlyphAtlasStatistics,
    coverage: GlyphCoverageTexture,
    color: GlyphColorTexture,
    context: TextPaintContext,
}
impl ResolvedTextPaint {
    /// Complete resolved painter-order inventory.
    pub fn commands(&self) -> &[ResolvedTextCommand] {
        &self.commands
    }
    /// Original ordered shared text-style records.
    pub fn styles(&self) -> &[GpuTextStyle] {
        &self.styles
    }
    /// Actual source keys required by this generation.
    pub fn residency(&self) -> &GlyphAtlasResidency {
        &self.residency
    }
    /// Retained actual grayscale texture generation.
    pub fn coverage(&self) -> &GlyphCoverageTexture {
        &self.coverage
    }
    /// Retained actual color bitmap texture generation.
    pub fn color(&self) -> &GlyphColorTexture {
        &self.color
    }
    /// Exact target/MVP/camera/clip state; early culling does not replace clipping.
    pub fn context(&self) -> TextPaintContext {
        self.context
    }
    /// Actual immutable generation/revision captured after all requests/flushes.
    pub fn atlas_statistics(&self) -> GlyphAtlasStatistics {
        self.statistics
    }
    /// Conservative original whole-generation gate plus actual owner/key lookup.
    /// A resident key alone cannot prove that its previous physical slot survived.
    pub fn is_current(&self, atlas: &GlyphAtlas) -> bool {
        let current = atlas.statistics();
        current.generation == self.statistics.generation
            && current.texture_revision == self.statistics.texture_revision
            && atlas.contains_residency(&self.residency)
    }
}
impl TextPainter {
    /// Resolve a plan inside the caller's active atlas batch, preserving every
    /// transition between atlas and vector draws. Flush before publication.
    /// The caller ends/abandons its batch on error or cancellation; this method
    /// never changes batch depth or owns unrelated callers' pending requests.
    pub async fn resolve(
        &mut self,
        atlas: &mut GlyphAtlas,
        plan: TextPaintPlan,
    ) -> Result<ResolvedTextPaint> {
        if !atlas.is_batch_active() {
            return Err(Error::InvalidInput.into());
        }
        let start = atlas.batch_usage_count();
        let mut commands = Vec::new();
        commands
            .try_reserve(plan.commands.len())
            .map_err(|_| Error::Capacity)?;
        for command in plan.commands {
            match command {
                TextPaintCommand::Vector(path) => {
                    push(&mut commands, ResolvedTextCommand::Vector(path))?
                }
                TextPaintCommand::Atlas(request) => {
                    let info = atlas
                        .get_or_create_glyph_by_index(
                            &request.font,
                            request.glyph,
                            request.raster_size,
                            request.phase,
                            request.prefer_atlas,
                        )
                        .await?;
                    if info.size.contains(&0) {
                        let mut transform = request.state.transform;
                        let mut position = request.fallback_position;
                        if request.state.draw.hinting_mode == TextHintingMode::IntegerPixel {
                            position = [0.0; 2];
                            transform.0[3][0] = request.snapped_position[0];
                            transform.0[3][1] = request.snapped_position[1];
                        }
                        let mut fallback = Vec::new();
                        self.fallback(
                            &mut fallback,
                            &request.font,
                            request.glyph,
                            &request.state,
                            position,
                            transform,
                        )?;
                        commands
                            .try_reserve(fallback.len())
                            .map_err(|_| Error::Capacity)?;
                        for item in fallback {
                            let TextPaintCommand::Vector(path) = item else {
                                return Err(Error::InvalidInput.into());
                            };
                            commands.push(ResolvedTextCommand::Vector(path));
                        }
                    } else {
                        let mut instances = [GlyphInstance::default(); 2];
                        let count = request.write_instances(info.into(), &mut instances)?;
                        commands.try_reserve(count).map_err(|_| Error::Capacity)?;
                        commands.extend(
                            instances[..count]
                                .iter()
                                .copied()
                                .map(ResolvedTextCommand::Glyph),
                        );
                    }
                }
            }
        }
        atlas.flush_pending().await?;
        let residency = atlas.capture_batch_residency(start, None)?;
        Ok(ResolvedTextPaint {
            commands,
            styles: plan.styles,
            residency,
            statistics: atlas.statistics(),
            coverage: atlas.coverage().clone(),
            color: atlas.color().clone(),
            context: plan.context,
        })
    }
}
impl From<GlyphInfo> for GlyphImageMetrics {
    fn from(value: GlyphInfo) -> Self {
        Self {
            origin: value.origin,
            size: value.size,
            bearing: value.bearing,
            render_size: value.render_size,
            raster_scale: value.raster_scale,
            is_color_bitmap: value.is_color_bitmap,
        }
    }
}
