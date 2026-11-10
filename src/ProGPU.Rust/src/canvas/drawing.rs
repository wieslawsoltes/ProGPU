//! Typed source drawing keeps actual geometry, pixels and prepared text owners.
use super::*;
use crate::vector::{PathFigure, PathSegment, PathSegmentKind};
/// Original point-array interpretation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CanvasPointMode {
    /// Independent zero-length stroked figures.
    Points,
    /// Independent pairs; an unmatched last point is ignored.
    Lines,
    /// One connected open figure.
    Polygon,
}
impl Canvas {
    /// Retain a complete source image inventory, including original nine-patch,
    /// lattice, cubic and anisotropic metadata. Patch batches keep their original
    /// independent edge policy; no ordinary-image half-pixel extension is added.
    pub fn draw_image_inventory(
        &mut self,
        mut draw: ImageDraw,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.matrix.affine()?;
        let m = (Matrix4::from_affine(draw.transform) * self.matrix.matrix4()).0;
        draw.transform = Affine([m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]);
        draw.validate()?;
        self.push(Command::Image {
            draw,
            paint: paint.map(Paint::snapshot),
        })
    }
    /// Retain a style-preserving native paragraph generation and its real source
    /// paint inventory without reconstructing shaping records or glyph placement.
    pub fn draw_native_paragraph(
        &mut self,
        geometry: Arc<crate::text::font_policy::FontReplayGeometry>,
        paints: &[crate::text::native_paragraph::NativeParagraphPaint],
        position: [f32; 2],
        mut context: crate::text::paint::TextPaintContext,
    ) -> Result<(), Error> {
        context.transform = context.transform * self.matrix.matrix4();
        context.semantic_opacity *= self.opacity;
        let mut scene = Scene::default();
        scene.draw_native_paragraph(geometry, paints, position, context)?;
        self.push(Command::Leaf(scene))
    }
    /// Retain the actual indexed source mesh with its independent vertex-color
    /// and destination blend policies. No per-triangle command reconstruction.
    pub fn draw_vertices(
        &mut self,
        mesh: crate::vector::VertexMesh,
        mode: crate::vector::VertexColorBlend,
        paint: &Paint,
    ) -> Result<(), Error> {
        if paint.mask_filter.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let mut draw = crate::vector::VertexMeshDraw::new(mesh, paint.fill_brush()?);
        draw.transform = self.matrix.matrix4();
        draw.color_blend = mode;
        draw.aliased = !paint.antialias;
        let mut scene = Scene::default();
        let blend = paint.canvas_blend()?;
        if blend != LayerBlend::SrcOver {
            scene.push_blend(blend)?;
        }
        scene.draw_vertex_mesh(draw)?;
        if blend != LayerBlend::SrcOver {
            scene.pop_blend()?;
        }
        self.push(Command::Leaf(scene))
    }
    /// Original device-space Coons density and immutable indexed mesh lowering.
    pub fn draw_patch(
        &mut self,
        patch: &crate::vector::CoonsPatch,
        mode: crate::vector::VertexColorBlend,
        paint: &Paint,
    ) -> Result<(), Error> {
        self.draw_vertices(patch.mesh(self.matrix.matrix4())?, mode, paint)
    }
    /// Retain an actual path and its independent inverse-fill policy.
    pub fn draw_path(
        &mut self,
        geometry: Arc<PathGeometry>,
        inverse: bool,
        paint: &Paint,
    ) -> Result<(), Error> {
        self.push(Command::Path {
            geometry,
            matrix: self.matrix,
            paint: paint.snapshot(),
            inverse,
            text: None,
        })
    }
    /// Exact region boundary geometry, independent of integer region scan.
    pub fn draw_region(&mut self, region: &Region, paint: &Paint) -> Result<(), Error> {
        self.draw_path(Arc::new(region.boundary_path()?), false, paint)
    }
    /// Original line drawing leaves the caller's fill/stroke style unchanged.
    pub fn draw_line(
        &mut self,
        start: [f32; 2],
        end: [f32; 2],
        paint: &Paint,
    ) -> Result<(), Error> {
        self.draw_path(Arc::new(lines(&[(start, end)])?), false, paint)
    }
    /// Original point drawing selects stroke on a private paint copy.
    pub fn draw_points(
        &mut self,
        mode: CanvasPointMode,
        points: &[[f32; 2]],
        paint: &Paint,
    ) -> Result<(), Error> {
        if points.is_empty() {
            return Ok(());
        }
        let geometry = match mode {
            CanvasPointMode::Points => lines(&points.iter().map(|p| (*p, *p)).collect::<Vec<_>>())?,
            CanvasPointMode::Lines => lines(
                &points
                    .chunks_exact(2)
                    .map(|p| (p[0], p[1]))
                    .collect::<Vec<_>>(),
            )?,
            CanvasPointMode::Polygon => {
                let mut figure = PathFigure::new(points[0], false);
                figure
                    .segments
                    .try_reserve(points.len() - 1)
                    .map_err(|_| Error::Capacity)?;
                figure.segments.extend(
                    points[1..]
                        .iter()
                        .map(|p| PathSegment::new(PathSegmentKind::Line(*p))),
                );
                PathGeometry {
                    figures: vec![figure],
                    ..Default::default()
                }
            }
        };
        let mut paint = paint.clone();
        paint.style = PaintStyle::Stroke;
        self.draw_path(Arc::new(geometry), false, &paint)
    }
    /// Original analytic rectangle bounds are left/top/right/bottom.
    pub fn draw_rect(&mut self, bounds: [f32; 4], paint: &Paint) -> Result<(), Error> {
        self.draw_rect_xywh(xywh(bounds), paint)
    }
    /// Original x/y/width/height rectangle preserves the supplied dimensions;
    /// endpoint reconstruction is used only by its actual source geometry.
    pub fn draw_rect_xywh(&mut self, b: [f32; 4], paint: &Paint) -> Result<(), Error> {
        self.push(Command::Analytic {
            geometry: Arc::new(PathGeometry::rectangle(b[0], b[1], b[2], b[3])),
            shape: crate::geometry::Shape::Rectangle,
            bounds: b,
            center: [b[0] + b[2] * 0.5, b[1] + b[3] * 0.5],
            radius: 0.0,
            matrix: self.matrix,
            paint: paint.snapshot(),
        })
    }
    /// Original ellipse keeps its analytic family and actual center/radii.
    pub fn draw_oval(&mut self, bounds: [f32; 4], paint: &Paint) -> Result<(), Error> {
        let b = xywh(bounds);
        self.push(Command::Analytic {
            geometry: Arc::new(PathGeometry::ellipse(
                [bounds[0] + b[2] / 2.0, bounds[1] + b[3] / 2.0],
                b[2] * 0.5,
                b[3] * 0.5,
            )),
            shape: crate::geometry::Shape::Ellipse,
            bounds: b,
            center: [bounds[0] + b[2] / 2.0, bounds[1] + b[3] / 2.0],
            radius: 0.0,
            matrix: self.matrix,
            paint: paint.snapshot(),
        })
    }
    /// Original center/radius convenience overload.
    pub fn draw_circle(
        &mut self,
        center: [f32; 2],
        radius: f32,
        paint: &Paint,
    ) -> Result<(), Error> {
        self.push(Command::Analytic {
            geometry: Arc::new(PathGeometry::ellipse(center, radius, radius)),
            shape: crate::geometry::Shape::Ellipse,
            bounds: [
                center[0] - radius,
                center[1] - radius,
                radius * 2.0,
                radius * 2.0,
            ],
            center,
            radius: 0.0,
            matrix: self.matrix,
            paint: paint.snapshot(),
        })
    }
    /// Preserve all four corner pairs; only a circular uniform rectangle selects
    /// the canonical single-radius analytic shader, other shapes retain paths.
    pub fn draw_round_rect(&mut self, rect: RoundRect, paint: &Paint) -> Result<(), Error> {
        let geometry = Arc::new(rect.to_geometry(crate::vector::SweepDirection::Clockwise, 0)?);
        let r = rect.radii();
        if r.iter().all(|v| *v == r[0]) && r[0][0] == r[0][1] {
            self.push(Command::Analytic {
                geometry,
                shape: crate::geometry::Shape::RoundedRectangle,
                bounds: xywh(rect.bounds()),
                center: [
                    rect.bounds()[0] + rect.width() * 0.5,
                    rect.bounds()[1] + rect.height() * 0.5,
                ],
                radius: r[0][0],
                matrix: self.matrix,
                paint: paint.snapshot(),
            })
        } else {
            self.draw_path(geometry, false, paint)
        }
    }
    /// Original DrawPaint uses the current matrix and the whole canvas rectangle.
    pub fn draw_paint(&mut self, paint: &Paint) -> Result<(), Error> {
        self.draw_rect([0.0, 0.0, self.size[0], self.size[1]], paint)
    }
    /// Original device-color operator is an identity rectangle under current
    /// clips and selected blending, independent of canvas transform/opacity.
    pub fn draw_color(&mut self, color: [f32; 4], blend: LayerBlend) -> Result<(), Error> {
        if color.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        self.push(Command::DeviceColor {
            color: color.map(|v| v.clamp(0.0, 1.0)),
            blend,
        })
    }
    /// Original Clear selects Src on the device-color rectangle. Target-storage
    /// clear remains its separate native operator and is never substituted here.
    pub fn clear(&mut self, color: [u8; 4]) -> Result<(), Error> {
        self.draw_color(color.map(|v| f32::from(v) / 255.0), LayerBlend::Src)
    }
    /// Source picture painting consumes only paint alpha and blend metadata.
    pub fn draw_picture(
        &mut self,
        picture: CanvasPicture,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let opacity = paint.map_or(1.0, |p| f32::from(p.color[3]) / 255.0);
        let blend = paint.map_or(Ok(LayerBlend::SrcOver), Paint::canvas_blend)?;
        self.push(Command::Picture {
            picture,
            matrix: self.matrix,
            opacity,
            blend,
        })
    }
    /// Actual immutable CPU/GPU image source. Source and destination use XYWH.
    pub fn draw_image(
        &mut self,
        image: impl Into<ImageSource>,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        sampling: ImageSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let mut draw = ImageDraw::new(image, destination);
        if let Some(source) = source {
            draw.source = source;
        }
        draw.transform = self.matrix.affine()?;
        draw.sampling = sampling;
        draw.cubic = [1.0 / 3.0; 2];
        draw.validate()?;
        self.push(Command::Image {
            draw,
            paint: paint.map(Paint::snapshot),
        })
    }
    /// Retain the selected immutable texture view, source color space and context.
    #[cfg(feature = "gpu")]
    pub fn draw_texture(
        &mut self,
        image: crate::image::TextureImage,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        sampling: ImageSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let info = image.info();
        let source = source.unwrap_or([0.0, 0.0, info.width as f32, info.height as f32]);
        self.push(Command::Texture {
            image,
            source,
            destination,
            sampling,
            matrix: self.matrix,
            paint: paint.map(Paint::snapshot),
        })
    }
    /// Retain actual already-shaped text and independently supplied target policy.
    pub fn draw_text(
        &mut self,
        draw: crate::text::paint::TextDraw,
        mut context: crate::text::paint::TextPaintContext,
    ) -> Result<(), Error> {
        context.transform = context.transform * self.matrix.matrix4();
        context.semantic_opacity *= self.opacity;
        let mut scene = Scene::default();
        scene.draw_text(draw, context)?;
        self.push(Command::Leaf(scene))
    }
    /// Retain actual hinted generations, including every descriptor/no-ink slot.
    #[cfg(feature = "gpu")]
    pub fn draw_hinted_text(
        &mut self,
        draw: crate::text::hinted_replay::HintedReplayDraw,
        mut context: crate::text::paint::TextPaintContext,
    ) -> Result<(), Error> {
        context.transform = context.transform * self.matrix.matrix4();
        context.semantic_opacity *= self.opacity;
        let mut scene = Scene::default();
        scene.draw_hinted_text(draw, context)?;
        self.push(Command::Leaf(scene))
    }
}
fn lines(pairs: &[([f32; 2], [f32; 2])]) -> Result<PathGeometry, Error> {
    let mut geometry = PathGeometry::default();
    geometry
        .figures
        .try_reserve(pairs.len())
        .map_err(|_| Error::Capacity)?;
    for (start, end) in pairs {
        let mut figure = PathFigure::new(*start, false);
        figure
            .segments
            .push(PathSegment::new(PathSegmentKind::Line(*end)));
        geometry.figures.push(figure);
    }
    Ok(geometry)
}
pub(super) fn xywh(b: [f32; 4]) -> [f32; 4] {
    [b[0], b[1], b[2] - b[0], b[3] - b[1]]
}
