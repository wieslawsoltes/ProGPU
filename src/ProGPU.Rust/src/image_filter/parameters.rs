//! Exact original compute records; builders preserve source normalization rules.
use super::*;
use bytemuck::{Pod, Zeroable};
fn finite(values: &[f32]) -> bool {
    values.iter().all(|v| v.is_finite())
}
/// Original 16-byte separable morphology record.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct MorphologyParams {
    /// Original horizontal or vertical integer direction.
    pub direction: [i32; 2],
    /// Original bounded sample radius.
    pub radius: u32,
    /// One for dilation, zero for erosion.
    pub dilate: u32,
}
impl MorphologyParams {
    /// Preserve original record construction without silently altering radius.
    pub fn new(direction: [i32; 2], radius: u32, dilate: bool) -> Self {
        Self {
            direction,
            radius,
            dilate: u32::from(dilate),
        }
    }
    /// Original retained kernel route admits the two explicit ordered axes.
    pub fn validate(&self) -> Result<(), Error> {
        if !matches!(self.direction, [1, 0] | [0, 1]) || self.radius > 128 || self.dilate > 1 {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
/// Original 48-byte convolution parameters; coefficient storage is independent.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct MatrixConvolutionParams {
    /// Exact original width and height, each in 1..=64 at kernel admission.
    pub kernel_size: [i32; 2],
    /// Original integer anchor.
    pub kernel_offset: [i32; 2],
    /// Original multiplier, normalized to zero only when nonfinite.
    pub gain: f32,
    /// Original byte-domain bias; shader retains its division by 255.
    pub bias: f32,
    /// Clamp/repeat/mirror/decal values 0..=3.
    pub tile_mode: u32,
    /// Original independent alpha convolution flag.
    pub convolve_alpha: u32,
    /// Original transformed crop-domain integer origin.
    pub tile_origin: [i32; 2],
    /// Original crop-domain integer width and height, independent of texture size.
    pub tile_size: [i32; 2],
}
impl MatrixConvolutionParams {
    /// Original parameter builder. Raw retained graph metadata remains separate.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        kernel_size: [i32; 2],
        kernel_offset: [i32; 2],
        gain: f32,
        bias: f32,
        tile_mode: u32,
        convolve_alpha: bool,
        tile_origin: [i32; 2],
        tile_size: [i32; 2],
    ) -> Self {
        Self {
            kernel_size,
            kernel_offset,
            gain: if gain.is_finite() { gain } else { 0.0 },
            bias: if bias.is_finite() { bias } else { 0.0 },
            tile_mode: tile_mode.min(3),
            convolve_alpha: u32::from(convolve_alpha),
            tile_origin,
            tile_size: tile_size.map(|v| v.max(0)),
        }
    }
    /// Validate exact kernel and shader integer-domain admission.
    pub fn validate(&self) -> Result<(), Error> {
        if self.kernel_size.iter().any(|v| !(1..=64).contains(v))
            || !self.gain.is_finite()
            || !self.bias.is_finite()
            || self.tile_mode > 3
            || self.convolve_alpha > 1
            || self.tile_size.iter().any(|v| *v < 0)
            || (self.tile_mode != 3
                && (self.tile_size.iter().any(|v| *v > 1_000_000_000)
                    || self
                        .tile_origin
                        .iter()
                        .any(|v| !(-1_000_000_000..=1_000_000_000).contains(v))))
        {
            return Err(Error::InvalidInput);
        }
        if self.tile_mode != 3 {
            for axis in 0..2 {
                self.tile_origin[axis]
                    .checked_add(self.tile_size[axis])
                    .ok_or(Error::Capacity)?;
            }
        }
        Ok(())
    }
}
/// Original 32-byte displacement record, retaining all four basis coefficients.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct DisplacementMapParams {
    /// Original X axis XY followed by Y axis XY.
    pub transform: [f32; 4],
    /// Original red/green/blue/alpha selector 0..=3.
    pub x_channel: u32,
    /// Independent original second selector.
    pub y_channel: u32,
    /// Exact original zero words.
    pub padding: [u32; 2],
}
impl DisplacementMapParams {
    /// Original whole-vector finite normalization and channel clamps.
    pub fn new(transform: [f32; 4], x_channel: u32, y_channel: u32) -> Self {
        Self {
            transform: if finite(&transform) {
                transform
            } else {
                [0.0; 4]
            },
            x_channel: x_channel.min(3),
            y_channel: y_channel.min(3),
            padding: [0; 2],
        }
    }
    /// Admit complete uniform bytes without rewriting an already-built record.
    pub fn validate(&self) -> Result<(), Error> {
        if !finite(&self.transform)
            || self.x_channel > 3
            || self.y_channel > 3
            || self.padding != [0; 2]
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
/// Original 80-byte magnifier record, with distinct lens/output/zoom frames.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct MagnifierParams {
    /// Original lens left/top/right/bottom.
    pub lens_bounds: [f32; 4],
    /// Independent original output left/top/right/bottom.
    pub output_bounds: [f32; 4],
    /// Original translation XY and inverse zoom XY.
    pub zoom_transform: [f32; 4],
    /// Independent nonnegative inverse physical inset.
    pub inverse_inset: [f32; 2],
    /// Original nearest/linear/cubic values 0..=2.
    pub sampling_mode: u32,
    /// Original zero word.
    pub padding0: u32,
    /// Original cubic B/C.
    pub cubic: [f32; 2],
    /// Original two zero floats.
    pub padding1: [f32; 2],
}
impl MagnifierParams {
    /// Original per-vector normalization and independent sampling clamp.
    pub fn new(
        lens_bounds: [f32; 4],
        output_bounds: [f32; 4],
        zoom_transform: [f32; 4],
        inverse_inset: [f32; 2],
        sampling_mode: u32,
        cubic: [f32; 2],
    ) -> Self {
        Self {
            lens_bounds: if finite(&lens_bounds) {
                lens_bounds
            } else {
                [0.0; 4]
            },
            output_bounds: if finite(&output_bounds) {
                output_bounds
            } else {
                [0.0; 4]
            },
            zoom_transform: if finite(&zoom_transform) {
                zoom_transform
            } else {
                [0.0; 4]
            },
            inverse_inset: if finite(&inverse_inset) {
                inverse_inset.map(|v| v.max(0.0))
            } else {
                [0.0; 2]
            },
            sampling_mode: sampling_mode.min(2),
            padding0: 0,
            cubic: if finite(&cubic) { cubic } else { [0.0; 2] },
            padding1: [0.0; 2],
        }
    }
    /// Complete original shader-record validation before resource publication.
    pub fn validate(&self) -> Result<(), Error> {
        if !finite(&self.lens_bounds)
            || !finite(&self.output_bounds)
            || !finite(&self.zoom_transform)
            || !finite(&self.inverse_inset)
            || !finite(&self.cubic)
            || self.inverse_inset.iter().any(|v| *v < 0.0)
            || self.sampling_mode > 2
            || self.padding0 != 0
            || self.padding1 != [0.0; 2]
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
/// Original named inputs to the lighting parameter builder.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ImageLightingInput {
    /// Actual transformed light position or direction.
    pub light_position: [f32; 3],
    /// Distant/point/spot values 0..=2.
    pub light_type: u32,
    /// Actual transformed spot target.
    pub light_target: [f32; 3],
    /// Original spot exponent.
    pub spot_exponent: f32,
    /// Original straight light RGBA.
    pub light_color: [f32; 4],
    /// Original transformed alpha height scale.
    pub surface_scale: f32,
    /// Original diffuse/specular coefficient.
    pub lighting_constant: f32,
    /// Original specular exponent.
    pub shininess: f32,
    /// Original spot cutoff in degrees.
    pub cutoff_angle: f32,
    /// Independent diffuse/specular selection.
    pub specular: bool,
}
/// Original 80-byte six-policy image-lighting record.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct ImageLightingParams {
    /// Original actual position/direction and type.
    pub light_position_and_type: [f32; 4],
    /// Original actual target and spot exponent.
    pub light_target_and_spot_exponent: [f32; 4],
    /// Original clamped straight color.
    pub light_color: [f32; 4],
    /// Original height scale, constant, shininess and cutoff.
    pub surface_params: [f32; 4],
    /// Original specular flag and three zero lanes.
    pub mode_params: [f32; 4],
}
impl ImageLightingParams {
    /// Preserve original scalar normalization, including absolute cutoff.
    pub fn new(v: ImageLightingInput) -> Self {
        let [x, y, z] = v.light_position;
        let [tx, ty, tz] = v.light_target;
        Self {
            light_position_and_type: [x, y, z, v.light_type.min(2) as f32],
            light_target_and_spot_exponent: [
                tx,
                ty,
                tz,
                if v.spot_exponent.is_finite() {
                    v.spot_exponent.max(0.0)
                } else {
                    0.0
                },
            ],
            light_color: v.light_color.map(|v| v.clamp(0.0, 1.0)),
            surface_params: [
                if v.surface_scale.is_finite() {
                    v.surface_scale
                } else {
                    0.0
                },
                if v.lighting_constant.is_finite() {
                    v.lighting_constant.max(0.0)
                } else {
                    0.0
                },
                if v.shininess.is_finite() {
                    v.shininess.clamp(1.0, 128.0)
                } else {
                    1.0
                },
                if v.cutoff_angle.is_finite() {
                    v.cutoff_angle.abs().clamp(0.0, 90.0)
                } else {
                    90.0
                },
            ],
            mode_params: [if v.specular { 1.0 } else { 0.0 }, 0.0, 0.0, 0.0],
        }
    }
    /// Finite GPU publication admission; raw original metadata stays retained.
    pub fn validate(&self) -> Result<(), Error> {
        if !finite(bytemuck::cast_slice(std::slice::from_ref(self)))
            || !matches!(self.light_position_and_type[3], 0.0 | 1.0 | 2.0)
            || self.light_target_and_spot_exponent[3] < 0.0
            || self.light_color.iter().any(|v| !(0.0..=1.0).contains(v))
            || self.surface_params[1] < 0.0
            || !(1.0..=128.0).contains(&self.surface_params[2])
            || !(0.0..=90.0).contains(&self.surface_params[3])
            || !matches!(self.mode_params[0], 0.0 | 1.0)
            || self.mode_params[1..] != [0.0; 3]
        {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
}
const _: () = {
    assert!(size_of::<MorphologyParams>() == 16);
    assert!(size_of::<MatrixConvolutionParams>() == 48);
    assert!(size_of::<DisplacementMapParams>() == 32);
    assert!(size_of::<MagnifierParams>() == 80);
    assert!(size_of::<ImageLightingParams>() == 80);
};
