//! Original encoder option/alpha/format policy over an explicit dependency API.
use super::{Bitmap, Pixmap, RasterError as E, RasterExecution as X};
/// Original JPEG downsampling metadata; the managed writer does not consume it.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum JpegDownsample {
    /// Original 4:2:0 metadata.
    #[default]
    Downsample420,
    /// Original 4:2:2 metadata.
    Downsample422,
    /// Original 4:4:4 metadata.
    Downsample444,
}
/// Original JPEG alpha handling before encoder delegation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum JpegAlphaOption {
    /// Keep straight RGB, discard alpha at the encoder.
    #[default]
    Ignore,
    /// Premultiply RGB on black and force alpha to 255.
    BlendOnBlack,
}
/// Original JPEG options, retaining independently ignored downsampling metadata.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JpegOptions {
    /// Original quality, clamped to 1..100 at execution.
    pub quality: i32,
    /// Original ignored writer metadata.
    pub downsample: JpegDownsample,
    /// Original source alpha policy.
    pub alpha: JpegAlphaOption,
}
impl Default for JpegOptions {
    fn default() -> Self {
        Self {
            quality: 100,
            downsample: JpegDownsample::Downsample420,
            alpha: JpegAlphaOption::Ignore,
        }
    }
}
/// Original PNG options. The managed writer ignores both fields when encoding.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PngOptions {
    /// Original filter bits: None8/Sub16/Up32/Avg64/Paeth128.
    pub filter_flags: u32,
    /// Original retained zlib level.
    pub zlib_level: i32,
}
impl Default for PngOptions {
    fn default() -> Self {
        Self {
            filter_flags: 0xf8,
            zlib_level: 6,
        }
    }
}
/// Original WebP compression metadata; original encoding remains unavailable.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum WebpCompression {
    /// Original lossy marker.
    #[default]
    Lossy,
    /// Original lossless marker.
    Lossless,
}
/// Original retained unsupported WebP options.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WebpOptions {
    /// Original compression marker.
    pub compression: WebpCompression,
    /// Original unmodified quality.
    pub quality: f32,
}
impl Default for WebpOptions {
    fn default() -> Self {
        Self {
            compression: WebpCompression::Lossy,
            quality: 100.0,
        }
    }
}
/// Exact original raster encoder requests and unsupported WebP policy.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum RasterEncoding {
    /// Original JPEG request.
    Jpeg(JpegOptions),
    /// Original PNG request.
    Png(PngOptions),
    /// Original unavailable WebP request.
    Webp(WebpOptions),
}
/// Dependency boundary corresponding to original StbImageWriteSharp. Metadata
/// ignored by the original producer is intentionally not passed to this backend.
pub trait RasterEncoder {
    /// Encode original prepared straight/black-composited RGBA with clamped quality.
    fn jpeg(&self, size: [u32; 2], rgba: &[u8], quality: u8) -> Result<Vec<u8>, E>;
    /// Encode original prepared straight RGBA, without applying ignored options.
    fn png(&self, size: [u32; 2], rgba: &[u8]) -> Result<Vec<u8>, E>;
}
/// Existing image dependency's public encoder API; byte-stream identity and JPEG
/// quality are independent of the managed StbImageWriteSharp backend.
#[derive(Clone, Copy, Debug, Default)]
pub struct ImageCrateRasterEncoder;
impl RasterEncoder for ImageCrateRasterEncoder {
    fn jpeg(&self, [width, height]: [u32; 2], rgba: &[u8], quality: u8) -> Result<Vec<u8>, E> {
        let mut rgb = Vec::new();
        rgb.try_reserve_exact(rgba.len() / 4 * 3)
            .map_err(|_| E::Capacity)?;
        for color in rgba.chunks_exact(4) {
            rgb.extend_from_slice(&color[..3]);
        }
        let mut output = Vec::new();
        ::image::codecs::jpeg::JpegEncoder::new_with_quality(&mut output, quality)
            .encode(&rgb, width, height, ::image::ExtendedColorType::Rgb8)
            .map_err(|e| E::Transport(e.to_string()))?;
        Ok(output)
    }
    fn png(&self, [width, height]: [u32; 2], rgba: &[u8]) -> Result<Vec<u8>, E> {
        use ::image::ImageEncoder;
        let mut output = Vec::new();
        ::image::codecs::png::PngEncoder::new(&mut output)
            .write_image(rgba, width, height, ::image::ExtendedColorType::Rgba8)
            .map_err(|e| E::Transport(e.to_string()))?;
        Ok(output)
    }
}
impl Pixmap<'_> {
    /// Apply all original encoder producer policies before backend invocation.
    /// WebP returns None without staging pixels or calling an encoder.
    pub fn encode_with(
        &self,
        encoding: RasterEncoding,
        execution: X,
        backend: &impl RasterEncoder,
    ) -> Result<Option<Vec<u8>>, E> {
        let size = [self.info.width as u32, self.info.height as u32];
        let encoded = match encoding {
            RasterEncoding::Webp(_) => return Ok(None),
            RasterEncoding::Jpeg(options) => backend.jpeg(
                size,
                &self.straight_rgba(options.alpha == JpegAlphaOption::BlendOnBlack, execution)?,
                options.quality.clamp(1, 100) as u8,
            )?,
            RasterEncoding::Png(_) => backend.png(size, &self.straight_rgba(false, execution)?)?,
        };
        Ok(Some(encoded))
    }
    /// Original policy using the already-present external image dependency.
    pub fn encode(&self, encoding: RasterEncoding, execution: X) -> Result<Option<Vec<u8>>, E> {
        self.encode_with(encoding, execution, &ImageCrateRasterEncoder)
    }
    /// Original complete-byte stream write after encoding succeeds.
    pub fn encode_to(
        &self,
        output: &mut impl std::io::Write,
        encoding: RasterEncoding,
        execution: X,
    ) -> Result<bool, E> {
        let Some(bytes) = self.encode(encoding, execution)? else {
            return Ok(false);
        };
        output
            .write_all(&bytes)
            .map_err(|e| E::Transport(e.to_string()))?;
        Ok(true)
    }
}
impl Bitmap {
    /// Original bitmap encoder delegates through an actual retained pixel view.
    pub fn encode(&self, encoding: RasterEncoding, execution: X) -> Result<Option<Vec<u8>>, E> {
        let pixels = self.pixels()?;
        Pixmap::new(self.info().clone(), self.row_bytes(), &pixels)?.encode(encoding, execution)
    }
}
