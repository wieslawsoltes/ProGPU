//! Original fused image effects and paired Gaussian source preparation.
//! CPU records retain exact shader layouts; realization is lazy and GPU-only.
use crate::Error;
use bytemuck::{Pod, Zeroable};
use wide::f32x4;
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{BlurInput, BlurMetrics, BlurStorage, ImageBlurEngine, PreparedImageBlur};

/// Original normalized YUV range and straight-RGB conversion rows.
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
#[repr(C)]
pub struct YuvConversion {
    /// Y offset/scale, chroma offset/scale; no inferred color-space policy.
    pub range: [f32; 4],
    /// Original red dot-product coefficients.
    pub red: [f32; 4],
    /// Original green dot-product coefficients.
    pub green: [f32; 4],
    /// Original blue dot-product coefficients.
    pub blue: [f32; 4],
}
impl YuvConversion {
    /// Validate all original components without clamping or selecting a matrix.
    pub fn validate(self) -> Result<(), Error> {
        finite(bytemuck::cast_slice(std::slice::from_ref(&self)))
    }
}
fn finite(values: &[f32]) -> Result<(), Error> {
    if values.iter().all(|v| v.is_finite()) {
        Ok(())
    } else {
        Err(Error::InvalidInput)
    }
}
fn flag(value: f32) -> bool {
    value == 0.0 || value == 1.0
}

/// Exact 288-byte shared ImageEffect.wgsl record. Original operation order is
/// brightness, contrast, saturation, grayscale, sepia, invert, luma, matrix, clamp.
/// Sampling/YUV/spherical mapping precede these operations; coverage follows.
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
#[repr(C)]
pub struct ImageEffectUniforms {
    /// Red matrix row, including the original source-alpha coefficient.
    pub color_matrix_red: [f32; 4],
    /// Green matrix row.
    pub color_matrix_green: [f32; 4],
    /// Blue matrix row.
    pub color_matrix_blue: [f32; 4],
    /// Alpha matrix row.
    pub color_matrix_alpha: [f32; 4],
    /// RGBA matrix offsets, added after each dot product.
    pub color_matrix_offset: [f32; 4],
    /// Brightness, contrast, saturation, grayscale.
    pub effects0: [f32; 4],
    /// Sepia, invert, original source blur sigma, primary-mask enable flag.
    pub effects1: [f32; 4],
    /// Actual mask width/height, source-premultiplied and output-premultiplied flags.
    pub texture0: [f32; 4],
    /// YUV enable, original reserved binary lane, color-matrix and luma flags.
    pub flags0: [f32; 4],
    /// Original Y/chroma offset and scale.
    pub yuv_range: [f32; 4],
    /// Original YUV red row.
    pub yuv_red: [f32; 4],
    /// Original YUV green row.
    pub yuv_green: [f32; 4],
    /// Original YUV blue row.
    pub yuv_blue: [f32; 4],
    /// Spherical enable, horizontal FOV radians, output aspect, original padding.
    pub spherical0: [f32; 4],
    /// Original normalized source x/y/width/height.
    pub spherical_uv_rect: [f32; 4],
    /// Original quaternion-derived matrix first row; no matrix reconstruction.
    pub spherical_rotation0: [f32; 4],
    /// Original quaternion-derived matrix second row.
    pub spherical_rotation1: [f32; 4],
    /// Original quaternion-derived matrix third row.
    pub spherical_rotation2: [f32; 4],
}
impl Default for ImageEffectUniforms {
    fn default() -> Self {
        Self {
            effects0: [0.0, 1.0, 1.0, 0.0],
            texture0: [1.0, 1.0, 0.0, 0.0],
            spherical_rotation0: [1.0, 0.0, 0.0, 0.0],
            spherical_rotation1: [0.0, 1.0, 0.0, 0.0],
            spherical_rotation2: [0.0, 0.0, 1.0, 0.0],
            ..Zeroable::zeroed()
        }
    }
}
impl ImageEffectUniforms {
    /// Original finite/binary/radius admission, allowing the managed alpha modes.
    /// Source-specific native transport retains its stricter straight-alpha rule.
    pub fn validate(&self) -> Result<(), Error> {
        finite(bytemuck::cast_slice(std::slice::from_ref(self)))?;
        if !(0.0..=32.0).contains(&self.effects1[2])
            || !flag(self.effects1[3])
            || self.flags0.iter().any(|v| !flag(*v))
            || self.texture0[0] <= 0.0
            || self.texture0[1] <= 0.0
            || !flag(self.texture0[2])
            || !flag(self.texture0[3])
            || !flag(self.spherical0[0])
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    /// Exact original native packet policy, independently of managed alpha modes.
    pub fn validate_native(&self, unfilterable_planar: bool) -> Result<(), Error> {
        self.validate()?;
        if self.texture0[2] != 0.0
            || self.texture0[3] != 0.0
            || (unfilterable_planar && self.flags0[0] != 1.0)
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    /// Original selected YUV descriptor, absent for RGB sources.
    pub fn yuv(&self) -> Option<YuvConversion> {
        (self.flags0[0] > 0.5).then_some(YuvConversion {
            range: self.yuv_range,
            red: self.yuv_red,
            green: self.yuv_green,
            blue: self.yuv_blue,
        })
    }
    /// Original live-blur output is RGB; disable only blur and YUV decoding.
    pub fn after_live_blur(mut self) -> Self {
        self.effects1[2] = 0.0;
        self.flags0[0] = 0.0;
        self
    }
}

/// Original 912-byte normalized Gaussian record, shared by the filterable and
/// integer-load planar shaders. At most 96 positive taps form 48 pairs.
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
#[repr(C)]
pub struct ImageBlurUniforms {
    /// Physical one-texel UV direction, normalized center weight, pair count.
    pub axis: [f32; 4],
    /// Original post-blur affine straight-RGB red row.
    pub red: [f32; 4],
    /// Original post-blur affine straight-RGB green row.
    pub green: [f32; 4],
    /// Original post-blur affine straight-RGB blue row.
    pub blue: [f32; 4],
    /// YUV decode flag and original unused lanes.
    pub options: [f32; 4],
    /// Original Y/chroma offset and scale.
    pub yuv_range: [f32; 4],
    /// Original YUV red row.
    pub yuv_red: [f32; 4],
    /// Original YUV green row.
    pub yuv_green: [f32; 4],
    /// Original YUV blue row.
    pub yuv_blue: [f32; 4],
    /// Reduced offset/combined weight and separate first/second weights.
    pub taps: [[f32; 4]; 48],
}
impl ImageBlurUniforms {
    /// Port of the original C++ paired-tap builder. Ordered double normalization
    /// retains its dependent sum; independent normalization/pair lanes use SIMD.
    pub fn new(sigma: f32, direction: [f32; 2], yuv: Option<YuvConversion>) -> Result<Self, Error> {
        Self::build(sigma, direction, yuv, false)
    }
    /// Original managed MathF.Exp/float-denominator variant. It deliberately
    /// remains independent of the native double-transcendental contract.
    pub fn managed(
        sigma: f32,
        direction: [f32; 2],
        yuv: Option<YuvConversion>,
    ) -> Result<Self, Error> {
        Self::build(sigma, direction, yuv, true)
    }
    fn build(
        sigma: f32,
        direction: [f32; 2],
        yuv: Option<YuvConversion>,
        managed: bool,
    ) -> Result<Self, Error> {
        if !sigma.is_finite() || !(0.0..=32.0).contains(&sigma) {
            return Err(Error::InvalidInput);
        }
        finite(&direction)?;
        if let Some(yuv) = yuv {
            yuv.validate()?;
        }
        let mut output = Self {
            axis: [direction[0], direction[1], 1.0, 0.0],
            red: [1.0, 0.0, 0.0, 0.0],
            green: [0.0, 1.0, 0.0, 0.0],
            blue: [0.0, 0.0, 1.0, 0.0],
            ..Zeroable::zeroed()
        };
        if let Some(yuv) = yuv {
            output.options[0] = 1.0;
            output.yuv_range = yuv.range;
            output.yuv_red = yuv.red;
            output.yuv_green = yuv.green;
            output.yuv_blue = yuv.blue;
        }
        if sigma <= 0.01 {
            return Ok(output);
        }
        let radius = (3.0 * sigma).ceil().min(96.0) as usize;
        let mut weights = [0.0f32; 100];
        weights[0] = 1.0;
        // Source std::exp double evaluation and its ordered rounded-float sum.
        // A vector transcendental approximation cannot replace this contract.
        let denominator = 2.0 * f64::from(sigma) * f64::from(sigma);
        let managed_denominator = 2.0f32 * sigma * sigma;
        let mut total = 1.0f64;
        for (i, weight) in weights.iter_mut().enumerate().take(radius + 1).skip(1) {
            *weight = if managed {
                (-((i * i) as f32) / managed_denominator).exp()
            } else {
                (-((i * i) as f64) / denominator).exp() as f32
            };
            total += 2.0 * f64::from(*weight);
        }
        let inverse = f32x4::splat((1.0 / total) as f32);
        for chunk in weights.chunks_exact_mut(4) {
            let values = f32x4::from([chunk[0], chunk[1], chunk[2], chunk[3]]) * inverse;
            chunk.copy_from_slice(&values.to_array());
        }
        let count = radius.div_ceil(2);
        output.axis[2] = weights[0];
        output.axis[3] = count as f32;
        for base in (0..count).step_by(4) {
            let first_index = std::array::from_fn::<_, 4, _>(|lane| (base + lane) * 2 + 1);
            let first = f32x4::from(first_index.map(|i| weights.get(i).copied().unwrap_or(0.0)));
            let second =
                f32x4::from(first_index.map(|i| weights.get(i + 1).copied().unwrap_or(0.0)));
            let combined = first + second;
            let indices = f32x4::from(first_index.map(|i| i as f32));
            let totals = combined.to_array();
            let safe = f32x4::from(totals.map(|v| if v > 0.0 { v } else { 1.0 }));
            let offsets = ((indices * first + (indices + f32x4::ONE) * second) / safe).to_array();
            let first = first.to_array();
            let second = second.to_array();
            for lane in 0..(count - base).min(4) {
                output.taps[base + lane] = [
                    if totals[lane] > 0.0 {
                        offsets[lane]
                    } else {
                        first_index[lane] as f32
                    },
                    totals[lane],
                    first[lane],
                    second[lane],
                ];
            }
        }
        Ok(output)
    }
    /// Both original axes; YUV decoding occurs only on the horizontal source.
    pub fn pair(
        sigma: f32,
        size: [u32; 2],
        yuv: Option<YuvConversion>,
    ) -> Result<[Self; 2], Error> {
        if size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        Ok([
            Self::new(sigma, [1.0 / size[0] as f32, 0.0], yuv)?,
            Self::new(sigma, [0.0, 1.0 / size[1] as f32], None)?,
        ])
    }
}
const _: () = {
    assert!(size_of::<ImageEffectUniforms>() == 288);
    assert!(std::mem::offset_of!(ImageEffectUniforms, spherical0) == 208);
    assert!(size_of::<ImageBlurUniforms>() == 912);
    assert!(std::mem::offset_of!(ImageBlurUniforms, taps) == 144);
};

/// Retained RGB pixels/texture or an actual owned planar texture pair.
#[derive(Clone, Debug)]
pub enum ImageEffectSource {
    /// Original immutable RGB pixels or actual sampled GPU handle.
    Rgb(crate::image::ImageSource),
    /// Actual GPU RGB/planar source, range/matrix and sampling capability.
    #[cfg(feature = "gpu")]
    Gpu(BlurInput),
}
impl ImageEffectSource {
    /// Actual full source extent, independent of the output quad.
    pub fn size(&self) -> [u32; 2] {
        match self {
            Self::Rgb(i) => i.size(),
            #[cfg(feature = "gpu")]
            Self::Gpu(i) => i.size(),
        }
    }
}
impl From<crate::image::Image> for ImageEffectSource {
    fn from(value: crate::image::Image) -> Self {
        Self::Rgb(value.into())
    }
}
impl From<crate::image::ImageSource> for ImageEffectSource {
    fn from(value: crate::image::ImageSource) -> Self {
        Self::Rgb(value)
    }
}
#[cfg(feature = "gpu")]
impl From<crate::image::GpuImage> for ImageEffectSource {
    fn from(value: crate::image::GpuImage) -> Self {
        Self::Rgb(value.into())
    }
}
#[cfg(feature = "gpu")]
impl From<BlurInput> for ImageEffectSource {
    fn from(value: BlurInput) -> Self {
        Self::Gpu(value)
    }
}
/// One immutable fused-effect draw. The four original vertices retain exact
/// position/color/UV bits; transformations belong to the producer, not the filter.
#[derive(Clone, Debug)]
pub struct ImageEffectDraw {
    /// Actual retained image or complete planar source.
    pub source: ImageEffectSource,
    /// Original four vertices in painter/root coordinates.
    pub vertices: [crate::geometry::VectorVertex; 4],
    /// Exact fused shader parameters. Source alpha/YUV and primary-mask flags
    /// are resolved from actual retained resources at preparation.
    pub effect: ImageEffectUniforms,
    /// Original hardware sampler. Cubic/Fant require the separate Texture shader
    /// and reject here rather than silently selecting linear reconstruction.
    pub sampling: crate::image::ImageSampling,
    /// Original independent addressing for the selected source sampler.
    pub address: [crate::image::ImageAddress; 2],
    /// Optional explicit source mask. Like the original extension, this overrides
    /// the per-draw outer mask chain, while independent binary clips remain active.
    pub mask: Option<crate::image::ImageSource>,
    /// Native versus managed live-blur storage and parameter arithmetic.
    pub blur_storage: ImageBlurPolicy,
}
/// CPU-recordable original live-blur policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ImageBlurPolicy {
    /// Original native RGBA8/double-exponential path.
    #[default]
    Native,
    /// Original managed source-format/float-exponential path.
    Managed,
}
impl ImageEffectDraw {
    /// Record a standard quad with normalized full-source UVs and unit color.
    /// No source sampling, shader initialization or font work occurs here.
    pub fn new(source: impl Into<ImageEffectSource>, bounds: [f32; 4]) -> Self {
        let [x, y, w, h] = bounds;
        let points = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
        let uv = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
        Self {
            source: source.into(),
            vertices: std::array::from_fn(|i| crate::geometry::VectorVertex {
                position: points[i],
                color: [1.0; 4],
                texture_coordinate: uv[i],
                ..Zeroable::zeroed()
            }),
            effect: Default::default(),
            sampling: crate::image::ImageSampling::Linear,
            address: [crate::image::ImageAddress::Clamp; 2],
            mask: None,
            blur_storage: Default::default(),
        }
    }
    /// Whole original numeric inventory; GPU identity/format remain realization checks.
    pub fn validate(&self) -> Result<(), Error> {
        self.effect.validate()?;
        finite(bytemuck::cast_slice(&self.vertices))?;
        if matches!(
            self.sampling,
            crate::image::ImageSampling::Cubic | crate::image::ImageSampling::Fant
        ) {
            return Err(Error::UnsupportedExecution);
        }
        if self.source.size().contains(&0) {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}
