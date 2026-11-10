use super::*;
use crate::image::{
    ColorFilter, ImageContext, ImageResourceError, PictureCaptureSession, TextureImage,
};
use crate::image_filter::{
    FilterPlanRequest, FilterTransform, ImageFilter, ImageFilterPlan, ImageFilteredImageDraw,
};
use std::{future::Future, pin::Pin};
mod image_api;
mod texture_batch;
type Result<T> = std::result::Result<T, ImageResourceError>;
impl CanvasPicture {
    /// Prepare all original source producers against one explicit context. Source
    /// ownership and descriptors are preflighted before the first GPU capture;
    /// immutable returned commands can replay without invoking source callbacks.
    pub async fn prepare_scene(&self, context: &ImageContext) -> Result<Scene> {
        self.prepare_with_backdrop(context, None).await
    }
    pub(crate) async fn prepare_with_backdrop(
        &self,
        context: &ImageContext,
        backdrop: Option<&TextureImage>,
    ) -> Result<Scene> {
        if context.is_abandoned() {
            return Err(ImageResourceError::Abandoned);
        }
        let mut count = 0;
        self.preflight(context, 0, &mut count)?;
        if let Some(backdrop) = backdrop {
            source_device(&ImageSource::Gpu(backdrop.gpu_image()), context)?;
        }
        let mut captures = PictureCaptureSession::new(context);
        self.prepare(context, backdrop, 0, &mut captures).await
    }
    pub(in crate::canvas) fn preflight(
        &self,
        context: &ImageContext,
        depth: u16,
        count: &mut usize,
    ) -> Result<()> {
        if depth >= 256 {
            return Err(Error::Capacity.into());
        }
        *count = count
            .checked_add(self.commands.len())
            .ok_or(Error::Capacity)?;
        if *count > 1_048_576 {
            return Err(Error::Capacity.into());
        }
        let canvas = extent(self.size, context)?;
        for command in self.commands.iter() {
            match command {
                Command::Clip(clip) => {
                    clip.matrix.affine()?;
                    let mut s = Scene::default();
                    push_clip(&mut s, clip, self.size)?;
                }
                Command::DeviceColor { blend, .. } => admit_blend(*blend)?,
                Command::Path { matrix, paint, .. } | Command::Analytic { matrix, paint, .. } => {
                    matrix.affine()?;
                    admit_paint(paint, context)?;
                    if let Some(mask) = &paint.mask_filter {
                        admit_filter(
                            &mask.image_filter(*matrix)?,
                            canvas,
                            self.size,
                            CanvasMatrix::IDENTITY,
                            context,
                        )?;
                    }
                    if let Some(shader) = paint.shader_source() {
                        admit_filter(
                            &ImageFilter::shader(Some(shader), paint.dither, None)?,
                            canvas,
                            self.size,
                            *matrix,
                            context,
                        )?;
                    }
                }
                Command::Image { draw, paint } => {
                    draw.validate()?;
                    source_device(&draw.image, context)?;
                    if let Some(paint) = paint {
                        admit_output_paint(paint, context)?;
                    }
                }
                Command::SampledImage(draw) => image_api::preflight(draw, context)?,
                Command::ImageBatch {
                    source,
                    layout,
                    sampling,
                    matrix,
                    paint,
                } => {
                    texture_batch::preflight(
                        source,
                        layout,
                        *sampling,
                        *matrix,
                        paint.as_deref(),
                        context,
                    )?;
                }
                Command::Texture {
                    image,
                    matrix,
                    paint,
                    ..
                } => {
                    matrix.affine()?;
                    if !image.context().same_context(context) && image.portable_pixels()?.is_none()
                    {
                        return Err(ImageResourceError::NotPortable);
                    }
                    if let Some(paint) = paint {
                        admit_output_paint(paint, context)?;
                    }
                }
                Command::Picture {
                    picture,
                    matrix,
                    blend,
                    ..
                } => {
                    matrix.affine()?;
                    admit_blend(*blend)?;
                    picture.preflight(context, depth + 1, count)?;
                }
                Command::Archive {
                    picture,
                    matrix,
                    opacity,
                    options,
                } => {
                    let scene = picture
                        .to_scene_with(options.placed(matrix.matrix4(), *opacity))
                        .map_err(crate::picture_archive::drawing_error)?;
                    scene.preflight_source_capture_devices(context.device())?;
                }
                Command::Leaf(scene) => scene.preflight_source_capture_devices(context.device())?,
                Command::Layer(layer) => {
                    layer.matrix.affine()?;
                    let size = layer_size(layer, self.size, context)?;
                    admit_capture(size, layer_format(layer))?;
                    if let Some(paint) = &layer.description.paint {
                        admit_output_paint(paint, context)?;
                        if let Some(filter) = &paint.image_filter {
                            admit_filter(filter, size, self.size, layer.matrix, context)?;
                        }
                    }
                    if let Some(filter) = &layer.description.backdrop {
                        admit_filter(filter, size, self.size, layer.matrix, context)?;
                    }
                    if let Some(prior) = &layer.prior {
                        prior.preflight(context, depth + 1, count)?;
                    }
                    layer.source.preflight(context, depth + 1, count)?;
                }
                Command::Pop => {}
            }
        }
        Ok(())
    }
    fn prepare<'a>(
        &'a self,
        context: &'a ImageContext,
        backdrop: Option<&'a TextureImage>,
        depth: u16,
        captures: &'a mut PictureCaptureSession,
    ) -> Pin<Box<dyn Future<Output = Result<Scene>> + 'a>> {
        Box::pin(async move {
            if depth >= 256 {
                return Err(Error::Capacity.into());
            }
            let mut scene = Scene::default();
            let mut clips: Vec<Clip> = Vec::new();
            for command in self.commands.iter() {
                match command {
                    Command::Clip(clip) => {
                        push_clip(&mut scene, clip, self.size)?;
                        clips.push(clip.clone());
                    }
                    Command::Pop => {
                        scene.pop_scope()?;
                        clips.pop().ok_or(Error::InvalidInput)?;
                    }
                    Command::Picture {
                        picture,
                        matrix,
                        opacity,
                        blend,
                    } => {
                        let child = picture
                            .prepare(context, backdrop, depth + 1, captures)
                            .await?
                            .place_visual_page(matrix.matrix4(), 1.0, false, *opacity, true)?;
                        scope(&mut scene, *blend, |scene| scene.append(&child))?;
                    }
                    Command::Layer(layer) => {
                        let size = layer_size(layer, self.size, context)?;
                        let format = layer_format(layer);
                        let mut source = Scene::default();
                        for clip in layer.clips.iter() {
                            push_clip(&mut source, clip, self.size)?;
                        }
                        let bounded = push_layer_bounds(&mut source, layer, self.size)?;
                        if let Some(prior) = &layer.prior {
                            let mut previous = Scene::default();
                            if let Some(backdrop) = backdrop {
                                let info = backdrop.info();
                                previous.draw_image(backdrop.draw([
                                    0.0,
                                    0.0,
                                    info.width as f32,
                                    info.height as f32,
                                ]))?;
                            }
                            previous.append(
                                &prior
                                    .prepare(context, backdrop, depth + 1, captures)
                                    .await?,
                            )?;
                            let mut initial =
                                capture_format(captures, previous, size, format).await?;
                            if let Some(filter) = &layer.description.backdrop {
                                initial = filtered(
                                    context,
                                    captures,
                                    initial,
                                    filter.clone(),
                                    self.size,
                                    layer.matrix,
                                )
                                .await?;
                            }
                            let info = initial.info();
                            source.draw_image(initial.draw([
                                0.0,
                                0.0,
                                info.width as f32,
                                info.height as f32,
                            ]))?;
                        }
                        source.append(
                            &layer
                                .source
                                .prepare(context, backdrop, depth + 1, captures)
                                .await?,
                        )?;
                        if bounded {
                            source.pop_scope()?;
                        }
                        for _ in layer.clips.iter() {
                            source.pop_scope()?;
                        }
                        let mut texture = capture_format(captures, source, size, format).await?;
                        if let Some(paint) = &layer.description.paint {
                            if let Some(filter) = &paint.image_filter {
                                texture = filtered(
                                    context,
                                    captures,
                                    texture,
                                    filter.clone(),
                                    self.size,
                                    layer.matrix,
                                )
                                .await?;
                            }
                            if let Some(filter) = &paint.color_filter {
                                texture = filtered(
                                    context,
                                    captures,
                                    texture,
                                    ImageFilter::color_filter(filter.clone(), None, None)?,
                                    self.size,
                                    CanvasMatrix::IDENTITY,
                                )
                                .await?;
                            }
                        }
                        let mut draw = texture.draw([
                            0.0,
                            0.0,
                            texture.info().width as f32,
                            texture.info().height as f32,
                        ]);
                        let blend = if let Some(paint) = &layer.description.paint {
                            draw.opacity = f32::from(paint.color[3]) / 255.0;
                            paint.canvas_blend()?
                        } else {
                            LayerBlend::SrcOver
                        };
                        let bounded = push_layer_bounds(&mut scene, layer, self.size)?;
                        scene.draw_visual_image(draw, blend)?;
                        if bounded {
                            scene.pop_scope()?;
                        }
                    }
                    Command::Texture {
                        image: source,
                        source: rect,
                        destination,
                        sampling,
                        matrix,
                        paint,
                    } => {
                        let (gpu, region) = source.for_context(context)?;
                        let full = region == [0, 0, gpu.size()[0], gpu.size()[1]];
                        let mipmapped = *sampling == ImageSampling::LinearMipmap;
                        let (gpu, region) = if !full || mipmapped {
                            let source = if source.context().same_context(context) {
                                source.clone()
                            } else {
                                TextureImage::from_texture(
                                    context,
                                    gpu,
                                    source.info(),
                                    crate::image::TextureOwnership::Borrowed,
                                    None,
                                )?
                            };
                            let normalized = source.to_texture_image(context, mipmapped).await?;
                            (normalized.gpu_image(), normalized.texture_region())
                        } else {
                            (gpu, region)
                        };
                        let mut draw = ImageDraw::new(gpu, *destination);
                        draw.source = [
                            region[0] as f32 + rect[0],
                            region[1] as f32 + rect[1],
                            rect[2],
                            rect[3],
                        ];
                        draw.transform = matrix.affine()?;
                        draw.sampling = *sampling;
                        draw.cubic = [1.0 / 3.0; 2];
                        draw.ignore_source_alpha =
                            source.info().alpha_type == crate::image::PixelAlphaType::Opaque;
                        if let Some(color) = source.info().color_space {
                            let transfer = color.to_filter_color_space();
                            if !transfer.is_srgb() {
                                let identity = std::array::from_fn::<_, 256, _>(|i| i as u8);
                                let table = transfer.to_srgb_table();
                                let filter = ColorFilter::tables(&identity, table, table, table)?;
                                draw =
                                    filter_draw_source(context, captures, draw, filter, self.size)
                                        .await?;
                            }
                        }
                        prepare_image(
                            &mut scene,
                            context,
                            captures,
                            draw,
                            paint.as_deref(),
                            self.size,
                        )
                        .await?;
                    }
                    Command::Image { draw, paint } => {
                        prepare_image(
                            &mut scene,
                            context,
                            captures,
                            draw.clone(),
                            paint.as_deref(),
                            self.size,
                        )
                        .await?
                    }
                    Command::SampledImage(draw) => {
                        image_api::prepare(&mut scene, draw, self.size, context, captures).await?;
                    }
                    Command::ImageBatch {
                        source,
                        layout,
                        sampling,
                        matrix,
                        paint,
                    } => {
                        texture_batch::prepare(
                            &mut scene,
                            source,
                            layout,
                            *sampling,
                            *matrix,
                            paint.as_deref(),
                            self.size,
                            context,
                            captures,
                        )
                        .await?;
                    }
                    Command::Path { paint, .. } | Command::Analytic { paint, .. }
                        if paint.mask_filter.is_some() || paint.fill_brush().is_err() =>
                    {
                        prepare_painted(&mut scene, context, captures, command, &clips, self.size)
                            .await?;
                    }
                    _ => ordinary(&mut scene, command, self.size)?,
                }
            }
            Ok(scene)
        })
    }
}
fn source_device(source: &ImageSource, context: &ImageContext) -> Result<()> {
    if let ImageSource::Gpu(image) = source {
        if image.device() != context.device() {
            return Err(Error::ResourceDomain.into());
        }
    }
    Ok(())
}
fn admit_blend(_blend: LayerBlend) -> Result<()> {
    // The typed enum retains all original compositor modes. Exact execution is
    // selected per draw family by Scene, not by a canvas-wide fixed-mode gate.
    Ok(())
}
fn admit_output_paint(paint: &Paint, context: &ImageContext) -> Result<()> {
    paint.canvas_blend()?;
    if let Some(filter) = &paint.color_filter {
        filter.preflight_source_devices(context.device())?;
        crate::image::ColorFilterPlan::new(filter, 16384)?;
    }
    Ok(())
}
fn admit_paint(paint: &Paint, context: &ImageContext) -> Result<()> {
    admit_blend(paint.canvas_blend()?)?;
    if let Some(filter) = &paint.color_filter {
        filter.preflight_source_devices(context.device())?;
    }
    if let Some(shader) = &paint.shader {
        shader.preflight_source_devices(context.device())?;
    }
    Ok(())
}
fn extent(size: [f32; 2], context: &ImageContext) -> Result<[u32; 2]> {
    let limit = context.device().limits().max_texture_dimension_2d;
    let mut result = [1; 2];
    for axis in 0..2 {
        if size[axis].is_finite() && size[axis] > 0.0 {
            let value = f64::from(size[axis]).ceil();
            if value > f64::from(limit) || value > f64::from(i32::MAX) {
                return Err(Error::Capacity.into());
            }
            result[axis] = value as u32;
        }
    }
    Ok(result)
}
fn layer_size(layer: &Layer, canvas: [f32; 2], context: &ImageContext) -> Result<[u32; 2]> {
    let b = layer.matrix.map_rect(layer.bounds);
    extent([canvas[0].max(b[2]), canvas[1].max(b[3])], context)
}
fn request(source: [u32; 2], canvas: [f32; 2], matrix: CanvasMatrix) -> FilterPlanRequest {
    let mut request = FilterPlanRequest::new(
        source,
        canvas.map(|v| v.ceil().max(1.0) as u32),
        FilterTransform::new(matrix.matrix4()),
    );
    request.canvas_logical_size = canvas;
    request
}
fn admit_filter(
    filter: &ImageFilter,
    source: [u32; 2],
    canvas: [f32; 2],
    matrix: CanvasMatrix,
    context: &ImageContext,
) -> Result<()> {
    filter.preflight_source_devices(context.device(), 16384)?;
    let plan = ImageFilterPlan::new(filter, request(source, canvas, matrix))?;
    if plan
        .steps()
        .iter()
        .any(|s| s.operation.is_explicitly_unsupported())
    {
        return Err(Error::UnsupportedExecution.into());
    }
    let limit = context.device().limits().max_texture_dimension_2d;
    if plan
        .steps()
        .iter()
        .any(|s| s.size.iter().any(|v| *v > limit))
    {
        return Err(Error::Capacity.into());
    }
    Ok(())
}
fn layer_format(layer: &Layer) -> wgpu::TextureFormat {
    if layer.description.flags.0 & SaveLayerFlags::F16_COLOR_TYPE.0 != 0 {
        wgpu::TextureFormat::Rgba16Float
    } else {
        wgpu::TextureFormat::Rgba8Unorm
    }
}
fn admit_capture(size: [u32; 2], format: wgpu::TextureFormat) -> Result<()> {
    let bytes = u64::from(size[0])
        .checked_mul(u64::from(size[1]))
        .and_then(|v| {
            v.checked_mul(if format == wgpu::TextureFormat::Rgba16Float {
                8
            } else {
                4
            })
        })
        .ok_or(Error::Capacity)?;
    if bytes > 256 * 1024 * 1024 {
        return Err(Error::Capacity.into());
    }
    Ok(())
}
async fn capture_format(
    captures: &mut PictureCaptureSession,
    scene: Scene,
    size: [u32; 2],
    format: wgpu::TextureFormat,
) -> Result<TextureImage> {
    admit_capture(size, format)?;
    captures
        .capture(Arc::new(scene), size, Matrix4::IDENTITY, format)
        .await
}
async fn capture(
    captures: &mut PictureCaptureSession,
    scene: Scene,
    size: [u32; 2],
) -> Result<TextureImage> {
    capture_format(captures, scene, size, wgpu::TextureFormat::Rgba8Unorm).await
}
async fn filtered(
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
    source: TextureImage,
    filter: ImageFilter,
    canvas: [f32; 2],
    matrix: CanvasMatrix,
) -> Result<TextureImage> {
    if !captures.context().same_context(context) {
        return Err(Error::ResourceDomain.into());
    }
    let size = [source.info().width as u32, source.info().height as u32];
    let mut filtered = ImageFilteredImageDraw::new(
        source.draw([0.0, 0.0, size[0] as f32, size[1] as f32]),
        filter,
    );
    let mips = filtered.request.source_mip_levels;
    filtered.request = request(size, canvas, matrix);
    filtered.request.source_mip_levels = mips;
    let plan = filtered.plan()?;
    let output = plan.output_size();
    let format = plan.output_texture_format(source.gpu_image().texture().format())?;
    filtered.draw.source = [0.0, 0.0, output[0] as f32, output[1] as f32];
    filtered.draw.destination = filtered.draw.source;
    filtered.draw.extended_source = true;
    let mut scene = Scene::default();
    scene.draw_image_filtered_image(filtered)?;
    capture_format(captures, scene, output, format).await
}
async fn filter_draw_source(
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
    mut draw: ImageDraw,
    filter: ColorFilter,
    canvas: [f32; 2],
) -> Result<ImageDraw> {
    // Filter the full actual texture before the caller's selected source view.
    let size = draw.image.size();
    let mut source = Scene::default();
    source.draw_image(ImageDraw::new(
        draw.image.clone(),
        [0.0, 0.0, size[0] as f32, size[1] as f32],
    ))?;
    let format = match &draw.image {
        ImageSource::Gpu(image) if image.texture().format() == wgpu::TextureFormat::Rgba16Float => {
            wgpu::TextureFormat::Rgba16Float
        }
        _ => wgpu::TextureFormat::Rgba8Unorm,
    };
    let texture = capture_format(captures, source, size, format).await?;
    let texture = filtered(
        context,
        captures,
        texture,
        ImageFilter::color_filter(filter, None, None)?,
        canvas,
        CanvasMatrix::IDENTITY,
    )
    .await?;
    draw.image = texture.gpu_image().into();
    Ok(draw)
}
async fn prepare_image(
    scene: &mut Scene,
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
    mut draw: ImageDraw,
    paint: Option<&Paint>,
    canvas: [f32; 2],
) -> Result<()> {
    if let Some(filter) = paint.and_then(|p| p.color_filter.as_ref()) {
        draw = filter_draw_source(context, captures, draw, filter.clone(), canvas).await?;
    }
    image(scene, draw, paint)?;
    Ok(())
}
fn push_layer_bounds(
    scene: &mut Scene,
    layer: &Layer,
    canvas: [f32; 2],
) -> std::result::Result<bool, Error> {
    let b = layer.bounds;
    let full = b[0].abs() <= 0.0001
        && b[1].abs() <= 0.0001
        && (b[2] - canvas[0]).abs() <= 0.0001
        && (b[3] - canvas[1]).abs() <= 0.0001;
    if !full {
        scene.push_image_filter_clip(b, layer.matrix.matrix4())?;
    }
    Ok(!full)
}
async fn prepare_painted(
    scene: &mut Scene,
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
    command: &Command,
    clips: &[Clip],
    canvas: [f32; 2],
) -> Result<()> {
    let (geometry, paint, matrix, inverse, analytic, text) = match command {
        Command::Path {
            geometry,
            paint,
            matrix,
            inverse,
            text,
        } => {
            let (g, p, i) = effect(geometry, paint, *matrix, *inverse)?;
            (g, p, *matrix, i, None, *text)
        }
        Command::Analytic {
            geometry,
            paint,
            matrix,
            shape,
            bounds,
            center,
            radius,
        } => (
            geometry.clone(),
            (**paint).clone(),
            *matrix,
            false,
            Some((*shape, *bounds, *radius, *center)),
            None,
        ),
        _ => return Err(Error::InvalidInput.into()),
    };
    let blend = paint.canvas_blend()?;
    let mut plain = paint.clone();
    plain.mask_filter = None;
    plain.set_blend_mode(LayerBlend::SrcOver);
    let size = extent(canvas, context)?;
    let mut source = Scene::default();
    if paint.mask_filter.is_some() {
        for clip in clips {
            push_clip(&mut source, clip, canvas)?;
        }
    }
    if plain.fill_brush().is_ok() {
        if let Some((shape, bounds, radius, center)) = analytic {
            ordinary(
                &mut source,
                &Command::Analytic {
                    geometry: geometry.clone(),
                    shape,
                    bounds,
                    center,
                    radius,
                    matrix,
                    paint: plain.snapshot(),
                },
                canvas,
            )?;
        } else {
            path(
                &mut source,
                geometry.clone(),
                &plain,
                matrix,
                inverse,
                canvas,
                text,
            )?;
        }
    } else {
        let shader = plain.shader_source().ok_or(Error::UnsupportedExecution)?;
        let empty = capture(captures, Scene::default(), size).await?;
        let shader = filtered(
            context,
            captures,
            empty,
            ImageFilter::shader(Some(shader), plain.dither, None)?,
            canvas,
            matrix,
        )
        .await?;
        // Fill and stroke remain independent painter operations; overlapping
        // antialiased coverage is not collapsed into a single combined mask.
        for style in [PaintStyle::Fill, PaintStyle::Stroke] {
            if (style == PaintStyle::Fill && plain.style == PaintStyle::Stroke)
                || (style == PaintStyle::Stroke && plain.style == PaintStyle::Fill)
            {
                continue;
            }
            let mut mask_paint = plain.clone();
            mask_paint.shader = None;
            mask_paint.color_filter = None;
            mask_paint.color = [255; 4];
            mask_paint.style = style;
            let mut mask = Scene::default();
            path(
                &mut mask,
                geometry.clone(),
                &mask_paint,
                matrix,
                inverse,
                canvas,
                None,
            )?;
            source.push_mask(Mask::Source(crate::mask::SourceMask::new(
                Arc::new(mask),
                Matrix4::IDENTITY,
            )?))?;
            let mut draw = shader.draw([
                0.0,
                0.0,
                shader.info().width as f32,
                shader.info().height as f32,
            ]);
            draw.opacity = f32::from(plain.color[3]) / 255.0;
            source.draw_image(draw)?;
            source.pop_scope()?;
        }
    }
    if paint.mask_filter.is_some() {
        for _ in clips {
            source.pop_scope()?;
        }
    }
    if let Some(mask) = &paint.mask_filter {
        let input = capture(captures, source, size).await?;
        let output = filtered(
            context,
            captures,
            input,
            mask.image_filter(matrix)?,
            canvas,
            CanvasMatrix::IDENTITY,
        )
        .await?;
        scene.draw_visual_image(
            output.draw([
                0.0,
                0.0,
                output.info().width as f32,
                output.info().height as f32,
            ]),
            blend,
        )?;
    } else {
        scope(scene, blend, |scene| scene.append(&source))?;
    }
    Ok(())
}
