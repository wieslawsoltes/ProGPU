//! Original full-source realization followed by one immutable indexed batch.
use super::*;
use crate::image::{ImageBatchSource, ImagePatchBatch, RasterExecution, RasterSampling};
pub(super) fn preflight(
    source: &ImageBatchSource,
    layout: &ImagePatchBatch,
    sampling: RasterSampling,
    matrix: CanvasMatrix,
    paint: Option<&Paint>,
    context: &ImageContext,
) -> Result<()> {
    matrix.affine()?;
    layout.validate(source.size(), sampling)?;
    if source
        .size()
        .iter()
        .any(|v| *v > context.device().limits().max_texture_dimension_2d)
    {
        return Err(Error::Capacity.into());
    }
    match source {
        ImageBatchSource::Image(image) => source_device(image, context)?,
        ImageBatchSource::Texture(image) => {
            if !image.context().same_context(context) && image.portable_pixels()?.is_none() {
                return Err(ImageResourceError::NotPortable);
            }
            if image.context().same_context(context) {
                let gpu = image.gpu_image();
                let region = image.texture_region();
                let needs_copy = region != [0, 0, gpu.size()[0], gpu.size()[1]]
                    || (sampling.cubic.is_none()
                        && (sampling.max_anisotropy > 0
                            || sampling.mipmap != crate::image::RasterMipmapMode::None));
                if needs_copy
                    && !gpu
                        .texture()
                        .usage()
                        .contains(wgpu::TextureUsages::COPY_SRC)
                {
                    return Err(ImageResourceError::TextureContract);
                }
            }
        }
        ImageBatchSource::Raster(source) => {
            // Admission is shared with the original bitmap texture producer;
            // actual copied bytes and context realization happen only in prepare.
            if !matches!(
                source.info().color_type,
                crate::image::PixelColorType::Rgba8888
                    | crate::image::PixelColorType::Bgra8888
                    | crate::image::PixelColorType::Rgb888x
                    | crate::image::PixelColorType::Rgb565
            ) {
                return Err(crate::image::RasterError::UnsupportedFormat.into());
            }
            source.pixmap()?;
        }
    }
    if let Some(paint) = paint {
        admit_output_paint(paint, context)?;
    }
    Ok(())
}
#[allow(clippy::too_many_arguments)]
pub(super) async fn prepare(
    scene: &mut Scene,
    source: &ImageBatchSource,
    layout: &ImagePatchBatch,
    sampling: RasterSampling,
    matrix: CanvasMatrix,
    paint: Option<&Paint>,
    canvas: [f32; 2],
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
) -> Result<()> {
    let mut draw = match source {
        ImageBatchSource::Image(image) => layout.image_draw(image.clone(), sampling)?,
        ImageBatchSource::Texture(image) => {
            texture_draw(image, layout, sampling, canvas, context, captures).await?
        }
        ImageBatchSource::Raster(source) => {
            let image = TextureImage::from_raster(context, source, RasterExecution::default())?;
            texture_draw(&image, layout, sampling, canvas, context, captures).await?
        }
    };
    draw.transform = matrix.affine()?;
    // The layout's fixed/atlas colors have already received original CPU color
    // filtering. The full source texture receives its own independent filter.
    prepare_image(scene, context, captures, draw, paint, canvas).await
}
async fn texture_draw(
    source: &TextureImage,
    layout: &ImagePatchBatch,
    sampling: RasterSampling,
    canvas: [f32; 2],
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
) -> Result<ImageDraw> {
    let (gpu, region) = source.for_context(context)?;
    let full = region == [0, 0, gpu.size()[0], gpu.size()[1]];
    let mipmapped = sampling.cubic.is_none()
        && (sampling.max_anisotropy > 0 || sampling.mipmap != crate::image::RasterMipmapMode::None);
    let gpu = if !full || mipmapped {
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
        source
            .to_texture_image(context, mipmapped)
            .await?
            .gpu_image()
    } else {
        gpu
    };
    let mut draw = layout.image_draw(gpu, sampling)?;
    if let Some(color) = source.info().color_space {
        let transfer = color.to_filter_color_space();
        if !transfer.is_srgb() {
            let identity = std::array::from_fn::<_, 256, _>(|i| i as u8);
            let table = transfer.to_srgb_table();
            let filter = ColorFilter::tables(&identity, table, table, table)?;
            draw = filter_draw_source(context, captures, draw, filter, canvas).await?;
        }
    }
    Ok(draw)
}
