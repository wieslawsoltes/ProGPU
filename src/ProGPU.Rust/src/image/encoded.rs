//! Original managed encoded-image selection, ICO/CUR decoding and PNG metadata.
//! Ordinary compressed raster decoding remains an explicit dependency boundary;
//! the managed producer uses StbImageSharp, not the separate native PNG contract.
use super::{ColorSpace, ColorSpaceTransferFunction, ColorSpaceXyz, Image, ImageAlpha};
use crate::{Error, path::ExecutionPreference};
use std::sync::Arc;
mod codec;
mod dib;
pub use codec::{
    CodecAnimationBlend, CodecAnimationDisposal, CodecError, CodecFrameInfo, CodecOptions,
    CodecResult, CodecScanlineOrder, EncodedImageFormat, EncodedOrigin, ImageCodec,
    IncrementalDecode, ZeroInitialized, detect_encoded_format,
};
mod rle;

/// Original malformed/unsupported encoded-image categories, with dependency
/// decoder and immutable-image publication errors kept independent.
#[derive(Debug, thiserror::Error)]
pub enum EncodedImageError {
    /// Invalid original ICO/CUR directory.
    #[error("Invalid ICO directory.")]
    IconDirectory,
    /// No nonempty in-range frame exists.
    #[error("ICO contains no valid image frames.")]
    NoIconFrame,
    /// Original bitmap header is not wholly present.
    #[error("ICO bitmap header is truncated.")]
    BitmapHeader,
    /// Unsupported original DIB header size.
    #[error("Only 12-byte core and 40-byte-or-newer ICO bitmap headers are supported.")]
    UnsupportedHeader,
    /// Unsupported original compression/depth combination.
    #[error(
        "Only indexed, RLE4/RLE8, 16-bit, 24-bit, 32-bit, and 16/32-bit bitfield ICO bitmap frames are supported."
    )]
    UnsupportedBitmap,
    /// Original DIB dimensions cannot describe a nonempty bitmap.
    #[error("ICO bitmap dimensions are invalid.")]
    Dimensions,
    /// Required external bitfield words are absent.
    #[error("ICO bitfield masks are truncated.")]
    TruncatedMasks,
    /// Overlapping, absent, noncontiguous or out-of-width channel masks.
    #[error("ICO bitfield channel masks are invalid.")]
    InvalidMasks,
    /// Original palette count is outside the selected index width.
    #[error("ICO color table size is invalid.")]
    PaletteSize,
    /// The complete original palette is absent.
    #[error("ICO color table is truncated.")]
    TruncatedPalette,
    /// A pixel selects an absent palette entry.
    #[error("ICO bitmap references a missing color table entry.")]
    PaletteIndex,
    /// Original complete XOR bitmap inventory is absent.
    #[error("ICO bitmap pixels are truncated.")]
    TruncatedPixels,
    /// Declared RLE size exceeds the actual frame.
    #[error("ICO RLE bitmap pixels are truncated.")]
    TruncatedRle,
    /// A two-byte command is incomplete.
    #[error("ICO RLE command is truncated.")]
    RleCommand,
    /// Too many original end-of-line commands.
    #[error("ICO RLE rows exceed the bitmap height.")]
    RleRows,
    /// Missing original delta operands.
    #[error("ICO RLE delta is truncated.")]
    RleDelta,
    /// Original delta reaches beyond the allowed end positions.
    #[error("ICO RLE delta exceeds the bitmap bounds.")]
    RleDeltaBounds,
    /// Absolute samples and their independent alignment byte are incomplete.
    #[error("ICO RLE absolute run is truncated.")]
    RleAbsolute,
    /// A run attempts to write outside the actual bitmap.
    #[error("ICO RLE run exceeds the bitmap bounds.")]
    RleRun,
    /// Original required end marker was not encountered.
    #[error("ICO RLE bitmap is missing its end marker.")]
    RleEnd,
    /// Checked original signed byte count or allocation cannot be represented.
    #[error("encoded-image capacity exceeded")]
    Capacity,
    /// The selected external raster backend rejected the source.
    #[error("encoded raster decoder failed: {0}")]
    Raster(String),
    /// Actual returned backend pixels do not match their declared extent.
    #[error("encoded raster decoder returned an invalid pixel inventory")]
    RasterInventory,
    /// Existing rendering/immutable-image domain admission failed.
    #[error(transparent)]
    Image(#[from] Error),
}

/// Actual original selected ICO/CUR directory entry. Cursor hotspots do not
/// participate in frame ranking, matching the original producer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IconFrame {
    /// Original directory index; ties retain the first valid entry.
    pub index: u16,
    /// Directory dimensions, where zero bytes represent 256.
    pub size: [u32; 2],
    /// Original ICO bit count, or CUR payload bit count used for tie-breaking.
    pub bit_count: u16,
    /// Original palette-count byte, used by core DIB headers.
    pub color_count: u8,
    /// Exact source payload offset.
    pub offset: usize,
    /// Exact source payload length.
    pub byte_count: usize,
    /// Original icon versus cursor marker.
    pub is_cursor: bool,
}

/// External raster backend output before immutable owner publication.
#[derive(Debug)]
pub struct EncodedRaster {
    /// Actual decoder extent, independent of ICO directory dimensions.
    pub size: [u32; 2],
    /// Tightly packed straight-alpha RGBA8 bytes.
    pub rgba: Vec<u8>,
}
/// Public dependency boundary corresponding to original StbImageSharp decoding.
/// The caller can provide a matching backend without replacing owned ICO policy.
pub trait EncodedRasterDecoder {
    /// Decode a complete ordinary raster or selected embedded PNG payload.
    fn decode_rgba(&self, source: &[u8]) -> Result<EncodedRaster, EncodedImageError>;
}
/// Already-selected image crate backend, using only its public API. Its format
/// admission, numeric conversion and malformed-input policy are independent of
/// StbImageSharp and are not presented as original-decoder qualification.
#[derive(Clone, Copy, Debug, Default)]
pub struct ImageCrateRasterDecoder;
impl EncodedRasterDecoder for ImageCrateRasterDecoder {
    fn decode_rgba(&self, source: &[u8]) -> Result<EncodedRaster, EncodedImageError> {
        if source.len() > i32::MAX as usize {
            return Err(EncodedImageError::Capacity);
        }
        let mut reader = ::image::ImageReader::new(std::io::Cursor::new(source))
            .with_guessed_format()
            .map_err(|e| EncodedImageError::Raster(e.to_string()))?;
        // Original producers ask for one straight-RGBA image. TIFF is an admitted
        // font metadata tag, not a format supplied by their StbImageSharp backend.
        // Keep that capability stable even if an unrelated Cargo feature later
        // enables TIFF in the dependency. Explicit caller providers stay separate.
        if reader.format() == Some(::image::ImageFormat::Tiff) {
            return Err(EncodedImageError::Raster(
                "TIFF requires an explicit raster provider".into(),
            ));
        }
        if reader.format() == Some(::image::ImageFormat::Jpeg) {
            return super::jpeg::JpegDecoder::new(source)
                .and_then(super::jpeg::JpegDecoder::decode_rgba)
                .map_err(super::jpeg::JpegDecodeError::encoded);
        }
        // Preserve the source's uncapped byte-array policy and final headerless
        // TGA attempt; format selection belongs to actual bytes, not a font tag.
        reader.no_limits();
        if reader.format().is_none() {
            reader.set_format(::image::ImageFormat::Tga);
        }
        let decoded = reader.decode().map_err(|e| match e {
            ::image::ImageError::Limits(_) => EncodedImageError::Capacity,
            other => EncodedImageError::Raster(other.to_string()),
        })?;
        let size = [decoded.width(), decoded.height()];
        let count = rgba_byte_count(size)?;
        let rgba = decoded.into_rgba8().into_raw();
        if rgba.len() != count {
            return Err(EncodedImageError::RasterInventory);
        }
        Ok(EncodedRaster { size, rgba })
    }
}
/// Actual owned DIB conversion route; bitstream/RLE control remains dependent
/// scalar traversal. External raster backends retain their own execution policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EncodedImageExecution {
    /// Four independent channel/pixel lanes use safe intrinsic vectors.
    CpuSimd,
    /// Explicit original scalar reference conversion.
    ScalarReference,
}
impl EncodedImageExecution {
    fn simd_available() -> bool {
        cfg!(any(
            target_feature = "sse2",
            target_feature = "neon",
            target_feature = "simd128"
        ))
    }
}
/// Immutable original decoded pixel and selected color-space generation.
#[derive(Clone, Debug)]
pub struct DecodedImage {
    size: [u32; 2],
    pixels: Arc<[u8]>,
    color_space: Option<ColorSpace>,
    frame: Option<IconFrame>,
}
impl DecodedImage {
    /// Actual decoded extent, not the advertised directory size.
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
    /// Original tightly packed straight-alpha bytes, retaining RGB beneath zero alpha.
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }
    /// Original selected sRGB/gAMA transfer; absent metadata stays absent.
    pub fn color_space(&self) -> Option<&ColorSpace> {
        self.color_space.as_ref()
    }
    /// Actual selected ICO/CUR entry, if this source contains a directory.
    pub fn icon_frame(&self) -> Option<IconFrame> {
        self.frame
    }
    /// Share the actual immutable pixels with the renderer's existing Image
    /// domain. Renderer size limits remain distinct from codec dimensions.
    pub fn to_image(&self) -> Result<Image, Error> {
        Image::rgba8(self.size, ImageAlpha::Straight, self.pixels.clone())
    }
    /// Preserve exact selected color metadata when entering the filter stack.
    pub fn to_filter_source(&self) -> Result<crate::image_filter::FilterImageSource, Error> {
        Ok(crate::image_filter::FilterImageSource {
            image: self.to_image()?.into(),
            color_space: self
                .color_space
                .as_ref()
                .map(ColorSpace::to_filter_color_space),
        })
    }
}
/// Complete original container selection and decode policy with explicit owned
/// CPU conversion selection. No GPU is initialized during image decoding.
#[derive(Clone, Copy, Debug)]
pub struct EncodedImageDecoder {
    execution: EncodedImageExecution,
}
impl Default for EncodedImageDecoder {
    fn default() -> Self {
        Self {
            execution: if EncodedImageExecution::simd_available() {
                EncodedImageExecution::CpuSimd
            } else {
                EncodedImageExecution::ScalarReference
            },
        }
    }
}
impl EncodedImageDecoder {
    /// Select owned DIB arithmetic explicitly; GPU preferences are incompatible
    /// with this codec API and forced intrinsic execution requires target support.
    pub fn with_execution_preference(preference: ExecutionPreference) -> Result<Self, Error> {
        let execution = match preference {
            ExecutionPreference::Automatic => return Ok(Self::default()),
            ExecutionPreference::CpuSimd if EncodedImageExecution::simd_available() => {
                EncodedImageExecution::CpuSimd
            }
            ExecutionPreference::ScalarReference => EncodedImageExecution::ScalarReference,
            _ => return Err(Error::UnsupportedExecution),
        };
        Ok(Self { execution })
    }
    /// Actual owned conversion policy; external raster backend execution is separate.
    pub fn execution_path(self) -> EncodedImageExecution {
        self.execution
    }
    /// Decode using the existing image dependency's explicit raster boundary.
    pub fn decode(self, source: &[u8]) -> Result<DecodedImage, EncodedImageError> {
        self.decode_with(source, &ImageCrateRasterDecoder)
    }
    /// Decode every original owned container policy with the selected external
    /// raster backend. Only the selected frame is decoded, with no fallback after
    /// malformed selected-frame failure.
    pub fn decode_with(
        self,
        source: &[u8],
        backend: &impl EncodedRasterDecoder,
    ) -> Result<DecodedImage, EncodedImageError> {
        let frame = select_icon_frame(source)?;
        let payload = frame.map_or(source, |f| &source[f.offset..f.offset + f.byte_count]);
        let dib_frame =
            frame.filter(|_| !(payload.len() >= 8 && payload[..4] == [0x89, b'P', b'N', b'G']));
        let (raster, color_space) = if let Some(frame) = dib_frame {
            (dib::decode(payload, frame, self.execution)?, None)
        } else {
            (backend.decode_rgba(payload)?, read_png_color_space(payload))
        };
        let count = rgba_byte_count(raster.size)?;
        if raster.rgba.len() != count {
            return Err(EncodedImageError::RasterInventory);
        }
        Ok(DecodedImage {
            size: raster.size,
            pixels: raster.rgba.into(),
            color_space,
            frame,
        })
    }
}
/// Original largest-area, then highest-depth, then first-entry frame policy.
/// Non-icon/cursor sources return None; invalid directory sources return errors.
pub fn select_icon_frame(source: &[u8]) -> Result<Option<IconFrame>, EncodedImageError> {
    if source.len() < 6 || source[..2] != [0, 0] || source[3] != 0 || !matches!(source[2], 1 | 2) {
        return Ok(None);
    }
    let count = le16(&source[4..]);
    if count == 0 || source.len() < 6 + usize::from(count) * 16 {
        return Err(EncodedImageError::IconDirectory);
    }
    let is_cursor = source[2] == 2;
    let mut selected: Option<IconFrame> = None;
    for index in 0..count {
        let entry = &source[6 + usize::from(index) * 16..][..16];
        let byte_count = le32(&entry[8..]) as usize;
        let offset = le32(&entry[12..]) as usize;
        let Some(end) = offset.checked_add(byte_count) else {
            continue;
        };
        if byte_count == 0 || end > source.len() {
            continue;
        }
        let payload = &source[offset..end];
        let bit_count = if is_cursor {
            if payload.len() < 12 || payload[..4] == [0x89, b'P', b'N', b'G'] {
                0
            } else if le32(payload) == 12 {
                le16(&payload[10..])
            } else if payload.len() < 16 {
                0
            } else {
                le16(&payload[14..])
            }
        } else {
            le16(&entry[6..])
        };
        let candidate = IconFrame {
            index,
            size: [entry[0], entry[1]].map(|v| if v == 0 { 256 } else { u32::from(v) }),
            bit_count,
            color_count: entry[2],
            offset,
            byte_count,
            is_cursor,
        };
        let replace = selected.is_none_or(|previous| {
            let a = candidate.size[0] * candidate.size[1];
            let b = previous.size[0] * previous.size[1];
            a > b || (a == b && candidate.bit_count > previous.bit_count)
        });
        if replace {
            selected = Some(candidate);
        }
    }
    selected.map(Some).ok_or(EncodedImageError::NoIconFrame)
}
/// Original tolerant PNG metadata scan, independent of PNG pixel/CRC admission:
/// first sRGB wins, last nonzero gAMA before IEND supplies a pure-power transfer.
pub fn read_png_color_space(source: &[u8]) -> Option<ColorSpace> {
    if !source.starts_with(b"\x89PNG\r\n\x1a\n") {
        return None;
    }
    let mut gamma = None;
    let mut offset = 8usize;
    while source.len().saturating_sub(offset) >= 12 {
        let length = u32::from_be_bytes(source[offset..offset + 4].try_into().ok()?) as usize;
        let Some(end) = offset
            .checked_add(12)
            .and_then(|v| v.checked_add(length))
            .filter(|&v| v <= source.len())
        else {
            break;
        };
        let ty = &source[offset + 4..offset + 8];
        if ty == b"sRGB" {
            return Some(ColorSpace::srgb());
        }
        if ty == b"gAMA" && length == 4 {
            let value = u32::from_be_bytes(source[offset + 8..offset + 12].try_into().ok()?);
            if value != 0 {
                gamma = Some(value as f32 / 100000.0);
            }
        }
        if ty == b"IEND" {
            break;
        }
        offset = end;
    }
    let gamma = gamma.filter(|v| v.is_finite() && *v > 0.0)?;
    if (gamma - 1.0 / 2.2).abs() <= 0.01 {
        return Some(ColorSpace::srgb());
    }
    ColorSpace::create_rgb(
        ColorSpaceTransferFunction([1.0 / gamma, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
        ColorSpaceXyz::SRGB,
    )
}
pub(super) fn le16(data: &[u8]) -> u16 {
    u16::from_le_bytes([data[0], data[1]])
}
pub(super) fn le32(data: &[u8]) -> u32 {
    u32::from_le_bytes([data[0], data[1], data[2], data[3]])
}
fn count(value: usize) -> Result<usize, EncodedImageError> {
    if value > i32::MAX as usize {
        Err(EncodedImageError::Capacity)
    } else {
        Ok(value)
    }
}
fn rgba_byte_count(size: [u32; 2]) -> Result<usize, EncodedImageError> {
    if size.contains(&0) {
        return Err(EncodedImageError::Dimensions);
    }
    count(
        (size[0] as usize)
            .checked_mul(size[1] as usize)
            .and_then(|v| v.checked_mul(4))
            .ok_or(EncodedImageError::Capacity)?,
    )
}
fn zeros(count: usize) -> Result<Vec<u8>, EncodedImageError> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(count)
        .map_err(|_| EncodedImageError::Capacity)?;
    bytes.resize(count, 0);
    Ok(bytes)
}
