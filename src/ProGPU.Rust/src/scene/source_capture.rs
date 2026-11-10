//! Original source-command placement before any path, stroke or text planning.
//! Allocation/projection remain owned by SourceRenderFrame and the renderer.
use super::*;
use crate::{
    composition::SourceRenderFrame,
    mask::{AnalyticMaskPolicy, Mask},
    vector::Matrix4,
};
use wide::f32x4 as V;

impl Scene {
    /// Place an original visual page before geometry/text planning and apply
    /// inherited visual opacity to its individual draws, preserving Clear.
    pub(crate) fn place_visual_page(
        &self,
        transform: Matrix4,
        dpi: f32,
        suppress: bool,
        opacity: f32,
        reset_late_camera: bool,
    ) -> Result<Self, Error> {
        if !opacity.is_finite() || !(0.0..=1.0).contains(&opacity) {
            return Err(Error::InvalidInput);
        }
        if !reset_late_camera {
            self.preflight_visual_camera()?;
        }
        let frame = SourceRenderFrame::visual_placement(transform, dpi, suppress)?;
        let mut result = self.place_source_capture(frame)?;
        apply_opacity(&mut result, opacity)?;
        Ok(result)
    }
    /// Place an immutable source recording into an actual implicit-input or raw
    /// cache capture. The complete candidate is admitted before publication;
    /// neither source commands nor their path/font/image generations are changed.
    /// A proven-empty cache still traverses all original source ownership/policy
    /// records before publishing an empty recording for its one-texel target.
    pub(crate) fn place_source_capture(&self, placement: SourceRenderFrame) -> Result<Self, Error> {
        let transform = placement.source_to_raster();
        affine(transform)?;
        let candidate = place_scene(self, placement, 0, &mut None)?;
        if placement.is_empty() {
            Ok(Self::default())
        } else {
            Ok(candidate)
        }
    }
}

fn place_scene(
    source: &Scene,
    frame: SourceRenderFrame,
    depth: u8,
    inverse: &mut Option<Matrix4>,
) -> Result<Scene, Error> {
    if source.scope_depth != 0 {
        return Err(Error::InvalidInput);
    }
    // A compiled group's inherited clip is an actual old physical target, not
    // source geometry. Reconstructing a logical rectangle would lose its proof.
    if source.inherited_clip.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    if depth > 16 {
        return Err(Error::Capacity);
    }
    let transform = frame.source_to_raster();
    let mut candidate = Scene {
        scope_depth: source.scope_depth,
        has_scopes: source.has_scopes,
        layer_depth: source.layer_depth,
        has_axis: source.has_axis,
        storage_clear: source.storage_clear,
        tree_text: source.tree_text,
        ..Default::default()
    };
    candidate
        .commands
        .try_reserve_exact(source.commands.len())
        .map_err(|_| Error::Capacity)?;
    for command in &source.commands {
        let placed = match command {
            Command::LateCamera(inner, camera) => {
                late_camera::validate(inner, *camera)?;
                let camera = *camera * transform;
                late_camera::validate(inner, camera)?;
                Command::LateCamera(inner.clone(), camera)
            }
            Command::Empty => Command::Empty,
            Command::CanvasTextPath(draw) => {
                let mut draw = draw.as_ref().clone();
                draw.context.transform = draw.context.transform * transform;
                draw.context.dpi_scale = frame.dpi_scale();
                draw.context.suppress_cached_cleartype |= frame.suppress_cached_cleartype();
                text::validate_context(draw.context)?;
                Command::CanvasTextPath(Arc::new(draw))
            }
            Command::NativeAtlasLease(plan) => Command::NativeAtlasLease(plan.clone()),
            Command::NativeDesignAtlas(draw) => {
                let m = transform.0;
                if m.iter().flatten().any(|v| !v.is_finite())
                    || m[0][2] != 0.0
                    || m[0][3] != 0.0
                    || m[1][2] != 0.0
                    || m[1][3] != 0.0
                    || m[2] != [0.0, 0.0, 1.0, 0.0]
                    || m[3][2] != 0.0
                    || m[3][3] != 1.0
                {
                    return Err(Error::UnsupportedExecution);
                }
                let mut context = draw.frame().then([
                    f64::from(m[0][0]),
                    f64::from(m[0][1]),
                    f64::from(m[1][0]),
                    f64::from(m[1][1]),
                    f64::from(m[3][0]),
                    f64::from(m[3][1]),
                ])?;
                context.suppress_cleartype |= frame.suppress_cached_cleartype();
                let placed = draw.with_frame(context)?;
                // Full actual-DPI admission, including explicit color generations,
                // precedes any source device callback or renderer mutation.
                placed.prepare(frame.dpi_scale())?;
                Command::NativeDesignAtlas(Arc::new(placed))
            }
            Command::NativeDesignLease(plan) => Command::NativeDesignLease(plan.clone()),
            Command::NativeDesignVector(draw) => {
                let mut context = draw.context();
                context.transform = context.transform * transform;
                context.dpi_scale = frame.dpi_scale();
                context.use_mvp = false;
                context.gpu_transforms_active = false;
                context.camera_view = Matrix4::IDENTITY;
                context.suppress_cached_cleartype |= frame.suppress_cached_cleartype();
                Command::NativeDesignVector(Arc::new(
                    draw.with_context(context)
                        .map_err(native_design::native_design_error)?,
                ))
            }
            Command::PushBlend(blend) => Command::PushBlend(*blend),
            Command::PopBlend => Command::PopBlend,
            Command::CachedPicture(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.transform = draw.transform * transform;
                placed.validate()?;
                Command::CachedPicture(Box::new(placed))
            }
            // Target-storage Clear is deliberately independent of the source
            // matrix. Its actual inherited binary clip is placed separately.
            Command::Clear(color) => Command::Clear(*color),
            Command::PopScope => Command::PopScope,
            Command::PopCoverage { kind, source_clip } => Command::PopCoverage {
                kind: *kind,
                source_clip: *source_clip,
            },
            Command::PushClip(bounds) => clip_command(bounds_edges(*bounds)?, transform)?,
            Command::PushClipEdges(edges) => clip_command(*edges, transform)?,
            Command::PushMask(mask) => Command::PushMask(Box::new(place_mask(mask, transform)?)),
            Command::PushMaskDifference(mask) => {
                Command::PushMaskDifference(Box::new(place_mask(mask, transform)?))
            }
            Command::Layer(layer) => {
                layer.validate()?;
                let child = place_scene(&layer.scene, frame, depth + 1, inverse)?;
                let edges = layer
                    .bounds_edges()
                    .map(|edges| storage_edges(edges, transform))
                    .transpose()?;
                let mut placed = layer.as_ref().clone();
                placed.scene = Arc::new(child);
                placed.place_bounds(edges);
                Command::Layer(Box::new(placed))
            }
            Command::AxisClip(axis) => {
                if !axis_aligned(transform) {
                    return Err(Error::UnsupportedExecution);
                }
                let edges = place_edges(axis.bounds_edges(), transform)?;
                let child = place_scene(axis.scene(), frame, depth + 1, inverse)?;
                Command::AxisClip(Box::new(axis.placed(Arc::new(child), edges)))
            }
            #[cfg(feature = "gpu")]
            Command::BackdropMaterial(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.transform = draw.transform * transform;
                placed.validate()?;
                Command::BackdropMaterial(Box::new(placed))
            }
            Command::CompileOnly(..) => return Err(Error::UnsupportedExecution),
            Command::ArchiveGeometry(draw) => {
                Command::ArchiveGeometry(Arc::new(draw.placed(transform)?))
            }
            Command::ArchiveVector(draw) => {
                Command::ArchiveVector(Arc::new(draw.placed(transform)?))
            }
            Command::ArchiveHatch(draw) => Command::ArchiveHatch(Arc::new(draw.placed(transform)?)),
            Command::ArchiveThreeD(draw) => {
                Command::ArchiveThreeD(Arc::new(draw.placed(transform)?))
            }
            Command::VertexMesh(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.transform = draw.transform * transform;
                Command::VertexMesh(Box::new(placed))
            }
            Command::Analytic(primitive) => {
                Command::Analytic(place_analytic(*primitive, transform)?)
            }
            Command::MaterialAnalytic(primitive, paint) => {
                // Analytic and filled-path brushes consume original local
                // coordinates. Their material matrices and domains do not move.
                Command::MaterialAnalytic(place_analytic(*primitive, transform)?, paint.clone())
            }
            Command::Path(path) => Command::Path(place_path(path, transform)?),
            Command::MaterialPath(path, paint) => {
                Command::MaterialPath(place_path(path, transform)?, paint.clone())
            }
            Command::TextPath(path, paint, policy) => {
                // Explicit vector-glyph records own this selected raster policy.
                // Its physical/logical raster_scale is not inferred from capture
                // allocation, and its original source positions remain intact.
                Command::TextPath(place_path(path, transform)?, paint.clone(), *policy)
            }
            Command::Stroke(stroke) | Command::PlacedStroke(stroke, _) => {
                // Keep the original path and complete pen, including source join,
                // dash, local/fixed/hairline and recorded-width metadata. Fresh
                // preparation state cannot replace a live source cache entry.
                let pen = place_pen(&stroke.pen, transform, inverse)?;
                let mut placed = StrokeDraw::new(stroke.path.clone(), pen);
                placed.frame = stroke.frame;
                placed.frame.transform = stroke.frame.transform * transform;
                affine(placed.frame.transform)?;
                placed.aliased = stroke.aliased;
                placed.semantic_opacity = stroke.semantic_opacity;
                placed.validate()?;
                let fill_brush = if let Command::PlacedStroke(_, brush) = command {
                    brush.clone()
                } else {
                    stroke.pen.brush.clone()
                };
                Command::PlacedStroke(placed, fill_brush)
            }
            Command::HintedText(record) => {
                let (draw, mut context) = *record.clone();
                context.transform = context.transform * transform;
                context.dpi_scale = frame.dpi_scale();
                context.use_mvp = false;
                context.gpu_transforms_active = false;
                context.camera_view = Matrix4::IDENTITY;
                context.suppress_cached_cleartype |= frame.suppress_cached_cleartype();
                draw.validate(context)?;
                Command::HintedText(Box::new((draw, context)))
            }
            Command::Text(record) => {
                let (draw, mut context) = *record.clone();
                // RenderOffscreenCore clears active late-camera compilation.
                // Retained source positions and command transforms remain before
                // target-specific glyph origins, sizing and fallback decisions.
                context.transform = context.transform * transform;
                context.dpi_scale = frame.dpi_scale();
                context.use_mvp = false;
                context.gpu_transforms_active = false;
                context.camera_view = Matrix4::IDENTITY;
                context.suppress_cached_cleartype |= frame.suppress_cached_cleartype();
                text::validate_context(context)?;
                draw.validate(context.transform, context.dpi_scale)?;
                Command::Text(Box::new((draw, context)))
            }
            Command::Image(draw, blend, bounds, raster_operation) => {
                if bounds.is_some() {
                    return Err(Error::UnsupportedExecution);
                }
                // ROP pattern origins anchor to actual destination pixels. Only
                // source geometry moves; raw pattern pixels/phase stay unchanged.
                Command::Image(
                    Box::new(place_image(draw, transform)?),
                    *blend,
                    None,
                    raster_operation.clone(),
                )
            }
            Command::ImageColor(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.draw = place_image(&draw.draw, transform)?;
                Command::ImageColor(Box::new(placed))
            }
            Command::ColorFilteredImage(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.draw = place_image(&draw.draw, transform)?;
                Command::ColorFilteredImage(Box::new(placed))
            }
            Command::ImageFilteredImage(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.draw = place_image(&draw.draw, transform)?;
                Command::ImageFilteredImage(Box::new(placed))
            }
            Command::CustomGrid(command) => Command::CustomGrid(command.clone()),
            Command::Series(draw) => Command::Series(Arc::new(draw.placed(transform))),
            #[cfg(feature = "gpu")]
            Command::Mesh3D(draw) => Command::Mesh3D(Arc::new(draw.placed(transform)?)),
            #[cfg(feature = "gpu")]
            Command::Mesh3DCamera(camera) => Command::Mesh3DCamera(*camera),
            Command::ImageEffect(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.place_source(transform)?;
                Command::ImageEffect(Box::new(placed))
            }
            Command::TileImage(draw) => {
                let mut vertices = draw.vertices()?;
                place_quad(&mut vertices, transform)?;
                Command::PlacedTileImage(draw.clone(), transform)
            }
            Command::PlacedTileImage(draw, previous) => {
                let placed = *previous * transform;
                affine(placed)?;
                let mut vertices = draw.vertices()?;
                place_quad(&mut vertices, placed)?;
                Command::PlacedTileImage(draw.clone(), placed)
            }
            #[cfg(feature = "gpu")]
            Command::OwnedShaderEffect(draw) => {
                let mut placed = draw.as_ref().clone();
                placed.transform = draw.transform * transform;
                placed.validate()?;
                Command::OwnedShaderEffect(Box::new(placed))
            }
            #[cfg(feature = "gpu")]
            Command::ShaderEffect(_)
            | Command::ManagedShaderEffect(_)
            | Command::ResolvedAxis(_)
            | Command::HintedGlyphPaint(_)
            | Command::Glyph(_) => {
                // All four carry an already-selected target/atlas generation.
                // Owned source scopes above prepare in this actual target; no
                // evaluated effect resampling or frozen-parent retargeting.
                return Err(Error::UnsupportedExecution);
            }
        };
        candidate.commands.push(placed);
    }
    Ok(candidate)
}

fn place_analytic(
    mut primitive: AnalyticPrimitive,
    transform: Matrix4,
) -> Result<AnalyticPrimitive, Error> {
    primitive.transform = compose(primitive.transform, transform)?;
    Ok(primitive)
}
fn place_path(path: &PathDraw, transform: Matrix4) -> Result<PathDraw, Error> {
    let mut placed = path.clone();
    placed.transform = compose(path.transform, transform)?;
    Ok(placed)
}
fn place_image(draw: &ImageDraw, transform: Matrix4) -> Result<ImageDraw, Error> {
    let mut placed = draw.clone();
    // Patch/quad UVs stay in their original source descriptor. Existing image
    // compilation composes each patch before this outer transform, and performs
    // optional pixel snapping only at the actual new target boundary.
    placed.transform = compose(draw.transform, transform)?;
    placed.validate()?;
    Ok(placed)
}
fn place_mask(mask: &Mask, transform: Matrix4) -> Result<Mask, Error> {
    mask.validate()?;
    let mut placed = mask.clone();
    match &mut placed {
        #[cfg(feature = "gpu")]
        Mask::RenderedTexture(_) | Mask::FrameAnalytic(_) => {
            return Err(Error::UnsupportedExecution);
        }
        Mask::Analytic(source) if source.policy == AnalyticMaskPolicy::AxisClipArea => {
            if !axis_aligned(transform) {
                return Err(Error::UnsupportedExecution);
            }
            return Mask::axis_clip_edges(place_edges(bounds_edges(source.bounds)?, transform)?);
        }
        Mask::AxisClipEdges(source) => {
            if !axis_aligned(transform) {
                return Err(Error::UnsupportedExecution);
            }
            return Mask::axis_clip_edges(place_edges(source.edges(), transform)?);
        }
        Mask::Source(source) => *source = source.placed(transform)?,
        Mask::Geometry(source) => source.transform = compose(source.transform, transform)?,
        Mask::Path(source) => source.transform = compose(source.transform, transform)?,
        Mask::Analytic(source) => source.transform = compose(source.transform, transform)?,
        Mask::Coverage(source) => source.transform = compose(source.transform, transform)?,
        Mask::Texture(source) => source.transform = compose(source.transform, transform)?,
    }
    // Original descriptors reach their established inverse/coverage producer;
    // completed inverse rows are never rounded, rebased or reconstructed here.
    placed.validate()?;
    Ok(placed)
}
fn compose(source: Affine, transform: Matrix4) -> Result<Affine, Error> {
    affine(Matrix4::from_affine(source) * transform)
}
pub(super) fn affine(matrix: Matrix4) -> Result<Affine, Error> {
    let m = matrix.0;
    if m.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    if m[0][2] != 0.0
        || m[0][3] != 0.0
        || m[1][2] != 0.0
        || m[1][3] != 0.0
        || m[2] != [0.0, 0.0, 1.0, 0.0]
        || m[3][2] != 0.0
        || m[3][3] != 1.0
    {
        return Err(Error::UnsupportedExecution);
    }
    Ok(Affine([
        m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1],
    ]))
}
fn bounds_edges(bounds: [f32; 4]) -> Result<[f32; 4], Error> {
    let [x, y, width, height] = bounds;
    if width < 0.0 || height < 0.0 || bounds.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    let edges = [x, y, x + width, y + height];
    validate_edges(edges)?;
    Ok(edges)
}
pub(super) fn validate_edges(edges: [f32; 4]) -> Result<(), Error> {
    if edges.iter().any(|v| !v.is_finite()) || edges[0] > edges[2] || edges[1] > edges[3] {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
fn place_edges(edges: [f32; 4], transform: Matrix4) -> Result<[f32; 4], Error> {
    validate_edges(edges)?;
    // Original implicit/cache frames use the exact positive-axis endpoint
    // route. Independent filter frames can additionally reflect or swap axes.
    if !axis_aligned(transform) {
        return Err(Error::UnsupportedExecution);
    }
    if transform.0[0][0] <= 0.0 || transform.0[1][1] <= 0.0 {
        return storage_edges(edges, transform);
    }
    let [left, top] = transform.transform_point([edges[0], edges[1]]);
    let [right, bottom] = transform.transform_point([edges[2], edges[3]]);
    let result = [left, top, right, bottom];
    validate_edges(result)?;
    Ok(result)
}
pub(super) fn place_quad(
    vertices: &mut [VectorVertex; 4],
    transform: Matrix4,
) -> Result<(), Error> {
    let xs = V::from(vertices.map(|v| v.position[0]));
    let ys = V::from(vertices.map(|v| v.position[1]));
    let x = (xs * V::splat(transform.0[0][0])
        + ys * V::splat(transform.0[1][0])
        + V::splat(transform.0[3][0]))
    .to_array();
    let y = (xs * V::splat(transform.0[0][1])
        + ys * V::splat(transform.0[1][1])
        + V::splat(transform.0[3][1]))
    .to_array();
    if x.iter().chain(&y).any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    for i in 0..4 {
        vertices[i].position = [x[i], y[i]];
    }
    Ok(())
}

fn place_pen(
    pen: &Arc<crate::vector::Pen>,
    transform: Matrix4,
    inverse: &mut Option<Matrix4>,
) -> Result<Arc<crate::vector::Pen>, Error> {
    if transform == Matrix4::IDENTITY
        || matches!(pen.brush.kind, BrushKind::Solid(_) | BrushKind::Tile(_))
    {
        return Ok(pen.clone());
    }
    let inverse = if let Some(value) = *inverse {
        value
    } else {
        let value = inverse_placement(transform)?;
        *inverse = Some(value);
        value
    };
    let mut brush = pen.brush.as_ref().clone();
    let matrix = match &mut brush.kind {
        BrushKind::Linear(v) => &mut v.options.transform,
        BrushKind::Radial(v) => &mut v.options.transform,
        BrushKind::Path(v) => &mut v.options.transform,
        BrushKind::TwoPointConical(v) => &mut v.options.transform,
        BrushKind::Sweep(v) => &mut v.options.transform,
        BrushKind::PerlinNoise(v) => &mut v.transform,
        BrushKind::Hatch(v) | BrushKind::CrossHatch(v) => &mut v.transform,
        BrushKind::HatchSet(v) => &mut v.transform,
        BrushKind::Solid(_) | BrushKind::Tile(_) => return Ok(pen.clone()),
    };
    // This Scene already owns its source target brush frame. Apply the new
    // parent inverse only, retaining original local-to-source paint semantics.
    *matrix = inverse * *matrix;
    let mut result = pen.as_ref().clone();
    result.brush = Arc::new(brush);
    Ok(Arc::new(result))
}

fn inverse_placement(matrix: Matrix4) -> Result<Matrix4, Error> {
    // Complete float cofactor/determinant route, not reciprocal axis shortcuts.
    // SourceRenderFrame's positive-axis input keeps singular/perspective frames
    // outside this admission; inversion never replaces source transform history.
    let m = matrix.0;
    let mut cofactors = [[0.0; 4]; 4];
    for (row, output) in cofactors.iter_mut().enumerate() {
        let minors: [[f32; 9]; 4] = std::array::from_fn(|column| {
            let mut values = [0.0; 9];
            let mut index = 0;
            for (r, values_row) in m.iter().enumerate() {
                if r == row {
                    continue;
                }
                for (c, value) in values_row.iter().enumerate() {
                    if c != column {
                        values[index] = *value;
                        index += 1;
                    }
                }
            }
            values
        });
        let lane = |i| V::from(minors.map(|v| v[i]));
        let values = lane(0) * (lane(4) * lane(8) - lane(5) * lane(7))
            - lane(1) * (lane(3) * lane(8) - lane(5) * lane(6))
            + lane(2) * (lane(3) * lane(7) - lane(4) * lane(6));
        *output = (values
            * V::from(std::array::from_fn(|c| {
                if (row + c) % 2 == 0 { 1.0 } else { -1.0 }
            })))
        .to_array();
    }
    let terms = (V::from(m[0]) * V::from(cofactors[0])).to_array();
    let determinant = terms[0] + terms[1] + terms[2] + terms[3];
    if !determinant.is_finite() || determinant.abs() < f32::from_bits(1) {
        return Err(Error::InvalidInput);
    }
    let reciprocal = 1.0 / determinant;
    let result = Matrix4(std::array::from_fn(|r| {
        (V::from(std::array::from_fn(|c| cofactors[c][r])) * V::splat(reciprocal)).to_array()
    }));
    if result.0.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    Ok(result)
}

#[cfg(feature = "gpu")]
impl Scene {
    /// Inspect actual retained resource owners before an empty source shortcut.
    /// Refresh live cached sources before inspecting their exact owned recording.
    /// No texture upload, font execution or GPU allocation occurs here.
    pub(crate) fn preflight_source_capture_devices(
        &self,
        device: &wgpu::Device,
    ) -> Result<(), Error> {
        let _guard = SourcePreflightDepth::enter()?;
        preflight_devices(self, device, 0, 0)
    }
}
#[cfg(feature = "gpu")]
std::thread_local! { static SOURCE_PREFLIGHT_DEPTH: std::cell::Cell<u16> = const { std::cell::Cell::new(0) }; }
#[cfg(feature = "gpu")]
struct SourcePreflightDepth;
#[cfg(feature = "gpu")]
impl SourcePreflightDepth {
    fn enter() -> Result<Self, Error> {
        SOURCE_PREFLIGHT_DEPTH.with(|depth| {
            if depth.get() >= 256 {
                return Err(Error::Capacity);
            }
            depth.set(depth.get() + 1);
            Ok(Self)
        })
    }
}
#[cfg(feature = "gpu")]
impl Drop for SourcePreflightDepth {
    fn drop(&mut self) {
        SOURCE_PREFLIGHT_DEPTH.with(|depth| depth.set(depth.get() - 1));
    }
}
#[cfg(feature = "gpu")]
fn preflight_devices(
    source: &Scene,
    device: &wgpu::Device,
    depth: u8,
    owned_depth: u16,
) -> Result<(), Error> {
    use crate::image_effect::ImageEffectSource;
    if depth > 16 || owned_depth >= 256 {
        return Err(Error::Capacity);
    }
    if source.scope_depth != 0 {
        return Err(Error::InvalidInput);
    }
    if source.inherited_clip.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    let maximum = device.limits().max_texture_dimension_2d;
    let extent = |size: [u32; 2]| {
        if size.contains(&0) {
            Err(Error::InvalidInput)
        } else if size.iter().any(|v| *v > maximum) {
            Err(Error::Capacity)
        } else {
            Ok(())
        }
    };
    let image = |source: &ImageSource| {
        extent(source.size())?;
        if let ImageSource::Gpu(gpu) = source
            && gpu.device() != device
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    };
    for command in &source.commands {
        let (command, camera) = late_camera::split(command);
        if let Some(camera) = camera {
            late_camera::validate(command, camera)?;
        }
        match command {
            Command::Layer(layer) => {
                preflight_devices(&layer.scene, device, depth + 1, owned_depth)?
            }
            Command::AxisClip(axis) => {
                preflight_devices(axis.scene(), device, depth + 1, owned_depth)?
            }
            Command::PushMask(mask) | Command::PushMaskDifference(mask) => match mask.as_ref() {
                Mask::Source(mask) => {
                    preflight_devices(mask.scene(), device, depth + 1, owned_depth)?
                }
                Mask::Coverage(mask) => extent(mask.image.size())?,
                Mask::Texture(mask) => image(&mask.source)?,
                Mask::RenderedTexture(_) | Mask::FrameAnalytic(_) => {
                    return Err(Error::UnsupportedExecution);
                }
                Mask::Analytic(_) | Mask::AxisClipEdges(_) | Mask::Path(_) | Mask::Geometry(_) => {}
            },
            Command::Image(draw, _, _, operation) => {
                image(&draw.image)?;
                if let Some(crate::composition::RasterOperation {
                    pattern: crate::composition::RasterPattern::Texture { image: pattern, .. },
                    ..
                }) = operation
                {
                    image(pattern)?;
                }
            }
            Command::CompileOnly(..) | Command::LateCamera(..) => {
                return Err(Error::UnsupportedExecution);
            }
            Command::ArchiveGeometry(draw) => draw.validate()?,
            Command::ArchiveVector(draw) => draw.validate()?,
            Command::ArchiveHatch(draw) => draw.validate()?,
            Command::ArchiveThreeD(draw) => draw.validate()?,
            Command::NativeDesignVector(draw) => {
                draw.validate()
                    .map_err(native_design::native_design_error)?;
            }
            Command::CanvasTextPath(draw) => text::validate_context(draw.context)?,
            Command::NativeDesignLease(_) | Command::NativeAtlasLease(_) => {}
            Command::NativeDesignAtlas(draw) => draw.validate()?,
            Command::HintedText(record) => {
                record.0.validate(record.1)?;
                if let Some(brush) = &record.0.image_brush {
                    image(&brush.draw.image)?;
                }
            }
            Command::ImageColor(draw) => image(&draw.draw.image)?,
            Command::ColorFilteredImage(draw) => {
                image(&draw.draw.image)?;
                draw.filter.preflight_source_devices(device)?;
            }
            Command::ImageFilteredImage(draw) => draw.preflight_source_devices(device)?,
            Command::TileImage(draw) | Command::PlacedTileImage(draw, _) => image(&draw.image)?,
            Command::BackdropMaterial(draw) => draw.preflight_device(device)?,
            Command::Mesh3D(draw) => {
                draw.validate()?;
                if !draw.is_empty() {
                    draw.request.resource_cost(device)?;
                }
            }
            Command::CustomGrid(command) => super::custom_grid::validate(command)?,
            Command::Series(draw) => {
                if let crate::series::SeriesSource::Gpu(buffer) = &draw.source {
                    if !buffer.is_owned_by(device) {
                        return Err(Error::ResourceDomain);
                    }
                }
            }
            Command::ImageEffect(draw) => {
                match &draw.source {
                    ImageEffectSource::Rgb(source) => image(source)?,
                    ImageEffectSource::Gpu(source) => {
                        if source.device() != device {
                            return Err(Error::ResourceDomain);
                        }
                        extent(source.size())?;
                    }
                }
                if let Some(mask) = &draw.mask {
                    image(mask)?;
                }
            }
            Command::CachedPicture(draw) => {
                draw.validate()?;
                if !draw.picture.is_closed() {
                    let generation = draw.picture.freeze()?;
                    preflight_devices(&generation.snapshot().scene, device, 0, owned_depth + 1)?;
                }
            }
            Command::OwnedShaderEffect(draw) => {
                draw.validate()?;
                preflight_devices(draw.recording.scene(), device, 0, owned_depth + 1)?;
            }
            Command::ShaderEffect(_)
            | Command::ManagedShaderEffect(_)
            | Command::ResolvedAxis(_)
            | Command::HintedGlyphPaint(_)
            | Command::Glyph(_) => return Err(Error::UnsupportedExecution),
            _ => {}
        }
    }
    Ok(())
}

fn axis_aligned(transform: Matrix4) -> bool {
    let m = transform.0;
    (m[0][1] == 0.0 && m[1][0] == 0.0) || (m[0][0] == 0.0 && m[1][1] == 0.0)
}
fn storage_edges(edges: [f32; 4], transform: Matrix4) -> Result<[f32; 4], Error> {
    validate_edges(edges)?;
    if transform.0[0][1] == 0.0
        && transform.0[1][0] == 0.0
        && transform.0[0][0] > 0.0
        && transform.0[1][1] > 0.0
    {
        return place_edges(edges, transform);
    }
    let [l, t, r, b] = edges;
    let corners = [[l, t], [r, t], [r, b], [l, b]].map(|p| transform.transform_point(p));
    if corners.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    let first = corners[0];
    let mut result = [first[0], first[1], first[0], first[1]];
    for point in &corners[1..] {
        result = [
            result[0].min(point[0]),
            result[1].min(point[1]),
            result[2].max(point[0]),
            result[3].max(point[1]),
        ];
    }
    Ok(result)
}
fn clip_command(edges: [f32; 4], transform: Matrix4) -> Result<Command, Error> {
    validate_edges(edges)?;
    affine(transform)?;
    if axis_aligned(transform) {
        return Ok(Command::PushClipEdges(place_edges(edges, transform)?));
    }
    if edges[0] == edges[2] || edges[1] == edges[3] {
        return Ok(Command::PushClipEdges([0.0; 4]));
    }
    let [l, t, r, b] = edges;
    let points = [[l, t], [r, t], [r, b], [l, b]];
    let segments = (0..4)
        .map(|i| crate::path::Segment::line(points[i], points[(i + 1) % 4]))
        .collect::<Vec<_>>();
    let path = crate::path::Path::new(segments, edges, crate::path::FillRule::NonZero)?;
    let mask = Mask::Path(crate::mask::ClipPath {
        path,
        transform: affine(transform)?,
        sample_grid: 1,
        operation: crate::mask::MaskOperation::Intersect,
    });
    mask.validate()?;
    Ok(Command::PushMask(Box::new(mask)))
}
impl Scene {
    /// Original filter Picture/Crop PushClip: preserve all transformed rectangle
    /// edges. Non-axis geometry takes the actual binary source path-mask route.
    pub(crate) fn push_image_filter_clip(
        &mut self,
        edges: [f32; 4],
        transform: Matrix4,
    ) -> Result<(), Error> {
        let command = clip_command(edges, transform)?;
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(command);
        self.scope_depth = depth;
        self.has_scopes = true;
        Ok(())
    }
}

// Original active-opacity stack affects source draws rather than isolating each
// visual. A retained explicit ordinary layer owns its own composite boundary.
fn apply_opacity(scene: &mut Scene, opacity: f32) -> Result<(), Error> {
    for command in &mut scene.commands {
        let command = if let Command::LateCamera(inner, camera) = command {
            late_camera::validate(inner, *camera)?;
            inner.as_mut()
        } else {
            command
        };
        match command {
            #[cfg(feature = "gpu")]
            Command::BackdropMaterial(v) => v.opacity *= opacity,
            Command::CompileOnly(..) | Command::LateCamera(..) => {
                return Err(Error::UnsupportedExecution);
            }
            Command::ArchiveGeometry(v) => *v = Arc::new(v.with_opacity(opacity)?),
            Command::ArchiveVector(v) => *v = Arc::new(v.with_opacity(opacity)?),
            Command::ArchiveHatch(v) => *v = Arc::new(v.with_opacity(opacity)?),
            Command::ArchiveThreeD(v) => *v = Arc::new(v.with_opacity(opacity)?),
            Command::VertexMesh(v) => v.semantic_opacity *= opacity,
            Command::Analytic(v) => v.color[3] *= opacity,
            Command::Path(v) => v.color[3] *= opacity,
            Command::MaterialAnalytic(_, p)
            | Command::MaterialPath(_, p)
            | Command::TextPath(_, p, _) => p.semantic_opacity *= opacity,
            Command::Stroke(v) | Command::PlacedStroke(v, _) => v.semantic_opacity *= opacity,
            Command::Text(v) => v.1.semantic_opacity *= opacity,
            Command::CanvasTextPath(v) => {
                let mut draw = v.as_ref().clone();
                draw.context.semantic_opacity *= opacity;
                *v = Arc::new(draw);
            }
            Command::NativeDesignAtlas(v) => {
                let mut frame = v.frame();
                frame.opacity *= opacity;
                *v = Arc::new(v.with_frame(frame)?);
            }
            Command::NativeDesignVector(v) => {
                let mut context = v.context();
                context.semantic_opacity *= opacity;
                *v = Arc::new(
                    v.with_context(context)
                        .map_err(native_design::native_design_error)?,
                );
            }
            Command::HintedText(v) => {
                v.1.semantic_opacity *= opacity;
                v.0.validate(v.1)?;
            }
            Command::Image(v, _, _, _) => v.opacity *= opacity,
            Command::ImageColor(v) => v.draw.opacity *= opacity,
            Command::ColorFilteredImage(v) => v.draw.opacity *= opacity,
            Command::ImageFilteredImage(v) => v.draw.opacity *= opacity,
            Command::CustomGrid(_) => {}
            Command::Series(v) => *v = Arc::new(v.with_opacity(opacity)?),
            #[cfg(feature = "gpu")]
            Command::Mesh3D(v) => *v = Arc::new(v.with_opacity(opacity)?),
            #[cfg(feature = "gpu")]
            Command::Mesh3DCamera(_) => {}
            Command::ImageEffect(v) => {
                v.apply_source_opacity(opacity)?;
            }
            Command::TileImage(v) | Command::PlacedTileImage(v, _) => v.opacity *= opacity,
            Command::CachedPicture(v) => v.opacity *= opacity,
            #[cfg(feature = "gpu")]
            Command::OwnedShaderEffect(v) => v.opacity *= opacity,
            Command::Layer(v) => v.opacity *= opacity,
            Command::AxisClip(v) => {
                let mut child = v.scene().clone();
                apply_opacity(&mut child, opacity)?;
                **v = v.placed(Arc::new(child), v.bounds_edges());
            }
            #[cfg(feature = "gpu")]
            Command::ShaderEffect(_)
            | Command::ManagedShaderEffect(_)
            | Command::ResolvedAxis(_)
            | Command::Glyph(_)
            | Command::HintedGlyphPaint(_) => return Err(Error::UnsupportedExecution),
            Command::Empty
            | Command::NativeDesignLease(_)
            | Command::NativeAtlasLease(_)
            | Command::Clear(_)
            | Command::PushBlend(_)
            | Command::PopBlend
            | Command::PopScope
            | Command::PopCoverage { .. }
            | Command::PushClip(_)
            | Command::PushClipEdges(_)
            | Command::PushMask(_)
            | Command::PushMaskDifference(_) => {}
        }
    }
    Ok(())
}

impl Scene {
    /// Managed visual rectangle clips use the original near-axis test. Geometry
    /// clips retain their full transformed path and original antialias grid.
    pub(crate) fn push_visual_clip(
        &mut self,
        bounds: [f32; 4],
        transform: Matrix4,
    ) -> Result<(Option<[f32; 4]>, Option<Mask>), Error> {
        let edges = bounds_edges(bounds)?;
        affine(transform)?;
        let m = transform.0;
        if m[0][1].abs() <= 0.0001 && m[1][0].abs() <= 0.0001 {
            let edges = storage_edges(edges, transform)?;
            self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.scope_depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
            self.has_scopes = true;
            self.commands.push(Command::PushClipEdges(edges));
            return Ok((Some(edges), None));
        }
        if bounds[2] == 0.0 || bounds[3] == 0.0 {
            self.push_clip_rect([0.0; 4])?;
            return Ok((Some([0.0; 4]), None));
        }
        let [l, t, r, b] = edges;
        let points = [[l, t], [r, t], [r, b], [l, b]];
        let path = crate::path::Path::new(
            (0..4)
                .map(|i| crate::path::Segment::line(points[i], points[(i + 1) % 4]))
                .collect(),
            edges,
            crate::path::FillRule::NonZero,
        )?;
        let mut clip = crate::mask::ClipPath::new(path);
        clip.transform = affine(transform)?;
        let mask = Mask::Path(clip);
        self.push_mask(mask.clone())?;
        Ok((None, Some(mask)))
    }
    pub(crate) fn draw_visual_image(
        &mut self,
        draw: ImageDraw,
        blend: LayerBlend,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::Image(Box::new(draw), blend, None, None));
        Ok(())
    }
}

impl Scene {
    fn preflight_visual_camera(&self) -> Result<(), Error> {
        for command in &self.commands {
            match command {
                Command::Text(record) if record.1.use_mvp || record.1.gpu_transforms_active => {
                    return Err(Error::UnsupportedExecution);
                }
                Command::HintedText(record)
                    if record.1.use_mvp || record.1.gpu_transforms_active =>
                {
                    return Err(Error::UnsupportedExecution);
                }
                Command::Layer(layer) => layer.scene.preflight_visual_camera()?,
                Command::AxisClip(axis) => axis.scene().preflight_visual_camera()?,
                _ => {}
            }
        }
        Ok(())
    }
    // Live source recipes have their own revision/target protocols. A containing
    // visual cache must realize them again until that source identity is part of
    // its own cache key; immutable ordinary pages remain reusable.
    pub(crate) fn visual_page_cacheable(&self) -> bool {
        self.commands.iter().all(|command| match command {
            Command::CachedPicture(_) => false,
            Command::PushMask(mask) | Command::PushMaskDifference(mask) => match mask.as_ref() {
                Mask::Source(source) => source.scene().visual_page_cacheable(),
                _ => true,
            },
            #[cfg(feature = "gpu")]
            Command::OwnedShaderEffect(_) | Command::Mesh3D(_) => false,
            Command::Layer(layer) => layer.scene.visual_page_cacheable(),
            Command::AxisClip(axis) => axis.scene().visual_page_cacheable(),
            _ => true,
        })
    }
}

impl Scene {
    pub(crate) fn source_mask_depth(&self) -> Result<u8, Error> {
        if self.scope_depth != 0 || self.inherited_clip.is_some() {
            return Err(Error::InvalidInput);
        }
        let mut depth = 0u8;
        for command in &self.commands {
            let next = match command {
                Command::PushMask(mask) | Command::PushMaskDifference(mask) => {
                    match mask.as_ref() {
                        Mask::Source(source) => source.depth(),
                        _ => 0,
                    }
                }
                Command::Layer(layer) => layer.scene.source_mask_depth()?,
                Command::AxisClip(axis) => axis.scene().source_mask_depth()?,
                _ => 0,
            };
            depth = depth.max(next);
        }
        Ok(depth)
    }
}
