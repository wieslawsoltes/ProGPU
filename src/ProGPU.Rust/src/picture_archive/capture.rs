//! Explicit capture of still-owned original source commands. Compiled resources
//! without the original archive representation fail before owner publication.
use super::*;
use crate::{
    canvas::{CanvasClipOperation, CanvasMatrix, CanvasPicture, RecordedPicture},
    geometry::Shape,
    mask::Mask,
    paint::{Paint, PaintStyle},
    scene::{LayerBlend, Scene},
    vector::{BooleanOperation, Brush, BrushKind, Matrix4, PathEffectKind, PathGeometry},
};

impl CanvasPicture {
    pub fn to_archive(&self, cull_bounds: [f32; 4]) -> Result<PictureArchive> {
        Ok(PictureArchive {
            version: CURRENT_VERSION,
            cull_bounds,
            picture: capture_canvas(self, 0)?,
        })
    }
}
impl Scene {
    pub fn to_archive(&self, cull_bounds: [f32; 4]) -> Result<PictureArchive> {
        Ok(PictureArchive {
            version: CURRENT_VERSION,
            cull_bounds,
            picture: capture_scene(self, 0)?,
        })
    }
}
impl RecordedPicture {
    pub fn to_archive(&self) -> Result<PictureArchive> {
        self.picture().to_archive(self.cull_bounds())
    }
    pub fn serialize(&self) -> Result<Vec<u8>> {
        self.to_archive()?.serialize()
    }
    pub fn deserialize(bytes: &[u8]) -> Result<Self> {
        let archive = PictureArchive::deserialize(bytes)?;
        let picture = archive.to_canvas_picture()?;
        Ok(Self::from_archive(picture, archive.cull_bounds))
    }
}
struct Builder {
    commands: Vec<ArchiveCommand>,
    scopes: Vec<ArchiveCommandKind>,
    blends: usize,
}
impl Builder {
    fn new() -> Self {
        Self {
            commands: Vec::new(),
            scopes: Vec::new(),
            blends: 0,
        }
    }
    fn push(&mut self, command: ArchiveCommand) -> Result<()> {
        if self.commands.len() >= MAX_COMMANDS {
            return Err(ArchiveError::Capacity);
        }
        self.commands
            .try_reserve(1)
            .map_err(|_| ArchiveError::Capacity)?;
        self.commands.push(command);
        Ok(())
    }
    fn clip(&mut self, command: ArchiveCommand, pop: ArchiveCommandKind) -> Result<()> {
        self.scopes
            .try_reserve(1)
            .map_err(|_| ArchiveError::Capacity)?;
        self.push(command)?;
        self.scopes.push(pop);
        Ok(())
    }
    fn pop(&mut self) -> Result<()> {
        let kind = self.scopes.pop().ok_or(ArchiveError::InvalidData)?;
        self.push(ArchiveCommand {
            kind,
            ..Default::default()
        })
    }
    fn scoped(&mut self, command: ArchiveCommand, opacity: f32, blend: LayerBlend) -> Result<()> {
        if blend != LayerBlend::SrcOver {
            self.push(blend_push(blend))?;
        }
        if opacity != 1.0 {
            self.push(ArchiveCommand {
                kind: ArchiveCommandKind::PushOpacity,
                font_size: opacity,
                ..Default::default()
            })?;
        }
        self.push(command)?;
        if opacity != 1.0 {
            self.push(ArchiveCommand {
                kind: ArchiveCommandKind::PopOpacity,
                ..Default::default()
            })?;
        }
        if blend != LayerBlend::SrcOver {
            self.push(ArchiveCommand {
                kind: ArchiveCommandKind::PopBlendMode,
                ..Default::default()
            })?;
        }
        Ok(())
    }
    fn picture(
        &mut self,
        picture: Arc<ArchivePicture>,
        transform: Matrix4,
        opacity: f32,
        blend: LayerBlend,
    ) -> Result<()> {
        self.scoped(
            ArchiveCommand {
                kind: ArchiveCommandKind::DrawPicture,
                picture: Some(picture),
                transform,
                ..Default::default()
            },
            opacity,
            blend,
        )
    }
    fn finish(self) -> Result<Arc<ArchivePicture>> {
        if !self.scopes.is_empty() || self.blends != 0 {
            return Err(ArchiveError::InvalidData);
        }
        Ok(Arc::new(ArchivePicture {
            commands: self.commands.into(),
            ..Default::default()
        }))
    }
}
fn capture_canvas(source: &CanvasPicture, level: u32) -> Result<Arc<ArchivePicture>> {
    depth(level)?;
    use crate::canvas::Command as C;
    let commands = source.archive_commands();
    // Raw archive-only pictures keep their exact complete side-buffer owners and
    // accepted source tree, even if a later rendering target cannot execute it.
    if let [
        C::Archive {
            picture,
            matrix,
            opacity,
            options,
        },
    ] = commands
    {
        archive_context(options)?;
        if matrix.0.map(f32::to_bits) == CanvasMatrix::IDENTITY.0.map(f32::to_bits)
            && *opacity == 1.0
            && matrix_same(options.transform, Matrix4::IDENTITY)
            && options.opacity == 1.0
        {
            return Ok(picture.clone());
        }
    }
    let mut out = Builder::new();
    for command in commands {
        match command {
            C::Archive {
                picture,
                matrix,
                opacity,
                options,
            } => {
                archive_context(options)?;
                out.picture(
                    picture.clone(),
                    options.transform * matrix.matrix4(),
                    options.opacity * *opacity,
                    LayerBlend::SrcOver,
                )?;
            }
            C::Picture {
                picture,
                matrix,
                opacity,
                blend,
            } => out.picture(
                capture_canvas(picture, level + 1)?,
                matrix.matrix4(),
                *opacity,
                *blend,
            )?,
            C::Leaf(scene) => out.picture(
                capture_scene(scene, level + 1)?,
                Matrix4::IDENTITY,
                1.0,
                LayerBlend::SrcOver,
            )?,
            C::DeviceColor { color, blend } => out.scoped(
                ArchiveCommand {
                    kind: ArchiveCommandKind::DrawRect,
                    rect: [0.0, 0.0, source.size()[0], source.size()[1]],
                    brush: Some(brush(Brush::new(BrushKind::Solid(*color)))),
                    transform: Matrix4::IDENTITY,
                    ..Default::default()
                },
                1.0,
                *blend,
            )?,
            C::Clip(clip) => {
                let (path, transform, rect) = if clip.operation == CanvasClipOperation::Difference {
                    let excluded = Arc::new(clip.geometry.transformed(clip.matrix.matrix4())?);
                    let whole = Arc::new(PathGeometry::rectangle(
                        0.0,
                        0.0,
                        source.size()[0],
                        source.size()[1],
                    ));
                    (
                        Arc::new(PathGeometry::combine_deferred(
                            whole,
                            excluded,
                            BooleanOperation::Difference,
                        )),
                        Matrix4::IDENTITY,
                        None,
                    )
                } else {
                    (clip.geometry.clone(), clip.matrix.matrix4(), clip.rect)
                };
                if let Some([l, t, r, b]) = rect {
                    out.clip(
                        ArchiveCommand {
                            kind: ArchiveCommandKind::PushClip,
                            rect: [l, t, r - l, b - t],
                            transform,
                            ..Default::default()
                        },
                        ArchiveCommandKind::PopClip,
                    )?;
                } else {
                    out.clip(
                        ArchiveCommand {
                            kind: ArchiveCommandKind::PushGeometryClip,
                            path: Some(Arc::new(ArchivePath::from_geometry(&path)?)),
                            transform,
                            ..Default::default()
                        },
                        ArchiveCommandKind::PopGeometryClip,
                    )?;
                }
            }
            C::Pop => out.pop()?,
            C::Path {
                geometry,
                matrix,
                paint,
                inverse,
                text,
            } => {
                if text.is_some() {
                    return Err(ArchiveError::Unsupported);
                }
                capture_path(&mut out, geometry, *matrix, paint, *inverse, source.size())?;
            }
            C::Analytic {
                shape,
                bounds,
                center,
                radius,
                matrix,
                paint,
                ..
            } => {
                let (material, pen, blend) = paint_parts(paint)?;
                let kind = match shape {
                    Shape::Rectangle => ArchiveCommandKind::DrawRect,
                    Shape::Ellipse => ArchiveCommandKind::DrawEllipse,
                    Shape::RoundedRectangle => ArchiveCommandKind::DrawRoundedRect,
                };
                let command = ArchiveCommand {
                    kind,
                    rect: *bounds,
                    position2: *center,
                    radius_x: if *shape == Shape::Ellipse {
                        bounds[2] * 0.5
                    } else {
                        *radius
                    },
                    radius_y: if *shape == Shape::Ellipse {
                        bounds[3] * 0.5
                    } else {
                        *radius
                    },
                    corner_radius: *radius,
                    brush: if paint.style == PaintStyle::Stroke {
                        None
                    } else {
                        Some(material)
                    },
                    is_pen_thickness_local: pen.is_some(),
                    pen,
                    transform: matrix.matrix4(),
                    is_edge_aliased: !paint.antialias,
                    ..Default::default()
                };
                out.scoped(command, 1.0, blend)?;
            }
            C::SampledImage(_) | C::Image { .. } | C::ImageBatch { .. } => {
                return Err(ArchiveError::Unsupported);
            }
            // Original archive has no GPU texture/filter/layer payload wire.
            // Rasterizing or expanding these resources would fabricate source.
            _ => return Err(ArchiveError::Unsupported),
        }
    }
    out.finish()
}
fn capture_path(
    out: &mut Builder,
    geometry: &Arc<PathGeometry>,
    matrix: CanvasMatrix,
    paint: &Paint,
    inverse: bool,
    size: [f32; 2],
) -> Result<()> {
    let (material, pen, blend) = paint_parts(paint)?;
    if inverse && paint.style != PaintStyle::Stroke {
        let excluded = Arc::new(geometry.transformed(matrix.matrix4())?);
        let whole = Arc::new(PathGeometry::rectangle(0.0, 0.0, size[0], size[1]));
        let fill = PathGeometry::combine_deferred(whole, excluded, BooleanOperation::Difference);
        out.scoped(
            ArchiveCommand {
                kind: ArchiveCommandKind::DrawPath,
                path: Some(Arc::new(ArchivePath::from_geometry(&fill)?)),
                brush: Some(material.clone()),
                transform: Matrix4::IDENTITY,
                is_edge_aliased: !paint.antialias,
                ..Default::default()
            },
            1.0,
            blend,
        )?;
        if pen.is_none() {
            return Ok(());
        }
    }
    out.scoped(
        ArchiveCommand {
            kind: ArchiveCommandKind::DrawPath,
            path: Some(Arc::new(ArchivePath::from_geometry(geometry)?)),
            brush: if inverse || paint.style == PaintStyle::Stroke {
                None
            } else {
                Some(material)
            },
            is_pen_thickness_local: pen.is_some(),
            pen,
            transform: matrix.matrix4(),
            is_edge_aliased: !paint.antialias,
            ..Default::default()
        },
        1.0,
        blend,
    )
}
fn paint_parts(paint: &Paint) -> Result<(Arc<ArchiveBrush>, Option<Arc<ArchivePen>>, LayerBlend)> {
    if paint.color_filter.is_some()
        || paint.image_filter.is_some()
        || paint.mask_filter.is_some()
        || paint
            .path_effect
            .as_ref()
            .is_some_and(|p| !matches!(p.kind(), PathEffectKind::Dash { .. }))
    {
        return Err(ArchiveError::Unsupported);
    }
    if paint
        .blender()
        .is_some_and(|v| !matches!(v, crate::image_filter::FilterBlender::Mode(_)))
    {
        return Err(ArchiveError::Unsupported);
    }
    let material = paint.fill_brush()?;
    let pen = paint
        .local_pen(material.clone())?
        .map(|p| Arc::new(ArchivePen::from_pen(&p)));
    Ok((brush((*material).clone()), pen, paint.canvas_blend()?))
}
fn capture_scene(source: &Scene, level: u32) -> Result<Arc<ArchivePicture>> {
    depth(level)?;
    use crate::scene::Command as C;
    let mut out = Builder::new();
    for command in source.archive_commands()? {
        match command {
            C::Series(draw) => out.picture(
                super::series::capture_series(draw)?,
                Matrix4::IDENTITY,
                1.0,
                LayerBlend::SrcOver,
            )?,
            C::Empty => {}
            C::PushBlend(blend) => {
                out.push(blend_push(*blend))?;
                out.blends = out.blends.checked_add(1).ok_or(ArchiveError::Capacity)?;
            }
            C::PopBlend => {
                out.blends = out.blends.checked_sub(1).ok_or(ArchiveError::InvalidData)?;
                out.push(ArchiveCommand {
                    kind: ArchiveCommandKind::PopBlendMode,
                    ..Default::default()
                })?;
            }
            C::PushClip(rect) => out.clip(
                ArchiveCommand {
                    kind: ArchiveCommandKind::PushClip,
                    rect: *rect,
                    transform: Matrix4::IDENTITY,
                    ..Default::default()
                },
                ArchiveCommandKind::PopClip,
            )?,
            C::PushMask(mask) => match mask.as_ref() {
                Mask::Geometry(g) => out.clip(
                    ArchiveCommand {
                        kind: ArchiveCommandKind::PushGeometryClip,
                        path: Some(Arc::new(ArchivePath::from_geometry(&g.geometry)?)),
                        transform: Matrix4::from_affine(g.transform),
                        ..Default::default()
                    },
                    ArchiveCommandKind::PopGeometryClip,
                )?,
                _ => return Err(ArchiveError::Unsupported),
            },
            C::PopScope => out.pop()?,
            C::VertexMesh(draw) => out.scoped(
                ArchiveCommand {
                    kind: ArchiveCommandKind::DrawVertexMesh,
                    brush: Some(brush((*draw.brush).clone())),
                    vertex_mesh: Some(Arc::new(draw.mesh.clone())),
                    transform: draw.transform,
                    vertex_color_blend_mode: draw.color_blend,
                    is_edge_aliased: draw.aliased,
                    ..Default::default()
                },
                draw.semantic_opacity,
                LayerBlend::SrcOver,
            )?,
            C::Stroke(draw) => {
                if draw.frame.recorded_transform.is_some() {
                    return Err(ArchiveError::Unsupported);
                }
                out.scoped(
                    ArchiveCommand {
                        kind: ArchiveCommandKind::DrawPath,
                        path: Some(Arc::new(ArchivePath::from_geometry(&draw.path)?)),
                        pen: Some(Arc::new(ArchivePen::from_pen(&draw.pen))),
                        is_pen_thickness_local: draw.frame.thickness_is_local,
                        transform: draw.frame.transform,
                        is_edge_aliased: draw.aliased,
                        ..Default::default()
                    },
                    draw.semantic_opacity,
                    LayerBlend::SrcOver,
                )?;
            }
            C::Text(record) => {
                let (draw, context) = record.as_ref();
                let crate::text::paint::TextSource::Positioned(run) = &draw.source else {
                    return Err(ArchiveError::Unsupported);
                };
                // Source v1-v10 has complete arrays but no selected-range wire.
                if run.range_start != 0 || run.range_count != 0 {
                    return Err(ArchiveError::Unsupported);
                }
                let mut admission = ArchiveReplayOptions::default();
                admission.text_context = *context;
                admission.text_context.transform = Matrix4::IDENTITY;
                admission.text_context.semantic_opacity = 1.0;
                archive_context(&admission)?;
                out.scoped(
                    ArchiveCommand {
                        kind: ArchiveCommandKind::DrawGlyphRun,
                        font: Some(run.font.clone()),
                        glyph_indices: Some(run.indices.clone()),
                        glyph_positions: Some(run.positions.clone()),
                        font_size: draw.font_size,
                        position: draw.position,
                        rotation: draw.rotation,
                        brush: draw.brush.as_ref().map(|b| brush((**b).clone())),
                        is_bold: draw.bold,
                        is_italic: draw.italic,
                        font_transform: draw.font_transform.unwrap_or([0.0; 2]),
                        has_font_transform: draw.font_transform.is_some(),
                        text_rendering_mode: draw.rendering_mode,
                        text_hinting_mode: draw.hinting_mode,
                        glyph_coverage_transfer: draw.coverage_transfer,
                        prefer_glyph_atlas: draw.prefer_atlas,
                        use_vector_glyph_rendering: draw.vector_rendering,
                        use_logical_glyph_atlas_resolution: draw.logical_atlas_resolution,
                        transform: context.transform,
                        ..Default::default()
                    },
                    context.semantic_opacity,
                    LayerBlend::SrcOver,
                )?;
            }
            C::ArchiveHatch(draw) => {
                let mut command = (**draw.command()).clone();
                if command.point_buffer_count != 0
                    || command.double_buffer_count != 0
                    || command.line3d_buffer_count != 0
                    || command.weight_buffer_count != 0
                    || command.float_buffer_count != 0
                {
                    return Err(ArchiveError::Unsupported);
                }
                command.transform = draw.transform();
                out.scoped(command, draw.opacity(), LayerBlend::SrcOver)?;
            }
            C::ArchiveVector(draw) => {
                if draw.uses_pen() || draw.command().pen.is_some() {
                    return Err(ArchiveError::Unsupported);
                }
                let mut command = (**draw.command()).clone();
                // Only the actual selected side-point owner survives this Scene
                // descriptor. Unrelated opaque buffer references cannot be rebuilt.
                if command.double_buffer_count != 0
                    || command.line3d_buffer_count != 0
                    || command.weight_buffer_count != 0
                    || command.float_buffer_count != 0
                {
                    return Err(ArchiveError::Unsupported);
                }
                command.transform = draw.transform();
                if command.brush.is_some() {
                    command.brush = Some(brush((*draw.brush()?).clone()));
                }
                let picture = Arc::new(ArchivePicture {
                    points: draw.points().clone(),
                    commands: vec![command].into(),
                    ..Default::default()
                });
                out.picture(
                    picture,
                    Matrix4::IDENTITY,
                    draw.opacity(),
                    LayerBlend::SrcOver,
                )?;
            }
            // Paths after GPU raster lowering, prepared text, images, layers,
            // source shaders and owner-only text markers lack this source wire.
            _ => return Err(ArchiveError::Unsupported),
        }
    }
    out.finish()
}
fn brush(value: Brush) -> Arc<ArchiveBrush> {
    Arc::new(ArchiveBrush::Vector(value))
}
fn blend_push(value: LayerBlend) -> ArchiveCommand {
    ArchiveCommand {
        kind: ArchiveCommandKind::PushBlendMode,
        int_param: value as i32,
        ..Default::default()
    }
}

// Only original source geometry/opacity have archive fields. The receiving text
// environment is not serialized by v1-v10 and must never disappear silently.
fn archive_context(options: &ArchiveReplayOptions) -> Result<()> {
    let c = options.text_context;
    if options.text.is_some()
        || !matrix_same(c.transform, Matrix4::IDENTITY)
        || c.dpi_scale.to_bits() != 1.0f32.to_bits()
        || c.static_zoom.to_bits() != 1.0f32.to_bits()
        || c.semantic_opacity.to_bits() != 1.0f32.to_bits()
        || c.use_mvp
        || c.suppress_cached_cleartype
        || c.gpu_transforms_active
        || !matrix_same(c.camera_view, Matrix4::IDENTITY)
        || c.clip.is_some()
    {
        return Err(ArchiveError::Unsupported);
    }
    Ok(())
}
fn matrix_same(a: Matrix4, b: Matrix4) -> bool {
    a.0.map(|v| v.map(f32::to_bits)) == b.0.map(|v| v.map(f32::to_bits))
}
