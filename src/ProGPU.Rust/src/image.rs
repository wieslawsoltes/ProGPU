//! Owned image generations and original retained image drawing contracts.
//! Pixels are never resampled on the CPU. Reconstruction uses canonical WGSL.
use crate::{
    Error,
    geometry::{Affine, Mesh, VectorVertex},
};
use std::sync::Arc;
mod color_filter;
pub use color_filter::{
    ColorFilter, ColorFilterKind, ColorFilterOperation, ColorFilterPlan, ColorFilterStep,
    ColorTableLayout, ColorTables, HighContrast, ImageColorMatrix, ImageColorUniforms, InvertStyle,
    NativeImageColorMatrix, NonlinearColorUniforms, OverdrawColorUniforms,
};
#[cfg(feature = "gpu")]
pub use color_filter::{
    ColorFilterComputeEngine, ColorFilterComputeMetrics, PreparedColorFilterCompute,
};
mod png;
pub use png::{
    PngDecodeError, PngDecodeExecution, PngDecodeRequirements, PngDecoder, PngImageError,
    decode_png_rgba, png_decode_requirements,
};
mod vertices;
pub use vertices::{ImageBrush, TileImageDraw, TileImageSampling};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::GpuImage;

/// Original source alpha representation, independent of draw opacity.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum ImageAlpha {
    /// Straight color components.
    #[default]
    Straight,
    /// Color components already multiplied by source alpha.
    Premultiplied,
}
/// Exact upload channel layout; no CPU swizzling or premultiplication.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ImageFormat {
    /// Four 8-bit RGBA unorm components.
    Rgba8,
    /// Four 8-bit BGRA unorm components.
    Bgra8,
    /// One 8-bit red unorm component, preserving native R8 source semantics.
    R8,
}
impl ImageFormat {
    /// Actual bytes per source pixel.
    pub fn bytes_per_pixel(self) -> u32 {
        if self == Self::R8 { 1 } else { 4 }
    }
}

#[derive(Debug)]
pub(crate) struct Pixels {
    pub size: [u32; 2],
    pub stride: u32,
    pub format: ImageFormat,
    pub alpha: ImageAlpha,
    pub data: Arc<[u8]>,
}
/// Immutable original pixels, stride and alpha interpretation. Cloning shares
/// the actual generation; new pixels require a new owner and cannot stale a draw.
#[derive(Clone, Debug)]
pub struct Image(pub(crate) Arc<Pixels>);
impl Image {
    /// Retain complete strided input. The final row requires its actual pixel
    /// bytes, not unused trailing padding. Original 16384-axis admission remains.
    pub fn new(
        size: [u32; 2],
        stride: u32,
        format: ImageFormat,
        alpha: ImageAlpha,
        data: impl Into<Arc<[u8]>>,
    ) -> Result<Self, Error> {
        let data = data.into();
        let row = size[0]
            .checked_mul(format.bytes_per_pixel())
            .ok_or(Error::Capacity)?;
        let required = u64::from(stride) * u64::from(size[1].saturating_sub(1)) + u64::from(row);
        if size.contains(&0)
            || size.iter().any(|v| *v > 16384)
            || stride < row
            || required > data.len() as u64
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self(Arc::new(Pixels {
            size,
            stride,
            format,
            alpha,
            data,
        })))
    }
    /// Create a tightly packed RGBA generation without changing pixel bytes.
    pub fn rgba8(
        size: [u32; 2],
        alpha: ImageAlpha,
        data: impl Into<Arc<[u8]>>,
    ) -> Result<Self, Error> {
        Self::new(
            size,
            size[0].checked_mul(4).ok_or(Error::Capacity)?,
            ImageFormat::Rgba8,
            alpha,
            data,
        )
    }
    /// Actual physical source extent.
    pub fn size(&self) -> [u32; 2] {
        self.0.size
    }
    /// Original immutable strided storage, including retained caller padding.
    pub fn pixels(&self) -> &[u8] {
        &self.0.data
    }
    /// Actual row byte stride.
    pub fn stride(&self) -> u32 {
        self.0.stride
    }
    /// Original channel interpretation.
    pub fn format(&self) -> ImageFormat {
        self.0.format
    }
    /// Original alpha interpretation.
    pub fn alpha(&self) -> ImageAlpha {
        self.0.alpha
    }
}
/// Owned pixels or an actual same-device GPU texture generation.
#[derive(Clone, Debug)]
pub enum ImageSource {
    /// Upload once per renderer/source generation.
    Pixels(Image),
    /// Retain the actual GPU handle; no readback or re-upload.
    #[cfg(feature = "gpu")]
    Gpu(GpuImage),
}
impl From<Image> for ImageSource {
    fn from(value: Image) -> Self {
        Self::Pixels(value)
    }
}
impl ImageSource {
    /// Actual physical source extent.
    pub fn size(&self) -> [u32; 2] {
        match self {
            Self::Pixels(i) => i.size(),
            #[cfg(feature = "gpu")]
            Self::Gpu(i) => i.size(),
        }
    }
    /// Actual source alpha policy.
    pub fn alpha(&self) -> ImageAlpha {
        match self {
            Self::Pixels(i) => i.alpha(),
            #[cfg(feature = "gpu")]
            Self::Gpu(i) => i.alpha(),
        }
    }
    pub(crate) fn identity(&self) -> (u8, usize) {
        match self {
            Self::Pixels(i) => (0, Arc::as_ptr(&i.0) as usize),
            #[cfg(feature = "gpu")]
            Self::Gpu(i) => (1, i.identity()),
        }
    }
}
/// Original native image sampling values, including all separate mip filters.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ImageSampling {
    /// Base nearest reconstruction.
    Nearest = 0,
    /// Base linear reconstruction.
    #[default]
    Linear = 1,
    /// Original 4-by-4 Mitchell-Netravali reconstruction.
    Cubic = 2,
    /// Linear magnification/minification/mip interpolation; supports anisotropy.
    LinearMipmap = 3,
    /// Linear magnification/minification, nearest mip.
    MagLinearMinLinearMipNearest = 4,
    /// Linear magnification, nearest minification, linear mip.
    MagLinearMinNearestMipLinear = 5,
    /// Linear magnification, nearest minification/mip.
    MagLinearMinNearestMipNearest = 6,
    /// Nearest magnification, linear minification/mip.
    MagNearestMinLinearMipLinear = 7,
    /// Nearest magnification/mip, linear minification.
    MagNearestMinLinearMipNearest = 8,
    /// Nearest magnification/minification, linear mip.
    MagNearestMinNearestMipLinear = 9,
    /// Original bounded 4-by-4 Fant footprint.
    Fant = 10,
}
impl ImageSampling {
    /// Whether this mode consumes a genuine mip chain.
    pub fn uses_mipmaps(self) -> bool {
        (3..=9).contains(&(self as u32))
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn filters(self) -> [bool; 3] {
        use ImageSampling::*;
        [
            matches!(
                self,
                Linear
                    | Cubic
                    | Fant
                    | LinearMipmap
                    | MagLinearMinLinearMipNearest
                    | MagLinearMinNearestMipLinear
                    | MagLinearMinNearestMipNearest
            ),
            matches!(
                self,
                Linear
                    | Cubic
                    | Fant
                    | LinearMipmap
                    | MagLinearMinLinearMipNearest
                    | MagNearestMinLinearMipLinear
                    | MagNearestMinLinearMipNearest
            ),
            matches!(
                self,
                LinearMipmap
                    | MagLinearMinNearestMipLinear
                    | MagNearestMinLinearMipLinear
                    | MagNearestMinNearestMipLinear
            ),
        ]
    }
}
/// Original out-of-extent addressing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum ImageAddress {
    /// Clamp to source edge.
    #[default]
    Clamp,
    /// Repeat the source extent.
    Repeat,
    /// Alternate forward/reversed source periods.
    MirrorRepeat,
}
/// Requested base-level sampling implementation; cubic and mip algorithms stay independent.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ImageSamplingPreference {
    /// Original adapter-aware selection.
    #[default]
    Automatic,
    /// Require native sampler support.
    NativeSampler,
    /// Original explicit nearest/linear/Fant texel-load implementation.
    ExplicitShader,
}
/// Actual original sampling implementation, recorded before geometry compilation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ImageSamplingPath {
    /// Native sampler operations.
    NativeSampler,
    /// Canonical explicit shader taps.
    ExplicitShader,
}
impl ImageSamplingPreference {
    /// Resolve from actual backend/adapter facts. A known unsupported Parallels
    /// D3D12 native sampler rejects a forced selection; no driver flags change.
    pub fn resolve(self, is_d3d12: bool, adapter_name: &str) -> Result<ImageSamplingPath, Error> {
        let unsupported = is_d3d12
            && adapter_name
                .to_ascii_lowercase()
                .contains("parallels display adapter");
        match self {
            Self::NativeSampler if unsupported => Err(Error::UnsupportedExecution),
            Self::ExplicitShader => Ok(ImageSamplingPath::ExplicitShader),
            Self::Automatic if unsupported => Ok(ImageSamplingPath::ExplicitShader),
            _ => Ok(ImageSamplingPath::NativeSampler),
        }
    }
}
/// Original source/destination color blend discriminants, independent of framebuffer blending.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum ImageColorBlend {
    /// Clear both colors.
    Clear,
    /// Source.
    Src,
    /// Destination.
    #[default]
    Dst,
    /// Source over destination.
    SrcOver,
    /// Destination over source.
    DstOver,
    /// Source in destination.
    SrcIn,
    /// Destination in source.
    DstIn,
    /// Source out of destination.
    SrcOut,
    /// Destination out of source.
    DstOut,
    /// Source atop destination.
    SrcATop,
    /// Destination atop source.
    DstATop,
    /// Exclusive source/destination.
    Xor,
    /// Add components.
    Plus,
    /// Component modulation.
    Modulate,
    /// Screen blend.
    Screen,
    /// Overlay blend.
    Overlay,
    /// Darken blend.
    Darken,
    /// Lighten blend.
    Lighten,
    /// Color dodge.
    ColorDodge,
    /// Color burn.
    ColorBurn,
    /// Hard light.
    HardLight,
    /// Soft light.
    SoftLight,
    /// Difference.
    Difference,
    /// Exclusion.
    Exclusion,
    /// Multiply.
    Multiply,
    /// Hue.
    Hue,
    /// Saturation.
    Saturation,
    /// Color.
    Color,
    /// Luminosity.
    Luminosity,
}
/// Original patch payload.
#[derive(Clone, Debug)]
pub enum ImagePatchKind {
    /// Sample original texture colors.
    Texture,
    /// Emit a fixed color without sampling.
    FixedColor([f32; 4]),
    /// Combine sampled source and per-patch destination using the canonical shader.
    AtlasColor {
        /// Original straight RGBA destination color.
        color: [f32; 4],
        /// Original source/destination blend policy.
        blend: ImageColorBlend,
    },
}
/// Original lattice/atlas patch with its independent local transform.
#[derive(Clone, Debug)]
pub struct ImagePatch {
    /// Source x/y/width/height in pixels.
    pub source: [f32; 4],
    /// Destination x/y/width/height in logical units.
    pub destination: [f32; 4],
    /// Original patch-local transform composed before the draw transform.
    pub transform: Affine,
    /// Sampled, fixed or atlas-color payload.
    pub kind: ImagePatchKind,
}
/// Explicit four-corner destination and original homogeneous UV weights.
#[derive(Clone, Copy, Debug)]
pub struct ImageQuad {
    /// Top-left, top-right, bottom-right, bottom-left original corners.
    pub points: [[f32; 2]; 4],
    /// Original four projective components; the shader retains its own threshold.
    pub weights: [f32; 4],
}
/// Retained native image draw plus original managed projective-quad transport.
#[derive(Clone, Debug)]
pub struct ImageDraw {
    /// Immutable actual pixel or GPU generation.
    pub image: ImageSource,
    /// Source x/y/width/height in pixels.
    pub source: [f32; 4],
    /// Destination x/y/width/height in logical units.
    pub destination: [f32; 4],
    /// Original complete affine placement.
    pub transform: Affine,
    /// Effective source draw opacity, finite 0..1.
    pub opacity: f32,
    /// Original independent reconstruction algorithm.
    pub sampling: ImageSampling,
    /// Cubic B/C, each finite and within -16..16.
    pub cubic: [f32; 2],
    /// Original anisotropy; zero canonicalizes to one, only LinearMipmap admits >1.
    pub max_anisotropy: u8,
    /// Independent horizontal/vertical sampler addressing.
    pub address: [ImageAddress; 2],
    /// Admit a source domain outside the full image; incompatible with patches.
    pub extended_source: bool,
    /// Preserve RGB while ignoring source alpha; incompatible with patches.
    pub ignore_source_alpha: bool,
    /// Round each actual transformed physical corner, with ties to even.
    pub snap_to_pixels: bool,
    /// Ordered complete patch inventory; None selects one ordinary quad.
    pub patches: Option<Arc<[ImagePatch]>>,
    /// Original explicit/projective destination, incompatible with patch batches.
    pub quad: Option<ImageQuad>,
}
impl ImageDraw {
    /// One full-source linear draw with unit opacity and identity placement.
    pub fn new(image: impl Into<ImageSource>, destination: [f32; 4]) -> Self {
        let image = image.into();
        let [w, h] = image.size();
        Self {
            image,
            source: [0.0, 0.0, w as f32, h as f32],
            destination,
            transform: Affine::IDENTITY,
            opacity: 1.0,
            sampling: ImageSampling::Linear,
            cubic: [0.0, 0.5],
            max_anisotropy: 1,
            address: [ImageAddress::Clamp; 2],
            extended_source: false,
            ignore_source_alpha: false,
            snap_to_pixels: false,
            patches: None,
            quad: None,
        }
    }
    /// Validate the complete original inventory before recording or publication.
    pub fn validate(&self) -> Result<(), Error> {
        vertices::validate(self)
    }
    /// Compile exact shader vertices at actual DPI and resolved sampling policy.
    pub fn compile(&self, dpi: f32, path: ImageSamplingPath) -> Result<Mesh, Error> {
        self.validate()?;
        let mut mesh = Mesh::default();
        self.append(&mut mesh, dpi, path)?;
        Ok(mesh)
    }
    pub(crate) fn append(
        &self,
        mesh: &mut Mesh,
        dpi: f32,
        path: ImageSamplingPath,
    ) -> Result<(), Error> {
        vertices::append(self, mesh, dpi, path)
    }
    pub(crate) fn binding_key(&self) -> ImageBindingKey {
        ImageBindingKey {
            source: self.image.identity(),
            sampling: self.sampling,
            anisotropy: self.max_anisotropy.max(1),
            address: self.address,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct ImageBindingKey {
    pub source: (u8, usize),
    pub sampling: ImageSampling,
    pub anisotropy: u8,
    pub address: [ImageAddress; 2],
}

/// Original native image matrix draw, retaining geometry and color metadata
/// independently of the shared source/sampler binding.
#[derive(Clone, Debug)]
pub struct ImageColorDraw {
    /// Complete original image/patch inventory and placement.
    pub draw: ImageDraw,
    /// Exact original semantic color matrix and after-matrix luma flag.
    pub matrix: ImageColorMatrix,
}
impl ImageColorDraw {
    /// Whole-inventory admission before recording or GPU mutation.
    pub fn validate(&self) -> Result<(), Error> {
        self.draw.validate()?;
        self.matrix.validate_native()
    }
}
/// Immutable full-source color-filter evaluation followed by the original image
/// placement. Final draw opacity, sampling and masks do not enter filter nodes.
#[derive(Clone, Debug)]
pub struct ColorFilteredImageDraw {
    /// Original final placement and source sampling domain.
    pub draw: ImageDraw,
    /// Actual immutable original filter generation.
    pub filter: ColorFilter,
    /// Checked maximum original operations admitted for this graph.
    pub maximum_steps: usize,
}
impl ColorFilteredImageDraw {
    /// Retain a filter and image with the renderer's bounded default work budget.
    pub fn new(draw: ImageDraw, filter: ColorFilter) -> Self {
        Self {
            draw,
            filter,
            maximum_steps: 16384,
        }
    }
    /// Complete CPU-only admission, including original ordered graph expansion.
    pub fn validate(&self) -> Result<(), Error> {
        self.draw.validate()?;
        self.plan().map(|_| ())
    }
    /// Freeze actual node/source dependencies; no textures or pixels are evaluated.
    pub fn plan(&self) -> Result<ColorFilterPlan, Error> {
        if self.maximum_steps > 16384 {
            return Err(Error::Capacity);
        }
        ColorFilterPlan::new(&self.filter, self.maximum_steps)
    }
}
