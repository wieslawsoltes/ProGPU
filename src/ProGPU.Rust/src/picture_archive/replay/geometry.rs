use super::*;
use crate::{
    text::paint::{TextPathPolicy, VectorGlyphPaint},
    vector::{PathFigure, PathSegment, PathSegmentKind, StrokeDraw, StrokeFrame},
};

pub(super) fn primitive(
    scene: &mut Scene,
    c: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
    options: &ArchiveReplayOptions,
) -> Result<()> {
    let mut command = c.clone();
    let path = match c.kind {
        K::DrawEllipse | K::DrawCircle => PathGeometry::ellipse(
            c.position2,
            c.radius_x,
            if c.kind == K::DrawCircle {
                c.radius_x
            } else {
                c.radius_y
            },
        ),
        K::DrawRect => PathGeometry::rectangle(c.rect[0], c.rect[1], c.rect[2], c.rect[3]),
        K::DrawRoundedRect => {
            let rx = c.radius_x.abs().min(c.rect[2] / 2.0);
            let ry = c.radius_y.abs().min(c.rect[3] / 2.0);
            if rx <= 0.0 || ry <= 0.0 {
                command.kind = K::DrawRect;
            } else if (rx - ry).abs() > 0.0001 {
                return path(
                    scene,
                    Arc::new(PathGeometry::rounded_rectangle(
                        c.rect[0], c.rect[1], c.rect[2], c.rect[3], rx, ry,
                    )),
                    c,
                    transform,
                    opacity,
                    options,
                );
            }
            command.radius_x = rx;
            command.radius_y = ry;
            PathGeometry::rounded_rectangle(c.rect[0], c.rect[1], c.rect[2], c.rect[3], rx, ry)
        }
        _ => return Err(ArchiveError::Unsupported),
    };
    if c.brush.is_some() {
        scene.draw_archive_vector(ArchiveVectorDraw::new(
            Arc::new(command.clone()),
            Arc::from([]),
            transform,
            opacity,
        )?)?;
    }
    if let Some(pen) = &c.pen {
        let pen = pen.to_pen()?;
        if pen.has_dash_pattern() || pen.options.use_wpf_join_semantics {
            stroke(scene, Arc::new(path), c, transform, opacity)?;
        } else {
            scene.draw_archive_vector(
                ArchiveVectorDraw::new(Arc::new(command), Arc::from([]), transform, opacity)?
                    .with_pen()?,
            )?;
        }
    }
    Ok(())
}
pub(super) fn path(
    scene: &mut Scene,
    path: Arc<PathGeometry>,
    c: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
    options: &ArchiveReplayOptions,
) -> Result<()> {
    if let Some(brush) = &c.brush {
        // This is the original general PathAtlas route, including its independent
        // 64-phase cache domain. Text-derived paths require the actual text-size
        // gamma resolver; they are not silently treated as ordinary geometry.
        if c.use_vector_glyph_rendering && c.font_size > 0.0 {
            return Err(ArchiveError::Unsupported);
        }
        scene.draw_text_path(VectorGlyphPaint {
            path: path.clone(),
            transform,
            brush: Some(Arc::new(brush.to_vector()?)),
            semantic_opacity: opacity,
            policy: TextPathPolicy {
                aliased: c.is_edge_aliased,
                sample_grid: c.path_sample_grid,
                coverage_gamma: if c.path_coverage_gamma < 0.0 {
                    0.0
                } else {
                    c.path_coverage_gamma
                },
                device_phase_grid: 64,
                quantize_scale: false,
                raster_scale: options.text_context.dpi_scale,
                coverage_transfer: Default::default(),
            },
        })?;
    }
    stroke(scene, path, c, transform, opacity)
}
pub(super) fn stroke(
    scene: &mut Scene,
    path: Arc<PathGeometry>,
    c: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
) -> Result<()> {
    let Some(pen) = &c.pen else {
        return Ok(());
    };
    let mut draw = StrokeDraw::new(path, Arc::new(pen.to_pen()?));
    draw.frame = StrokeFrame {
        transform,
        thickness_is_local: c.is_pen_thickness_local,
        recorded_transform: (c.transform != Matrix4([[0.0; 4]; 4])).then_some(c.transform),
    };
    draw.aliased = c.is_edge_aliased;
    draw.semantic_opacity = opacity;
    scene.draw_stroke(draw)?;
    Ok(())
}
pub(super) fn stroke_command(
    scene: &mut Scene,
    picture: &ArchivePicture,
    c: &ArchiveCommand,
    transform: Matrix4,
    opacity: f32,
) -> Result<()> {
    if c.pen.is_none() {
        return Ok(());
    }
    let mut figure = PathFigure::new(c.position, false);
    figure.is_filled = false;
    match c.kind {
        K::DrawLine => figure
            .segments
            .push(PathSegment::new(PathSegmentKind::Line(c.position2))),
        K::DrawBezier => figure
            .segments
            .push(PathSegment::new(PathSegmentKind::Quadratic([
                c.position2,
                c.position3,
            ]))),
        K::DrawCubicBezier => figure
            .segments
            .push(PathSegment::new(PathSegmentKind::Cubic([
                c.position2,
                c.position3,
                c.position4,
            ]))),
        K::DrawPolyline => {
            let points = match &c.polyline_points {
                Some(points) if !points.is_empty() => points.as_ref(),
                _ if c.point_buffer_count > 0 => {
                    picture.points(c.point_buffer_offset, c.point_buffer_count)?
                }
                _ => &[],
            };
            if points.len() < 2 {
                return Ok(());
            }
            figure.start = points[0];
            figure.is_closed = c.is_closed;
            figure
                .segments
                .try_reserve(points.len() - 1)
                .map_err(|_| ArchiveError::Capacity)?;
            figure.segments.extend(
                points[1..]
                    .iter()
                    .map(|p| PathSegment::new(PathSegmentKind::Line(*p))),
            );
        }
        _ => return Err(ArchiveError::Unsupported),
    }
    stroke(
        scene,
        Arc::new(PathGeometry {
            figures: vec![figure],
            ..Default::default()
        }),
        c,
        transform,
        opacity,
    )
}
