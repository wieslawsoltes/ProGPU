//! Original chromaticity solve and explicit Bradford-to-D50 source arithmetic.
use super::ColorSpaceXyz;
use crate::Error;
/// Original RX/RY/GX/GY/BX/BY/WX/WY values. Construction does not normalize or
/// replace boundary primaries; conversion preserves the source's exact gates.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ColorSpacePrimaries(pub [f32; 8]);
impl ColorSpacePrimaries {
    /// Original all-zero empty descriptor.
    pub const EMPTY: Self = Self([0.0; 8]);
    /// Retain the original three xy primaries and independent reference white.
    pub const fn new(red: [f32; 2], green: [f32; 2], blue: [f32; 2], white: [f32; 2]) -> Self {
        Self([
            red[0], red[1], green[0], green[1], blue[0], blue[1], white[0], white[1],
        ])
    }
    /// Original primary conversion, including successful nonfinite results for
    /// admitted boundary white points. ColorSpace::create_rgb has its own finite
    /// gate; do not silently combine these two original policies.
    pub fn to_xyz(self) -> Option<ColorSpaceXyz> {
        let [rx, ry, gx, gy, bx, by, wx, wy] = self.0;
        if wx == 0.3127 && wy == 0.3290 {
            if rx == 0.64 && ry == 0.33 && gx == 0.30 && gy == 0.60 && bx == 0.15 && by == 0.06 {
                return Some(ColorSpaceXyz([
                    0.43602818,
                    0.38510093,
                    0.14309105,
                    0.22247864,
                    0.7168975,
                    0.060624108,
                    0.013926373,
                    0.097092114,
                    0.7141915,
                ]));
            }
            if rx == 0.68 && ry == 0.32 && gx == 0.265 && gy == 0.69 && bx == 0.15 && by == 0.06 {
                return Some(ColorSpaceXyz([
                    0.51510215,
                    0.29196474,
                    0.1571531,
                    0.24118185,
                    0.6922364,
                    0.06658185,
                    -0.0010494092,
                    0.041881755,
                    0.7843777,
                ]));
            }
        }
        if self.0.iter().any(|v| !(*v >= 0.0 && *v <= 1.0)) {
            return None;
        }
        let rz = 1.0 - rx - ry;
        let gz = 1.0 - gx - gy;
        let bz = 1.0 - bx - by;
        let wz = 1.0 - wx - wy;
        let determinant =
            rx * (gy * bz - by * gz) - gx * (ry * bz - by * rz) + bx * (ry * gz - gy * rz);
        if determinant == 0.0 || !determinant.is_finite() {
            return None;
        }
        let inverse_determinant = 1.0 / determinant;
        let sr = ((gy * bz - by * gz) * wx + (bx * gz - gx * bz) * wy + (gx * by - bx * gy) * wz)
            * inverse_determinant;
        let sg = ((by * rz - ry * bz) * wx + (rx * bz - bx * rz) * wy + (bx * ry - rx * by) * wz)
            * inverse_determinant;
        let sb = ((ry * gz - gy * rz) * wx + (gx * rz - rx * gz) * wy + (rx * gy - gx * ry) * wz)
            * inverse_determinant;
        let reciprocal_white_y = 1.0 / wy;
        let m00 = rx * sr * reciprocal_white_y;
        let m01 = gx * sg * reciprocal_white_y;
        let m02 = bx * sb * reciprocal_white_y;
        let m10 = ry * sr * reciprocal_white_y;
        let m11 = gy * sg * reciprocal_white_y;
        let m12 = by * sb * reciprocal_white_y;
        let m20 = rz * sr * reciprocal_white_y;
        let m21 = gz * sg * reciprocal_white_y;
        let m22 = bz * sb * reciprocal_white_y;
        let source_x = wx * reciprocal_white_y;
        let source_z = wz * reciprocal_white_y;
        let source_l = 0.8951 * source_x + 0.2664 - 0.1614 * source_z;
        let source_m = -0.7502 * source_x + 1.7135 + 0.0367 * source_z;
        let source_s = 0.0389 * source_x - 0.0685 + 1.0296 * source_z;
        let destination_l = 0.8951_f32 * 0.96422 + 0.2664 - 0.1614 * 0.82521;
        let destination_m = -0.7502_f32 * 0.96422 + 1.7135 + 0.0367 * 0.82521;
        let destination_s = 0.0389_f32 * 0.96422 - 0.0685 + 1.0296 * 0.82521;
        let scale_l = destination_l / source_l;
        let scale_m = destination_m / source_m;
        let scale_s = destination_s / source_s;
        const I00: f32 = 0.986992905467;
        const I01: f32 = -0.147054256421;
        const I02: f32 = 0.159962651664;
        const I10: f32 = 0.432305269723;
        const I11: f32 = 0.518360271537;
        const I12: f32 = 0.049291228213;
        const I20: f32 = -0.008528664575;
        const I21: f32 = 0.040042821654;
        const I22: f32 = 0.968486695788;
        let a00 = I00 * scale_l * 0.8951 + I01 * scale_m * -0.7502 + I02 * scale_s * 0.0389;
        let a01 = I00 * scale_l * 0.2664 + I01 * scale_m * 1.7135 + I02 * scale_s * -0.0685;
        let a02 = I00 * scale_l * -0.1614 + I01 * scale_m * 0.0367 + I02 * scale_s * 1.0296;
        let a10 = I10 * scale_l * 0.8951 + I11 * scale_m * -0.7502 + I12 * scale_s * 0.0389;
        let a11 = I10 * scale_l * 0.2664 + I11 * scale_m * 1.7135 + I12 * scale_s * -0.0685;
        let a12 = I10 * scale_l * -0.1614 + I11 * scale_m * 0.0367 + I12 * scale_s * 1.0296;
        let a20 = I20 * scale_l * 0.8951 + I21 * scale_m * -0.7502 + I22 * scale_s * 0.0389;
        let a21 = I20 * scale_l * 0.2664 + I21 * scale_m * 1.7135 + I22 * scale_s * -0.0685;
        let a22 = I20 * scale_l * -0.1614 + I21 * scale_m * 0.0367 + I22 * scale_s * 1.0296;
        Some(ColorSpaceXyz([
            a00 * m00 + a01 * m10 + a02 * m20,
            a00 * m01 + a01 * m11 + a02 * m21,
            a00 * m02 + a01 * m12 + a02 * m22,
            a10 * m00 + a11 * m10 + a12 * m20,
            a10 * m01 + a11 * m11 + a12 * m21,
            a10 * m02 + a11 * m12 + a12 * m22,
            a20 * m00 + a21 * m10 + a22 * m20,
            a20 * m01 + a21 * m11 + a22 * m21,
            a20 * m02 + a21 * m12 + a22 * m22,
        ]))
    }
    /// Original value-returning overload, retaining EMPTY on failed admission.
    pub fn xyz_or_empty(self) -> ColorSpaceXyz {
        self.to_xyz().unwrap_or(ColorSpaceXyz::EMPTY)
    }
}
impl TryFrom<&[f32]> for ColorSpacePrimaries {
    type Error = Error;
    fn try_from(values: &[f32]) -> Result<Self, Error> {
        Ok(Self(values.try_into().map_err(|_| Error::InvalidInput)?))
    }
}
