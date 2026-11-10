//! Original inline row-major XYZ values and unchanged inverse/concatenation math.
use crate::Error;
use wide::f32x4 as V;
/// Original RGB-to-XYZ D50 matrix in row-major order. Values remain independently
/// owned, including nonfinite values; color-space construction admits them later.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ColorSpaceXyz(pub [f32; 9]);
impl ColorSpaceXyz {
    /// Original all-zero failure/empty sentinel.
    pub const EMPTY: Self = Self([0.0; 9]);
    /// Original identity matrix and XYZ named space.
    pub const IDENTITY: Self = Self([1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]);
    /// Original fixed-point-derived sRGB matrix; not the primary solve exception.
    pub const SRGB: Self = Self([
        0.43606567,
        0.3851471,
        0.1430664,
        0.2224884,
        0.71687317,
        0.06060791,
        0.013916016,
        0.097076416,
        0.71409607,
    ]);
    /// Original named Adobe RGB matrix.
    pub const ADOBE_RGB: Self = Self([
        0.6097412,
        0.20527649,
        0.14918518,
        0.31111145,
        0.6256714,
        0.06321716,
        0.019470215,
        0.06086731,
        0.7445679,
    ]);
    /// Original named Display P3 matrix.
    pub const DISPLAY_P3: Self = Self([
        0.515102,
        0.291965,
        0.157153,
        0.241182,
        0.692236,
        0.0665819,
        -0.00104941,
        0.0418818,
        0.784378,
    ]);
    /// Original named Rec.2020 matrix.
    pub const REC2020: Self = Self([
        0.673459,
        0.165661,
        0.1251,
        0.279033,
        0.675338,
        0.0456288,
        -0.00193139,
        0.0299794,
        0.797162,
    ]);
    /// Original XYZ alias.
    pub const XYZ: Self = Self::IDENTITY;
    /// Fill every inline entry with the original scalar value.
    pub const fn splat(value: f32) -> Self {
        Self([value; 9])
    }
    /// Original column/row indexing with explicit bounds admission.
    pub fn get(self, column: usize, row: usize) -> Option<f32> {
        if column < 3 && row < 3 {
            Some(self.0[column + row * 3])
        } else {
            None
        }
    }
    /// Original exact component-wise finiteness admission.
    pub fn is_finite(self) -> bool {
        self.0.iter().all(|v| v.is_finite())
    }
    /// Original double-precision inverse, with the exact zero/determinant/final
    /// finite gates. Failure returns EMPTY and retains the source signed-zero route.
    pub fn inverse(self) -> Self {
        let [m00, m01, m02, m10, m11, m12, m20, m21, m22] = self.0.map(f64::from);
        let (a00, a01, a02, a10, a11, a12, a20, a21, a22) =
            (m00, m10, m20, m01, m11, m21, m02, m12, m22);
        let mut b0 = a00 * a11 - a01 * a10;
        let mut b1 = a00 * a12 - a02 * a10;
        let mut b2 = a01 * a12 - a02 * a11;
        let mut b3 = a20;
        let mut b4 = a21;
        let mut b5 = a22;
        let determinant = b0 * b5 - b1 * b4 + b2 * b3;
        if determinant == 0.0 {
            return Self::EMPTY;
        }
        let reciprocal = 1.0 / determinant;
        if reciprocal > f64::from(f32::MAX)
            || reciprocal < -f64::from(f32::MAX)
            || !reciprocal.is_finite()
        {
            return Self::EMPTY;
        }
        b0 *= reciprocal;
        b1 *= reciprocal;
        b2 *= reciprocal;
        b3 *= reciprocal;
        b4 *= reciprocal;
        b5 *= reciprocal;
        let result = Self([
            (a11 * b5 - a12 * b4) as f32,
            (a12 * b3 - a10 * b5) as f32,
            (a10 * b4 - a11 * b3) as f32,
            (a02 * b4 - a01 * b5) as f32,
            (a00 * b5 - a02 * b3) as f32,
            (a01 * b3 - a00 * b4) as f32,
            b2 as f32,
            (-b1) as f32,
            b0 as f32,
        ]);
        if result.is_finite() {
            result
        } else {
            Self::EMPTY
        }
    }
    /// Original left-times-right concatenation. Independent column lanes retain
    /// each source multiply/add order without fused arithmetic or reassociation.
    pub fn concat(self, other: Self) -> Self {
        let rows: [V; 3] = std::array::from_fn(|r| {
            V::from([other.0[r * 3], other.0[r * 3 + 1], other.0[r * 3 + 2], 0.0])
        });
        let mut output = [0.0; 9];
        for (row, destination) in output.chunks_exact_mut(3).enumerate() {
            let result = (V::splat(self.0[row * 3]) * rows[0]
                + V::splat(self.0[row * 3 + 1]) * rows[1]
                + V::splat(self.0[row * 3 + 2]) * rows[2])
                .to_array();
            destination.copy_from_slice(&result[..3]);
        }
        Self(output)
    }
}
impl TryFrom<&[f32]> for ColorSpaceXyz {
    type Error = Error;
    fn try_from(values: &[f32]) -> Result<Self, Error> {
        Ok(Self(values.try_into().map_err(|_| Error::InvalidInput)?))
    }
}
impl std::ops::Index<(usize, usize)> for ColorSpaceXyz {
    type Output = f32;
    fn index(&self, (column, row): (usize, usize)) -> &f32 {
        assert!(column < 3 && row < 3, "XYZ column and row must be in 0..3");
        &self.0[column + row * 3]
    }
}
