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
            Command::Empty => Command::Empty,
            // Target-storage Clear is deliberately independent of the source
            // matrix. Its actual inherited binary clip is placed separately.
            Command::Clear(color) => Command::Clear(*color),
            Command::PopScope => Command::PopScope,
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
            Command::ImageEffect(draw) => {
                // These are the original explicitly supplied texture quad
                // positions, never already-rasterized vector/path vertices.
                let mut placed = draw.as_ref().clone();
                place_quad(&mut placed.vertices, transform)?;
                placed.validate()?;
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
    /// No texture upload, font execution, cache mutation or provider callback is
    /// needed to prove the creating device and complete source dimensions.
    pub(crate) fn preflight_source_capture_devices(
        &self,
        device: &wgpu::Device,
    ) -> Result<(), Error> {
        preflight_devices(self, device, 0, 0)
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
        match command {
            Command::Layer(layer) => {
                preflight_devices(&layer.scene, device, depth + 1, owned_depth)?
            }
            Command::AxisClip(axis) => {
                preflight_devices(axis.scene(), device, depth + 1, owned_depth)?
            }
            Command::PushMask(mask) | Command::PushMaskDifference(mask) => match mask.as_ref() {
                Mask::Coverage(mask) => extent(mask.image.size())?,
                Mask::Texture(mask) => image(&mask.source)?,
                Mask::Analytic(_) | Mask::AxisClipEdges(_) | Mask::Path(_) => {}
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
            Command::ImageColor(draw) => image(&draw.draw.image)?,
            Command::ColorFilteredImage(draw) => image(&draw.draw.image)?,
            Command::ImageFilteredImage(draw) => draw.preflight_source_devices(device)?,
            Command::TileImage(draw) | Command::PlacedTileImage(draw, _) => image(&draw.image)?,
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
            Command::OwnedShaderEffect(draw) => {
                draw.validate()?;
                preflight_devices(draw.recording.scene(), device, 0, owned_depth + 1)?;
            }
            Command::ShaderEffect(_)
            | Command::ManagedShaderEffect(_)
            | Command::ResolvedAxis(_)
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
