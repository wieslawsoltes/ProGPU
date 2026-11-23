//! Safe JPEG dependency boundary for original straight-RGBA image/font producers.
//! The original C++ font API selects encoded payloads; it does not own a JPEG
//! decoder. Only the image crate's public decoder API is consumed here.
use super::{EncodedImageExecution, EncodedRaster, Image, ImageAlpha};
use crate::{Error, path::ExecutionPreference};
use ::image::{ColorType, ImageDecoder};
use std::io::Cursor;
use wide::u32x4;

/// Actual JPEG output representation supplied by the selected dependency.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum JpegPixelLayout {
    Gray8,
    Rgb8,
}
impl JpegPixelLayout {
    fn channels(self) -> usize {
        match self {
            Self::Gray8 => 1,
            Self::Rgb8 => 3,
        }
    }
}
/// Source-sized publication policy. Defaults preserve the managed byte-array
/// count bound, without adding the dependency's unrelated default allocation cap.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JpegDecodeLimits {
    pub max_width: u32,
    pub max_height: u32,
    pub max_rgba_bytes: usize,
}
impl Default for JpegDecodeLimits {
    fn default() -> Self {
        Self {
            max_width: i32::MAX as u32,
            max_height: i32::MAX as u32,
            max_rgba_bytes: i32::MAX as usize,
        }
    }
}
impl JpegDecodeLimits {
    /// Existing immutable Image domain; this is independent of font strike size.
    pub fn image() -> Self {
        Self {
            max_width: 16_384,
            max_height: 16_384,
            ..Self::default()
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JpegDecodeRequirements {
    pub size: [u32; 2],
    /// Exact dependency output before adding opaque alpha.
    pub decoded_bytes: usize,
    /// Tightly packed straight-alpha RGBA8 output.
    pub rgba_bytes: usize,
    pub layout: JpegPixelLayout,
}
/// Native PNG errors remain a separate original numeric contract. These errors
/// describe the explicit JPEG dependency and Rust publication boundary.
#[derive(Debug, thiserror::Error)]
pub enum JpegDecodeError {
    #[error("invalid JPEG source or decode limits")]
    InvalidInput,
    #[error("JPEG decoder returned an unsupported output representation")]
    UnsupportedOutput,
    #[error("JPEG storage capacity exceeded")]
    Capacity,
    #[error("insufficient JPEG RGBA output capacity")]
    InsufficientBuffer,
    #[error("JPEG decoder failed: {0}")]
    Backend(String),
    #[error(transparent)]
    Image(#[from] Error),
}
impl JpegDecodeError {
    pub(crate) fn encoded(self) -> super::EncodedImageError {
        match self {
            Self::Capacity | Self::InsufficientBuffer => super::EncodedImageError::Capacity,
            Self::Image(error) => super::EncodedImageError::Image(error),
            other => super::EncodedImageError::Raster(other.to_string()),
        }
    }
}
fn backend(error: ::image::ImageError) -> JpegDecodeError {
    match error {
        ::image::ImageError::Limits(_) => JpegDecodeError::Capacity,
        other => JpegDecodeError::Backend(other.to_string()),
    }
}
fn simd_available() -> bool {
    cfg!(any(
        target_feature = "sse2",
        target_feature = "neon",
        target_feature = "simd128"
    ))
}
fn automatic() -> EncodedImageExecution {
    if simd_available() {
        EncodedImageExecution::CpuSimd
    } else {
        EncodedImageExecution::ScalarReference
    }
}
fn storage(count: usize) -> Result<Vec<u8>, JpegDecodeError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| JpegDecodeError::Capacity)?;
    output.resize(count, 0);
    Ok(output)
}

/// One immutable borrowed source and its actual admitted JPEG decoder generation.
/// Header parsing does not publish pixels. Decoder internals and compressed-codec
/// SIMD selection belong to the dependency, separately from the RGBA lane policy.
pub struct JpegDecoder<'a> {
    source: &'a [u8],
    decoder: ::image::codecs::jpeg::JpegDecoder<Cursor<&'a [u8]>>,
    requirements: JpegDecodeRequirements,
    conversion: EncodedImageExecution,
}
impl std::fmt::Debug for JpegDecoder<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("JpegDecoder")
            .field("encoded_bytes", &self.source.len())
            .field("requirements", &self.requirements)
            .field("conversion", &self.conversion)
            .finish_non_exhaustive()
    }
}
impl<'a> JpegDecoder<'a> {
    pub fn new(source: &'a [u8]) -> Result<Self, JpegDecodeError> {
        Self::with_limits(source, JpegDecodeLimits::default())
    }
    pub fn with_limits(
        source: &'a [u8],
        limits: JpegDecodeLimits,
    ) -> Result<Self, JpegDecodeError> {
        if source.is_empty()
            || limits.max_width == 0
            || limits.max_height == 0
            || limits.max_rgba_bytes == 0
        {
            return Err(JpegDecodeError::InvalidInput);
        }
        if source.len() > i32::MAX as usize {
            return Err(JpegDecodeError::Capacity);
        }
        let mut decoder =
            ::image::codecs::jpeg::JpegDecoder::new(Cursor::new(source)).map_err(backend)?;
        let (width, height) = decoder.dimensions();
        if width == 0 || height == 0 {
            return Err(JpegDecodeError::InvalidInput);
        }
        let rgba = u64::from(width)
            .checked_mul(u64::from(height))
            .and_then(|n| n.checked_mul(4))
            .ok_or(JpegDecodeError::Capacity)?;
        if width > limits.max_width
            || height > limits.max_height
            || rgba > limits.max_rgba_bytes as u64
            || rgba > i32::MAX as u64
        {
            return Err(JpegDecodeError::Capacity);
        }
        let layout = match decoder.color_type() {
            ColorType::L8 => JpegPixelLayout::Gray8,
            ColorType::Rgb8 => JpegPixelLayout::Rgb8,
            _ => return Err(JpegDecodeError::UnsupportedOutput),
        };
        let decoded_bytes = (width as usize)
            .checked_mul(height as usize)
            .and_then(|n| n.checked_mul(layout.channels()))
            .ok_or(JpegDecodeError::Capacity)?;
        if decoder.total_bytes() != decoded_bytes as u64 {
            return Err(JpegDecodeError::UnsupportedOutput);
        }
        let mut codec_limits = ::image::Limits::no_limits();
        codec_limits.max_image_width = Some(limits.max_width);
        codec_limits.max_image_height = Some(limits.max_height);
        decoder.set_limits(codec_limits).map_err(backend)?;
        Ok(Self {
            source,
            decoder,
            requirements: JpegDecodeRequirements {
                size: [width, height],
                decoded_bytes,
                rgba_bytes: rgba as usize,
                layout,
            },
            conversion: automatic(),
        })
    }
    /// Select only our byte-preserving RGBA conversion. The compressed JPEG
    /// backend remains independently selected and must not be called a forced
    /// scalar or forced SIMD codec. GPU preferences are incompatible here.
    pub fn with_pixel_conversion(mut self, preference: ExecutionPreference) -> Result<Self, Error> {
        self.conversion = match preference {
            ExecutionPreference::Automatic => automatic(),
            ExecutionPreference::CpuSimd if simd_available() => EncodedImageExecution::CpuSimd,
            ExecutionPreference::ScalarReference => EncodedImageExecution::ScalarReference,
            _ => return Err(Error::UnsupportedExecution),
        };
        Ok(self)
    }
    pub fn pixel_conversion(&self) -> EncodedImageExecution {
        self.conversion
    }
    pub fn encoded(&self) -> &'a [u8] {
        self.source
    }
    pub fn requirements(&self) -> JpegDecodeRequirements {
        self.requirements
    }
    /// Decode once into owned storage. No EXIF orientation, ICC transform,
    /// premultiplication, resizing or source-strike substitution is performed.
    pub fn decode_rgba(self) -> Result<EncodedRaster, JpegDecodeError> {
        let size = self.requirements.size;
        let mut output = storage(self.requirements.rgba_bytes)?;
        self.decode_rgba_into(&mut output)?;
        Ok(EncodedRaster { size, rgba: output })
    }
    /// Short capacity rejects before pixel decoding. Malformed streams and
    /// allocation failures leave the complete caller output unchanged. Only a
    /// successful private decode starts infallible RGBA conversion; trailing
    /// caller bytes are untouched.
    pub fn decode_rgba_into(
        self,
        output: &mut [u8],
    ) -> Result<JpegDecodeRequirements, JpegDecodeError> {
        let requirements = self.requirements;
        if output.len() < requirements.rgba_bytes {
            return Err(JpegDecodeError::InsufficientBuffer);
        }
        let mut decoded = storage(requirements.decoded_bytes)?;
        self.decoder.read_image(&mut decoded).map_err(backend)?;
        convert(
            &decoded,
            &mut output[..requirements.rgba_bytes],
            requirements.layout,
            self.conversion,
        );
        Ok(requirements)
    }
}
impl Image {
    /// Decode the selected JPEG payload through the owned safe codec boundary.
    /// Native PNG remains `decode_png` with its independent original algorithm.
    pub fn decode_jpeg(source: &[u8]) -> Result<Self, JpegDecodeError> {
        let decoded = JpegDecoder::with_limits(source, JpegDecodeLimits::image())?.decode_rgba()?;
        Ok(Self::rgba8(
            decoded.size,
            ImageAlpha::Straight,
            decoded.rgba,
        )?)
    }
}
fn convert(
    input: &[u8],
    output: &mut [u8],
    layout: JpegPixelLayout,
    execution: EncodedImageExecution,
) {
    let channels = layout.channels();
    let count = output.len() / 4;
    let mut pixel = 0usize;
    if execution == EncodedImageExecution::CpuSimd {
        while pixel + 4 <= count {
            let base = pixel * channels;
            let words = match layout {
                JpegPixelLayout::Gray8 => {
                    let gray =
                        u32x4::from(std::array::from_fn(|lane| u32::from(input[base + lane])));
                    gray | (gray << 8) | (gray << 16) | u32x4::splat(0xff00_0000)
                }
                JpegPixelLayout::Rgb8 => {
                    let red = u32x4::from(std::array::from_fn(|lane| {
                        u32::from(input[base + lane * 3])
                    }));
                    let green = u32x4::from(std::array::from_fn(|lane| {
                        u32::from(input[base + lane * 3 + 1])
                    }));
                    let blue = u32x4::from(std::array::from_fn(|lane| {
                        u32::from(input[base + lane * 3 + 2])
                    }));
                    red | (green << 8) | (blue << 16) | u32x4::splat(0xff00_0000)
                }
            }
            .to_array();
            for (lane, word) in words.into_iter().enumerate() {
                output[(pixel + lane) * 4..(pixel + lane + 1) * 4]
                    .copy_from_slice(&word.to_le_bytes());
            }
            pixel += 4;
        }
    }
    for i in pixel..count {
        let start = i * channels;
        let rgba = match layout {
            JpegPixelLayout::Gray8 => [input[start], input[start], input[start], 255],
            JpegPixelLayout::Rgb8 => [input[start], input[start + 1], input[start + 2], 255],
        };
        output[i * 4..i * 4 + 4].copy_from_slice(&rgba);
    }
}
