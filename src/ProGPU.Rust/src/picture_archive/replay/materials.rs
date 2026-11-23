//! Original DrawingContext rectangle/rounded-rectangle material interception.
use super::*;
use std::borrow::Cow;

/// Return the remaining ordinary command after the original brush factory has
/// emitted its ordered material fill. Stroke stays after the fill.
pub(super) fn realize<'a>(
    scene: &mut Scene,
    source: &'a ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
) -> Result<Option<Cow<'a, ArchiveCommand>>> {
    let Some(super::super::ArchiveBrush::BackdropMaterial(brush)) = source.brush.as_deref() else {
        return Ok(Some(Cow::Borrowed(source)));
    };
    if !matches!(source.kind, K::DrawRect | K::DrawRoundedRect) {
        return Err(ArchiveError::Unsupported);
    }
    #[cfg(not(feature = "gpu"))]
    {
        let _ = (scene, brush, transform, opacity);
        return Err(ArchiveError::Unsupported);
    }
    #[cfg(feature = "gpu")]
    {
        let rounded = source.kind == K::DrawRoundedRect;
        let parameters = crate::backdrop::BackdropMaterialParameters::from_brush(
            brush,
            source.rect,
            [if rounded { source.radius_x } else { 0.0 }; 4],
            [if rounded { source.radius_y } else { 0.0 }; 4],
        );
        draw(scene, &parameters, transform, opacity)?;
        if source.pen.is_none() {
            return Ok(None);
        }
        let mut remainder = source.clone();
        remainder.brush = None;
        Ok(Some(Cow::Owned(remainder)))
    }
}
#[cfg(feature = "gpu")]
pub(super) fn draw(
    scene: &mut Scene,
    parameters: &crate::backdrop::BackdropMaterialParameters,
    transform: Matrix4,
    opacity: f32,
) -> Result<()> {
    let mut draw = crate::backdrop::BackdropMaterialDraw::new(parameters.clone());
    draw.transform = transform;
    draw.opacity = opacity;
    scene.draw_backdrop_material(draw)?;
    Ok(())
}
