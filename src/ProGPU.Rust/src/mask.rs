//! Retained native analytic and R8 coverage masks. These are per-draw coverage
//! operations; isolated group opacity and source AA-layer composition are separate.
use crate::{
    Error,
    geometry::Affine,
    image::{Image, ImageFormat},
};
use bytemuck::{Pod, Zeroable};
use wide::f32x4;

/// Original rounded-mask coverage policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum AnalyticMaskPolicy {
    /// Original derivative-based rounded rectangle coverage.
    #[default]
    Rounded,
    /// Exact physical rectangular pixel area. Requires identity transform,
    /// zero radii and opacity one; it does not implement isolated AA layers.
    AxisClipArea,
}

/// Immutable-at-recording native rounded rectangle descriptor.
#[derive(Clone, Debug)]
pub struct AnalyticMask {
    /// Original local X, Y, width and height.
    pub bounds: [f32; 4],
    /// Local-to-target logical mapping.
    pub transform: Affine,
    /// Top-left, top-right, bottom-right and bottom-left horizontal radii.
    pub radii_x: [f32; 4],
    /// Independent vertical radii in the same order.
    pub radii_y: [f32; 4],
    /// Mask opacity in 0..=1.
    pub opacity: f32,
    /// Explicit original coverage algorithm.
    pub policy: AnalyticMaskPolicy,
}
impl AnalyticMask {
    /// A target-logical rectangle with ordinary rounded-distance coverage.
    pub fn rectangle(bounds: [f32; 4]) -> Self {
        Self {
            bounds,
            transform: Affine::IDENTITY,
            radii_x: [0.0; 4],
            radii_y: [0.0; 4],
            opacity: 1.0,
            policy: AnalyticMaskPolicy::Rounded,
        }
    }
    /// An explicit axis-aligned per-draw area mask.
    pub fn axis_clip_area(bounds: [f32; 4]) -> Self {
        Self {
            policy: AnalyticMaskPolicy::AxisClipArea,
            ..Self::rectangle(bounds)
        }
    }
}

/// Original coverage bitmap filtering, with no mip generation or reconstruction.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum MaskSampling {
    /// Nearest original R8 texel.
    Nearest,
    /// Original bilinear filtering.
    #[default]
    Linear,
}

/// Owned original strided R8 coverage, mapped independently from paint.
#[derive(Clone, Debug)]
pub struct CoverageMask {
    /// Immutable R8 pixels. Other formats are rejected.
    pub image: Image,
    /// Original local X, Y, width and height.
    pub bounds: [f32; 4],
    /// Local-to-target logical mapping.
    pub transform: Affine,
    /// Native nearest or linear sampling.
    pub sampling: MaskSampling,
    /// Coverage multiplier in 0..=1.
    pub opacity: f32,
}

/// Retained coverage source. Recorders retain pixels without copying them.
#[derive(Clone, Debug)]
pub enum Mask {
    /// Native analytic rounded rectangle or explicit axis area.
    Analytic(AnalyticMask),
    /// Original immutable coverage bitmap.
    Coverage(CoverageMask),
}
impl From<AnalyticMask> for Mask {
    fn from(value: AnalyticMask) -> Self {
        Self::Analytic(value)
    }
}
impl From<CoverageMask> for Mask {
    fn from(value: CoverageMask) -> Self {
        Self::Coverage(value)
    }
}

/// Original 96-byte shader record. No CPU evaluation replaces the shared WGSL.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct MaskUniforms {
    /// Physical-position-to-local/UV first row.
    pub coordinate0: [f32; 4],
    /// Physical-position-to-local/UV second row.
    pub coordinate1: [f32; 4],
    /// Original near and far edges.
    pub bounds: [f32; 4],
    /// Normalized original horizontal corner radii.
    pub radii_x: [f32; 4],
    /// Normalized original vertical corner radii.
    pub radii_y: [f32; 4],
    /// Original kind, opacity and sampling flags.
    pub options: [f32; 4],
}
const _: () = assert!(size_of::<MaskUniforms>() == 96);

fn rect(bounds: [f32; 4]) -> Result<(), Error> {
    if bounds.iter().any(|v| !v.is_finite()) || bounds[2] <= 0.0 || bounds[3] <= 0.0 {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
fn narrow(values: [f64; 6]) -> Result<[f32; 6], Error> {
    if values
        .iter()
        .any(|v| !v.is_finite() || v.abs() > f64::from(f32::MAX))
    {
        return Err(Error::InvalidInput);
    }
    Ok(values.map(|v| v as f32))
}
// Fixed dependent determinant and original scalar double inverse; no array-sized
// CPU raster workload. Independent radius and edge lanes use SIMD below.
fn inverse(m: [f64; 6]) -> Result<[f64; 6], Error> {
    let [a, b, c, d, x, y] = m;
    let det = a * d - b * c;
    if !det.is_finite() || det.abs() <= 0.000001 {
        return Err(Error::InvalidInput);
    }
    let r = 1.0 / det;
    let result = [
        d * r,
        -b * r,
        -c * r,
        a * r,
        (c * y - d * x) * r,
        (b * x - a * y) * r,
    ];
    narrow(result)?;
    Ok(result)
}
impl Mask {
    /// Whole-descriptor native admission, independent of a future target DPI.
    pub fn validate(&self) -> Result<(), Error> {
        let (bounds, transform, opacity) = match self {
            Self::Analytic(mask) => {
                if mask
                    .radii_x
                    .iter()
                    .chain(&mask.radii_y)
                    .any(|v| !v.is_finite() || *v < 0.0)
                    || (mask.policy == AnalyticMaskPolicy::AxisClipArea
                        && (mask.transform != Affine::IDENTITY
                            || mask.opacity != 1.0
                            || mask.radii_x != [0.0; 4]
                            || mask.radii_y != [0.0; 4]))
                {
                    return Err(Error::InvalidInput);
                }
                (mask.bounds, mask.transform, mask.opacity)
            }
            Self::Coverage(mask) => {
                if mask.image.format() != ImageFormat::R8 {
                    return Err(Error::InvalidInput);
                }
                (mask.bounds, mask.transform, mask.opacity)
            }
        };
        rect(bounds)?;
        if !opacity.is_finite()
            || !(0.0..=1.0).contains(&opacity)
            || transform.0.iter().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        inverse(transform.0.map(f64::from))?;
        Ok(())
    }

    /// Compile the exact native root-target uniform arithmetic at actual DPI.
    /// Viewport rebasing and independent X/Y presentation DPI remain separate APIs.
    pub fn uniforms(&self, dpi: f32) -> Result<MaskUniforms, Error> {
        self.validate()?;
        if !dpi.is_finite() || dpi <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let mut out = MaskUniforms::zeroed();
        match self {
            Self::Analytic(mask) => {
                let [x, y, w, h] = mask.bounds;
                let edges = [x, y, x + w, y + h];
                if mask.policy == AnalyticMaskPolicy::AxisClipArea {
                    // Original float projection, no divide/multiply round trip.
                    out.bounds = (f32x4::from(edges) * f32x4::splat(dpi) + f32x4::ZERO).to_array();
                    out.coordinate0[0] = 1.0;
                    out.coordinate1[1] = 1.0;
                    out.options = [5.0, 1.0, 0.0, 0.0];
                } else {
                    let inv = narrow(inverse(
                        mask.transform.0.map(|v| f64::from(v) * f64::from(dpi)),
                    )?)?;
                    out.coordinate0 = [inv[0], inv[2], inv[4], 0.0];
                    out.coordinate1 = [inv[1], inv[3], inv[5], 0.0];
                    out.bounds = edges;
                    let rx = mask.radii_x;
                    let ry = mask.radii_y;
                    let mut scale = 1.0_f32;
                    for (available, requested) in [
                        (w, rx[0] + rx[1]),
                        (w, rx[3] + rx[2]),
                        (h, ry[0] + ry[3]),
                        (h, ry[1] + ry[2]),
                    ] {
                        if requested > 0.0 {
                            scale = scale.min(available / requested);
                        }
                    }
                    let scale = f32x4::splat(scale.clamp(0.0, 1.0));
                    out.radii_x = (f32x4::from(rx) * scale).to_array();
                    out.radii_y = (f32x4::from(ry) * scale).to_array();
                    out.options = [2.0, mask.opacity, 0.0, 0.0];
                }
            }
            Self::Coverage(mask) => {
                // Semantic legacy UV route uses division by determinant and
                // retains the float DPI*extent product before widening.
                let [a, b, c, d, x, y] = mask.transform.0.map(f64::from);
                let det = a * d - b * c;
                let [bx, by, w, h] = mask.bounds;
                let px = f64::from(dpi * w);
                let py = f64::from(dpi * h);
                let values = narrow([
                    (d / det) / px,
                    (-c / det) / px,
                    (((c * y - d * x) / det) - f64::from(bx)) / f64::from(w),
                    (-b / det) / py,
                    (a / det) / py,
                    (((b * x - a * y) / det) - f64::from(by)) / f64::from(h),
                ])?;
                out.coordinate0 = [values[0], values[1], values[2], 0.0];
                out.coordinate1 = [values[3], values[4], values[5], 0.0];
                out.options = [1.0, mask.opacity, 1.0, 1.0];
            }
        }
        if bytemuck::cast_slice::<_, f32>(std::slice::from_ref(&out))
            .iter()
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(out)
    }
}
