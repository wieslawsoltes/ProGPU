//! Connected original image factories, subset gates and explicit CPU views.
use super::super::{Bitmap, EncodedImageFormat, RasterSampling, RasterSnapshot};
use super::*;
/// Materialized immutable CPU pixels with the real source image lease. The
/// returned pixmap borrows this object, retaining source callbacks until its end.
#[derive(Clone, Debug)]
pub struct TextureRasterView {
    image: TextureImage,
    pixels: RasterSnapshot,
}
impl TextureRasterView {
    /// Borrow the selected original image-format pixel inventory.
    pub fn pixmap(&self) -> Result<Pixmap<'_>, RasterError> {
        self.pixels.pixmap()
    }
    /// Actual source image, retained independently of the caller's wrapper.
    pub fn image(&self) -> &TextureImage {
        &self.image
    }
    /// Immutable byte-generation owner, independent of mutable raster storage.
    pub fn snapshot(&self) -> &RasterSnapshot {
        &self.pixels
    }
}
impl TextureImage {
    /// Original per-wrapper identity; explicit subsets have independent identities
    /// while Rust clones keep that exact wrapper generation.
    pub fn unique_id(&self) -> usize {
        Arc::as_ptr(&self.cache) as usize
    }
    /// Original public Subset admission. Texture-backed images require their
    /// exact valid context; raster-compatible views allow omitted contexts.
    pub fn subset_for_context(
        &self,
        context: Option<&ImageContext>,
        bounds: [u32; 4],
    ) -> Result<Option<Self>, ImageResourceError> {
        if self.is_texture_backed() && context.is_none() {
            return Ok(None);
        }
        if context
            .is_some_and(|c| c.is_abandoned() || (self.is_texture_backed() && !self.is_valid(c)))
        {
            return Ok(None);
        }
        match self.subset(bounds) {
            Ok(image) => Ok(Some(image)),
            Err(ImageResourceError::Bounds) => Ok(None),
            Err(error) => Err(error),
        }
    }
    /// Exact original fast common-format source copy, then original Bitmap
    /// conversion for remaining admitted formats. A callback belongs to this
    /// immutable image generation even though its source pixels are copied.
    pub fn from_pixel_copy(
        context: &ImageContext,
        source: &Pixmap<'_>,
        release: Option<TextureRelease>,
        execution: RasterExecution,
    ) -> Result<Self, ImageResourceError> {
        let info = source.info().clone();
        let image = if matches!(
            info.color_type,
            PixelColorType::Rgba8888 | PixelColorType::Bgra8888 | PixelColorType::Srgba8888
        ) {
            let size = [info.width as u32, info.height as u32];
            if info.is_empty() {
                return Err(ImageResourceError::Bounds);
            }
            let mut bytes = super::super::raster::zeros(byte_count(size)?)?;
            let row = size[0] as usize * 4;
            for y in 0..size[1] as usize {
                bytes[y * row..(y + 1) * row].copy_from_slice(
                    &source.pixels()
                        [y * source.row_bytes() as usize..y * source.row_bytes() as usize + row],
                );
            }
            if info.color_type == PixelColorType::Bgra8888 {
                upload::swizzle(&mut bytes, execution);
            }
            if info.alpha_type == PixelAlphaType::Opaque {
                upload::force_opaque(&mut bytes, execution);
            }
            let gpu = upload::upload(context, &info, &bytes, 1, false)?;
            Self::create(
                context.clone(),
                gpu,
                info,
                TextureOwnership::Owned,
                Some(bytes.into()),
                false,
                None,
            )
        } else {
            let bitmap = Bitmap::from_pixel_copy(source)?;
            Self::from_raster(context, &bitmap.snapshot()?, execution)?
        };
        // Candidate is private here; attach the selected original source callback
        // only after pixel/source/device construction succeeds.
        let mut storage = Arc::try_unwrap(image.storage).map_err(|_| Error::ResourceDomain)?;
        storage.release = ReleaseAction(release);
        Ok(Self {
            storage: Arc::new(storage),
            region: image.region,
            cache: image.cache,
        })
    }
    /// Execute an original retained picture against a transparent premultiplied
    /// RGBA target at DPI one. Full unsupported projective frames reject through
    /// the existing source-frame contract before recording is evaluated.
    pub async fn from_picture(
        context: &ImageContext,
        picture: impl Into<Arc<crate::Scene>>,
        size: [u32; 2],
        matrix: crate::vector::Matrix4,
    ) -> Result<Self, ImageResourceError> {
        Self::from_picture_with_format(
            context,
            picture,
            size,
            matrix,
            wgpu::TextureFormat::Rgba8Unorm,
        )
        .await
    }
    /// Original SaveLayer capture target selection. Float layers preserve their
    /// actual RGBA16F storage through source drawing, snapshots and texture reuse;
    /// ordinary picture factories continue to select RGBA8. This does not extend
    /// the separate original four-byte image readback/portable-raster contract.
    pub async fn from_picture_with_format(
        context: &ImageContext,
        picture: impl Into<Arc<crate::Scene>>,
        size: [u32; 2],
        matrix: crate::vector::Matrix4,
        format: wgpu::TextureFormat,
    ) -> Result<Self, ImageResourceError> {
        PictureCaptureSession::new(context)
            .capture(picture, size, matrix, format)
            .await
    }

    /// Original PeekPixels gate and selected-format materialization. A texture-only
    /// image returns None; direct read_pixels remains available independently.
    pub async fn peek_pixels(
        &self,
        execution: RasterExecution,
    ) -> Result<Option<TextureRasterView>, ImageResourceError> {
        if self.is_texture_backed() {
            return Ok(None);
        }
        if let Some(pixels) = self
            .cache
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .raster
            .clone()
        {
            return Ok(Some(TextureRasterView {
                image: self.clone(),
                pixels,
            }));
        }
        let readback = self
            .readback(ImageCachingHint::Allow)?
            .complete_host()
            .await?;
        let mut bitmap = Bitmap::tight(self.info())?;
        let stride = bitmap.row_bytes();
        let info = bitmap.info().clone();
        {
            let mut bytes = bitmap.pixels_mut()?;
            if !readback.pixmap()?.read_pixels(
                &mut PixmapMut::new(info, stride, &mut bytes)?,
                [0, 0],
                execution,
            )? {
                return Err(RasterError::UnsupportedFormat.into());
            }
        }
        let pixels = bitmap.snapshot()?;
        self.cache.lock().unwrap_or_else(|e| e.into_inner()).raster = Some(pixels.clone());
        Ok(Some(TextureRasterView {
            image: self.clone(),
            pixels,
        }))
    }
    /// Original explicit CPU image scaling. Sampling uses the owned CPU raster
    /// producer, not a silently substituted rendering path or GPU resize.
    pub async fn scale_pixels(
        &self,
        destination: &mut PixmapMut<'_>,
        sampling: RasterSampling,
        execution: RasterExecution,
    ) -> Result<bool, ImageResourceError> {
        Ok(self
            .readback(ImageCachingHint::Allow)?
            .complete_host()
            .await?
            .pixmap()?
            .scale_pixels(destination, sampling, execution)?)
    }
    /// Original image encoding chooses JPEG only for Jpeg; every other original
    /// format selects PNG. Encoder bytes remain the external public provider's
    /// responsibility; this preserves original source and alpha preparation.
    pub async fn encode(
        &self,
        format: EncodedImageFormat,
        quality: i32,
        execution: RasterExecution,
    ) -> Result<Vec<u8>, ImageResourceError> {
        use super::super::raster::{JpegOptions, PngOptions, RasterEncoding};
        let bytes = self.rgba_pixels(execution).await?;
        let mut info = self.info();
        info.color_type = PixelColorType::Rgba8888;
        info.alpha_type = if self.storage.image.alpha() == ImageAlpha::Premultiplied {
            PixelAlphaType::Premultiplied
        } else {
            PixelAlphaType::Straight
        };
        let encoding = if format == EncodedImageFormat::Jpeg {
            RasterEncoding::Jpeg(JpegOptions {
                quality,
                ..Default::default()
            })
        } else {
            RasterEncoding::Png(PngOptions::default())
        };
        Pixmap::new(info, self.region[2] as i32 * 4, &bytes)?
            .encode(encoding, execution)?
            .ok_or_else(|| RasterError::UnsupportedFormat.into())
    }
}

impl TextureImage {
    /// Decode through the original Bitmap/Codec format gate, then retain the
    /// exact encoded source inventory independently of its premultiplied pixels.
    pub fn from_encoded_data(
        context: &ImageContext,
        source: &[u8],
        execution: RasterExecution,
    ) -> Result<Self, ImageResourceError> {
        let bitmap = Bitmap::decode(source, execution)?;
        let image = Self::from_raster(context, &bitmap.snapshot()?, execution)?;
        image
            .cache
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .encoded = Some(Arc::from(source));
        Ok(image)
    }
    /// Retained original encoded data, present only on the decoding wrapper.
    /// Views and generated texture/raster copies do not fabricate encoded bytes.
    pub fn encoded_data(&self) -> Option<Arc<[u8]>> {
        self.cache
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .encoded
            .clone()
    }
}
