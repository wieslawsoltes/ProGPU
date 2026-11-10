//! Original bitmap/surface producers and complete ordinary-image sampling.
use super::*;
use crate::image::{
    Bitmap, ImageBatchSource, PixelAlphaType, PixelColorType, RasterError, RasterSampling,
};

/// One frozen ordinary-image command, distinct from patch batches: its exact
/// geometry edge clip and far-side raster extension remain enabled by paint.
#[derive(Clone, Debug)]
pub(crate) struct SampledImage {
    pub(crate) image: ImageBatchSource,
    pub(crate) source: [f32; 4],
    pub(crate) destination: [f32; 4],
    pub(crate) sampling: RasterSampling,
    pub(crate) matrix: CanvasMatrix,
    pub(crate) paint: Option<PaintSnapshot>,
}
impl SampledImage {
    pub(crate) fn draw(&self, image: ImageSource) -> Result<ImageDraw, Error> {
        let mut draw = ImageDraw::new(image, self.destination);
        draw.source = self.source;
        draw.transform = self.matrix.affine()?;
        draw.patch_policy = crate::image::ImagePatchPolicy::ManagedImage;
        draw.extended_source = true;
        crate::image::apply_sampling(&mut draw, self.sampling);
        draw.ignore_source_alpha = match &self.image {
            ImageBatchSource::Raster(image) => image.info().alpha_type == PixelAlphaType::Opaque,
            #[cfg(feature = "gpu")]
            ImageBatchSource::Texture(image) => image.info().alpha_type == PixelAlphaType::Opaque,
            _ => false,
        };
        draw.validate()?;
        Ok(draw)
    }
    pub(crate) fn cpu_draw(&self) -> Result<ImageDraw, Error> {
        let image = match &self.image {
            ImageBatchSource::Image(image) => image.clone(),
            ImageBatchSource::Raster(image) => {
                admit_bitmap_format(image.info().color_type)?;
                if image
                    .info()
                    .color_space
                    .as_ref()
                    .is_some_and(|space| !space.to_filter_color_space().is_srgb())
                {
                    return Err(Error::UnsupportedExecution);
                }
                image
                    .to_image(crate::image::RasterExecution::default())
                    .map_err(raster_error)?
                    .into()
            }
            #[cfg(feature = "gpu")]
            ImageBatchSource::Texture(_) => return Err(Error::UnsupportedExecution),
        };
        self.draw(image)
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        self.matrix.affine()?;
        if self
            .source
            .iter()
            .chain(&self.destination)
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if self.image.size().iter().any(|v| *v == 0 || *v > 16384)
            || (self.sampling.cubic.is_some() && self.sampling.max_anisotropy > 1)
        {
            return Err(Error::InvalidInput);
        }
        if let ImageBatchSource::Image(image) = &self.image {
            self.draw(image.clone())?;
        }
        if let Some(paint) = &self.paint {
            paint.canvas_blend()?;
        }
        Ok(())
    }
}

#[cfg(feature = "gpu")]
impl crate::image::RenderSurface<'_> {
    /// Original surface Draw forwards one immutable snapshot into the receiving
    /// canvas; actual null surfaces remain a no-op before snapshot creation.
    pub async fn draw(
        &mut self,
        canvas: &mut Canvas,
        point: [f32; 2],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), crate::image::ImageResourceError> {
        canvas.draw_surface(self, point, sampling, paint).await
    }
    /// Original convenience overload uses paint's legacy quality, then DrawImage.
    pub async fn draw_legacy(
        &mut self,
        canvas: &mut Canvas,
        point: [f32; 2],
        paint: Option<&Paint>,
    ) -> Result<(), crate::image::ImageResourceError> {
        canvas.draw_surface_legacy(self, point, paint).await
    }
}
fn raster_error(error: RasterError) -> Error {
    match error {
        RasterError::Image(error) => error,
        RasterError::Capacity => Error::Capacity,
        _ => Error::UnsupportedExecution,
    }
}
pub(crate) fn admit_bitmap_format(format: PixelColorType) -> Result<(), Error> {
    if matches!(
        format,
        PixelColorType::Rgba8888
            | PixelColorType::Bgra8888
            | PixelColorType::Rgb888x
            | PixelColorType::Rgb565
    ) {
        Ok(())
    } else {
        Err(Error::UnsupportedExecution)
    }
}
fn xywh(rect: [f32; 4]) -> [f32; 4] {
    [rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1]]
}
pub(super) fn legacy(paint: Option<&Paint>) -> Result<RasterSampling, Error> {
    if let Some(paint) = paint {
        paint.image_sampling()?;
    }
    Ok(RasterSampling::from_quality(
        paint.map_or(0, |paint| paint.filter_quality),
    ))
}
impl Canvas {
    /// Original complete sampling overload. Rectangles are left/top/right/bottom,
    /// preserving float endpoint subtraction independently of source dimensions.
    /// Raster inputs freeze their actual bytes; textures retain their source lease.
    pub fn draw_sampled_image(
        &mut self,
        image: impl Into<ImageBatchSource>,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let image = image.into();
        let size = image.size();
        let draw = SampledImage {
            image,
            source: xywh(source.unwrap_or([0.0, 0.0, size[0] as f32, size[1] as f32])),
            destination: xywh(destination),
            sampling,
            matrix: self.matrix,
            paint: paint.map(Paint::snapshot),
        };
        draw.validate()?;
        self.push(Command::SampledImage(draw))
    }
    /// Original point overload computes far endpoints by float addition first.
    pub fn draw_sampled_image_at(
        &mut self,
        image: impl Into<ImageBatchSource>,
        point: [f32; 2],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        let image = image.into();
        let size = image.size();
        self.draw_sampled_image(
            image,
            None,
            [
                point[0],
                point[1],
                point[0] + size[0] as f32,
                point[1] + size[1] as f32,
            ],
            sampling,
            paint,
        )
    }
    /// Original legacy-quality overload, including unknown-quality rejection.
    pub fn draw_image_legacy(
        &mut self,
        image: impl Into<ImageBatchSource>,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_sampled_image(image, source, destination, legacy(paint)?, paint)
    }
    /// Original legacy point overload with exact endpoint arithmetic.
    pub fn draw_image_at_legacy(
        &mut self,
        image: impl Into<ImageBatchSource>,
        point: [f32; 2],
        paint: Option<&Paint>,
    ) -> Result<(), Error> {
        self.draw_sampled_image_at(image, point, legacy(paint)?, paint)
    }
    /// Freeze the complete bitmap generation before ordinary image recording.
    pub fn draw_bitmap(
        &mut self,
        bitmap: &Bitmap,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        Ok(self.draw_sampled_image(bitmap.snapshot()?, source, destination, sampling, paint)?)
    }
    /// Original bitmap point overload; later bitmap mutation cannot change it.
    pub fn draw_bitmap_at(
        &mut self,
        bitmap: &Bitmap,
        point: [f32; 2],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        Ok(self.draw_sampled_image_at(bitmap.snapshot()?, point, sampling, paint)?)
    }
    /// Original deprecated bitmap rectangle/source convenience producer.
    pub fn draw_bitmap_legacy(
        &mut self,
        bitmap: &Bitmap,
        source: Option<[f32; 4]>,
        destination: [f32; 4],
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        let image = bitmap.snapshot()?;
        Ok(self.draw_sampled_image(image, source, destination, legacy(paint)?, paint)?)
    }
    /// Original deprecated bitmap point convenience producer.
    pub fn draw_bitmap_at_legacy(
        &mut self,
        bitmap: &Bitmap,
        point: [f32; 2],
        paint: Option<&Paint>,
    ) -> Result<(), RasterError> {
        let image = bitmap.snapshot()?;
        Ok(self.draw_sampled_image_at(image, point, legacy(paint)?, paint)?)
    }
    /// Snapshot the actual surface once before recording. A null surface emits
    /// no command; subsequent surface writes cannot mutate this image generation.
    #[cfg(feature = "gpu")]
    pub async fn draw_surface(
        &mut self,
        surface: &mut crate::image::RenderSurface<'_>,
        point: [f32; 2],
        sampling: RasterSampling,
        paint: Option<&Paint>,
    ) -> Result<(), crate::image::ImageResourceError> {
        if surface.context().is_none() {
            return Ok(());
        }
        let image = surface.snapshot().await?;
        self.draw_sampled_image_at(image, point, sampling, paint)?;
        Ok(())
    }
    /// Original surface convenience overload selects paint's legacy sampling.
    #[cfg(feature = "gpu")]
    pub async fn draw_surface_legacy(
        &mut self,
        surface: &mut crate::image::RenderSurface<'_>,
        point: [f32; 2],
        paint: Option<&Paint>,
    ) -> Result<(), crate::image::ImageResourceError> {
        if surface.context().is_none() {
            return Ok(());
        }
        let image = surface.snapshot().await?;
        self.draw_sampled_image_at(image, point, legacy(paint)?, paint)?;
        Ok(())
    }
}
