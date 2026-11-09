//! Original native PNG admission, caller-owned decode and immutable Image publication.
//! Parsing is O(encoded bytes + chunks), decoding O(encoded bytes + pixels).
//! The low-level core uses caller scratch and fixed stack storage, without a GPU.
use super::{Image, ImageAlpha};
use crate::Error;

mod deflate;
mod filter;
mod layout;
mod parse;
mod pixels;

/// Original native image-error values; success is represented by `Ok`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[repr(u32)]
pub enum PngDecodeError {
    /// Original invalid argument category.
    #[error("invalid PNG decode argument")]
    InvalidArgument = 1,
    /// The complete eight-byte PNG signature is absent.
    #[error("invalid PNG signature")]
    InvalidSignature = 2,
    /// Invalid chunk length, order, inventory or terminal data.
    #[error("invalid PNG chunk")]
    InvalidChunk = 3,
    /// Original PNG chunk CRC mismatch.
    #[error("PNG chunk checksum mismatch")]
    ChecksumMismatch = 4,
    /// Unsupported header/critical chunk or overflowing output layout.
    #[error("unsupported PNG format")]
    UnsupportedFormat = 5,
    /// Caller scratch/output is short, or inflated output exceeds its extent.
    #[error("insufficient PNG decode buffer")]
    InsufficientBuffer = 6,
    /// Invalid zlib/DEFLATE, filter, palette index or inflated byte count.
    #[error("invalid compressed PNG data")]
    InvalidCompressedData = 7,
}

/// Resolved CPU route. Codec work never initializes a GPU; unsupported forced
/// GPU preferences reject independently of the original native error inventory.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PngDecodeExecution {
    /// Intrinsic lanes for independent filter/component arithmetic; dependent
    /// bitstream, checksum and left-neighbor traversal remains ordered scalar.
    CpuSimd,
    /// Original scalar reference arithmetic and traversal.
    ScalarReference,
}
impl PngDecodeExecution {
    fn simd_available() -> bool {
        cfg!(any(
            target_feature = "sse2",
            target_feature = "neon",
            target_feature = "simd128"
        ))
    }
    fn automatic() -> Self {
        if Self::simd_available() {
            Self::CpuSimd
        } else {
            Self::ScalarReference
        }
    }
}

/// Complete original native requirements, produced only after every chunk CRC,
/// inventory/order rule, terminal byte and Adam7 pass layout has been admitted.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PngDecodeRequirements {
    /// Original pixel width.
    pub width: u32,
    /// Original pixel height.
    pub height: u32,
    /// Sum of original consecutive IDAT payload lengths.
    pub compressed_bytes: usize,
    /// Complete decompressed filter-byte/row inventory, including all Adam7 passes.
    pub filtered_bytes: usize,
    /// Tightly packed straight-alpha RGBA8 output bytes.
    pub rgba_bytes: usize,
    /// Original PNG sample depth: 1, 2, 4, 8 or 16 according to color type.
    pub bit_depth: u8,
    /// Original PNG color type: grayscale 0, RGB 2, indexed 3, gray/alpha 4, RGBA 6.
    pub color_type: u8,
    /// Actual stored samples per pixel, independent of RGBA output.
    pub channel_count: u8,
    /// Original interlace mode: noninterlaced 0 or Adam7 1.
    pub interlace_method: u8,
}

/// An owned Image can additionally reject allocation or its existing image-size
/// contract. These failures remain distinct from the native PNG decode result.
#[derive(Debug, thiserror::Error)]
pub enum PngImageError {
    /// Original native codec admission or decode failure.
    #[error(transparent)]
    Decode(#[from] PngDecodeError),
    /// The requested scratch/pixel allocation could not be reserved.
    #[error("PNG storage allocation failed")]
    Allocation,
    /// Existing immutable Image resource admission failed.
    #[error(transparent)]
    Image(#[from] Error),
}

#[derive(Clone, Copy, Debug)]
struct Metadata<'a> {
    requirements: PngDecodeRequirements,
    palette: &'a [u8],
    transparency: &'a [u8],
}

/// Immutable borrowed PNG generation with its complete validated source inventory.
/// Parsing performs no pixel allocation, decompression or WebGPU initialization.
/// Decode reuses this exact immutable byte slice; no second CRC parse is needed.
#[derive(Clone, Debug)]
pub struct PngDecoder<'a> {
    input: &'a [u8],
    metadata: Metadata<'a>,
    layout: layout::Layout,
    execution: PngDecodeExecution,
}
impl<'a> PngDecoder<'a> {
    /// Validate the complete original PNG and retain its exact immutable source.
    pub fn new(input: &'a [u8]) -> Result<Self, PngDecodeError> {
        let (metadata, layout) = parse::parse(input)?;
        Ok(Self {
            input,
            metadata,
            layout,
            execution: PngDecodeExecution::automatic(),
        })
    }
    /// Select the explicit CPU/reference policy without decoding or allocating.
    /// NativeCompute/GpuShader are incompatible with this CPU codec contract;
    /// forced SIMD also rejects targets without a compiled intrinsic backend.
    pub fn with_execution_preference(
        mut self,
        preference: crate::path::ExecutionPreference,
    ) -> Result<Self, Error> {
        use crate::path::ExecutionPreference;
        self.execution = match preference {
            ExecutionPreference::Automatic => PngDecodeExecution::automatic(),
            ExecutionPreference::CpuSimd if PngDecodeExecution::simd_available() => {
                PngDecodeExecution::CpuSimd
            }
            ExecutionPreference::ScalarReference => PngDecodeExecution::ScalarReference,
            _ => return Err(Error::UnsupportedExecution),
        };
        Ok(self)
    }
    /// Actual selected lane policy for source/performance receipts.
    pub fn execution_path(&self) -> PngDecodeExecution {
        self.execution
    }
    /// Exact immutable encoded source retained by this decoder, without a copy.
    pub fn encoded(&self) -> &'a [u8] {
        self.input
    }
    /// Original dimensions and complete scratch/output sizes. O(1), no allocation.
    pub fn requirements(&self) -> PngDecodeRequirements {
        self.metadata.requirements
    }
    /// Decode straight RGBA8 into caller storage without allocating.
    /// All three capacities are checked before writing. Codec failures may update
    /// scratch; RGBA publication follows complete filter and palette admission.
    /// Unused trailing caller storage remains unchanged.
    pub fn decode_rgba_into(
        &self,
        compressed_scratch: &mut [u8],
        filtered_scratch: &mut [u8],
        rgba_output: &mut [u8],
    ) -> Result<PngDecodeRequirements, PngDecodeError> {
        let r = self.requirements();
        if compressed_scratch.len() < r.compressed_bytes
            || filtered_scratch.len() < r.filtered_bytes
            || rgba_output.len() < r.rgba_bytes
        {
            return Err(PngDecodeError::InsufficientBuffer);
        }
        let compressed = &mut compressed_scratch[..r.compressed_bytes];
        let filtered = &mut filtered_scratch[..r.filtered_bytes];
        // The source is immutably borrowed for the lifetime of this decoder.
        // These chunk extents and the complete IDAT inventory were admitted once.
        let mut offset = 8;
        let mut target = 0;
        while offset < self.input.len() {
            let length = parse::read_u32(self.input, offset) as usize;
            if &self.input[offset + 4..offset + 8] == b"IDAT" {
                compressed[target..target + length]
                    .copy_from_slice(&self.input[offset + 8..offset + 8 + length]);
                target += length;
            }
            offset += length + 12;
        }
        let written = deflate::inflate_zlib(compressed, filtered)?;
        if written != r.filtered_bytes {
            return Err(PngDecodeError::InvalidCompressedData);
        }
        filter::unfilter(&self.layout, filtered, self.execution)?;
        pixels::convert(
            self.metadata,
            &self.layout,
            filtered,
            &mut rgba_output[..r.rgba_bytes],
            self.execution,
        )?;
        Ok(r)
    }
    /// Decode and publish one immutable tightly packed straight-alpha Image.
    /// The existing 16384-axis Image limit is checked before scratch allocation;
    /// the independent low-level codec retains its original wider size admission.
    pub fn decode_image(&self) -> Result<Image, PngImageError> {
        let r = self.requirements();
        if r.width > 16384 || r.height > 16384 {
            return Err(Error::InvalidInput.into());
        }
        fn allocate(length: usize) -> Result<Vec<u8>, PngImageError> {
            let mut bytes = Vec::new();
            bytes
                .try_reserve_exact(length)
                .map_err(|_| PngImageError::Allocation)?;
            bytes.resize(length, 0);
            Ok(bytes)
        }
        let mut compressed = allocate(r.compressed_bytes)?;
        let mut filtered = allocate(r.filtered_bytes)?;
        let mut rgba = allocate(r.rgba_bytes)?;
        self.decode_rgba_into(&mut compressed, &mut filtered, &mut rgba)?;
        Ok(Image::rgba8(
            [r.width, r.height],
            ImageAlpha::Straight,
            rgba,
        )?)
    }
}

/// Original native complete requirements query without pixel allocation.
pub fn png_decode_requirements(input: &[u8]) -> Result<PngDecodeRequirements, PngDecodeError> {
    Ok(PngDecoder::new(input)?.requirements())
}
/// Original native caller-scratch decode, retaining precise error categories.
pub fn decode_png_rgba(
    input: &[u8],
    compressed_scratch: &mut [u8],
    filtered_scratch: &mut [u8],
    rgba_output: &mut [u8],
) -> Result<PngDecodeRequirements, PngDecodeError> {
    PngDecoder::new(input)?.decode_rgba_into(compressed_scratch, filtered_scratch, rgba_output)
}
impl Image {
    /// Decode original native PNG into an independently owned straight RGBA8
    /// generation. The immutable encoded inventory is parsed once; no GPU is used.
    pub fn decode_png(input: &[u8]) -> Result<Self, PngImageError> {
        PngDecoder::new(input)?.decode_image()
    }
}
