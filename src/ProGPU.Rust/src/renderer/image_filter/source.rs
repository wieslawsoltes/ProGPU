//! Original shader-source wrappers, tile captures and source-space brush policies.
use super::*;
use crate::{
    composition::SourceRenderFrame,
    image::{ColorFilterPlan, ImageSampling},
    image_filter::{
        FilterArithmetic, FilterSampling, FilterShaderSource as Shader, FilterTileMode,
    },
    scene::MaterialPaint,
    vector::{Brush, BrushKind, Matrix4},
};
use std::{future::Future, pin::Pin};

pub(super) fn preflight(
    renderer: &Renderer,
    shader: &Shader,
    transform: FilterTransform,
    size: [u32; 2],
    depth: usize,
) -> Result<(u64, u32), Error> {
    let mut count = 0;
    preflight_inner(
        renderer,
        shader,
        transform,
        size,
        Matrix4::IDENTITY,
        depth,
        &mut count,
    )
}
fn preflight_inner(
    renderer: &Renderer,
    shader: &Shader,
    transform: FilterTransform,
    size: [u32; 2],
    local: Matrix4,
    depth: usize,
    count: &mut usize,
) -> Result<(u64, u32), Error> {
    *count = count.checked_add(1).ok_or(Error::Capacity)?;
    if depth >= 32 || *count > 16_384 {
        return Err(Error::Capacity);
    }
    let mut cost = (0, 0);
    let bytes = u64::from(size[0]) * u64::from(size[1]) * 4;
    match shader {
        Shader::UnsupportedRuntime { .. } => return Err(Error::UnsupportedExecution),
        Shader::Empty => {}
        Shader::Brush(brush) => {
            let brush = placed_brush(brush, local, transform, depth == 1)?;
            let scene = brush_scene(brush, size)?;
            preflight::source_scene(renderer, &scene, size, depth, &mut cost)?;
        }
        Shader::LocalMatrix { source, matrix } => {
            return preflight_inner(
                renderer,
                source,
                transform,
                size,
                concat(local, *matrix)?,
                depth + 1,
                count,
            );
        }
        Shader::ColorFilter { source, filter } => {
            cost = preflight_inner(renderer, source, transform, size, local, depth + 1, count)?;
            let plan = ColorFilterPlan::new(filter, 16_384)?;
            let passes = plan.steps().iter().try_fold(0u32, |v, s| {
                v.checked_add(
                    if matches!(s.operation, ColorFilterOperation::Blend { .. }) {
                        2
                    } else {
                        1
                    },
                )
                .ok_or(Error::Capacity)
            })?;
            preflight::add_cost(
                &mut cost,
                (
                    bytes
                        .checked_mul(u64::from(passes))
                        .ok_or(Error::Capacity)?,
                    passes,
                ),
            )?;
        }
        Shader::Compose {
            background,
            foreground,
            arithmetic,
            ..
        } => {
            if let Some(p) = arithmetic {
                if p.coefficients.iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
            }
            cost = preflight_inner(
                renderer,
                background,
                transform,
                size,
                local,
                depth + 1,
                count,
            )?;
            preflight::add_cost(
                &mut cost,
                preflight_inner(
                    renderer,
                    foreground,
                    transform,
                    size,
                    local,
                    depth + 1,
                    count,
                )?,
            )?;
            preflight::compute(&renderer.device, size)?;
            preflight::add_cost(&mut cost, (bytes, 1))?;
        }
        Shader::Image {
            source,
            tile,
            sampling,
            matrix,
            ..
        } => {
            preflight::image_source(renderer, &source.image)?;
            preflight::sampling_record(*sampling)?;
            let local = concat(local, *matrix)?;
            tile_range(
                FilterRect::texture(size),
                FilterRect::texture(source.image.size()),
                local,
                *tile,
            )?;
            preflight::add_cost(
                &mut cost,
                (
                    u64::from(source.image.size()[0]) * u64::from(source.image.size()[1]) * 8,
                    2,
                ),
            )?;
        }
        Shader::Picture {
            source,
            matrix,
            tile,
            tile_modes,
            sampling,
        } => {
            if !matches!(sampling, ImageSampling::Nearest | ImageSampling::Linear) {
                return Err(Error::InvalidInput);
            }
            let local = concat(local, *matrix)?;
            if tile.width() > 0.0 && tile.height() > 0.0 {
                let tile_size = picture_tile_size(*tile, local)?;
                preflight::extent(&renderer.device, tile_size)?;
                let frame = picture_frame(*tile, tile_size)?;
                let scene = source.scene.place_source_capture(frame)?;
                preflight::source_scene(renderer, &scene, tile_size, depth, &mut cost)?;
                preflight::add_cost(
                    &mut cost,
                    (u64::from(tile_size[0]) * u64::from(tile_size[1]) * 4, 1),
                )?;
                tile_range(FilterRect::texture(size), *tile, local, *tile_modes)?;
            }
        }
    }
    preflight::add_cost(&mut cost, (bytes, 1))?;
    Ok(cost)
}

pub(super) async fn prepare(
    renderer: &Renderer,
    state: &mut ImageFilterState,
    images: &mut image::ImageState,
    shader: &Arc<Shader>,
    transform: FilterTransform,
    size: [u32; 2],
    sampling: ImageSamplingPath,
    metrics: &mut ImageFilterMetrics,
    passes: &mut Vec<Pass>,
) -> Result<Pass, Error> {
    prepare_inner(
        renderer,
        state,
        images,
        shader,
        transform,
        size,
        sampling,
        Matrix4::IDENTITY,
        true,
        false,
        0,
        metrics,
        passes,
    )
    .await
}
#[allow(clippy::too_many_arguments)]
fn prepare_inner<'a>(
    renderer: &'a Renderer,
    state: &'a mut ImageFilterState,
    images: &'a mut image::ImageState,
    shader: &'a Shader,
    transform: FilterTransform,
    size: [u32; 2],
    sampling: ImageSamplingPath,
    local: Matrix4,
    direct: bool,
    outer_filter: bool,
    depth: usize,
    metrics: &'a mut ImageFilterMetrics,
    passes: &'a mut Vec<Pass>,
) -> Pin<Box<dyn Future<Output = Result<Pass, Error>> + 'a>> {
    Box::pin(async move {
        if depth >= 32 {
            return Err(Error::Capacity);
        }
        match shader {
            Shader::UnsupportedRuntime { .. } => Err(Error::UnsupportedExecution),
            Shader::Empty => {
                scene_pass(renderer, Scene::default(), size, None, sampling, metrics).await
            }
            Shader::Brush(brush) => {
                scene_pass(
                    renderer,
                    brush_scene(placed_brush(brush, local, transform, direct)?, size)?,
                    size,
                    None,
                    sampling,
                    metrics,
                )
                .await
            }
            Shader::LocalMatrix { source, matrix } => {
                prepare_inner(
                    renderer,
                    state,
                    images,
                    source,
                    transform,
                    size,
                    sampling,
                    concat(local, *matrix)?,
                    false,
                    outer_filter,
                    depth + 1,
                    metrics,
                    passes,
                )
                .await
            }
            Shader::Compose {
                background,
                foreground,
                mode,
                arithmetic,
            } => {
                if !outer_filter
                    && !has_filter(background)
                    && !has_filter(foreground)
                    && arithmetic.is_none()
                    && *mode == LayerBlend::SrcOver
                {
                    if let Some(brush) = composed_conical(background, foreground, local)? {
                        return scene_pass(
                            renderer,
                            brush_scene(brush, size)?,
                            size,
                            None,
                            sampling,
                            metrics,
                        )
                        .await;
                    }
                }
                let background = prepare_inner(
                    renderer,
                    state,
                    images,
                    background,
                    transform,
                    size,
                    sampling,
                    local,
                    false,
                    false,
                    depth + 1,
                    metrics,
                    passes,
                )
                .await?;
                let b = background.output().clone();
                passes.push(background);
                let foreground = prepare_inner(
                    renderer,
                    state,
                    images,
                    foreground,
                    transform,
                    size,
                    sampling,
                    local,
                    false,
                    false,
                    depth + 1,
                    metrics,
                    passes,
                )
                .await?;
                let f = foreground.output().clone();
                passes.push(foreground);
                let p = if let Some(p) = arithmetic {
                    state
                        .color
                        .prepare_arithmetic(&b, &f, p.coefficients, p.enforce_premultiplied)
                        .await?
                } else {
                    state.color.prepare_blend(&b, &f, *mode, false).await?
                };
                metrics.compute(&p)?;
                let output = p.output().clone();
                passes.push(Pass::Color(p));
                let mut scene = Scene::default();
                scene.draw_image(ImageDraw::new(output, capture::full(size)))?;
                scene_pass(renderer, scene, size, None, sampling, metrics).await
            }
            Shader::ColorFilter { source, filter } => {
                let child = prepare_inner(
                    renderer,
                    state,
                    images,
                    source,
                    transform,
                    size,
                    sampling,
                    local,
                    false,
                    true,
                    depth + 1,
                    metrics,
                    passes,
                )
                .await?;
                let input = child.output().clone();
                passes.push(child);
                let output = color_graph(
                    renderer, state, images, filter, input, size, sampling, metrics, passes,
                )
                .await?;
                let mut scene = Scene::default();
                scene.draw_image(ImageDraw::new(output, capture::full(size)))?;
                scene_pass(renderer, scene, size, None, sampling, metrics).await
            }
            Shader::Image {
                source,
                tile,
                sampling: source_sampling,
                matrix,
                is_raw,
            } => {
                let mut input = source_image(
                    renderer,
                    state,
                    images,
                    &source.image,
                    sampling,
                    metrics,
                    passes,
                )
                .await?;
                if !*is_raw {
                    if let Some(color) = source.color_space.as_ref().filter(|c| !c.is_srgb()) {
                        input = transfer(state, input, color, metrics, passes).await?;
                    }
                }
                let rect = FilterRect::texture(source.image.size());
                let local = concat(local, *matrix)?;
                let range = tile_range(FilterRect::texture(size), rect, local, *tile)?;
                let mut scene = Scene::default();
                for y in range[1]..=range[3] {
                    for x in range[0]..=range[2] {
                        let matrix = FilterTransform::new(tile_placement(rect, [x, y], *tile))
                            .concatenate(local);
                        let mut draw =
                            ImageDraw::new(input.clone(), capture::full(source.image.size()));
                        draw.transform = capture::xy(matrix);
                        capture::sample(&mut draw, *source_sampling);
                        scene.draw_image(draw)?;
                    }
                }
                scene_pass(renderer, scene, size, None, sampling, metrics).await
            }
            Shader::Picture {
                source,
                matrix,
                tile,
                tile_modes,
                sampling: picture_sampling,
            } => {
                if tile.width() <= 0.0 || tile.height() <= 0.0 {
                    return scene_pass(renderer, Scene::default(), size, None, sampling, metrics)
                        .await;
                }
                let local = concat(local, *matrix)?;
                let tile_size = picture_tile_size(*tile, local)?;
                let frame = picture_frame(*tile, tile_size)?;
                let p = capture::prepare(
                    renderer,
                    source.scene.clone(),
                    tile_size,
                    Some(frame),
                    sampling,
                    true,
                )
                .await?;
                p.add_metrics(metrics)?;
                let input = p.output().clone();
                passes.push(Pass::Scene(p));
                let range = tile_range(FilterRect::texture(size), *tile, local, *tile_modes)?;
                let mut scene = Scene::default();
                for y in range[1]..=range[3] {
                    for x in range[0]..=range[2] {
                        let matrix =
                            FilterTransform::new(tile_placement(*tile, [x, y], *tile_modes))
                                .concatenate(local);
                        let mut draw = ImageDraw::new(input.clone(), capture::rect(*tile));
                        draw.transform = capture::xy(matrix);
                        draw.sampling = *picture_sampling;
                        scene.draw_image(draw)?;
                    }
                }
                scene_pass(renderer, scene, size, None, sampling, metrics).await
            }
        }
    })
}
async fn scene_pass(
    renderer: &Renderer,
    scene: Scene,
    size: [u32; 2],
    frame: Option<SourceRenderFrame>,
    sampling: ImageSamplingPath,
    metrics: &mut ImageFilterMetrics,
) -> Result<Pass, Error> {
    let p = capture::prepare(renderer, Arc::new(scene), size, frame, sampling, true).await?;
    p.add_metrics(metrics)?;
    Ok(Pass::Scene(p))
}
async fn source_image(
    renderer: &Renderer,
    state: &mut ImageFilterState,
    images: &mut image::ImageState,
    source: &ImageSource,
    sampling: ImageSamplingPath,
    metrics: &mut ImageFilterMetrics,
    passes: &mut Vec<Pass>,
) -> Result<GpuImage, Error> {
    let input = match source {
        ImageSource::Gpu(i) => i.clone(),
        ImageSource::Pixels(i) => {
            let (image, bytes, mips) = images
                .upload(&renderer.device, &renderer.queue, i, false)
                .await?;
            metrics.source_upload_bytes = metrics
                .source_upload_bytes
                .checked_add(bytes)
                .ok_or(Error::Capacity)?;
            metrics.mip_passes = metrics
                .mip_passes
                .checked_add(mips)
                .ok_or(Error::Capacity)?;
            image
        }
    };
    if ImageFilteredImageDraw::needs_normalization(source) {
        let p = normalize(renderer, state, images, &input, sampling, metrics).await?;
        let output = p.output.clone();
        passes.push(Pass::Capture(p));
        Ok(output)
    } else {
        Ok(input)
    }
}
async fn transfer(
    state: &mut ImageFilterState,
    input: GpuImage,
    color: &crate::image_filter::ImageFilterColorSpace,
    metrics: &mut ImageFilterMetrics,
    passes: &mut Vec<Pass>,
) -> Result<GpuImage, Error> {
    let identity: [u8; 256] = std::array::from_fn(|i| i as u8);
    let table = color.to_srgb_table();
    let p = color_pass(
        state,
        ColorFilterOperation::Table(ColorTables::channels(&identity, table, table, table)?),
        &input,
        None,
        metrics,
    )
    .await?;
    let output = p.output().clone();
    passes.push(p);
    Ok(output)
}
#[allow(clippy::too_many_arguments)]
async fn color_graph(
    renderer: &Renderer,
    state: &mut ImageFilterState,
    images: &mut image::ImageState,
    filter: &crate::image::ColorFilter,
    input: GpuImage,
    size: [u32; 2],
    sampling: ImageSamplingPath,
    metrics: &mut ImageFilterMetrics,
    passes: &mut Vec<Pass>,
) -> Result<GpuImage, Error> {
    let plan = ColorFilterPlan::new(filter, 16_384)?;
    let mut results = vec![input];
    for step in plan.steps() {
        let first = results
            .get(step.inputs[0] as usize)
            .ok_or(Error::ResourceDomain)?
            .clone();
        let (operation, inputs) = match &step.operation {
            ColorFilterOperation::Matrix(m) => (Operation::ColorMatrix(*m), vec![first]),
            ColorFilterOperation::Table(t) => (Operation::ColorTable(t.clone()), vec![first]),
            ColorFilterOperation::Nonlinear(p) => (Operation::NonlinearColor(*p), vec![first]),
            ColorFilterOperation::Overdraw(p) => (Operation::Overdraw(*p), vec![first]),
            ColorFilterOperation::Blend { color, mode } => {
                let p = Box::pin(renderer.prepare_filter_operation(
                    state,
                    images,
                    &Operation::Solid(*color),
                    &[],
                    size,
                    sampling,
                    metrics,
                    passes,
                ))
                .await?;
                let solid = p.output().clone();
                passes.push(p);
                (Operation::Blend(*mode), vec![first, solid])
            }
            ColorFilterOperation::Lerp { weight } => (
                Operation::Arithmetic(FilterArithmetic {
                    coefficients: [0.0, *weight, 1.0 - *weight, 0.0],
                    enforce_premultiplied: true,
                }),
                vec![
                    first,
                    results
                        .get(step.inputs[1] as usize)
                        .ok_or(Error::ResourceDomain)?
                        .clone(),
                ],
            ),
        };
        let pass = Box::pin(renderer.prepare_filter_operation(
            state, images, &operation, &inputs, size, sampling, metrics, passes,
        ))
        .await?;
        results.push(pass.output().clone());
        passes.push(pass);
    }
    results
        .get(plan.output() as usize)
        .cloned()
        .ok_or(Error::ResourceDomain)
}
fn has_filter(shader: &Shader) -> bool {
    match shader {
        Shader::ColorFilter { .. } => true,
        Shader::LocalMatrix { source, .. } => has_filter(source),
        Shader::Compose {
            background,
            foreground,
            ..
        } => has_filter(background) || has_filter(foreground),
        _ => false,
    }
}
fn brush_scene(brush: Brush, size: [u32; 2]) -> Result<Scene, Error> {
    let mut scene = Scene::default();
    scene.draw_material_analytic(
        crate::geometry::AnalyticPrimitive::rectangle(capture::full(size), [1.0; 4]),
        MaterialPaint::new(Arc::new(brush)),
        [size[0] as f32 * 0.5, size[1] as f32 * 0.5],
    )?;
    Ok(scene)
}
fn placed_brush(
    brush: &Brush,
    local: Matrix4,
    filter: FilterTransform,
    direct: bool,
) -> Result<Brush, Error> {
    let mut brush = brush.clone();
    preflight::finite_matrix(local)?;
    if direct {
        if let BrushKind::PerlinNoise(noise) = &mut brush.kind {
            if let Some(inverse) = filter.inverse_effective() {
                noise.transform = FilterTransform::new(inverse).concatenate(noise.transform);
            }
            return Ok(brush);
        }
    }
    if matches!(brush.kind, BrushKind::Solid(_)) {
        return Ok(brush);
    }
    let Some(inverse) = FilterTransform::new(local).inverse_raw() else {
        return Ok(Brush::solid_rgba(0));
    };
    let target = match &mut brush.kind {
        BrushKind::Linear(v) => Some(&mut v.options.transform),
        BrushKind::Radial(v) => Some(&mut v.options.transform),
        BrushKind::TwoPointConical(v) => Some(&mut v.options.transform),
        BrushKind::Sweep(v) => Some(&mut v.options.transform),
        BrushKind::PerlinNoise(v) => Some(&mut v.transform),
        _ => None,
    };
    if let Some(target) = target {
        *target = FilterTransform::new(inverse).concatenate(*target);
    }
    Ok(brush)
}
fn to_brush(shader: &Shader) -> Result<Option<Brush>, Error> {
    match shader {
        Shader::Brush(b) => Ok(Some(b.as_ref().clone())),
        Shader::LocalMatrix { source, matrix } => to_brush(source)?
            .map(|b| placed_brush(&b, *matrix, FilterTransform::IDENTITY, false))
            .transpose(),
        _ => Ok(None),
    }
}
fn composed_conical(
    background: &Shader,
    foreground: &Shader,
    local: Matrix4,
) -> Result<Option<Brush>, Error> {
    let Some(destination) = to_brush(background)? else {
        return Ok(None);
    };
    let Some(mut source) = to_brush(foreground)? else {
        return Ok(None);
    };
    let BrushKind::Solid(mut destination_color) = destination.kind else {
        return Ok(None);
    };
    let BrushKind::TwoPointConical(conical) = &mut source.kind else {
        return Ok(None);
    };
    destination_color[3] *= destination.opacity.clamp(0.0, 1.0);
    for stop in &mut conical.stops {
        let mut c = stop.color;
        c[3] *= source.opacity.clamp(0.0, 1.0);
        stop.color = source_over(c, destination_color);
    }
    conical.outside_color = Some(destination_color);
    source.opacity = 1.0;
    placed_brush(&source, local, FilterTransform::IDENTITY, false).map(Some)
}
fn source_over(source: [f32; 4], destination: [f32; 4]) -> [f32; 4] {
    let a = source[3].clamp(0.0, 1.0);
    let b = destination[3].clamp(0.0, 1.0);
    let alpha = a + b * (1.0 - a);
    if alpha <= 0.0 {
        return [0.0; 4];
    }
    let rgb = ((wide::f32x4::from(source) * wide::f32x4::splat(a)
        + wide::f32x4::from(destination) * wide::f32x4::splat(b) * wide::f32x4::splat(1.0 - a))
        / wide::f32x4::splat(alpha))
    .to_array();
    [rgb[0], rgb[1], rgb[2], alpha]
}
/// Original SKMatrix.Concat affine double-product additions, mapped to row vectors.
fn concat(first: Matrix4, second: Matrix4) -> Result<Matrix4, Error> {
    if first == Matrix4::IDENTITY {
        return Ok(second);
    }
    if second == Matrix4::IDENTITY {
        return Ok(first);
    }
    for m in [first, second] {
        preflight::finite_matrix(m)?;
        let a = m.0;
        if a[0][2] != 0.0
            || a[0][3] != 0.0
            || a[1][2] != 0.0
            || a[1][3] != 0.0
            || a[2] != [0.0, 0.0, 1.0, 0.0]
            || a[3][2] != 0.0
            || a[3][3] != 1.0
        {
            return Err(Error::UnsupportedExecution);
        }
    }
    let a = first.0;
    let b = second.0;
    let first = wide::f64x4::from([a[0][0], a[0][1], a[0][0], a[0][1]].map(f64::from));
    let second = wide::f64x4::from([a[1][0], a[1][1], a[1][0], a[1][1]].map(f64::from));
    let basis = (first * wide::f64x4::from([b[0][0], b[0][0], b[1][0], b[1][0]].map(f64::from))
        + second * wide::f64x4::from([b[0][1], b[0][1], b[1][1], b[1][1]].map(f64::from)))
    .to_array()
    .map(|v| v as f32);
    let translation = (first * wide::f64x4::splat(f64::from(b[3][0]))
        + second * wide::f64x4::splat(f64::from(b[3][1])))
    .to_array()
    .map(|v| v as f32);
    Ok(Matrix4([
        [basis[0], basis[1], 0.0, 0.0],
        [basis[2], basis[3], 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [translation[0] + a[3][0], translation[1] + a[3][1], 0.0, 1.0],
    ]))
}
fn tile_range(
    target: FilterRect,
    tile: FilterRect,
    local: Matrix4,
    modes: [FilterTileMode; 2],
) -> Result<[i32; 4], Error> {
    preflight::finite_matrix(local)?;
    if !tile.is_valid() {
        return Err(Error::InvalidInput);
    }
    let bounds = FilterTransform::new(local)
        .inverse_effective()
        .map_or(target, |inverse| {
            FilterTransform::new(inverse).bounds(target)
        });
    let mut result = [0; 4];
    for axis in 0..2 {
        if matches!(modes[axis], FilterTileMode::Repeat | FilterTileMode::Mirror) {
            let width = if axis == 0 {
                tile.width()
            } else {
                tile.height()
            };
            let start = (bounds.0[axis] / width).floor();
            let end = (bounds.0[axis + 2] / width).floor();
            if !start.is_finite()
                || !end.is_finite()
                || f64::from(start) < f64::from(i32::MIN) + 1.0
                || f64::from(end) > f64::from(i32::MAX) - 1.0
            {
                return Err(Error::Capacity);
            }
            let mut start = start as i32 - 1;
            let mut end = end as i32 + 1;
            let count = i64::from(end) - i64::from(start) + 1;
            if count > 128 {
                let center = i64::from(start) + count / 2;
                start = i32::try_from(center - 64).map_err(|_| Error::Capacity)?;
                end = start.checked_add(127).ok_or(Error::Capacity)?;
            }
            result[axis] = start;
            result[axis + 2] = end;
        }
    }
    Ok(result)
}
fn tile_placement(tile: FilterRect, index: [i32; 2], modes: [FilterTileMode; 2]) -> Matrix4 {
    let mut m = Matrix4::IDENTITY;
    for axis in 0..2 {
        let width = if axis == 0 {
            tile.width()
        } else {
            tile.height()
        };
        let mirror = modes[axis] == FilterTileMode::Mirror && (index[axis] & 1) != 0;
        m.0[axis][axis] = if mirror { -1.0 } else { 1.0 };
        m.0[3][axis] = if mirror {
            tile.0[axis] + (index[axis] + 1) as f32 * width
        } else {
            index[axis] as f32 * width - tile.0[axis]
        };
    }
    m
}
fn picture_tile_size(tile: FilterRect, matrix: Matrix4) -> Result<[u32; 2], Error> {
    let scale = FilterTransform::new(matrix).axis_scales();
    let mut width = tile.width() * scale[0];
    let mut height = tile.height() * scale[1];
    let area = width * height;
    if area > 2048.0 * 2048.0 {
        let scale = (2048.0 * 2048.0 / area).sqrt();
        width *= scale;
        height *= scale;
    }
    if !width.is_finite() || !height.is_finite() {
        return Err(Error::InvalidInput);
    }
    Ok([width, height].map(|v| f64::from(v).ceil().clamp(1.0, 8192.0) as u32))
}
fn picture_frame(tile: FilterRect, size: [u32; 2]) -> Result<SourceRenderFrame, Error> {
    let mut m = Matrix4::IDENTITY;
    m.0[0][0] = size[0] as f32 / tile.width();
    m.0[1][1] = size[1] as f32 / tile.height();
    m.0[3][0] = -tile.0[0] * m.0[0][0];
    m.0[3][1] = -tile.0[1] * m.0[1][1];
    SourceRenderFrame::image_filter(size, m)
}
