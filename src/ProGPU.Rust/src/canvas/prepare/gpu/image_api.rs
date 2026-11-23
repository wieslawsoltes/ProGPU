//! Ordinary sampled-image realization keeps the original source view and AA path.
use super::*;
use crate::{
    canvas::image_api::{SampledImage, admit_bitmap_format},
    image::{ImageBatchSource, RasterExecution, RasterMipmapMode},
};
pub(super) fn preflight(draw: &SampledImage, context: &ImageContext) -> Result<()> {
    draw.validate()?;
    let size = draw.image.size();
    if size.contains(&0)
        || size
            .iter()
            .any(|v| *v > context.device().limits().max_texture_dimension_2d)
    {
        return Err(Error::Capacity.into());
    }
    match &draw.image {
        ImageBatchSource::Image(image) => {
            source_device(image, context)?;
        }
        ImageBatchSource::Raster(image) => {
            admit_bitmap_format(image.info().color_type)?;
            image.pixmap()?;
        }
        ImageBatchSource::Texture(image) => {
            if !image.context().same_context(context) && image.portable_pixels()?.is_none() {
                return Err(ImageResourceError::NotPortable);
            }
            let gpu = image.gpu_image();
            if image.context().same_context(context)
                && (image.texture_region() != [0, 0, gpu.size()[0], gpu.size()[1]] || mips(draw))
                && !gpu
                    .texture()
                    .usage()
                    .contains(wgpu::TextureUsages::COPY_SRC)
            {
                return Err(ImageResourceError::TextureContract);
            }
        }
    }
    if let Some(paint) = &draw.paint {
        admit_output_paint(paint, context)?;
    }
    Ok(())
}
fn mips(draw: &SampledImage) -> bool {
    draw.sampling.cubic.is_none()
        && (draw.sampling.max_anisotropy > 0 || draw.sampling.mipmap != RasterMipmapMode::None)
}
pub(super) async fn prepare(
    scene: &mut Scene,
    draw: &SampledImage,
    canvas: [f32; 2],
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
) -> Result<()> {
    let image = match &draw.image {
        ImageBatchSource::Image(image) => draw.draw(image.clone())?,
        ImageBatchSource::Raster(source) => {
            let image = TextureImage::from_raster(context, source, RasterExecution::default())?;
            texture(draw, &image, canvas, context, captures).await?
        }
        ImageBatchSource::Texture(image) => texture(draw, image, canvas, context, captures).await?,
    };
    prepare_image(
        scene,
        context,
        captures,
        image,
        draw.paint.as_deref(),
        canvas,
    )
    .await
}
async fn texture(
    draw: &SampledImage,
    source: &TextureImage,
    canvas: [f32; 2],
    context: &ImageContext,
    captures: &mut PictureCaptureSession,
) -> Result<ImageDraw> {
    let (gpu, region) = source.for_context(context)?;
    let full = region == [0, 0, gpu.size()[0], gpu.size()[1]];
    let mipmapped = mips(draw);
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
    let mut output = draw.draw(gpu.into())?;
    if let Some(color) = source.info().color_space {
        let transfer = color.to_filter_color_space();
        if !transfer.is_srgb() {
            let identity = std::array::from_fn::<_, 256, _>(|i| i as u8);
            let table = transfer.to_srgb_table();
            let filter = ColorFilter::tables(&identity, table, table, table)?;
            output = filter_draw_source(context, captures, output, filter, canvas).await?;
        }
    }
    Ok(output)
}
