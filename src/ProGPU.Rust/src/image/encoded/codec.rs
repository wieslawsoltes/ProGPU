//! Original SKCodec container admission, size policy and incremental target lease.
use super::{
    DecodedImage, EncodedImageDecoder, EncodedImageError, EncodedRasterDecoder,
    ImageCrateRasterDecoder,
};
use crate::image::raster::{
    Bitmap, ImageInfo, PixelAlphaType as A, PixelColorType as C, RasterError, RasterExecution,
};
use std::sync::Arc;
/// Complete original encoded-format numeric inventory; detection is separate
/// from original CPU decode capability.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EncodedImageFormat {
    /// Bitmap container.
    Bmp = 0,
    /// GIF container.
    Gif = 1,
    /// ICO or CUR container.
    Ico = 2,
    /// JPEG container.
    Jpeg = 3,
    /// PNG container.
    Png = 4,
    /// Original WBMP metadata (no signature detection).
    Wbmp = 5,
    /// WebP metadata.
    Webp = 6,
    /// PKM metadata.
    Pkm = 7,
    /// KTX metadata.
    Ktx = 8,
    /// ASTC metadata.
    Astc = 9,
    /// TIFF/DNG metadata.
    Dng = 10,
    /// HEIF metadata.
    Heif = 11,
    /// AVIF metadata.
    Avif = 12,
    /// JPEG XL metadata.
    Jpegxl = 13,
}
impl EncodedImageFormat {
    /// Original five CPU-decoder families, regardless of external backend support.
    pub const fn is_cpu_decodable(self) -> bool {
        matches!(
            self,
            Self::Bmp | Self::Gif | Self::Ico | Self::Jpeg | Self::Png
        )
    }
}
/// Original codec result numeric values.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CodecResult {
    /// Complete successful operation.
    #[default]
    Success = 0,
    /// Recognized source could not be completely decoded.
    IncompleteInput = 1,
    /// Original input error marker.
    ErrorInInput = 2,
    /// Unsupported target conversion.
    InvalidConversion = 3,
    /// Unsupported scaled dimensions.
    InvalidScale = 4,
    /// Invalid target/options.
    InvalidParameters = 5,
    /// Original invalid input marker.
    InvalidInput = 6,
    /// Original rewind failure marker.
    CouldNotRewind = 7,
    /// Original internal/stream failure marker.
    InternalError = 8,
    /// Original unsupported operation/format.
    Unimplemented = 9,
}
/// Original declared initial storage state, retained but not used by decode.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ZeroInitialized {
    /// Original zero-initialized storage marker.
    Yes = 0,
    /// Original default uninitialized marker.
    #[default]
    No = 1,
}
/// Original codec options; selected subsets remain explicitly unimplemented.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CodecOptions {
    /// Original ignored destination initialization metadata.
    pub zero_initialized: ZeroInitialized,
    /// Original requested subset, independently gated.
    pub subset: Option<[i32; 4]>,
    /// Original frame index; only zero is admitted.
    pub frame_index: i32,
    /// Original prior-frame metadata; values below -1 reject.
    pub prior_frame: i32,
}
impl Default for CodecOptions {
    fn default() -> Self {
        Self {
            zero_initialized: ZeroInitialized::No,
            subset: None,
            frame_index: 0,
            prior_frame: -1,
        }
    }
}
/// Original orientation metadata inventory. This producer always selects TopLeft.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum EncodedOrigin {
    /// Top-left origin.
    #[default]
    TopLeft = 1,
    /// Top-right origin.
    TopRight = 2,
    /// Bottom-right origin.
    BottomRight = 3,
    /// Bottom-left origin.
    BottomLeft = 4,
    /// Left-top origin.
    LeftTop = 5,
    /// Right-top origin.
    RightTop = 6,
    /// Right-bottom origin.
    RightBottom = 7,
    /// Left-bottom origin.
    LeftBottom = 8,
}
/// Original animation blend metadata; no frame inventory is manufactured.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CodecAnimationBlend {
    /// Original source-over marker.
    #[default]
    SrcOver = 0,
    /// Original source marker.
    Src = 1,
}
/// Original disposal markers; zero is retained separately for default frame data.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CodecAnimationDisposal {
    /// Original zero-initialized unspecified value.
    #[default]
    Unspecified = 0,
    /// Keep previous content.
    Keep = 1,
    /// Restore background.
    RestoreBackgroundColor = 2,
    /// Restore previous content.
    RestorePrevious = 3,
}
/// Complete original animation frame metadata. This producer returns no frames.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CodecFrameInfo {
    /// Original required frame.
    pub required_frame: i32,
    /// Original duration.
    pub duration: i32,
    /// Original fully-received marker.
    pub fully_received: bool,
    /// Original frame alpha type.
    pub alpha_type: A,
    /// Original bounded alpha marker.
    pub has_alpha_within_bounds: bool,
    /// Original disposal policy.
    pub disposal: CodecAnimationDisposal,
    /// Original blend policy.
    pub blend: CodecAnimationBlend,
    /// Original frame rectangle.
    pub frame_rect: [i32; 4],
}
/// Original scanline-order metadata; this producer always selects TopDown.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CodecScanlineOrder {
    /// Original top-down marker.
    TopDown,
    /// Original bottom-up marker.
    BottomUp,
}
/// Codec creation preserves original unsupported/incomplete decisions separately
/// from resource and transport errors that the original factory did not contain.
#[derive(Debug, thiserror::Error)]
pub enum CodecError {
    /// Original unknown or unsupported CPU format.
    #[error("encoded image format is not supported")]
    Unimplemented,
    /// Recognized original CPU format with malformed/incomplete data.
    #[error("incomplete encoded image: {0}")]
    Incomplete(EncodedImageError),
    /// An independent allocation/domain error, not a fabricated incomplete source.
    #[error(transparent)]
    Decode(#[from] EncodedImageError),
    /// Actual stream transport failed.
    #[error(transparent)]
    Io(#[from] std::io::Error),
}
impl CodecError {
    /// Original factory out-result for ordinary source/stream failures.
    pub fn result(&self) -> Option<CodecResult> {
        match self {
            Self::Unimplemented => Some(CodecResult::Unimplemented),
            Self::Incomplete(_) => Some(CodecResult::IncompleteInput),
            Self::Io(_) => Some(CodecResult::InternalError),
            Self::Decode(_) => None,
        }
    }
}
/// Owned original encoded bytes and complete decoded source generation.
#[derive(Clone, Debug)]
pub struct ImageCodec {
    source: Arc<[u8]>,
    decoded: DecodedImage,
    info: ImageInfo,
    format: EncodedImageFormat,
}
impl ImageCodec {
    /// Original advertised minimum buffered byte count.
    pub const MIN_BUFFERED_BYTES_NEEDED: usize = 32;
    /// Original format policy with the existing explicit raster backend.
    pub fn create(source: impl Into<Arc<[u8]>>) -> Result<Self, CodecError> {
        Self::create_with(source, &ImageCrateRasterDecoder)
    }
    /// Preserve original format/error policies with an independently selected
    /// external raster backend corresponding to the original dependency boundary.
    pub fn create_with(
        source: impl Into<Arc<[u8]>>,
        backend: &impl EncodedRasterDecoder,
    ) -> Result<Self, CodecError> {
        let source = source.into();
        let format = detect_encoded_format(&source)
            .filter(|v| v.is_cpu_decodable())
            .ok_or(CodecError::Unimplemented)?;
        let decoded = EncodedImageDecoder::default()
            .decode_with(&source, backend)
            .map_err(|error| match error {
                EncodedImageError::Capacity | EncodedImageError::Image(_) => {
                    CodecError::Decode(error)
                }
                _ => CodecError::Incomplete(error),
            })?;
        let [w, h] = decoded.size();
        let info = ImageInfo::with_format(
            i32::try_from(w).map_err(|_| CodecError::Decode(EncodedImageError::Capacity))?,
            i32::try_from(h).map_err(|_| CodecError::Decode(EncodedImageError::Capacity))?,
            C::Rgba8888,
            A::Straight,
            decoded.color_space().cloned(),
        );
        Ok(Self {
            source,
            decoded,
            info,
            format,
        })
    }
    /// Read remaining actual stream bytes, without assuming seekability.
    pub fn from_reader(reader: &mut impl std::io::Read) -> Result<Self, CodecError> {
        let mut data = Vec::new();
        reader.read_to_end(&mut data)?;
        Self::create(data)
    }
    /// Retain original file-open failures separately from malformed file data.
    pub fn from_path(path: impl AsRef<std::path::Path>) -> Result<Self, CodecError> {
        Self::from_reader(&mut std::fs::File::open(path)?)
    }
    /// Actual immutable encoded source bytes.
    pub fn encoded_bytes(&self) -> &[u8] {
        &self.source
    }
    /// Actual immutable decoded generation.
    pub fn decoded(&self) -> &DecodedImage {
        &self.decoded
    }
    /// Original straight-RGBA decoded metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual detected original container.
    pub fn encoded_format(&self) -> EncodedImageFormat {
        self.format
    }
    /// Original producer always reports top-left origin.
    pub fn encoded_origin(&self) -> EncodedOrigin {
        EncodedOrigin::TopLeft
    }
    /// Original no-animation result, not the number of stored ICO directory entries.
    pub fn frame_count(&self) -> usize {
        0
    }
    /// Original zero repetition count.
    pub fn repetition_count(&self) -> i32 {
        0
    }
    /// Original empty frame inventory, including GIF sources.
    pub fn frame_info(&self) -> &[CodecFrameInfo] {
        &[]
    }
    /// Original frame query always reports absent.
    pub fn get_frame_info(&self, _index: i32) -> Option<CodecFrameInfo> {
        None
    }
    /// Original subset query remains unavailable.
    pub fn valid_subset(&self, _subset: &mut [i32; 4]) -> bool {
        false
    }
    /// Original top-down scanline metadata.
    pub fn scanline_order(&self) -> CodecScanlineOrder {
        CodecScanlineOrder::TopDown
    }
    /// Original unavailable scanline cursor.
    pub fn next_scanline(&self) -> i32 {
        -1
    }
    /// Original scanline source transport is explicitly unimplemented.
    pub fn start_scanline_decode(&self, _info: &ImageInfo, _options: CodecOptions) -> CodecResult {
        CodecResult::Unimplemented
    }
    /// Original scanline writer writes zero rows.
    pub fn get_scanlines(&self, _destination: &mut [u8], _count: i32, _row_bytes: i32) -> usize {
        0
    }
    /// Original scanline skip remains unavailable.
    pub fn skip_scanlines(&self, _count: i32) -> bool {
        false
    }
    /// Original output scanline mapping is unchanged input.
    pub fn output_scanline(&self, input: i32) -> i32 {
        input
    }
    /// Original JPEG one-eighth scaling policy; non-JPEG/NaN/upscale returns full size.
    pub fn scaled_dimensions(&self, scale: f32) -> [i32; 2] {
        if scale <= 0.0 {
            return [0, 0];
        }
        if self.format != EncodedImageFormat::Jpeg || scale.is_nan() || scale >= 1.0 {
            return self.info.size();
        }
        self.jpeg_size(((scale * 8.0 + 0.5).floor() as i32).clamp(1, 8))
    }
    fn jpeg_size(&self, n: i32) -> [i32; 2] {
        self.info
            .size()
            .map(|v| ((i64::from(v) * i64::from(n) + 7) / 8) as i32)
    }
    /// Complete original target metadata admission before pixel conversion.
    pub fn validate_target(
        &self,
        info: &ImageInfo,
        row_bytes: i32,
        options: CodecOptions,
    ) -> Result<CodecResult, RasterError> {
        if options.subset.is_some() {
            return Ok(CodecResult::Unimplemented);
        }
        if options.frame_index != 0
            || options.prior_frame < -1
            || info.is_empty()
            || row_bytes < info.row_bytes()?
        {
            return Ok(CodecResult::InvalidParameters);
        }
        if info.bytes_per_pixel() <= 0 {
            return Ok(CodecResult::InvalidConversion);
        }
        if info.size() == self.info.size()
            || (self.format == EncodedImageFormat::Jpeg
                && (1..8).any(|v| self.jpeg_size(v) == info.size()))
        {
            Ok(CodecResult::Success)
        } else {
            Ok(CodecResult::InvalidScale)
        }
    }
    /// Original row copy / decode conversion and untouched destination padding.
    pub fn get_pixels(
        &self,
        info: &ImageInfo,
        destination: &mut [u8],
        row_bytes: i32,
        options: CodecOptions,
        execution: RasterExecution,
    ) -> Result<CodecResult, RasterError> {
        let result = self.validate_target(info, row_bytes, options)?;
        if result != CodecResult::Success {
            return Ok(result);
        }
        if destination.len() < info.validate_storage(row_bytes)? {
            return Ok(CodecResult::InvalidParameters);
        }
        let same_space = match (&info.color_space, &self.info.color_space) {
            (None, None) => true,
            (Some(a), Some(b)) => a.same_owner(b),
            _ => false,
        };
        let width = info.row_bytes()? as usize;
        if info.color_type == C::Rgba8888
            && info.alpha_type == A::Straight
            && info.size() == self.info.size()
            && same_space
        {
            for y in 0..info.height as usize {
                destination[y * row_bytes as usize..][..width]
                    .copy_from_slice(&self.decoded.pixels()[y * width..][..width]);
            }
        } else {
            if !info.color_type.supports_resize() {
                return Ok(CodecResult::InvalidConversion);
            }
            let bitmap = Bitmap::from_decoded(&self.decoded, info.clone(), execution)?;
            let pixels = bitmap.pixels()?;
            for y in 0..info.height as usize {
                destination[y * row_bytes as usize..][..width]
                    .copy_from_slice(&pixels[y * bitmap.row_bytes() as usize..][..width]);
            }
        }
        Ok(CodecResult::Success)
    }
    /// Original allocating read; checked original packed bytes are allocated
    /// before target-result selection, matching the producer's overload order.
    pub fn pixels(
        &self,
        info: &ImageInfo,
        execution: RasterExecution,
    ) -> Result<(CodecResult, Vec<u8>), RasterError> {
        let size = usize::try_from(info.bytes_size()?).map_err(|_| RasterError::Capacity)?;
        let mut bytes = super::super::raster::zeros(size)?;
        let result = self.get_pixels(
            info,
            &mut bytes,
            info.row_bytes()?,
            CodecOptions::default(),
            execution,
        )?;
        Ok((result, bytes))
    }
    /// Freeze one original incremental target under an exclusive codec and
    /// destination borrow. No caller pointer can outlive its actual storage.
    pub fn start_incremental<'a>(
        &'a mut self,
        info: ImageInfo,
        destination: &'a mut [u8],
        row_bytes: i32,
        options: CodecOptions,
        execution: RasterExecution,
    ) -> Result<Result<IncrementalDecode<'a>, CodecResult>, RasterError> {
        let result = self.validate_target(&info, row_bytes, options)?;
        if result != CodecResult::Success {
            return Ok(Err(result));
        }
        if destination.len() < info.validate_storage(row_bytes)? {
            return Ok(Err(CodecResult::InvalidParameters));
        }
        Ok(Ok(IncrementalDecode {
            codec: self,
            info,
            destination,
            row_bytes,
            options,
            execution,
            started: true,
        }))
    }
}
/// Original one-shot deferred full decode, retaining rows_decoded=0 even on
/// success. It is not streaming or a manufactured progressive-frame decoder.
pub struct IncrementalDecode<'a> {
    codec: &'a mut ImageCodec,
    info: ImageInfo,
    destination: &'a mut [u8],
    row_bytes: i32,
    options: CodecOptions,
    execution: RasterExecution,
    started: bool,
}
impl IncrementalDecode<'_> {
    /// Execute once; subsequent calls preserve original InvalidParameters/zero rows.
    pub fn decode(&mut self) -> Result<(CodecResult, i32), RasterError> {
        if !self.started {
            return Ok((CodecResult::InvalidParameters, 0));
        }
        let result = self.codec.get_pixels(
            &self.info,
            self.destination,
            self.row_bytes,
            self.options,
            self.execution,
        )?;
        self.started = false;
        Ok((result, 0))
    }
}
/// Original signature recognition, including recognized-but-unimplemented formats.
pub fn detect_encoded_format(data: &[u8]) -> Option<EncodedImageFormat> {
    use EncodedImageFormat::*;
    if data.starts_with(b"\x89PNG") {
        return Some(Png);
    }
    if data.starts_with(&[0xff, 0xd8]) {
        return Some(Jpeg);
    }
    if data.starts_with(b"GIF") {
        return Some(Gif);
    }
    if data.starts_with(b"BM") {
        return Some(Bmp);
    }
    if data.len() >= 4 && data[..2] == [0, 0] && data[3] == 0 && matches!(data[2], 1 | 2) {
        return Some(Ico);
    }
    if data.len() >= 12 && &data[..4] == b"RIFF" && &data[8..12] == b"WEBP" {
        return Some(Webp);
    }
    if data.starts_with(b"PKM ") {
        return Some(Pkm);
    }
    if data.starts_with(&[0x13, 0xab, 0xa1, 0x5c]) {
        return Some(Astc);
    }
    if data.len() >= 12 && data.starts_with(b"\xabKTX") {
        return Some(Ktx);
    }
    if data.len() >= 12 && &data[4..8] == b"ftyp" {
        let brand = &data[8..12];
        if brand == b"avif" || brand == b"avis" {
            return Some(Avif);
        }
        if [b"heic", b"heix", b"hevc", b"hevx", b"mif1", b"msf1"]
            .iter()
            .any(|v| brand == *v)
        {
            return Some(Heif);
        }
    }
    if data.starts_with(&[0xff, 0x0a]) || (data.len() >= 12 && data.starts_with(b"\0\0\0\x0cJXL "))
    {
        return Some(Jpegxl);
    }
    if data.starts_with(b"II\x2a\0") || data.starts_with(b"MM\0\x2a") {
        return Some(Dng);
    }
    None
}
