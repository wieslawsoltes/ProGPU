use super::*;
use crate::{
    canvas::CanvasMatrix,
    image::ColorFilter,
    image_filter::{FilterArithmetic, FilterTileMode},
};
/// Original mask-blur composition, independent of ordinary image blur.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum MaskBlurStyle {
    /// Blur original coverage.
    #[default]
    Normal,
    /// Original coverage over blurred coverage.
    Solid,
    /// Blurred coverage outside original.
    Outer,
    /// Blurred coverage inside original.
    Inner,
}
/// Original immutable coverage-only brush effect.
#[derive(Clone, Debug)]
pub enum PaintMaskFilter {
    /// Original sigma/style and independent respect-current-transform policy.
    Blur {
        /// Source blur style.
        style: MaskBlurStyle,
        /// Positive finite original sigma.
        sigma: f32,
        /// Scale sigma by the larger current basis length.
        respect_transform: bool,
    },
    /// Exact alpha table; RGB retains the identity table.
    Table(Arc<[u8; 256]>),
    /// Actual shader owner used as original DstIn coverage.
    Shader(Arc<FilterShaderSource>),
}
impl PaintMaskFilter {
    /// Original invalid-sigma factory returns None.
    pub fn blur(style: MaskBlurStyle, sigma: f32, respect_transform: bool) -> Option<Self> {
        (sigma.is_finite() && sigma > 0.0).then_some(Self::Blur {
            style,
            sigma,
            respect_transform,
        })
    }
    /// Exact copied inventory; short/long tables reject atomically.
    pub fn table(table: &[u8]) -> Result<Self, Error> {
        Ok(Self::Table(Arc::new(
            table.try_into().map_err(|_| Error::InvalidInput)?,
        )))
    }
    /// Original threshold/interpolated clip transfer and ties-to-even rounding.
    pub fn clip(minimum: u8, maximum: u8) -> Self {
        let mut table = [0; 256];
        if minimum < maximum {
            let scale = 255.0 / f32::from(maximum - minimum);
            let mut i = usize::from(minimum) + 1;
            while i + 4 <= usize::from(maximum) {
                let lanes = wide::f32x4::from(std::array::from_fn(|lane| {
                    (i + lane - usize::from(minimum)) as f32
                })) * wide::f32x4::splat(scale);
                for (out, value) in table[i..i + 4].iter_mut().zip(lanes.to_array()) {
                    *out = value.round_ties_even().clamp(0.0, 255.0) as u8;
                }
                i += 4;
            }
            while i < usize::from(maximum) {
                table[i] = ((i - usize::from(minimum)) as f32 * scale)
                    .round_ties_even()
                    .clamp(0.0, 255.0) as u8;
                i += 1;
            }
        }
        table[usize::from(maximum)..].fill(255);
        Self::Table(Arc::new(table))
    }
    /// Original gamma factory. Scalar powf preserves its original transcendental
    /// behavior; independent scale/round lanes retain exact source arithmetic.
    pub fn gamma(gamma: f32) -> Option<Self> {
        if !gamma.is_finite() || gamma <= 0.0 {
            return None;
        }
        let mut table = [0; 256];
        for (group, output) in table.chunks_exact_mut(4).enumerate() {
            let input = (wide::f32x4::from(std::array::from_fn(|lane| (group * 4 + lane) as f32))
                / wide::f32x4::splat(255.0))
            .to_array();
            let scaled = (wide::f32x4::from(input.map(|v| v.powf(gamma)))
                * wide::f32x4::splat(255.0))
            .to_array();
            for (output, value) in output.iter_mut().zip(scaled) {
                *output = value.round_ties_even().clamp(0.0, 255.0) as u8;
            }
        }
        Some(Self::Table(Arc::new(table)))
    }
    /// Original finite radius conversion.
    pub fn radius_to_sigma(radius: f32) -> f32 {
        if radius.is_finite() && radius > 0.0 {
            0.57735 * radius + 0.5
        } else {
            0.0
        }
    }
    /// Original inverse conversion and half-sigma threshold.
    pub fn sigma_to_radius(sigma: f32) -> f32 {
        if sigma.is_finite() && sigma > 0.5 {
            (sigma - 0.5) / 0.57735
        } else {
            0.0
        }
    }
    /// Lower the original coverage expression to the owned image-filter kernels.
    pub fn image_filter(&self, transform: CanvasMatrix) -> Result<ImageFilter, Error> {
        match self {
            Self::Blur {
                style,
                sigma,
                respect_transform,
            } => {
                let mut sigma = *sigma;
                let m = transform.0;
                if *respect_transform {
                    let x = (m[0] * m[0] + m[3] * m[3]).sqrt();
                    let y = (m[1] * m[1] + m[4] * m[4]).sqrt();
                    let scale = if x.is_nan() || y.is_nan() {
                        f32::NAN
                    } else {
                        x.max(y)
                    };
                    if scale.is_finite() && scale > 0.0 {
                        sigma *= scale;
                    }
                }
                let blur = ImageFilter::blur([sigma; 2], FilterTileMode::Decal, None, None)?;
                match style {
                    MaskBlurStyle::Normal => Ok(blur),
                    MaskBlurStyle::Solid => ImageFilter::blend(
                        FilterBlender::Mode(LayerBlend::SrcOver),
                        Some(blur),
                        None,
                        None,
                    ),
                    MaskBlurStyle::Outer | MaskBlurStyle::Inner => ImageFilter::arithmetic(
                        FilterArithmetic {
                            coefficients: if *style == MaskBlurStyle::Outer {
                                [-1.0, 1.0, 0.0, 0.0]
                            } else {
                                [1.0, 0.0, 0.0, 0.0]
                            },
                            enforce_premultiplied: true,
                        },
                        Some(blur),
                        None,
                        None,
                    ),
                }
            }
            Self::Table(table) => {
                let identity = std::array::from_fn::<_, 256, _>(|i| i as u8);
                ImageFilter::color_filter(
                    ColorFilter::tables(table.as_ref(), &identity, &identity, &identity)?,
                    None,
                    None,
                )
            }
            Self::Shader(shader) => ImageFilter::blend(
                FilterBlender::Mode(LayerBlend::DstIn),
                None,
                Some(ImageFilter::shader(Some(shader.clone()), false, None)?),
                None,
            ),
        }
    }
}
