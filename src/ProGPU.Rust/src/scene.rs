//! CPU-only retained scene recording and native-compatible path placement.
//! Recording owns immutable path generations. Compilation preserves painter
//! order across vector/text ranges and deduplicates raster identities within a frame.
//!
//! ```
//! use progpu::{geometry::AnalyticPrimitive, scene::Scene};
//! let mut scene = Scene::default();
//! scene.draw_analytic(AnalyticPrimitive::rectangle(
//!     [10.0, 20.0, 80.0, 40.0], [0.2, 0.6, 1.0, 1.0],
//! ))?;
//! let compiled = scene.compile(2.0)?; // Actual target DPI; no GPU is needed.
//! assert_eq!(compiled.mesh().indices().len(), 6);
//! # Ok::<(), progpu::Error>(())
//! ```

use crate::{
    Error,
    geometry::{Affine, AnalyticPrimitive, Mesh, VectorVertex},
    image::{ImageBindingKey, ImageDraw, ImageSamplingPath, ImageSource, TileImageDraw},
    path::{Path, RasterRequest},
    text::paint::{GlyphInstance, TextDraw, TextPaintContext, TextPathPolicy, VectorGlyphPaint},
    vector::{Brush, BrushKind, MaterialPage, MaterialTable, PreparedStroke, StrokeDraw},
};
use std::collections::HashMap;
use std::sync::Arc;

mod axis_clip;
pub use axis_clip::AxisClipDraw;
#[cfg(feature = "gpu")]
pub(crate) use axis_clip::{GroupOutput, GroupRef};
mod clear;
pub use clear::{ClearColor, TargetAlpha};
mod layer;
mod mask;
pub use layer::{LayerBlend, LayerDraw};
mod text;
#[cfg(feature = "gpu")]
pub(crate) use mask::CompiledDrawState;
#[cfg(feature = "gpu")]
pub(crate) use text::ResolvedSceneText;

#[derive(Clone, Debug)]
pub(crate) enum DrawRange {
    State(usize),
    Clear(ClearColor),
    ImageEffect(usize),
    #[cfg(feature = "gpu")]
    Axis(usize),
    Vector(std::ops::Range<u32>),
    #[cfg(feature = "gpu")]
    Text(std::ops::Range<u32>),
    Image(std::ops::Range<u32>, usize, LayerBlend),
    #[cfg(feature = "gpu")]
    Advanced(AdvancedImage),
}

#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub(crate) struct AdvancedImage {
    pub binding: usize,
    pub blend: LayerBlend,
    pub bounds: [u32; 4],
    pub opacity: f32,
}

fn append_draw(draws: &mut Vec<DrawRange>, draw: DrawRange) {
    match (draws.last_mut(), &draw) {
        (Some(DrawRange::Vector(last)), DrawRange::Vector(next)) if last.end == next.start => {
            last.end = next.end;
        }
        #[cfg(feature = "gpu")]
        (Some(DrawRange::Text(last)), DrawRange::Text(next)) if last.end == next.start => {
            last.end = next.end;
        }
        (
            Some(DrawRange::Image(last, binding, blend)),
            DrawRange::Image(next, next_binding, next_blend),
        ) if binding == next_binding && blend == next_blend && last.end == next.start => {
            last.end = next.end;
        }
        _ => draws.push(draw),
    }
}

/// Native path sampling and optional explicit source grayscale contrast.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct PathSampling(u32);

impl PathSampling {
    /// Create ordinary sampling with exactly 1, 4 or 8 samples per axis.
    pub fn new(sample_grid: u32) -> Result<Self, Error> {
        if !matches!(sample_grid, 1 | 4 | 8) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(sample_grid))
    }
    /// Select original explicit source grayscale contrast 0..=12. Binary
    /// sampling rejects this metadata; ordinary sampling remains unchanged.
    pub fn with_grayscale_contrast(self, contrast: u8) -> Result<Self, Error> {
        if contrast > 12 || self.sample_grid() == 1 {
            return Err(Error::InvalidInput);
        }
        Ok(Self(self.sample_grid() | ((u32::from(contrast) + 1) << 8)))
    }
    /// Original low-byte sample grid.
    pub fn sample_grid(self) -> u32 {
        self.0 & 0xff
    }
    /// Explicit original source contrast, absent for ordinary rendering.
    pub fn grayscale_contrast(self) -> Option<u8> {
        (self.0 >> 8 != 0).then(|| ((self.0 >> 8) - 1) as u8)
    }
    fn transfer(self) -> f32 {
        if self.0 >> 8 == 0 {
            1.0
        } else {
            -((self.0 >> 8) as f32)
        }
    }
}

impl Default for PathSampling {
    fn default() -> Self {
        Self(8)
    }
}

/// A filled original path with independent paint and placement.
#[derive(Clone, Debug)]
pub struct PathDraw {
    /// Immutable path source and fill rule.
    pub path: Path,
    /// Source-to-target logical transform.
    pub transform: Affine,
    /// Straight RGBA color; finite HDR values are preserved.
    pub color: [f32; 4],
    /// Sampling and explicit source coverage metadata.
    pub sampling: PathSampling,
}

/// Immutable source brush and independent semantic draw opacity. Recording
/// keeps raw opacity; original material registration performs the multiplication.
#[derive(Clone, Debug)]
pub struct MaterialPaint {
    /// Actual retained source brush generation.
    pub brush: Arc<Brush>,
    /// Original ancestor/draw opacity, independent of brush opacity.
    pub semantic_opacity: f32,
}
impl MaterialPaint {
    /// Retain a source brush with unit semantic opacity.
    pub fn new(brush: Arc<Brush>) -> Self {
        Self {
            brush,
            semantic_opacity: 1.0,
        }
    }
}

#[derive(Clone, Debug)]
enum Command {
    Empty,
    Clear(ClearColor),
    Layer(Box<LayerDraw>),
    ImageEffect(Box<crate::image_effect::ImageEffectDraw>),
    AxisClip(Box<AxisClipDraw>),
    #[cfg(feature = "gpu")]
    ResolvedAxis(usize),
    PushMask(Box<crate::mask::Mask>),
    PushMaskDifference(Box<crate::mask::Mask>),
    PushClip([f32; 4]),
    PopScope,
    Analytic(AnalyticPrimitive),
    Path(PathDraw),
    MaterialAnalytic(AnalyticPrimitive, MaterialPaint),
    MaterialPath(PathDraw, MaterialPaint),
    Stroke(StrokeDraw),
    Text(Box<(TextDraw, TextPaintContext)>),
    TextPath(PathDraw, MaterialPaint, TextPathPolicy),
    #[cfg(feature = "gpu")]
    Glyph(GlyphInstance),
    Image(Box<ImageDraw>, LayerBlend, Option<[u32; 4]>),
    TileImage(Box<TileImageDraw>),
}

/// Ordered retained commands; recording and CPU compilation perform no GPU work.
/// Text realization is explicitly owned by `Renderer::prepare_scene`.
#[derive(Clone, Debug, Default)]
pub struct Scene {
    commands: Vec<Command>,
    scope_depth: usize,
    has_scopes: bool,
    layer_depth: u8,
    has_axis: bool,
    storage_clear: bool,
    tree_text: bool,
    inherited_clip: Option<[u32; 4]>,
}

impl Scene {
    /// Append a balanced retained recording in source order. Source owners stay
    /// shared; O(C) command copies perform no geometry, font or GPU work. Nested
    /// coverage capacity is checked during whole-scene compilation.
    pub fn append(&mut self, source: &Scene) -> Result<(), Error> {
        if source.scope_depth != 0 {
            return Err(Error::InvalidInput);
        }
        self.commands
            .try_reserve(source.commands.len())
            .map_err(|_| Error::Capacity)?;
        self.commands.extend(source.commands.iter().cloned());
        self.has_scopes |= source.has_scopes;
        self.has_axis |= source.has_axis;
        self.storage_clear |= source.storage_clear;
        self.tree_text |= source.tree_text;
        self.layer_depth = self.layer_depth.max(source.layer_depth);
        Ok(())
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn image_effects(
        &self,
    ) -> impl Iterator<Item = &crate::image_effect::ImageEffectDraw> {
        self.commands.iter().filter_map(|c| match c {
            Command::ImageEffect(draw) => Some(draw.as_ref()),
            _ => None,
        })
    }
    /// Retain one original fused image effect in painter order, without GPU work.
    pub fn draw_image_effect(
        &mut self,
        draw: crate::image_effect::ImageEffectDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ImageEffect(Box::new(draw)));
        Ok(())
    }
    /// Retain an original image/patch draw after whole-inventory admission.
    pub fn draw_image(&mut self, draw: ImageDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::Image(Box::new(draw), LayerBlend::SrcOver, None));
        Ok(())
    }
    /// Retain an explicit occupied tile-page draw. This primitive always uses
    /// its original shader taps, independently of ordinary image sampling policy.
    pub fn draw_tile_image(&mut self, draw: TileImageDraw) -> Result<(), Error> {
        draw.vertices()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::TileImage(Box::new(draw)));
        Ok(())
    }
    /// Record original text without shaping, loading outlines, or allocating GPU
    /// resources. The actual preparation frame supplies DPI. This renderer's
    /// current direct target accepts affine placement and semantic opacity;
    /// late camera/MVP transforms and clips require their separate consumers.
    pub fn draw_text(&mut self, draw: TextDraw, context: TextPaintContext) -> Result<(), Error> {
        text::validate_context(context)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Text(Box::new((draw, context))));
        self.tree_text = true;
        Ok(())
    }

    /// Append a positioned text outline with its original per-axis raster scale,
    /// phase grid and coverage transfer. A missing brush is an unpainted path.
    pub fn draw_text_path(&mut self, paint: VectorGlyphPaint) -> Result<(), Error> {
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        if let Some(command) = text::path_command(paint)? {
            self.commands.push(command);
        }
        Ok(())
    }

    pub(crate) fn has_text(&self) -> bool {
        self.commands
            .iter()
            .any(|command| matches!(command, Command::Text(_)))
    }
    /// Recorded text and its source context, in original scene order. CPU-only
    /// consumers can inspect/plan these records without any GPU dependency.
    pub fn text_commands(&self) -> impl Iterator<Item = (&TextDraw, TextPaintContext)> {
        self.commands.iter().filter_map(|command| match command {
            Command::Text(record) => Some((&record.0, record.1)),
            _ => None,
        })
    }
    /// Retain an original path stroke and its actual pen/material frame.
    /// Curves remain analytic except for the source's explicit affine sections.
    /// Dash preparation is deferred and cached until compilation. Directed
    /// terminal-cap unions use the same ordered path atlas as ordinary fills.
    pub fn draw_stroke(&mut self, draw: StrokeDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Stroke(draw));
        Ok(())
    }
    /// Append an analytic shape, rejecting invalid or singular input atomically.
    pub fn draw_analytic(&mut self, primitive: AnalyticPrimitive) -> Result<(), Error> {
        let [_, minimum] = primitive.transform.scales()?;
        primitive.vertices(1.5 / minimum)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Analytic(primitive));
        Ok(())
    }
    /// Retain the selected path generation without copying segments.
    pub fn draw_path(&mut self, draw: PathDraw) -> Result<(), Error> {
        validate_path_draw(&draw)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Path(draw));
        Ok(())
    }
    /// Retain an analytic draw with original material semantics. The caller
    /// supplies the actual source shape center used by the shader's local brush
    /// frame, avoiding reconstruction from rounded bounds or transformed points.
    /// Solid brushes supply vertex color; other brushes encode this center.
    pub fn draw_material_analytic(
        &mut self,
        mut primitive: AnalyticPrimitive,
        paint: MaterialPaint,
        source_center: [f32; 2],
    ) -> Result<(), Error> {
        primitive.color = match paint.brush.kind {
            BrushKind::Solid(color) => color,
            _ => [source_center[0], source_center[1], 0.0, 0.0],
        };
        let [_, minimum] = primitive.transform.scales()?;
        primitive.vertices(1.5 / minimum)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::MaterialAnalytic(primitive, paint));
        Ok(())
    }
    /// Retain a path material independently of its raster identity. Original
    /// source coordinates remain in shape_size, so materials never sample the
    /// atlas UV frame. The material supplies the original solid/white payload.
    pub fn draw_material_path(
        &mut self,
        mut draw: PathDraw,
        paint: MaterialPaint,
    ) -> Result<(), Error> {
        draw.color = match paint.brush.kind {
            BrushKind::Solid(color) => color,
            _ => [1.0; 4],
        };
        validate_path_draw(&draw)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::MaterialPath(draw, paint));
        Ok(())
    }
    /// Number of retained source commands.
    pub fn len(&self) -> usize {
        self.commands.len()
    }
    /// Whether this recorder has no commands.
    pub fn is_empty(&self) -> bool {
        self.commands.is_empty()
    }
    /// Release recorded generations while retaining command capacity.
    pub fn clear(&mut self) {
        self.commands.clear();
        self.scope_depth = 0;
        self.has_scopes = false;
        self.layer_depth = 0;
        self.has_axis = false;
        self.storage_clear = false;
        self.tree_text = false;
        self.inherited_clip = None;
    }

    /// Compile one immutable generation at the actual target DPI. Geometry takes
    /// expected O(C + P) work for C commands and P distinct raster requests;
    /// materials add the original first-equal registration cost. Source segment
    /// arrays remain shared. Native atlas limits remain in force. Unresolved
    /// text and isolated layers require the GPU renderer and return
    /// `UnsupportedExecution` here.
    pub fn compile(&self, dpi_scale: f32) -> Result<CompiledScene, Error> {
        self.compile_with_materials(dpi_scale, &mut MaterialTable::default())
    }

    /// Compile using a reusable original material table/noise cache. Its frame
    /// records are cleared first; the returned snapshot cannot be changed by
    /// later reuse. Failure can leave partial scratch records, never a published
    /// scene. Material registration retains its original O(C*B + S) first-equal
    /// search for C commands, B brushes and S compared/emitted auxiliary records.
    pub fn compile_with_materials(
        &self,
        dpi_scale: f32,
        materials: &mut MaterialTable,
    ) -> Result<CompiledScene, Error> {
        self.compile_with_sampling(dpi_scale, materials, ImageSamplingPath::NativeSampler)
    }

    /// Compile image reconstruction metadata for the actual resolved sampling
    /// route. This policy does not change cubic, mip or occupied-tile algorithms.
    pub fn compile_with_sampling(
        &self,
        dpi_scale: f32,
        materials: &mut MaterialTable,
        image_sampling: ImageSamplingPath,
    ) -> Result<CompiledScene, Error> {
        if !dpi_scale.is_finite() || dpi_scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        if self.has_text() || self.layer_depth != 0 {
            return Err(Error::UnsupportedExecution);
        }
        let (states, state_changes) = self.compile_states(dpi_scale)?;
        let mut commands = Vec::new();
        commands
            .try_reserve_exact(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            let resolved = if let Command::Stroke(draw) = command {
                draw.prepare()?.map(|prepared| match prepared {
                    PreparedStroke::Stroke(draw) => Command::Stroke(draw),
                    PreparedStroke::Empty => Command::Empty,
                    PreparedStroke::Fill(path) => {
                        let m = draw.frame.transform.0;
                        let color = match draw.pen.brush.kind {
                            BrushKind::Solid(color) => color,
                            _ => [1.0; 4],
                        };
                        Command::MaterialPath(
                            PathDraw {
                                path,
                                transform: Affine([
                                    m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1],
                                ]),
                                color,
                                sampling: if draw.aliased {
                                    PathSampling(1)
                                } else {
                                    PathSampling::default()
                                },
                            },
                            MaterialPaint {
                                brush: draw.pen.brush.clone(),
                                semantic_opacity: draw.semantic_opacity,
                            },
                        )
                    }
                })
            } else {
                None
            };
            commands.push(resolved.map_or_else(
                || std::borrow::Cow::Borrowed(command),
                std::borrow::Cow::Owned,
            ));
        }
        materials.clear();
        let has_materials = commands.iter().any(|command| {
            matches!(
                command.as_ref(),
                Command::MaterialAnalytic(..)
                    | Command::MaterialPath(..)
                    | Command::TextPath(..)
                    | Command::Stroke(_)
            )
        });
        let mut rasters = Vec::new();
        let mut retained = HashMap::new();
        let mut placements = Vec::with_capacity(commands.len());
        for command in &commands {
            match command.as_ref() {
                Command::Empty
                | Command::Clear(_)
                | Command::PushMask(_)
                | Command::PushMaskDifference(_)
                | Command::PushClip(_)
                | Command::PopScope
                | Command::Analytic(_)
                | Command::MaterialAnalytic(..)
                | Command::Stroke(_)
                | Command::Image(..)
                | Command::TileImage(_)
                | Command::ImageEffect(_) => placements.push(None),
                #[cfg(feature = "gpu")]
                Command::Glyph(_) | Command::ResolvedAxis(_) => placements.push(None),
                Command::Text(_) | Command::Layer(_) | Command::AxisClip(_) => {
                    return Err(Error::UnsupportedExecution);
                }
                Command::Path(draw)
                | Command::MaterialPath(draw, _)
                | Command::TextPath(draw, _, _) => {
                    let placement = if let Command::TextPath(_, _, policy) = command.as_ref() {
                        text::placement(draw, *policy)?
                    } else {
                        Placement::new(draw, dpi_scale)?
                    };
                    let key = RasterKey {
                        source: draw.path.raster_identity(),
                        bounds: draw.path.bounds().map(f32::to_bits),
                        fill_rule: draw.path.fill_rule() as u32,
                        scale: placement.request.scale.map(f32::to_bits),
                        phase: placement.phase.map(f32::to_bits),
                        sampling: draw.sampling.0,
                    };
                    let next = rasters.len();
                    let index = *retained.entry(key).or_insert_with(|| {
                        rasters.push((draw.path.clone(), placement.request));
                        next
                    });
                    placements.push(Some((index, placement)));
                }
            }
        }
        let layout = AtlasLayout::new(rasters.iter().map(|(_, request)| request.size))?;
        let mut mesh = Mesh::default();
        let mut image_mesh = Mesh::default();
        let mut images = Vec::new();
        let mut image_bindings = HashMap::new();
        let glyphs = Vec::new();
        #[cfg(feature = "gpu")]
        let mut glyphs = glyphs;
        let mut draws = Vec::new();
        if self.inherited_clip.is_some() {
            draws.push(DrawRange::State(0));
        }
        let mut image_effects = Vec::new();
        let mut exact_loads = 0;
        let legacy_solid = Brush::default();
        // A mixed native scene reserves its existing unit-opacity vertex-color
        // binding first. Original capacity fallback then cannot turn a legacy
        // draw into whichever non-solid source material happened to come first.
        let legacy_index = if has_materials
            && commands
                .iter()
                .any(|command| matches!(command.as_ref(), Command::Analytic(_) | Command::Path(_)))
        {
            materials.register(Some(&legacy_solid), 1.0)?
        } else {
            0
        };
        for (command_index, (command, placement)) in commands.iter().zip(placements).enumerate() {
            let command = command.as_ref();
            if let Some(index) = state_changes.get(command_index).copied().flatten() {
                draws.push(DrawRange::State(index));
                continue;
            }
            let brush_index = match command {
                Command::MaterialAnalytic(_, paint)
                | Command::MaterialPath(_, paint)
                | Command::TextPath(_, paint, _) => {
                    materials.register(Some(&paint.brush), paint.semantic_opacity)?
                }
                Command::Stroke(draw) if draw.is_renderable()? => {
                    materials.register(Some(&draw.pen.brush), draw.semantic_opacity)?
                }
                // Legacy draws keep their vertex colors and unit opacity even
                // when material zero happens to be a non-solid source brush.
                _ => legacy_index,
            } as f32;
            let first_index = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
            match command {
                Command::Empty
                | Command::PushMask(_)
                | Command::PushMaskDifference(_)
                | Command::PushClip(_)
                | Command::PopScope => {}
                Command::Clear(color) => draws.push(DrawRange::Clear(*color)),
                Command::ImageEffect(draw) => {
                    draw.validate()?;
                    image_effects.try_reserve(1).map_err(|_| Error::Capacity)?;
                    draws.push(DrawRange::ImageEffect(image_effects.len()));
                    image_effects.push(draw.as_ref().clone());
                }
                #[cfg(feature = "gpu")]
                Command::ResolvedAxis(index) => draws.push(DrawRange::Axis(*index)),
                Command::Image(draw, blend, bounds) => {
                    let start =
                        u32::try_from(image_mesh.indices().len()).map_err(|_| Error::Capacity)?;
                    if blend.is_fixed() {
                        draw.append(&mut image_mesh, dpi_scale, image_sampling)?;
                    }
                    let key = draw.binding_key();
                    let next = images.len();
                    let binding = *image_bindings.entry(key).or_insert_with(|| {
                        images.push(ImageBinding {
                            image: draw.image.clone(),
                            key,
                        });
                        next
                    });
                    if !blend.is_fixed() {
                        #[cfg(feature = "gpu")]
                        draws.push(DrawRange::Advanced(AdvancedImage {
                            binding,
                            blend: *blend,
                            bounds: bounds.ok_or(Error::ResourceDomain)?,
                            opacity: draw.opacity,
                        }));
                        #[cfg(not(feature = "gpu"))]
                        {
                            let _ = bounds;
                            return Err(Error::UnsupportedExecution);
                        }
                    }
                    let end =
                        u32::try_from(image_mesh.indices().len()).map_err(|_| Error::Capacity)?;
                    if end != start {
                        append_draw(&mut draws, DrawRange::Image(start..end, binding, *blend));
                    }
                }
                Command::TileImage(draw) => {
                    let start =
                        u32::try_from(image_mesh.indices().len()).map_err(|_| Error::Capacity)?;
                    image_mesh.push_quad(draw.vertices()?)?;
                    let key = ImageBindingKey {
                        source: draw.image.identity(),
                        sampling: crate::image::ImageSampling::Linear,
                        anisotropy: 1,
                        address: [crate::image::ImageAddress::Clamp; 2],
                    };
                    let next = images.len();
                    let binding = *image_bindings.entry(key).or_insert_with(|| {
                        images.push(ImageBinding {
                            image: draw.image.clone(),
                            key,
                        });
                        next
                    });
                    append_draw(
                        &mut draws,
                        DrawRange::Image(
                            start..start.checked_add(6).ok_or(Error::Capacity)?,
                            binding,
                            LayerBlend::SrcOver,
                        ),
                    );
                }
                Command::Text(_) | Command::Layer(_) | Command::AxisClip(_) => {
                    return Err(Error::UnsupportedExecution);
                }
                #[cfg(feature = "gpu")]
                Command::Glyph(instance) => {
                    let index = u32::try_from(glyphs.len()).map_err(|_| Error::Capacity)?;
                    let end = index.checked_add(1).ok_or(Error::Capacity)?;
                    glyphs.try_reserve(1).map_err(|_| Error::Capacity)?;
                    glyphs.push(*instance);
                    append_draw(&mut draws, DrawRange::Text(index..end));
                }
                Command::Stroke(draw) => draw.append(&mut mesh, brush_index)?,
                Command::Analytic(primitive) | Command::MaterialAnalytic(primitive, _) => {
                    let [_, minimum] = primitive.transform.scales()?;
                    // Original native analytic execution expands in the local
                    // affine frame; DPI belongs to the final projection.
                    let padding = 1.5 / minimum;
                    let mut vertices = primitive.vertices(padding)?;
                    for vertex in &mut vertices {
                        vertex.brush_index = brush_index;
                    }
                    mesh.push_quad(vertices)?;
                }
                Command::Path(draw)
                | Command::MaterialPath(draw, _)
                | Command::TextPath(draw, _, _) => {
                    let (index, placement) = placement.ok_or(Error::InvalidInput)?;
                    let mut vertices = if let Command::TextPath(_, _, policy) = command {
                        text::vertices(&placement, draw, *policy, layout.origins[index], dpi_scale)
                    } else {
                        placement.vertices(draw, layout.origins[index], dpi_scale)?
                    };
                    for vertex in &mut vertices {
                        vertex.brush_index = brush_index;
                    }
                    exact_loads += u32::from(vertices[0].stroke_thickness < 0.0);
                    mesh.push_quad(vertices)?;
                }
            }
            let end = u32::try_from(mesh.indices().len()).map_err(|_| Error::Capacity)?;
            if end != first_index {
                append_draw(&mut draws, DrawRange::Vector(first_index..end));
            }
        }
        // Reject finite-input overflow before publishing the CPU generation.
        if mesh.vertices().iter().any(|v| {
            bytemuck::cast_slice::<_, f32>(std::slice::from_ref(v))
                .iter()
                .any(|v| !v.is_finite())
        }) {
            return Err(Error::InvalidInput);
        }
        Ok(CompiledScene {
            mesh,
            rasters,
            layout,
            dpi_scale,
            exact_loads,
            materials: materials.snapshot()?,
            glyphs,
            draws,
            states,
            image_mesh,
            image_effects,
            images,
            image_sampling,
            has_tile_images: self
                .commands
                .iter()
                .any(|c| matches!(c, Command::TileImage(_))),
        })
    }
}

/// Frozen CPU compilation that can be prepared on a worker without a GPU.
#[derive(Debug)]
pub struct CompiledScene {
    pub(crate) mesh: Mesh,
    pub(crate) rasters: Vec<(Path, RasterRequest)>,
    pub(crate) layout: AtlasLayout,
    dpi_scale: f32,
    exact_loads: u32,
    materials: MaterialPage,
    pub(crate) glyphs: Vec<GlyphInstance>,
    pub(crate) draws: Vec<DrawRange>,
    pub(crate) states: Vec<mask::CompiledDrawState>,
    pub(crate) image_mesh: Mesh,
    pub(crate) image_effects: Vec<crate::image_effect::ImageEffectDraw>,
    pub(crate) images: Vec<ImageBinding>,
    pub(crate) image_sampling: ImageSamplingPath,
    pub(crate) has_tile_images: bool,
}

#[derive(Debug)]
pub(crate) struct ImageBinding {
    pub image: ImageSource,
    pub key: ImageBindingKey,
}

impl CompiledScene {
    /// Retained fused-effect inputs in painter order, inspectable without a GPU.
    pub fn image_effects(&self) -> impl Iterator<Item = &crate::image_effect::ImageEffectDraw> {
        self.draws.iter().filter_map(|draw| match draw {
            DrawRange::ImageEffect(index) => Some(&self.image_effects[*index]),
            _ => None,
        })
    }
    /// Original image vertices in their independent shader stream.
    pub fn image_mesh(&self) -> &Mesh {
        &self.image_mesh
    }
    /// Distinct source/sampler bindings retained by image ranges.
    pub fn image_binding_count(&self) -> usize {
        self.images.len()
    }
    /// Actual image reconstruction route encoded by CPU compilation.
    pub fn image_sampling_path(&self) -> ImageSamplingPath {
        self.image_sampling
    }
    /// Whether an explicit occupied-tile primitive requires shader sampling.
    pub fn has_tile_images(&self) -> bool {
        self.has_tile_images
    }
    /// Actual retained image sources and their original sampler selections.
    pub fn image_sources(
        &self,
    ) -> impl Iterator<Item = (&ImageSource, crate::image::ImageSampling)> {
        self.images.iter().map(|i| (&i.image, i.key.sampling))
    }
    /// Ordered vector/text pipeline ranges after compilation.
    pub fn draw_count(&self) -> usize {
        self.draws
            .iter()
            .filter(|d| !matches!(d, DrawRange::State(_)))
            .count()
    }
    /// Frozen mask/clip state records, including the unmasked root record.
    pub fn coverage_state_count(&self) -> usize {
        self.states.len()
    }
    /// Ordered range-stream offset and frozen state index at every coverage
    /// transition. State transitions are not counted as GPU draws.
    pub fn coverage_state_changes(&self) -> impl Iterator<Item = (usize, usize)> {
        self.draws
            .iter()
            .enumerate()
            .filter_map(|(offset, draw)| match draw {
                DrawRange::State(index) => Some((offset, *index)),
                _ => None,
            })
    }
    /// Actual glyph instance inventory; atlas resolution is a GPU operation.
    pub fn glyph_instances(&self) -> &[GlyphInstance] {
        &self.glyphs
    }
    /// Original painter-ordered vector records.
    pub fn mesh(&self) -> &Mesh {
        &self.mesh
    }
    /// Exact target DPI used for path placement and projection admission.
    pub fn dpi_scale(&self) -> f32 {
        self.dpi_scale
    }
    /// Number of unique source/scale/phase/sampling raster identities.
    pub fn path_raster_count(&self) -> usize {
        self.rasters.len()
    }
    /// Square retained atlas extent, or zero for an analytic-only scene.
    pub fn atlas_size(&self) -> u32 {
        self.layout.size
    }
    /// Number of path draws admitted to exact integer texel loads.
    pub fn exact_path_draws(&self) -> u32 {
        self.exact_loads
    }
    /// Immutable exact registered-material generation for this ordered scene.
    pub fn materials(&self) -> &MaterialPage {
        &self.materials
    }
}

#[derive(Hash, PartialEq, Eq)]
struct RasterKey {
    source: (usize, usize),
    bounds: [u32; 4],
    fill_rule: u32,
    scale: [u32; 2],
    phase: [u32; 2],
    sampling: u32,
}

fn validate_path_draw(draw: &PathDraw) -> Result<(), Error> {
    draw.transform.scales()?;
    let [min_x, min_y, max_x, max_y] = draw.path.bounds();
    if min_x >= max_x || min_y >= max_y || draw.color.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    Ok(())
}

struct Placement {
    request: RasterRequest,
    minimum: [f32; 2],
    maximum: [f32; 2],
    phase: [f32; 2],
}

impl Placement {
    fn new(draw: &PathDraw, dpi_scale: f32) -> Result<Self, Error> {
        validate_path_draw(draw)?;
        let scale = draw.transform.scales()?[0] * dpi_scale;
        if !scale.is_finite() || scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let phase = [
            quantize_phase(draw.transform.0[4] * dpi_scale),
            quantize_phase(draw.transform.0[5] * dpi_scale),
        ];
        if phase.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let bounds = draw.path.bounds();
        let minimum = [
            (bounds[0] * scale).floor() - 4.0,
            (bounds[1] * scale).floor() - 4.0,
        ];
        let maximum = [
            (bounds[2] * scale).ceil() + 4.0,
            (bounds[3] * scale).ceil() + 4.0,
        ];
        let extent = [maximum[0] - minimum[0], maximum[1] - minimum[1]];
        if extent
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.0 || *v > 4092.0)
        {
            return Err(Error::Capacity);
        }
        Ok(Self {
            minimum,
            maximum,
            phase,
            request: RasterRequest {
                origin: [minimum[0] - phase[0], minimum[1] - phase[1]],
                scale: [scale; 2],
                size: extent.map(|v| v as u32),
                sample_grid: draw.sampling.sample_grid(),
            },
        })
    }

    fn vertices(
        &self,
        draw: &PathDraw,
        origin: [u32; 2],
        dpi: f32,
    ) -> Result<[VectorVertex; 4], Error> {
        let scale = self.request.scale[0];
        let [left, top] = self.minimum.map(|v| v / scale);
        let [right, bottom] = self.maximum.map(|v| v / scale);
        let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
        let [x, y] = origin.map(|v| v as f32);
        let [width, height] = self.request.size.map(|v| v as f32);
        let [sx, sy] = self.phase;
        let atlas = [
            [x + sx, y + sy],
            [x + width + sx, y + sy],
            [x + width + sx, y + height + sy],
            [x + sx, y + height + sy],
        ];
        let positions = local.map(|p| draw.transform.transform_point(p));
        let physical = positions.map(|p| [p[0] * dpi, p[1] * dpi]);
        let exact = exact_projection(dpi) && exact_mapping(physical, atlas);
        Ok(std::array::from_fn(|i| VectorVertex {
            position: positions[i],
            color: draw.color,
            texture_coordinate: atlas[i],
            brush_index: 0.0,
            shape_size: local[i],
            corner_radius: draw.sampling.transfer(),
            stroke_thickness: if exact {
                if dpi == 1.0 { -1.0 } else { -2.0 }
            } else {
                0.0
            },
            shape_type: 4.0,
        }))
    }
}

fn quantize_phase(mut value: f32) -> f32 {
    value -= value.floor();
    let quantized = (value * 64.0).round() / 64.0;
    if quantized >= 1.0 { 0.0 } else { quantized }
}

pub(crate) fn exact_projection(dpi: f32) -> bool {
    dpi.is_finite() && dpi >= 1.0 && dpi.to_bits() & 0x007f_ffff == 0
}

pub(crate) fn exact_mapping(positions: [[f32; 2]; 4], atlas: [[f32; 2]; 4]) -> bool {
    let offset = [atlas[0][0] - positions[0][0], atlas[0][1] - positions[0][1]];
    positions.iter().zip(atlas).all(|(p, a)| {
        (0..2).all(|axis| {
            p[axis].abs() <= 8_388_608.0
                && a[axis].abs() <= 8_388_608.0
                && p[axis].trunc() == p[axis]
                && a[axis].trunc() == a[axis]
                && a[axis] - p[axis] == offset[axis]
        })
    })
}

#[derive(Debug)]
pub(crate) struct AtlasLayout {
    pub size: u32,
    pub origins: Vec<[u32; 2]>,
}

impl AtlasLayout {
    pub(crate) fn new(sizes: impl IntoIterator<Item = [u32; 2]>) -> Result<Self, Error> {
        let mut size = 1024;
        let mut x = 2u32;
        let mut y = 2u32;
        let mut row_height = 0;
        let mut origins = Vec::new();
        for [width, height] in sizes {
            if width == 0 || height == 0 || width > 4092 || height > 4092 {
                return Err(Error::Capacity);
            }
            while width + 4 > size && size < 8192 {
                size *= 2;
            }
            if x + width + 2 > size {
                x = 2;
                y = y.checked_add(row_height + 2).ok_or(Error::Capacity)?;
                row_height = 0;
            }
            while y + height + 2 > size && size < 8192 {
                size *= 2;
            }
            if y + height + 2 > size {
                return Err(Error::Capacity);
            }
            origins.push([x, y]);
            x += width + 2;
            row_height = row_height.max(height);
        }
        if origins.is_empty() {
            size = 0;
        }
        Ok(Self { size, origins })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::path::{FillRule, Segment};

    fn draw() -> PathDraw {
        let points = [[0.0, 0.0], [16.0, 0.0], [16.0, 16.0], [0.0, 16.0]];
        let segments: Vec<_> = (0..4)
            .map(|i| Segment::line(points[i], points[(i + 1) % 4]))
            .collect();
        PathDraw {
            path: Path::new(segments, [0.0, 0.0, 16.0, 16.0], FillRule::NonZero).unwrap(),
            transform: Affine::IDENTITY,
            color: [1.0; 4],
            sampling: PathSampling::default(),
        }
    }

    #[test]
    fn raster_identity_keeps_phase_scale_and_sampling_but_shares_integer_moves() {
        let mut scene = Scene::default();
        let mut draw = draw();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[4] = 20.0;
        draw.color = [0.0, 0.0, 1.0, 0.5];
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[4] = 20.25;
        scene.draw_path(draw.clone()).unwrap();
        draw.sampling = draw.sampling.with_grayscale_contrast(0).unwrap();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform.0[0] = 2.0;
        scene.draw_path(draw).unwrap();
        let compiled = scene.compile(1.0).unwrap();
        assert_eq!(compiled.path_raster_count(), 4);
        assert_eq!(compiled.mesh().indices().len(), 30);
        assert_eq!(compiled.exact_path_draws(), 2);
        assert_eq!(compiled.mesh().vertices()[12].corner_radius, -1.0);
        scene.clear();
        assert_eq!(compiled.path_raster_count(), 4);
        assert_eq!(compiled.rasters[0].0.segments().len(), 4);
    }

    #[test]
    fn exact_mapping_checks_all_corners_and_the_actual_dpi_projection() {
        let mut scene = Scene::default();
        scene.draw_path(draw()).unwrap();
        for dpi in [1.0, 2.0, 4.0] {
            assert_eq!(scene.compile(dpi).unwrap().exact_path_draws(), 1);
        }
        for dpi in [0.5, 1.25, 3.0] {
            assert_eq!(scene.compile(dpi).unwrap().exact_path_draws(), 0);
        }
        let positions = [[0.0, 0.0], [4.0, 0.0], [4.0, 4.0], [0.0, 4.0]];
        let mut atlas = positions.map(|p| [p[0] + 8.0, p[1] + 4.0]);
        assert!(exact_mapping(positions, atlas));
        atlas[3][0] += 1.0;
        assert!(!exact_mapping(positions, atlas));
        assert_eq!(quantize_phase(-0.0078125), 0.0);
        assert_eq!(quantize_phase(-0.5), 0.5);
    }

    #[test]
    fn native_shelf_grows_without_repacking_old_tiles() {
        let layout = AtlasLayout::new([[1000, 1000], [1000, 1000], [2000, 1000]]).unwrap();
        assert_eq!(layout.origins, [[2, 2], [2, 1004], [2, 2006]]);
        assert_eq!(layout.size, 4096);
        assert!(AtlasLayout::new([[4093, 1]]).is_err());
        assert!(AtlasLayout::new([[4092, 4092]; 5]).is_err());
        assert_eq!(AtlasLayout::new([]).unwrap().size, 0);
    }

    #[test]
    fn invalid_commands_and_source_contrast_reject_atomically() {
        let mut scene = Scene::default();
        let mut draw = draw();
        scene.draw_path(draw.clone()).unwrap();
        draw.transform = Affine([0.0; 6]);
        assert!(scene.draw_path(draw).is_err());
        assert_eq!(scene.len(), 1);
        assert!(PathSampling::new(2).is_err());
        assert!(
            PathSampling::new(1)
                .unwrap()
                .with_grayscale_contrast(0)
                .is_err()
        );
        assert!(PathSampling::default().with_grayscale_contrast(13).is_err());
        assert_eq!(PathSampling::default().grayscale_contrast(), None);
        assert_eq!(
            PathSampling::default()
                .with_grayscale_contrast(12)
                .unwrap()
                .grayscale_contrast(),
            Some(12)
        );
    }
}
