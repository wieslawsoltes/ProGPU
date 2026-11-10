use super::*;
use crate::text::paint::TextHintingMode;

pub(super) fn draw(
    scene: &mut Scene,
    c: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
    options: &ArchiveReplayOptions,
) -> Result<()> {
    let Some(draw) = prepare(c, options)? else {
        return Ok(());
    };
    let mut context = options.text_context;
    context.transform = transform;
    context.semantic_opacity = opacity;
    scene.draw_text(draw, context)?;
    Ok(())
}

pub(in crate::picture_archive) fn prepare(
    c: &ArchiveCommand,
    options: &ArchiveReplayOptions,
) -> Result<Option<TextDraw>> {
    let mut draw = if c.kind == K::DrawGlyphRun {
        let (Some(font), Some(indices), Some(positions)) =
            (&c.font, &c.glyph_indices, &c.glyph_positions)
        else {
            return Ok(None);
        };
        TextDraw::positioned(
            PositionedGlyphRun::new(font.clone(), indices.clone(), positions.clone()),
            c.font_size,
        )
    } else {
        if c.text_hinting_mode == TextHintingMode::IntegerPixel {
            return Err(ArchiveError::Unsupported);
        }
        let (Some(font), Some(text)) = (&c.font, &c.text) else {
            return Ok(None);
        };
        let environment = options.text.as_ref().ok_or(ArchiveError::Unsupported)?;
        let mut units = Vec::new();
        units
            .try_reserve(text.len())
            .map_err(|_| ArchiveError::Capacity)?;
        units.extend(text.encode_utf16());
        let layout = ManagedTextLayout::new(
            units,
            font.clone(),
            c.font_size,
            ManagedTextLayoutOptions {
                max_width: if c.rect[2] > 0.0 && c.rect[2].is_finite() {
                    c.rect[2]
                } else {
                    10000.0
                },
                ..Default::default()
            },
            environment.preparation.clone(),
            environment.fallback_fonts.as_ref(),
        )
        .map_err(ArchiveError::Text)?;
        TextDraw::layout(Arc::new(layout))
    };
    draw.position = c.position;
    draw.rotation = c.rotation;
    draw.brush = c
        .brush
        .as_ref()
        .map(|b| b.to_vector().map(Arc::new))
        .transpose()?;
    draw.bold = c.is_bold;
    draw.italic = c.is_italic;
    draw.font_transform = c.has_font_transform.then_some(c.font_transform);
    draw.rendering_mode = c.text_rendering_mode;
    draw.hinting_mode = c.text_hinting_mode;
    draw.coverage_transfer = c.glyph_coverage_transfer;
    draw.prefer_atlas = c.prefer_glyph_atlas;
    draw.vector_rendering = c.use_vector_glyph_rendering;
    draw.logical_atlas_resolution = c.use_logical_glyph_atlas_resolution;
    Ok(Some(draw))
}
