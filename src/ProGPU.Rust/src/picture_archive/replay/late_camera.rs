//! Normal retained-picture +100 camera route, distinct from static DXF +200.
use super::*;
use crate::vector::BrushKind;

pub(super) fn record(
    target: &mut Scene,
    picture: &ArchivePicture,
    source: &ArchiveCommand,
    transform: Matrix4,
    camera: Matrix4,
    opacity: f32,
    options: &ArchiveReplayOptions,
) -> Result<()> {
    if !matches!(
        source.kind,
        K::DrawText
            | K::DrawGlyphRun
            | K::DrawRect
            | K::DrawRoundedRect
            | K::DrawEllipse
            | K::DrawCircle
            | K::FillTriangle
            | K::FillQuad
            | K::DrawPointBatch
            | K::DrawDotGrid
            | K::DrawDeviceDotGrid
            | K::DrawLine
            | K::DrawBezier
            | K::DrawCubicBezier
            | K::DrawPolyline
            | K::DrawPath
    ) {
        return Err(ArchiveError::Unsupported);
    }
    // Source replay rebases non-local brush coordinates before compilation;
    // path fills remain local while their pen brush uses the model inverse.
    let command = transformed_brushes(source, transform)?;
    let c = &command;
    let mut scene = Scene::default();
    match c.kind {
        K::DrawText | K::DrawGlyphRun => {
            text::draw(&mut scene, c, transform, opacity, options)?;
        }
        K::DrawRect | K::DrawRoundedRect | K::DrawEllipse | K::DrawCircle => {
            // Texture/backdrop interception has its own pipeline/frame policy.
            if c.brush
                .as_deref()
                .is_some_and(|b| !matches!(b, super::super::ArchiveBrush::Vector(_)))
            {
                return Err(ArchiveError::Unsupported);
            }
            geometry::primitive(&mut scene, c, transform, opacity, options)?;
        }
        K::FillTriangle
        | K::FillQuad
        | K::DrawPointBatch
        | K::DrawDotGrid
        | K::DrawDeviceDotGrid => {
            if c.brush.is_some() {
                scene.draw_archive_vector(ArchiveVectorDraw::new(
                    Arc::new(c.clone()),
                    picture.points.clone(),
                    transform,
                    opacity,
                )?)?;
            }
        }
        K::DrawPath => {
            if let Some(path) = &c.path {
                geometry::path(
                    &mut scene,
                    Arc::new(path.to_geometry()?),
                    c,
                    transform,
                    opacity,
                    options,
                )?;
            }
        }
        K::DrawLine | K::DrawBezier | K::DrawCubicBezier | K::DrawPolyline => {
            geometry::stroke_command(&mut scene, picture, c, transform, opacity)?;
        }
        _ => return Err(ArchiveError::Unsupported),
    }
    target.append_archive_camera(scene, camera)?;
    Ok(())
}
fn transformed_brushes(source: &ArchiveCommand, transform: Matrix4) -> Result<ArchiveCommand> {
    let mut command = source.clone();
    let local = matches!(
        source.kind,
        K::DrawRect
            | K::DrawRoundedRect
            | K::DrawEllipse
            | K::DrawCircle
            | K::DrawPath
            | K::DrawPointBatch
            | K::DrawDotGrid
            | K::DrawDeviceDotGrid
    );
    if local && source.kind != K::DrawPath {
        return Ok(command);
    }
    let Some(inverse) = crate::image_filter::FilterTransform(transform).inverse_raw() else {
        return Ok(command);
    };
    if inverse == Matrix4::IDENTITY {
        return Ok(command);
    }
    if !local && let Some(brush) = &source.brush {
        command.brush = Some(transform_brush(brush, inverse)?);
    }
    if let Some(pen) = &source.pen {
        let brush = transform_brush(&pen.brush, inverse)?;
        if !Arc::ptr_eq(&brush, &pen.brush) {
            let mut value = pen.as_ref().clone();
            value.brush = brush;
            command.pen = Some(Arc::new(value));
        }
    }
    Ok(command)
}
fn transform_brush(
    brush: &Arc<super::super::ArchiveBrush>,
    inverse: Matrix4,
) -> Result<Arc<super::super::ArchiveBrush>> {
    let super::super::ArchiveBrush::Vector(value) = brush.as_ref() else {
        return Ok(brush.clone());
    };
    let mut value = value.clone();
    let coordinate = match &mut value.kind {
        BrushKind::Linear(v) => &mut v.options.transform,
        BrushKind::Radial(v) => &mut v.options.transform,
        BrushKind::Path(v) => &mut v.options.transform,
        BrushKind::TwoPointConical(v) => &mut v.options.transform,
        BrushKind::Sweep(v) => &mut v.options.transform,
        BrushKind::PerlinNoise(v) => &mut v.transform,
        BrushKind::Hatch(v) | BrushKind::CrossHatch(v) => &mut v.transform,
        BrushKind::HatchSet(v) => &mut v.transform,
        BrushKind::Solid(_) | BrushKind::Tile(_) => return Ok(brush.clone()),
    };
    *coordinate = inverse * *coordinate;
    Ok(Arc::new(super::super::ArchiveBrush::Vector(value)))
}
