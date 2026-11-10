//! Pure lowering and explicit GPU source preparation. A published scene owns all
//! actual shader/filter results; encoding never calls a mutable source producer.
use super::*;
use crate::{
    geometry::AnalyticPrimitive,
    mask::{ClipPath, Mask},
    scene::{MaterialPaint, PathDraw},
    vector::{Brush, EffectPaintStyle, StrokeDraw},
};
#[cfg(feature = "gpu")]
mod gpu;
impl CanvasPicture {
    /// Lower ordinary source commands without a GPU. Source captures, filtered
    /// layers and special shader producers require `prepare_scene` instead.
    pub fn to_scene(&self) -> Result<Scene, Error> {
        self.lower(0)
    }
    fn lower(&self, depth: u16) -> Result<Scene, Error> {
        if depth >= 256 {
            return Err(Error::Capacity);
        }
        let mut scene = Scene::default();
        for command in self.commands.iter() {
            match command {
                Command::Picture {
                    picture,
                    matrix,
                    opacity,
                    blend,
                } => {
                    let child = picture.lower(depth + 1)?;
                    #[cfg(feature = "gpu")]
                    let child =
                        child.place_visual_page(matrix.matrix4(), 1.0, false, *opacity, true)?;
                    #[cfg(not(feature = "gpu"))]
                    if *matrix != CanvasMatrix::IDENTITY || *opacity != 1.0 {
                        return Err(Error::UnsupportedExecution);
                    }
                    scope(&mut scene, *blend, |scene| scene.append(&child))?;
                }
                _ => ordinary(&mut scene, command, self.size)?,
            }
        }
        Ok(scene)
    }
}
fn scope(
    scene: &mut Scene,
    blend: LayerBlend,
    operation: impl FnOnce(&mut Scene) -> Result<(), Error>,
) -> Result<(), Error> {
    if blend == LayerBlend::SrcOver {
        return operation(scene);
    }
    scene.push_blend(blend)?;
    operation(scene)?;
    scene.pop_blend()
}
fn ordinary(scene: &mut Scene, command: &Command, size: [f32; 2]) -> Result<(), Error> {
    match command {
        Command::Clip(clip) => push_clip(scene, clip, size),
        Command::Pop => scene.pop_scope(),
        Command::DeviceColor { color, blend } => scope(scene, *blend, |scene| {
            scene.draw_analytic(AnalyticPrimitive::rectangle(
                [0.0, 0.0, size[0], size[1]],
                *color,
            ))
        }),
        Command::Path {
            geometry,
            matrix,
            paint,
            inverse,
        } => {
            if paint.mask_filter.is_some() {
                return Err(Error::UnsupportedExecution);
            }
            let (geometry, paint, inverse) = effect(geometry, paint, *matrix, *inverse)?;
            scope(scene, paint.canvas_blend()?, |scene| {
                path(scene, geometry, &paint, *matrix, inverse, size)
            })
        }
        Command::Analytic {
            geometry,
            shape,
            bounds,
            radius,
            matrix,
            paint,
        } => {
            if paint.mask_filter.is_some() {
                return Err(Error::UnsupportedExecution);
            }
            scope(scene, paint.canvas_blend()?, |scene| {
                let brush = paint.fill_brush()?;
                if paint.style != PaintStyle::Stroke {
                    let primitive = AnalyticPrimitive {
                        shape: *shape,
                        bounds: *bounds,
                        color: [1.0; 4],
                        transform: matrix.affine()?,
                        corner_radius: *radius,
                        stroke_thickness: 0.0,
                        aliased: !paint.antialias,
                    };
                    scene.draw_material_analytic(primitive, MaterialPaint::new(brush.clone()))?;
                }
                stroke(scene, geometry.clone(), paint, *matrix, brush)
            })
        }
        Command::Image { draw, paint } => {
            if paint.as_ref().is_some_and(|p| p.color_filter.is_some()) {
                return Err(Error::UnsupportedExecution);
            }
            image(scene, draw.clone(), paint.as_deref())
        }
        Command::Leaf(leaf) => scene.append(leaf),
        Command::Layer(_) | Command::Picture { .. } => Err(Error::UnsupportedExecution),
        #[cfg(feature = "gpu")]
        Command::Texture { .. } => Err(Error::UnsupportedExecution),
    }
}
fn effect(
    geometry: &Arc<PathGeometry>,
    paint: &Paint,
    matrix: CanvasMatrix,
    inverse: bool,
) -> Result<(Arc<PathGeometry>, Paint, bool), Error> {
    let mut paint = paint.clone();
    if let Some(effect) = &paint.path_effect
        && !matches!(effect.kind(), PathEffectKind::Dash { .. })
    {
        let source = PathEffectSource::from_geometry(geometry, inverse)?;
        let output = effect.apply(
            &source,
            matrix.matrix4().stroke_scales().map_or(1.0, |v| v[0]),
        )?;
        if output.applied {
            paint.path_effect = None;
            if let Some(style) = output.paint.style {
                paint.style = match style {
                    EffectPaintStyle::Fill => PaintStyle::Fill,
                    EffectPaintStyle::Stroke => PaintStyle::Stroke,
                };
            }
            if let Some(width) = output.paint.stroke_width {
                paint.set_stroke_width(width);
            }
            return Ok((
                Arc::new(output.output.geometry()?),
                paint,
                output.output.is_inverse(),
            ));
        }
    }
    Ok((geometry.clone(), paint, inverse))
}
fn path(
    scene: &mut Scene,
    geometry: Arc<PathGeometry>,
    paint: &Paint,
    matrix: CanvasMatrix,
    inverse: bool,
    size: [f32; 2],
) -> Result<(), Error> {
    let brush = paint.fill_brush()?;
    if paint.style != PaintStyle::Stroke {
        if inverse {
            let excluded = Arc::new(geometry.transformed(matrix.matrix4())?);
            let whole = Arc::new(PathGeometry::rectangle(0.0, 0.0, size[0], size[1]));
            let difference = PathGeometry::combine_deferred(
                whole,
                excluded,
                crate::vector::BooleanOperation::Difference,
            );
            if let Some(path) = difference.compile_raster_path()? {
                scene.draw_material_path(
                    PathDraw {
                        path,
                        transform: Affine::IDENTITY,
                        color: [1.0; 4],
                        sampling: PathSampling::new(if paint.antialias { 8 } else { 1 })?,
                    },
                    MaterialPaint::new(brush.clone()),
                )?;
            }
        } else if let Some(path) = geometry.compile_raster_path()? {
            scene.draw_material_path(
                PathDraw {
                    path,
                    transform: matrix.affine()?,
                    color: [1.0; 4],
                    sampling: PathSampling::new(if paint.antialias { 8 } else { 1 })?,
                },
                MaterialPaint::new(brush.clone()),
            )?;
        }
    }
    stroke(scene, geometry, paint, matrix, brush)
}
fn stroke(
    scene: &mut Scene,
    geometry: Arc<PathGeometry>,
    paint: &Paint,
    matrix: CanvasMatrix,
    brush: Arc<Brush>,
) -> Result<(), Error> {
    if let Some(pen) = paint.local_pen(brush)? {
        let mut draw = StrokeDraw::new(geometry, pen);
        draw.frame.transform = matrix.matrix4();
        draw.aliased = !paint.antialias;
        scene.draw_stroke(draw)?;
    }
    Ok(())
}
fn push_clip(scene: &mut Scene, clip: &Clip, size: [f32; 2]) -> Result<(), Error> {
    if clip.operation == CanvasClipOperation::Difference {
        let whole = Arc::new(PathGeometry::rectangle(0.0, 0.0, size[0], size[1]));
        let excluded = Arc::new(clip.geometry.transformed(clip.matrix.matrix4())?);
        let difference = PathGeometry::combine_deferred(
            whole,
            excluded,
            crate::vector::BooleanOperation::Difference,
        );
        return push_clip(
            scene,
            &Clip {
                geometry: Arc::new(difference),
                matrix: CanvasMatrix::IDENTITY,
                operation: CanvasClipOperation::Intersect,
                rect: None,
            },
            size,
        );
    }
    let transform = clip.matrix.affine()?;
    if let Some(rect) = clip.rect {
        if clip.matrix.0[1] == 0.0 && clip.matrix.0[3] == 0.0 {
            let b = clip.matrix.map_rect(rect);
            return scene.push_clip_rect(drawing::xywh(b));
        }
    }
    let Some(path) = clip.geometry.compile_raster_path()? else {
        return scene.push_clip_rect(if clip.operation == CanvasClipOperation::Intersect {
            [0.0; 4]
        } else {
            [0.0, 0.0, size[0], size[1]]
        });
    };
    let mut path = ClipPath::new(path);
    path.transform = transform;
    path.sample_grid = if clip.rect.is_some() { 1 } else { 8 };
    match clip.operation {
        CanvasClipOperation::Intersect => scene.push_mask(Mask::Path(path)),
        CanvasClipOperation::Difference => scene.push_mask_difference(Mask::Path(path)),
    }
}
fn image(scene: &mut Scene, mut draw: ImageDraw, paint: Option<&Paint>) -> Result<(), Error> {
    let antialias =
        draw.patches.is_none() && draw.quad.is_none() && paint.is_none_or(|p| p.antialias);
    let blend = paint.map_or(Ok(LayerBlend::SrcOver), Paint::canvas_blend)?;
    if let Some(paint) = paint {
        draw.opacity *= f32::from(paint.color[3]) / 255.0;
    }
    // Source alpha and paint RGB remain independent. Only far edges extend.
    if antialias {
        let b = draw.destination;
        static UNIT: std::sync::OnceLock<Arc<PathGeometry>> = std::sync::OnceLock::new();
        let geometry = UNIT
            .get_or_init(|| Arc::new(PathGeometry::rectangle(0.0, 0.0, 1.0, 1.0)))
            .clone();
        let scale = Matrix4([
            [b[2], 0.0, 0.0, 0.0],
            [0.0, b[3], 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ]);
        let translation = Matrix4([
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [b[0], b[1], 0.0, 1.0],
        ]);
        let placed = (scale * translation * Matrix4::from_affine(draw.transform)).0;
        let matrix = crate::vector::EffectMatrix([
            placed[0][0],
            placed[1][0],
            placed[3][0],
            placed[0][1],
            placed[1][1],
            placed[3][1],
            0.0,
            0.0,
            1.0,
        ]);
        push_clip(
            scene,
            &Clip {
                geometry,
                matrix,
                operation: CanvasClipOperation::Intersect,
                rect: None,
            },
            [f32::MAX; 2],
        )?;
        draw.destination[2] += 0.5;
        draw.destination[3] += 0.5;
        if b[2] != 0.0 {
            draw.source[2] += 0.5 * draw.source[2] / b[2];
        }
        if b[3] != 0.0 {
            draw.source[3] += 0.5 * draw.source[3] / b[3];
        }
        draw.extended_source = true;
    }
    #[cfg(feature = "gpu")]
    scene.draw_visual_image(draw, blend)?;
    #[cfg(not(feature = "gpu"))]
    scope(scene, blend, |scene| scene.draw_image(draw))?;
    if antialias {
        scene.pop_scope()?;
    }
    Ok(())
}
