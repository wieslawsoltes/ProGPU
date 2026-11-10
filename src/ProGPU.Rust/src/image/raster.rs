//! Original CPU pixel-info, pixmap and bitmap producers with safe storage leases.
//! Rendering continues to use the GPU image/reconstruction stack. These explicit
//! raster APIs preserve original byte conversion and CPU resize semantics.
use super::{ColorSpace, Image, ImageAlpha, ImageFormat};
use crate::{Error, path::ExecutionPreference};
mod bitmap;
mod color;
mod encode;
mod info;
mod pixmap;
mod resize;
pub use bitmap::{Bitmap, BitmapPixels, BitmapPixelsMut, RasterSnapshot};
pub use encode::{
    ImageCrateRasterEncoder, JpegAlphaOption, JpegDownsample, JpegOptions, PngOptions,
    RasterEncoder, RasterEncoding, WebpCompression, WebpOptions,
};
pub use info::{ImageInfo, PixelAlphaType, PixelColorType};
pub use pixmap::{Pixmap, PixmapMut};

/// Original CPU reconstruction filter, independent of mip-chain selection.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum RasterFilterMode {
    /// Original nearest reconstruction.
    #[default]
    Nearest,
    /// Original linear reconstruction.
    Linear,
}
/// Original retained mip request; CPU bitmap resize does not sample a mip chain.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum RasterMipmapMode {
    /// No mip chain.
    #[default]
    None,
    /// Nearest mip.
    Nearest,
    /// Linear mip interpolation.
    Linear,
}
/// Complete original bitmap sampling metadata; CPU resize retains its deliberate
/// independent policy for cubic, anisotropy, filter and ignored mip requests.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct RasterSampling {
    /// Original independent min/mag reconstruction selection.
    pub filter: RasterFilterMode,
    /// Original requested mip policy, ignored by CPU bitmap resize.
    pub mipmap: RasterMipmapMode,
    /// Original explicit cubic coefficients; absence retains filter selection.
    pub cubic: Option<[f32; 2]>,
    /// Original maximum anisotropy; positive values select linear CPU sampling.
    pub max_anisotropy: i32,
}
impl RasterSampling {
    /// Original anisotropic constructor clamps the requested maximum to at least one.
    pub fn anisotropic(maximum: i32) -> Self {
        Self {
            max_anisotropy: maximum.max(1),
            ..Self::NEAREST
        }
    }
    /// Original default nearest/no-mip request.
    pub const NEAREST: Self = Self {
        filter: RasterFilterMode::Nearest,
        mipmap: RasterMipmapMode::None,
        cubic: None,
        max_anisotropy: 0,
    };
    /// Original linear/no-mip request.
    pub const LINEAR: Self = Self {
        filter: RasterFilterMode::Linear,
        ..Self::NEAREST
    };
    /// Original Mitchell cubic request.
    pub const MITCHELL: Self = Self {
        cubic: Some([1.0 / 3.0, 1.0 / 3.0]),
        ..Self::NEAREST
    };
    /// Original deprecated quality lowering, including unknown-value nearest.
    pub fn from_quality(quality: i32) -> Self {
        match quality {
            1 => Self::LINEAR,
            2 => Self {
                mipmap: RasterMipmapMode::Linear,
                ..Self::LINEAR
            },
            3 => Self::MITCHELL,
            _ => Self::NEAREST,
        }
    }
}

/// Typed original raster producer execution; external codec backends keep their
/// own implementation policy and no GPU is implicitly initialized here.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RasterExecution {
    /// Independent component arithmetic uses safe hardware intrinsic lanes.
    CpuSimd,
    /// Explicit scalar reference arithmetic.
    ScalarReference,
}
impl Default for RasterExecution {
    fn default() -> Self {
        if Self::simd_available() {
            Self::CpuSimd
        } else {
            Self::ScalarReference
        }
    }
}
impl RasterExecution {
    fn simd_available() -> bool {
        cfg!(any(
            target_feature = "sse2",
            target_feature = "neon",
            target_feature = "simd128"
        ))
    }
    /// Resolve the explicit CPU producer contract. Higher rendering APIs retain
    /// their GPU-first policy; forced GPU requests are not silently run here.
    pub fn from_preference(preference: ExecutionPreference) -> Result<Self, Error> {
        match preference {
            ExecutionPreference::Automatic => Ok(Self::default()),
            ExecutionPreference::CpuSimd if Self::simd_available() => Ok(Self::CpuSimd),
            ExecutionPreference::ScalarReference => Ok(Self::ScalarReference),
            _ => Err(Error::UnsupportedExecution),
        }
    }
}
/// Raster publication/admission failure. Unsupported source pixel APIs remain
/// separate from valid storage of an otherwise known format.
#[derive(Debug, thiserror::Error)]
pub enum RasterError {
    /// Original dimensions, row stride or source/destination extent is invalid.
    #[error("invalid raster storage or extent")]
    InvalidStorage,
    /// Caller byte inventory is smaller than its complete declared storage.
    #[error("pixel buffer is smaller than the declared image storage")]
    InsufficientPixels,
    /// Coordinates are outside the original pixel extent.
    #[error("pixel coordinates must be inside the bitmap")]
    Coordinates,
    /// This operation has no original producer implementation for the format.
    #[error("pixel format is not supported by this raster operation")]
    UnsupportedFormat,
    /// An original signed byte count or allocation exceeds capacity.
    #[error("raster capacity exceeded")]
    Capacity,
    /// The selected byte storage was poisoned by an unwinding mutable client.
    #[error("raster storage is poisoned")]
    Poisoned,
    /// External encoding or stream transport failed.
    #[error("raster transport failed: {0}")]
    Transport(String),
    /// Actual decoder failed before bitmap publication.
    #[error(transparent)]
    Decode(#[from] super::EncodedImageError),
    /// Original codec factory rejected an unsupported/incomplete source.
    #[error(transparent)]
    Codec(#[from] super::CodecError),
    /// Existing image/render policy rejected the requested publication.
    #[error(transparent)]
    Image(#[from] Error),
}
pub(super) fn zeros(size: usize) -> Result<Vec<u8>, RasterError> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(size)
        .map_err(|_| RasterError::Capacity)?;
    bytes.resize(size, 0);
    Ok(bytes)
}
fn copied(bytes: &[u8]) -> Result<Vec<u8>, RasterError> {
    let mut out = Vec::new();
    out.try_reserve_exact(bytes.len())
        .map_err(|_| RasterError::Capacity)?;
    out.extend_from_slice(bytes);
    Ok(out)
}
