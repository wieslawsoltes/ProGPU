//! Ordered original archive compilation. Transport and execution admission are
//! deliberately separate: decoding never executes fonts, providers or effects.
use super::{
    ArchiveCommand, ArchiveCommandKind as K, ArchiveError, ArchivePicture, ArchiveVectorDraw,
    PictureArchive, Result,
};
use crate::{
    Error,
    geometry::Affine,
    mask::{GeometryMask, Mask, SourceMask},
    scene::{LayerBlend, Scene},
    text::{
        layout::{ManagedTextLayout, ManagedTextLayoutOptions},
        paint::{PositionedGlyphRun, TextDraw, TextPaintContext},
        preparation::ManagedPreparationOptions,
    },
    vector::{Matrix4, PathGeometry, VertexMeshDraw},
};
use std::sync::Arc;
mod geometry;
mod text;

/// Actual immutable Unicode preparation and fallback inputs. An absent host
/// environment does not fabricate the original platform's font discovery facts.
#[derive(Clone, Debug)]
pub struct ArchiveTextEnvironment {
    /// Witnessed original Unicode preparation profiles and context.
    pub preparation: ManagedPreparationOptions,
    /// Actual ordered immutable font owners for missing source scalars.
    pub fallback_fonts: Arc<[crate::font::ManagedFont]>,
}
/// Explicit receiving frame for archive compilation, before raster planning.
#[derive(Clone, Debug)]
pub struct ArchiveReplayOptions {
    /// Original parent transform, composed after each nonzero command matrix.
    pub transform: Matrix4,
    /// Original initial semantic opacity; source opacity commands remain ordered.
    pub opacity: f32,
    /// Actual text raster frame. Translation is taken from `transform`; independent
    /// DPI, ClearType suppression and original text policy remain retained.
    pub text_context: TextPaintContext,
    /// Required for DrawText shaping; positioned glyph runs own their arrays.
    pub text: Option<Arc<ArchiveTextEnvironment>>,
}
impl Default for ArchiveReplayOptions {
    fn default() -> Self {
        Self {
            transform: Matrix4::IDENTITY,
            opacity: 1.0,
            text_context: TextPaintContext::default(),
            text: None,
        }
    }
}
impl ArchiveReplayOptions {
    pub(crate) fn placed(&self, matrix: Matrix4, opacity: f32) -> Self {
        let mut result = self.clone();
        result.transform = result.transform * matrix;
        result.opacity *= opacity;
        result
    }
}
impl ArchivePicture {
    /// Compile an immutable archive into a private scene using unit-DPI defaults.
    /// Actual source text preparation must be supplied through `to_scene_with`.
    pub fn to_scene(&self) -> Result<Scene> {
        self.to_scene_with(ArchiveReplayOptions::default())
    }
    /// Compile the complete candidate atomically. Unsupported source providers,
    /// late GPU camera transforms and malformed scope nesting fail before output.
    pub fn to_scene_with(&self, options: ArchiveReplayOptions) -> Result<Scene> {
        affine(options.transform)?;
        if !options.opacity.is_finite() {
            return Err(ArchiveError::InvalidData);
        }
        let mut replay = Replay {
            scene: Scene::default(),
            opacity: options.opacity,
            opacity_stack: Vec::new(),
            coverage: Vec::new(),
            blends: 0,
            visited: 0,
            options,
        };
        let transform = replay.options.transform;
        replay.picture(self, transform, 0)?;
        if !replay.coverage.is_empty() || replay.blends != 0 || !replay.opacity_stack.is_empty() {
            return Err(ArchiveError::InvalidData);
        }
        Ok(replay.scene)
    }
}
impl PictureArchive {
    /// Compile at unit DPI. Cull bounds remain metadata and never translate ink.
    pub fn to_scene(&self) -> Result<Scene> {
        self.picture.to_scene()
    }
    /// Compile against the caller's exact receiving text/geometry frame.
    pub fn to_scene_with(&self, options: ArchiveReplayOptions) -> Result<Scene> {
        self.picture.to_scene_with(options)
    }
    /// Retain the complete raw archive as one ordered Canvas command. No font or
    /// drawing work occurs here; the original independent cull extent is retained.
    pub fn to_canvas_picture(&self) -> Result<crate::canvas::CanvasPicture> {
        crate::canvas::CanvasPicture::from_archive(self.picture.clone(), self.cull_bounds)
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum Scope {
    Clip,
    Mask,
}
struct Replay {
    scene: Scene,
    options: ArchiveReplayOptions,
    opacity: f32,
    opacity_stack: Vec<f32>,
    coverage: Vec<Scope>,
    blends: usize,
    visited: usize,
}
impl Replay {
    fn picture(&mut self, picture: &ArchivePicture, parent: Matrix4, level: u32) -> Result<()> {
        super::depth(level)?;
        self.visited = self
            .visited
            .checked_add(picture.commands.len())
            .ok_or(ArchiveError::Capacity)?;
        if self.visited > super::MAX_COMMANDS {
            return Err(ArchiveError::Capacity);
        }
        for command in picture.commands.iter() {
            if command.use_gpu_transforms {
                return Err(ArchiveError::Unsupported);
            }
            let transform = if command.transform == Matrix4([[0.0; 4]; 4]) {
                parent
            } else {
                command.transform * parent
            };
            affine(transform)?;
            match command.kind {
                K::PushOpacity => {
                    self.opacity_stack
                        .try_reserve(1)
                        .map_err(|_| ArchiveError::Capacity)?;
                    self.opacity_stack.push(self.opacity);
                    self.opacity *= command.font_size;
                    if !self.opacity.is_finite() {
                        return Err(ArchiveError::InvalidData);
                    }
                }
                K::PopOpacity => self.opacity = self.opacity_stack.pop().unwrap_or(1.0),
                K::PushBlendMode => {
                    self.scene.push_blend(blend(command.int_param)?)?;
                    self.blends += 1;
                }
                K::PopBlendMode => {
                    if self.blends != 0 {
                        self.scene.pop_blend()?;
                        self.blends -= 1;
                    }
                }
                K::PushClip => {
                    clip(&mut self.scene, command.rect, transform)?;
                    self.scope(Scope::Clip)?;
                }
                K::PushGeometryClip => {
                    if let Some(path) = &command.path {
                        let path = Arc::new(path.to_geometry()?);
                        if let Some([l, t, r, b]) = path.axis_aligned_rectangle_bounds() {
                            clip(&mut self.scene, [l, t, r - l, b - t], transform)?;
                        } else {
                            self.scene.push_mask(Mask::Geometry(GeometryMask {
                                geometry: path,
                                transform: affine(transform)?,
                            }))?;
                        }
                        self.scope(Scope::Clip)?;
                    }
                }
                K::PopClip | K::PopGeometryClip => self.pop(Scope::Clip)?,
                K::PushOpacityMask => self.mask(command, transform, level)?,
                K::PopOpacityMask => self.pop(Scope::Mask)?,
                K::DrawPicture => {
                    if let Some(child) = &command.picture {
                        self.picture(child, transform, level + 1)?;
                    }
                }
                K::FillTriangle
                | K::FillQuad
                | K::DrawPointBatch
                | K::DrawDotGrid
                | K::DrawDeviceDotGrid => {
                    if command.brush.is_some() {
                        self.scene.draw_archive_vector(ArchiveVectorDraw::new(
                            Arc::new(command.clone()),
                            picture.points.clone(),
                            transform,
                            self.opacity,
                        )?)?;
                    }
                }
                K::DrawRect | K::DrawRoundedRect | K::DrawEllipse | K::DrawCircle => {
                    geometry::primitive(
                        &mut self.scene,
                        command,
                        transform,
                        self.opacity,
                        &self.options,
                    )?
                }
                K::DrawPath => {
                    if let Some(path) = &command.path {
                        geometry::path(
                            &mut self.scene,
                            Arc::new(path.to_geometry()?),
                            command,
                            transform,
                            self.opacity,
                            &self.options,
                        )?;
                    }
                }
                K::DrawLine | K::DrawBezier | K::DrawCubicBezier | K::DrawPolyline => {
                    geometry::stroke_command(
                        &mut self.scene,
                        picture,
                        command,
                        transform,
                        self.opacity,
                    )?
                }
                K::DrawGlyphRun | K::DrawText => text::draw(
                    &mut self.scene,
                    command,
                    transform,
                    self.opacity,
                    &self.options,
                )?,
                K::DrawVertexMesh => {
                    if let Some(mesh) = &command.vertex_mesh {
                        let brush = match &command.brush {
                            Some(b) => Arc::new(b.to_vector()?),
                            None => Arc::new(crate::vector::Brush::new(
                                crate::vector::BrushKind::Solid([1.0; 4]),
                            )),
                        };
                        let mut draw = VertexMeshDraw::new((**mesh).clone(), brush);
                        draw.transform = transform;
                        draw.color_blend = command.vertex_color_blend_mode;
                        draw.semantic_opacity = self.opacity;
                        draw.aliased = command.is_edge_aliased;
                        self.scene.draw_vertex_mesh(draw)?;
                    }
                }
                // Original serialization rejects actual Texture/Visual/static
                // resource owners. Their decoded null command is a source no-op.
                K::DrawTexture | K::DrawVisual | K::DrawStaticDxf => {}
                // These source families select distinct extension/native producer
                // contracts. A transport record is not an executable provider.
                K::DrawLine3D
                | K::DrawHatch
                | K::DrawAcisSolid
                | K::DrawGpuLineSeries
                | K::DrawGpuScatterSeries
                | K::DrawExtension
                | K::DrawSpline
                | K::DrawHintedGlyphs => return Err(ArchiveError::Unsupported),
            }
        }
        Ok(())
    }
    fn scope(&mut self, value: Scope) -> Result<()> {
        self.coverage
            .try_reserve(1)
            .map_err(|_| ArchiveError::Capacity)?;
        self.coverage.push(value);
        Ok(())
    }
    fn pop(&mut self, expected: Scope) -> Result<()> {
        match self.coverage.last() {
            None => Ok(()),
            Some(actual) if *actual == expected => {
                self.scene.pop_scope()?;
                self.coverage.pop();
                Ok(())
            }
            _ => Err(ArchiveError::Unsupported),
        }
    }
    fn mask(&mut self, command: &ArchiveCommand, transform: Matrix4, level: u32) -> Result<()> {
        let mut source = Scene::default();
        if let Some(picture) = &command.picture {
            let mut options = self.options.clone();
            options.transform = Matrix4::IDENTITY;
            options.opacity = 1.0;
            let mut nested = Replay {
                scene: Scene::default(),
                opacity: 1.0,
                opacity_stack: Vec::new(),
                coverage: Vec::new(),
                blends: 0,
                visited: self.visited,
                options,
            };
            nested.picture(picture, Matrix4::IDENTITY, level + 1)?;
            if !nested.coverage.is_empty() || nested.blends != 0 || !nested.opacity_stack.is_empty()
            {
                return Err(ArchiveError::InvalidData);
            }
            self.visited = nested.visited;
            clip(&mut source, command.rect, Matrix4::IDENTITY)?;
            source.append(&nested.scene)?;
            source.pop_scope()?;
        } else if command.path.is_some() && command.pen.is_some() || command.brush.is_some() {
            // Original brush/stroke masks crop their private storage to outward
            // rounded declared bounds. Full-root source captures do not prove
            // that allocation; keep this route explicit until that adapter exists.
            return Err(ArchiveError::Unsupported);
        } else {
            return Ok(());
        }
        self.scene
            .push_mask(Mask::Source(SourceMask::new(Arc::new(source), transform)?))?;
        self.scope(Scope::Mask)
    }
}
pub(super) fn affine(matrix: Matrix4) -> Result<Affine> {
    let m = matrix.0;
    if m.iter().flatten().any(|v| !v.is_finite()) {
        return Err(ArchiveError::InvalidData);
    }
    if m[0][2] != 0.0
        || m[0][3] != 0.0
        || m[1][2] != 0.0
        || m[1][3] != 0.0
        || m[2] != [0.0, 0.0, 1.0, 0.0]
        || m[3][2] != 0.0
        || m[3][3] != 1.0
    {
        return Err(ArchiveError::Unsupported);
    }
    Ok(Affine([
        m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1],
    ]))
}
fn clip(scene: &mut Scene, rect: [f32; 4], transform: Matrix4) -> Result<()> {
    let [x, y, w, h] = rect;
    let m = transform.0;
    if m[0][1].abs() <= 0.0001 && m[1][0].abs() <= 0.0001 {
        let p =
            [[x, y], [x + w, y], [x + w, y + h], [x, y + h]].map(|p| transform.transform_point(p));
        let l = p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0]));
        let t = p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1]));
        let r = p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0]));
        let b = p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1]));
        scene.push_clip_rect([l, t, r - l, b - t])?;
    } else {
        scene.push_mask(Mask::Geometry(GeometryMask {
            geometry: Arc::new(PathGeometry::rectangle(x, y, w, h)),
            transform: affine(transform)?,
        }))?;
    }
    Ok(())
}
pub(super) fn blend(value: i32) -> Result<LayerBlend> {
    use LayerBlend::*;
    Ok(match value {
        0 => SrcOver,
        1 => Src,
        2 => Dst,
        3 => SrcIn,
        4 => DstIn,
        5 => SrcOut,
        6 => DstOut,
        7 => SrcAtop,
        8 => DstAtop,
        9 => Xor,
        10 => DstOver,
        11 => Multiply,
        12 => Screen,
        13 => Darken,
        14 => Lighten,
        15 => Exclusion,
        16 => Plus,
        17 => Clear,
        18 => Overlay,
        19 => ColorDodge,
        20 => ColorBurn,
        21 => HardLight,
        22 => SoftLight,
        23 => Difference,
        24 => Hue,
        25 => Saturation,
        26 => Color,
        27 => Luminosity,
        28 => Modulate,
        _ => return Err(ArchiveError::Unsupported),
    })
}
/// Map archive/host failures into the existing drawing admission boundary while
/// callers of the archive APIs retain the complete typed error.
pub(crate) fn drawing_error(error: ArchiveError) -> Error {
    match error {
        ArchiveError::Drawing(error) => error,
        ArchiveError::Capacity => Error::Capacity,
        ArchiveError::InvalidData | ArchiveError::Truncated => Error::InvalidInput,
        _ => Error::UnsupportedExecution,
    }
}
