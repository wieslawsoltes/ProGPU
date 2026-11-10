//! Retained native analytic, sampled and materialized coverage masks.
//! Isolated group opacity and source AA-layer composition remain separate.
use crate::{
    Error,
    geometry::Affine,
    image::{Image, ImageFormat, ImageSource},
};
use bytemuck::{Pod, Zeroable};
use wide::f32x4;

mod clip;
pub use clip::{ClipPath, ClipPlan, ClipVertex, ComposeUniforms};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{MaskComposer, MaskCompositionMetrics, PreparedMask, SampledMaskInput};

/// Original ordered clip combination. Difference removes coverage from the
/// preceding result, including an initially opaque target for the first node.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum MaskOperation {
    #[default]
    /// Multiply preceding coverage by this component.
    Intersect = 0,
    /// Multiply preceding coverage by one minus this component.
    Difference = 1,
}

/// Original selected coverage channel, independent of source alpha encoding.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum MaskChannel {
    #[default]
    /// Original red-channel coverage.
    Red,
    /// Original RGBA alpha, without unpremultiplication or extraction.
    Alpha,
}

/// Original sampled-mask mapping. GPU sources retain actual handles; picture
/// alpha can be consumed directly without extracting or reading pixels.
#[derive(Clone, Debug)]
pub struct TextureMask {
    /// Actual immutable pixels or retained same-device texture.
    pub source: ImageSource,
    /// Original local X, Y, width and height.
    pub bounds: [f32; 4],
    /// Original local-to-target logical mapping.
    pub transform: Affine,
    /// Original nearest/linear sampling, without mip generation.
    pub sampling: MaskSampling,
    /// Independent coverage opacity in 0..=1.
    pub opacity: f32,
    /// Original selected source component.
    pub channel: MaskChannel,
}
impl TextureMask {
    /// Retain a source with identity mapping, linear sampling and unit opacity.
    pub fn new(source: impl Into<ImageSource>, bounds: [f32; 4], channel: MaskChannel) -> Self {
        Self {
            source: source.into(),
            bounds,
            transform: Affine::IDENTITY,
            sampling: MaskSampling::Linear,
            opacity: 1.0,
            channel,
        }
    }
    /// Original native sampled-mask inverse for an actual cropped target. The
    /// physical origin is subtracted in double before inversion, without a
    /// round trip through a narrowed translated affine or a completed UV row.
    pub fn uniforms_for_target(&self, dpi: f32, origin: [u32; 2]) -> Result<MaskUniforms, Error> {
        Mask::Texture(self.clone()).validate()?;
        if !dpi.is_finite() || dpi <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let [a, b, c, d, tx, ty] = self.transform.0.map(f64::from);
        let x = tx - f64::from(origin[0]) / f64::from(dpi);
        let y = ty - f64::from(origin[1]) / f64::from(dpi);
        let det = a * d - b * c;
        let [bx, by, w, h] = self.bounds;
        let px = f64::from(dpi * w);
        let py = f64::from(dpi * h);
        let v = narrow([
            (d / det) / px,
            (-c / det) / px,
            (((c * y - d * x) / det) - f64::from(bx)) / f64::from(w),
            (-b / det) / py,
            (a / det) / py,
            (((b * x - a * y) / det) - f64::from(by)) / f64::from(h),
        ])?;
        Ok(MaskUniforms {
            coordinate0: [v[0], v[1], v[2], 0.0],
            coordinate1: [v[3], v[4], v[5], 0.0],
            options: [
                1.0,
                self.opacity,
                1.0,
                if self.channel == MaskChannel::Alpha {
                    2.0
                } else {
                    1.0
                },
            ],
            ..Zeroable::zeroed()
        })
    }
}
impl From<CoverageMask> for TextureMask {
    fn from(value: CoverageMask) -> Self {
        Self {
            source: value.image.into(),
            bounds: value.bounds,
            transform: value.transform,
            sampling: value.sampling,
            opacity: value.opacity,
            channel: MaskChannel::Red,
        }
    }
}

/// A completed opacity-mask texture in its actual root physical frame. The
/// constructor is private to the renderer; ordinary sampled textures cannot
/// assert this proof or be retargeted through source placement.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct RenderedTextureMask {
    pub(crate) texture: TextureMask,
    pub(crate) size: [u32; 2],
    pub(crate) dpi: f32,
    pub(crate) projection: [f32; 16],
    pub(crate) replaces_chain: bool,
}
#[cfg(feature = "gpu")]
impl RenderedTextureMask {
    pub(crate) fn uniforms(&self, origin: [u32; 2]) -> MaskUniforms {
        MaskUniforms {
            coordinate0: [-(origin[0] as f32), -(origin[1] as f32), 0.0, 0.0],
            coordinate1: [
                1.0 / self.size[0] as f32,
                1.0 / self.size[1] as f32,
                0.0,
                0.0,
            ],
            options: [1.0, 1.0, 0.0, 2.0],
            ..Zeroable::zeroed()
        }
    }
    pub(crate) fn validate_target(
        &self,
        size: [u32; 2],
        dpi: f32,
        projection: [f32; 16],
    ) -> Result<(), Error> {
        if size != self.size
            || dpi.to_bits() != self.dpi.to_bits()
            || projection.map(f32::to_bits) != self.projection.map(f32::to_bits)
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}

/// Original source analytic mask rows computed in the actual physical target.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug)]
pub struct FrameAnalyticMask {
    pub(crate) uniforms: MaskUniforms,
    pub(crate) size: [u32; 2],
    pub(crate) dpi: f32,
    pub(crate) projection: [f32; 16],
    pub(crate) replaces_chain: bool,
}
#[cfg(feature = "gpu")]
impl FrameAnalyticMask {
    pub(crate) fn validate_target(
        &self,
        size: [u32; 2],
        dpi: f32,
        projection: [f32; 16],
    ) -> Result<(), Error> {
        if size != self.size
            || dpi.to_bits() != self.dpi.to_bits()
            || projection.map(f32::to_bits) != self.projection.map(f32::to_bits)
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}

/// Immutable-at-recording mask and its original ordered operation.
#[derive(Clone, Debug)]
pub struct MaskComponent {
    /// Actual retained coverage source.
    pub mask: Mask,
    /// Original operation at this position in the ordered chain.
    pub operation: MaskOperation,
}

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

/// Exact independent target-logical axis edges retained by source capture.
/// Construction remains internal so callers cannot bypass the original area
/// mask's identity mapping, zero-radii and unit-opacity admission.
#[derive(Clone, Debug)]
pub struct AxisClipMaskEdges {
    edges: [f32; 4],
}
impl AxisClipMaskEdges {
    /// Original left/top/right/bottom, without a width reconstruction.
    pub fn edges(&self) -> [f32; 4] {
        self.edges
    }
}

/// Retained coverage source. Recorders retain pixels without copying them.
#[derive(Clone, Debug)]
pub enum Mask {
    /// Native analytic rounded rectangle or explicit axis area.
    Analytic(AnalyticMask),
    /// Original area coverage with independently placed source far edges.
    AxisClipEdges(AxisClipMaskEdges),
    /// Original path raster coverage. The path record must use Intersect;
    /// ordered intersection/difference belongs to the containing mask scope.
    Path(ClipPath),
    /// Original immutable coverage bitmap.
    Coverage(CoverageMask),
    /// Original red/alpha sampled source, including an owned GPU generation.
    Texture(TextureMask),
    /// Completed actual-target visual opacity mask with immutable frame ownership.
    #[cfg(feature = "gpu")]
    RenderedTexture(RenderedTextureMask),
    /// Original analytic source coverage resolved in one immutable physical frame.
    #[cfg(feature = "gpu")]
    FrameAnalytic(FrameAnalyticMask),
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
impl From<TextureMask> for Mask {
    fn from(value: TextureMask) -> Self {
        Self::Texture(value)
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
    pub(crate) fn axis_clip_edges(edges: [f32; 4]) -> Result<Self, Error> {
        let result = Self::AxisClipEdges(AxisClipMaskEdges { edges });
        result.validate()?;
        Ok(result)
    }
    /// Whole-descriptor native admission, independent of a future target DPI.
    pub fn validate(&self) -> Result<(), Error> {
        let (bounds, transform, opacity) = match self {
            #[cfg(feature = "gpu")]
            Self::FrameAnalytic(mask) => {
                if mask.size.contains(&0)
                    || !mask.dpi.is_finite()
                    || mask.dpi <= 0.0
                    || bytemuck::cast_slice::<_, f32>(std::slice::from_ref(&mask.uniforms))
                        .iter()
                        .chain(&mask.projection)
                        .any(|v| !v.is_finite())
                {
                    return Err(Error::InvalidInput);
                }
                return Ok(());
            }
            #[cfg(feature = "gpu")]
            Self::RenderedTexture(mask) => {
                if mask.size.contains(&0)
                    || mask.texture.source.size() != mask.size
                    || !mask.dpi.is_finite()
                    || mask.dpi <= 0.0
                    || mask.projection.iter().any(|v| !v.is_finite())
                {
                    return Err(Error::InvalidInput);
                }
                return Self::Texture(mask.texture.clone()).validate();
            }
            Self::Path(path) => {
                if !matches!(path.sample_grid, 1 | 4 | 8)
                    || path.operation != MaskOperation::Intersect
                {
                    return Err(Error::InvalidInput);
                }
                path.transform.scales()?;
                return Ok(());
            }
            Self::AxisClipEdges(mask) => {
                let [left, top, right, bottom] = mask.edges;
                return if mask.edges.iter().all(|v| v.is_finite()) && left <= right && top <= bottom
                {
                    Ok(())
                } else {
                    Err(Error::InvalidInput)
                };
            }
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
            Self::Texture(mask) => (mask.bounds, mask.transform, mask.opacity),
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
            #[cfg(feature = "gpu")]
            Self::FrameAnalytic(mask) => {
                if dpi.to_bits() != mask.dpi.to_bits() {
                    return Err(Error::ResourceDomain);
                }
                return Ok(mask.uniforms);
            }
            #[cfg(feature = "gpu")]
            Self::RenderedTexture(mask) => {
                if dpi.to_bits() != mask.dpi.to_bits() {
                    return Err(Error::ResourceDomain);
                }
                return Ok(mask.uniforms([0; 2]));
            }
            Self::Path(_) => return Err(Error::UnsupportedExecution),
            Self::AxisClipEdges(mask) => {
                out.bounds = (f32x4::from(mask.edges) * f32x4::splat(dpi) + f32x4::ZERO).to_array();
                out.coordinate0[0] = 1.0;
                out.coordinate1[1] = 1.0;
                out.options = [5.0, 1.0, 0.0, 0.0];
            }
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
                return Self::Texture(mask.clone().into()).uniforms(dpi);
            }
            Self::Texture(mask) => return mask.uniforms_for_target(dpi, [0; 2]),
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
