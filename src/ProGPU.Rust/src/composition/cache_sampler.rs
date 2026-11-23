//! Original independent bitmap-cache shader-sampler frame.
use crate::Error;
use wide::{f32x4, f64x2};

/// Exact source metadata and independently sized raw-cache raster. It is not an
/// effect-input frame or an ordinary cached-picture paint mapping. Construction
/// retains supplied provider facts and does no GPU work or ownership inference.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CacheSamplerRasterFrame {
    source: [f64; 4],
    render_scale: f64,
    primary: [f32; 2],
    maximum: [u32; 2],
    pixels: [u32; 2],
    source_to_raster: [f32; 16],
    empty: bool,
}
impl CacheSamplerRasterFrame {
    /// Port of original TryCreate. Source is XYWH; actual primary per-axis scale
    /// and live device limits are required. Missing source bounds must be rejected
    /// by the caller; only proven finite empty/null geometry uses an empty source.
    pub fn new(
        source: [f64; 4],
        render_scale: f64,
        primary: [f32; 2],
        maximum: [u32; 2],
    ) -> Result<Self, Error> {
        if source.iter().any(|v| !v.is_finite())
            || source[2] < 0.0
            || source[3] < 0.0
            || !render_scale.is_finite()
            || render_scale < 0.0
            || primary.iter().any(|v| !v.is_finite() || *v <= 0.0)
            || maximum.contains(&0)
        {
            return Err(Error::InvalidInput);
        }
        // Narrow each original far endpoint independently, before subtraction.
        let far =
            (f64x2::from([source[0], source[1]]) + f64x2::from([source[2], source[3]])).to_array();
        let left = source[0] as f32;
        let top = source[1] as f32;
        let right = far[0] as f32;
        let bottom = far[1] as f32;
        let extents = (f32x4::from([right, bottom, 0.0, 0.0]) - f32x4::from([left, top, 0.0, 0.0]))
            .to_array();
        if [left, top, right, bottom, extents[0], extents[1]]
            .iter()
            .any(|v| !v.is_finite())
            || extents[0] < 0.0
            || extents[1] < 0.0
        {
            return Err(Error::InvalidInput);
        }
        let mut empty = extents[0] == 0.0 || extents[1] == 0.0 || render_scale == 0.0;
        let mut pixels = [1, 1];
        let mut transform = [
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
        ];
        if !empty {
            // Independent axes share SIMD operations, retaining the original
            // separate double products and subsequent binary32 relative test.
            let scaled = f64x2::from([f64::from(extents[0]), f64::from(extents[1])])
                * f64x2::splat(render_scale);
            let physical = (scaled * f64x2::from(primary.map(f64::from))).to_array();
            if physical
                .iter()
                .any(|v| !v.is_finite() || *v < 0.0 || *v > f64::from(u32::MAX))
            {
                return Err(Error::InvalidInput);
            }
            let mut integral = physical.map(|v| v as u32);
            let rounded = physical.map(|v| v as f32);
            let difference = f32x4::from([integral[0] as f32, integral[1] as f32, 0.0, 0.0])
                - f32x4::from([rounded[0], rounded[1], 0.0, 0.0]);
            let relative = (difference
                / f32x4::from([
                    if rounded[0] == 0.0 { 1.0 } else { rounded[0] },
                    if rounded[1] == 0.0 { 1.0 } else { rounded[1] },
                    1.0,
                    1.0,
                ]))
            .to_array();
            let mut effective = primary.map(f64::from);
            // Admission/capping is bounded structural policy; numeric work on
            // both independent axes is vectorized before and after these branches.
            for axis in 0..2 {
                if !(relative[axis].abs() < 10.0 / 8388608.0) {
                    integral[axis] = integral[axis].checked_add(1).ok_or(Error::Capacity)?;
                }
                pixels[axis] = integral[axis].min(maximum[axis]);
                if integral[axis] > maximum[axis] {
                    effective[axis] *= f64::from(maximum[axis]) / f64::from(integral[axis]);
                }
            }
            let scales = (f64x2::splat(render_scale) * f64x2::from(effective))
                .to_array()
                .map(|v| v as f32);
            for axis in 0..2 {
                if pixels[axis] != 0 && (!scales[axis].is_finite() || scales[axis] <= 0.0) {
                    return Err(Error::InvalidInput);
                }
            }
            empty = pixels.contains(&0);
            if empty {
                pixels = [1, 1];
            } else {
                let offsets = (f32x4::from([-left, -top, 0.0, 0.0])
                    * f32x4::from([scales[0], scales[1], 0.0, 0.0]))
                .to_array();
                if offsets[..2].iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
                transform[0] = scales[0];
                transform[5] = scales[1];
                transform[12] = offsets[0];
                transform[13] = offsets[1];
            }
        }
        Ok(Self {
            source,
            render_scale,
            primary,
            maximum,
            pixels,
            source_to_raster: transform,
            empty,
        })
    }
    /// Original double XYWH, without replacing rounded endpoints.
    pub fn source(self) -> [f64; 4] {
        self.source
    }
    /// Selected already-coerced cache scale, including zero and signed zero.
    pub fn render_scale(self) -> f64 {
        self.render_scale
    }
    /// Actual source provider's original primary DPI scales.
    pub fn primary_scales(self) -> [f32; 2] {
        self.primary
    }
    /// Actual per-axis texture limits used for this realization.
    pub fn maximum_dimensions(self) -> [u32; 2] {
        self.maximum
    }
    /// Exact texture extent; proven empty realizations require one transparent texel.
    pub fn size(self) -> [u32; 2] {
        self.pixels
    }
    /// Original float matrix, with separate scale and translation expressions.
    pub fn source_to_raster(self) -> [f32; 16] {
        self.source_to_raster
    }
    /// Whether source geometry/scale proves that the raw cache has no paint.
    pub fn is_empty(self) -> bool {
        self.empty
    }
    /// Exact metadata identity, retaining signed-zero and all original bits.
    pub fn same_generation(self, other: Self) -> bool {
        self.source.map(f64::to_bits) == other.source.map(f64::to_bits)
            && self.render_scale.to_bits() == other.render_scale.to_bits()
            && self.primary.map(f32::to_bits) == other.primary.map(f32::to_bits)
            && self.maximum == other.maximum
            && self.pixels == other.pixels
            && self.source_to_raster.map(f32::to_bits) == other.source_to_raster.map(f32::to_bits)
            && self.empty == other.empty
    }
}
