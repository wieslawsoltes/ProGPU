//! Original seven-parameter transfer curves and retained filter-source identity.
// Literal precision is retained from the original owned approximation, not
// replaced with different constants before source-numerics qualification.
#![allow(clippy::excessive_precision)]
use super::*;
use std::sync::OnceLock;
use wide::f32x4 as V;
/// Original transfer classification, including independent named HDR policies.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TransferFunctionKind {
    /// Original rejected coefficient classification.
    Invalid,
    /// Original piecewise numerical transfer (called Srgb by the producer).
    Numerical,
    /// Original parameterized PQ curve.
    Pq,
    /// Original parameterized HLG curve.
    Hlg,
    /// Original inverse HLG parameterization.
    HlgInverse,
    /// Original named PQ curve.
    PqNamed,
    /// Original named HLG curve.
    HlgNamed,
}
/// Exact original G/A/B/C/D/E/F coefficients, without ICC or primary inference.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TransferFunction(pub [f32; 7]);
impl TransferFunction {
    /// Original empty/invalid sentinel.
    pub const EMPTY: Self = Self([0.0; 7]);
    /// Original selected sRGB coefficients.
    pub const SRGB: Self = Self([2.4, 0.9478673, 0.0521327, 0.07739938, 0.04045, 0.0, 0.0]);
    /// Original gamma 2.2.
    pub const TWO_DOT_TWO: Self = Self([2.2, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]);
    /// Original exact linear policy.
    pub const LINEAR: Self = Self([1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]);
    /// Original Rec.2020 coefficients.
    pub const REC2020: Self = Self([2.22222, 0.909672, 0.0903276, 0.222222, 0.0812429, 0.0, 0.0]);
    /// Original named PQ metadata.
    pub const PQ: Self = Self([-5.0, 203.0, 0.0, 0.0, 0.0, 0.0, 0.0]);
    /// Original named HLG metadata.
    pub const HLG: Self = Self([-6.0, 203.0, 1000.0, 1.2, 0.0, 0.0, 0.0]);
    /// Exact original branch admission, preserving raw negative-kind records.
    pub fn kind(self) -> TransferFunctionKind {
        use TransferFunctionKind::*;
        let [g, a, b, c, d, e, f] = self.0;
        if g < 0.0 {
            if g < -128.0 {
                return Invalid;
            }
            let kind = -(g as i32);
            if -(kind as f32) != g {
                return Invalid;
            }
            return match kind {
                2 => Pq,
                3 => Hlg,
                4 => HlgInverse,
                5 if b == 0.0 && c == 0.0 && d == 0.0 && e == 0.0 && f == 0.0 => PqNamed,
                6 if d == 0.0 && e == 0.0 && f == 0.0 => HlgNamed,
                _ => Invalid,
            };
        }
        let sum = a + b + c + d + e + f + g;
        if sum.is_finite() && a >= 0.0 && c >= 0.0 && d >= 0.0 && g >= 0.0 && a * d + b >= 0.0 {
            Numerical
        } else {
            Invalid
        }
    }
    /// Original numerical evaluator; independent lanes use the same owned fast
    /// power/log/exponential arithmetic in `transform_four`.
    pub fn transform(self, x: f32) -> f32 {
        self.transform_four([x; 4])[0]
    }
    /// Four independent original transfer values, evaluated with intrinsic lanes.
    pub fn transform_four(self, values: [f32; 4]) -> [f32; 4] {
        let [g, a, b, c, d, e, f] = self.0.map(V::splat);
        let x = V::from(values);
        let sign = V::from(values.map(|v| if v < 0.0 { -1.0 } else { 1.0 }));
        let mut v = x * sign;
        use TransferFunctionKind::*;
        let result = match self.kind() {
            HlgNamed => {
                sign * select(
                    v.to_array().map(|v| v <= 0.5),
                    v * v / V::splat(3.0),
                    (fast_exp((v - V::splat(0.55991073)) / V::splat(0.17883277))
                        + V::splat(0.28466892))
                        / V::splat(12.0),
                )
            }
            Hlg => {
                let scale = f + V::ONE;
                scale
                    * sign
                    * select(
                        (v * a).to_array().map(|v| v <= 1.0),
                        fast_pow(v * a, b),
                        fast_exp((v - e) * c) + d,
                    )
            }
            HlgInverse => {
                v = v / (f + V::ONE);
                sign * select(
                    v.to_array().map(|v| v <= 1.0),
                    a * fast_pow(v, b),
                    c * fast_log(v - d) + e,
                )
            }
            Numerical => {
                sign * select(
                    v.to_array().map(|v| v < self.0[4]),
                    c * v + f,
                    fast_pow(a * v + b, g) + e,
                )
            }
            PqNamed => {
                let p = fast_pow(v, V::splat(1.0 / (2523.0 / 32.0)));
                fast_pow(
                    (p - V::splat(107.0 / 128.0))
                        / (V::splat(2413.0 / 128.0) - V::splat(2392.0 / 128.0) * p),
                    V::splat(1.0 / (1305.0 / 8192.0)),
                )
            }
            Pq => {
                let p = fast_pow(v, c);
                sign * fast_pow((a + b * p) / (d + e * p), f)
            }
            Invalid => V::ZERO,
        };
        result.to_array()
    }
    /// Original inverse construction, preserving unsupported named-HDR sentinels.
    pub fn inverse(self) -> Self {
        use TransferFunctionKind::*;
        let [g, a, b, c, d, e, f] = self.0;
        match self.kind() {
            Invalid | PqNamed | HlgNamed => return Self::EMPTY,
            Pq => return Self([-2.0, -a, d, 1.0 / f, b, -e, 1.0 / c]),
            Hlg => return Self([-4.0, 1.0 / a, 1.0 / b, 1.0 / c, d, e, f]),
            HlgInverse => return Self([-3.0, 1.0 / a, 1.0 / b, 1.0 / c, d, e, f]),
            Numerical => {}
        }
        let pow = |v, p| fast_pow(V::splat(v), V::splat(p)).to_array()[0];
        let left = c * d + f;
        let right = pow(a * d + b, g) + e;
        if (left - right).abs() > 1.0 / 512.0 {
            return Self::EMPTY;
        }
        let mut out = [0.0; 7];
        out[4] = left;
        if left > 0.0 {
            out[3] = 1.0 / c;
            out[6] = -f / c;
        }
        let scale = pow(a, -g);
        out[0] = 1.0 / g;
        out[1] = scale;
        out[2] = -scale * e;
        out[5] = -b / a;
        if out[1] < 0.0 {
            return Self::EMPTY;
        }
        if out[1] * out[4] + out[2] < 0.0 {
            out[2] = -out[1] * out[4];
        }
        if Self(out).kind() != Numerical {
            return Self::EMPTY;
        }
        let mut one = self.transform(1.0);
        if !one.is_finite() {
            return Self::EMPTY;
        }
        let sign = if one < 0.0 { -1.0 } else { 1.0 };
        one *= sign;
        if one < out[4] {
            out[6] = 1.0 - sign * out[3] * one;
        } else {
            out[5] = 1.0 - sign * pow(out[1] * one + out[2], out[0]);
        }
        let inverse = Self(out);
        if inverse.kind() == Numerical {
            inverse
        } else {
            Self::EMPTY
        }
    }
}
fn select(mask: [bool; 4], yes: V, no: V) -> V {
    let yes = yes.to_array();
    let no = no.to_array();
    V::from(std::array::from_fn(
        |i| if mask[i] { yes[i] } else { no[i] },
    ))
}
fn fast_log2(value: V) -> V {
    let bits = value.to_array().map(|v| v.to_bits() as i32);
    let exponent = V::from(bits.map(|v| v as f32)) * V::splat(1.0 / (1 << 23) as f32);
    let mantissa = V::from(bits.map(|v| f32::from_bits(((v & 0x007fffff) | 0x3f000000) as u32)));
    exponent
        - V::splat(124.225514990)
        - V::splat(1.498030302) * mantissa
        - V::splat(1.725879990) / (V::splat(0.3520887068) + mantissa)
}
fn fast_log(value: V) -> V {
    V::splat(0.69314718) * fast_log2(value)
}
fn fast_exp2(value: V) -> V {
    let fraction = value - value.floor();
    let bits = V::splat((1 << 23) as f32)
        * (value + V::splat(121.274057500) - V::splat(1.490129070) * fraction
            + V::splat(27.728023300) / (V::splat(4.84252568) - fraction));
    let values = value.to_array();
    let bits = bits.to_array();
    V::from(std::array::from_fn(|i| {
        if values[i] > 128.0 || bits[i] >= i32::MAX as f32 {
            f32::INFINITY
        } else if values[i] < -127.0 || bits[i] < 0.0 {
            0.0
        } else {
            f32::from_bits((bits[i] as i32) as u32)
        }
    }))
}
fn fast_exp(value: V) -> V {
    fast_exp2(V::splat(1.4426950408889634074) * value)
}
fn fast_pow(value: V, power: V) -> V {
    let values = value.to_array();
    select(
        values.map(|v| v <= 0.0),
        V::ZERO,
        select(
            values.map(|v| v == 1.0),
            V::ONE,
            fast_exp2(fast_log2(value) * power),
        ),
    )
}
#[derive(Debug)]
struct ColorSpace {
    transfer: TransferFunction,
    table: OnceLock<[u8; 256]>,
}
/// Immutable selected transfer identity for Image/Picture source conversion.
/// Original image-filter conversion uses this curve, independently of primaries.
#[derive(Clone, Debug)]
pub struct ImageFilterColorSpace(Arc<ColorSpace>);
impl ImageFilterColorSpace {
    /// Retain an actual selected original transfer descriptor without inference.
    pub fn new(transfer: TransferFunction) -> Self {
        Self(Arc::new(ColorSpace {
            transfer,
            table: OnceLock::new(),
        }))
    }
    /// Exact selected transfer coefficients.
    pub fn transfer(&self) -> TransferFunction {
        self.0.transfer
    }
    /// Original strict linear-policy identity used by displacement source search.
    pub fn is_linear(&self) -> bool {
        self.0.transfer == TransferFunction::LINEAR
    }
    /// Original strict sRGB-policy identity, selecting conversion bypass.
    pub fn is_srgb(&self) -> bool {
        self.0.transfer == TransferFunction::SRGB
    }
    /// Original 256-entry conversion, cached once per retained color-space owner.
    pub fn to_srgb_table(&self) -> &[u8; 256] {
        self.0.table.get_or_init(|| {
            let mut table = [0; 256];
            for base in (0..256).step_by(4) {
                let input = std::array::from_fn(|i| (base + i) as f32 / 255.0);
                let linear = self.transfer().transform_four(input);
                for i in 0..4 {
                    let linear = linear[i].clamp(0.0, 1.0);
                    // Preserve MathF.Pow here: substituting the source transfer's
                    // fast-power approximation changes the final byte table.
                    let srgb = if linear <= 0.0031308 {
                        linear * 12.92
                    } else {
                        1.055 * linear.powf(1.0 / 2.4) - 0.055
                    };
                    table[base + i] = (srgb * 255.0).round_ties_even().clamp(0.0, 255.0) as u8;
                }
            }
            table
        })
    }
}
