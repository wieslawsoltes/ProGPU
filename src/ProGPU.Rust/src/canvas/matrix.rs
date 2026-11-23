//! Original SKMatrix state arithmetic and perspective-clipped bounds.
use super::*;
/// Reuse the existing original nine-coefficient source matrix inventory. Matrix
/// state retains perspective even where a specific drawing route rejects it.
pub type CanvasMatrix = crate::vector::EffectMatrix;
impl CanvasMatrix {
    /// Original complete perspective marker.
    pub fn has_perspective(self) -> bool {
        self.0[6] != 0.0 || self.0[7] != 0.0 || self.0[8] != 1.0
    }
    /// Original affine double products, float translation addition, and full
    /// perspective float dot-product order; identity shortcuts preserve bits.
    pub fn concat(self, rhs: Self) -> Self {
        if self == Self::IDENTITY {
            return rhs;
        }
        if rhs == Self::IDENTITY {
            return self;
        }
        let a = self.0;
        let b = rhs.0;
        if !self.has_perspective() && !rhs.has_perspective() {
            let left = wide::f64x4::from([
                f64::from(a[0]),
                f64::from(a[0]),
                f64::from(a[3]),
                f64::from(a[3]),
            ]);
            let right = wide::f64x4::from([
                f64::from(a[1]),
                f64::from(a[1]),
                f64::from(a[4]),
                f64::from(a[4]),
            ]);
            let basis = (left
                * wide::f64x4::from([
                    f64::from(b[0]),
                    f64::from(b[1]),
                    f64::from(b[0]),
                    f64::from(b[1]),
                ])
                + right
                    * wide::f64x4::from([
                        f64::from(b[3]),
                        f64::from(b[4]),
                        f64::from(b[3]),
                        f64::from(b[4]),
                    ]))
            .to_array()
            .map(|v| v as f32);
            let add = |x: f32, y: f32, z: f32, w: f32| {
                (f64::from(x) * f64::from(y) + f64::from(z) * f64::from(w)) as f32
            };
            return Self([
                basis[0],
                basis[1],
                add(a[0], b[2], a[1], b[5]) + a[2],
                basis[2],
                basis[3],
                add(a[3], b[2], a[4], b[5]) + a[5],
                0.0,
                0.0,
                1.0,
            ]);
        }
        Self(std::array::from_fn(|i| {
            let r = i / 3 * 3;
            let c = i % 3;
            a[r] * b[c] + a[r + 1] * b[3 + c] + a[r + 2] * b[6 + c]
        }))
    }
    /// Complete original finite inverse, preserving diagonal fast-path arithmetic.
    pub fn invert_full(self) -> Option<Self> {
        if self == Self::IDENTITY {
            return Some(self);
        }
        let [a, b, x, c, d, y, p, q, w] = self.0;
        if !self.has_perspective() && b == 0.0 && c == 0.0 {
            let ix = 1.0 / a;
            let iy = 1.0 / d;
            let tx = -x * ix;
            let ty = -y * iy;
            return [ix, iy, tx, ty]
                .iter()
                .all(|v| v.is_finite())
                .then_some(Self([ix, 0.0, tx, 0.0, iy, ty, 0.0, 0.0, 1.0]));
        }
        let cross = |a: f32, b: f32, c: f32, d: f32| {
            f64::from(a) * f64::from(b) - f64::from(c) * f64::from(d)
        };
        let determinant = if self.has_perspective() {
            f64::from(a) * cross(d, w, y, q)
                + f64::from(b) * cross(y, p, c, w)
                + f64::from(x) * cross(c, q, d, p)
        } else {
            cross(a, d, b, c)
        };
        if !determinant.is_finite() || (determinant as f32).abs() <= 1.0 / 68719476736.0 {
            return None;
        }
        let inverse = 1.0 / determinant;
        let result = if self.has_perspective() {
            Self(
                [
                    cross(d, w, y, q),
                    cross(x, q, b, w),
                    cross(b, y, x, d),
                    cross(y, p, c, w),
                    cross(a, w, x, p),
                    cross(x, c, a, y),
                    cross(c, q, d, p),
                    cross(b, p, a, q),
                    cross(a, d, b, c),
                ]
                .map(|v| (v * inverse) as f32),
            )
        } else {
            Self([
                (f64::from(d) * inverse) as f32,
                (f64::from(-b) * inverse) as f32,
                (cross(b, y, d, x) * inverse) as f32,
                (f64::from(-c) * inverse) as f32,
                (f64::from(a) * inverse) as f32,
                (cross(c, x, a, y) * inverse) as f32,
                0.0,
                0.0,
                1.0,
            ])
        };
        result.0.iter().all(|v| v.is_finite()).then_some(result)
    }
    /// Original full source matrix embedding, with no inferred affine rewrite.
    pub fn matrix4(self) -> crate::vector::Matrix4 {
        let m = self.0;
        crate::vector::Matrix4([
            [m[0], m[3], 0.0, m[6]],
            [m[1], m[4], 0.0, m[7]],
            [0.0, 0.0, 1.0, 0.0],
            [m[2], m[5], 0.0, m[8]],
        ])
    }
    /// Explicit current renderer admission; state itself remains complete.
    pub fn affine(self) -> Result<crate::geometry::Affine, Error> {
        if self.has_perspective() || self.0.iter().any(|v| !v.is_finite()) {
            return Err(Error::UnsupportedExecution);
        }
        let m = self.0;
        Ok(crate::geometry::Affine([
            m[0], m[3], m[1], m[4], m[2], m[5],
        ]))
    }
    /// Original source rotation uses double sine/cosine narrowed independently.
    pub fn rotation(radians: f32) -> Self {
        if radians == 0.0 {
            return Self::IDENTITY;
        }
        let sine = f64::from(radians).sin() as f32;
        let cosine = f64::from(radians).cos() as f32;
        Self([cosine, -sine, 0.0, sine, cosine, 0.0, 0.0, 0.0, 1.0])
    }
    /// Original source MapRect, including clipping against W >= 1/16384.
    pub fn map_rect(self, bounds: [f32; 4]) -> [f32; 4] {
        let m = self.0;
        let [l, t, r, b] = bounds;
        if !self.has_perspective() {
            let xs = [l * m[0], r * m[0]];
            let xk = [t * m[1], b * m[1]];
            let yk = [l * m[3], r * m[3]];
            let ys = [t * m[4], b * m[4]];
            return [
                minimum(xs[0], xs[1]) + minimum(xk[0], xk[1]) + m[2],
                minimum(yk[0], yk[1]) + minimum(ys[0], ys[1]) + m[5],
                maximum(xs[0], xs[1]) + maximum(xk[0], xk[1]) + m[2],
                maximum(yk[0], yk[1]) + maximum(ys[0], ys[1]) + m[5],
            ];
        }
        let input = [[l, t], [r, t], [r, b], [l, b]].map(|[x, y]| {
            [
                x * m[0] + y * m[1] + m[2],
                x * m[3] + y * m[4] + m[5],
                x * m[6] + y * m[7] + m[8],
            ]
        });
        let mut points = [[0.0; 3]; 8];
        let mut count = 0;
        let near = 1.0 / 16384.0;
        let mut previous = input[3];
        let mut inside = previous[2] >= near;
        for current in input {
            let current_inside = current[2] >= near;
            if inside != current_inside {
                let amount = (near - previous[2]) / (current[2] - previous[2]);
                points[count] = [
                    previous[0] + (current[0] - previous[0]) * amount,
                    previous[1] + (current[1] - previous[1]) * amount,
                    near,
                ];
                count += 1;
            }
            if current_inside {
                points[count] = current;
                count += 1;
            }
            previous = current;
            inside = current_inside;
        }
        if count == 0 {
            return [0.0; 4];
        }
        let inverse = 1.0 / points[0][2];
        let first = [points[0][0] * inverse, points[0][1] * inverse];
        let mut result = [first[0], first[1], first[0], first[1]];
        for p in &points[1..count] {
            let inverse = 1.0 / p[2];
            let x = p[0] * inverse;
            let y = p[1] * inverse;
            result = [
                minimum(result[0], x),
                minimum(result[1], y),
                maximum(result[2], x),
                maximum(result[3], y),
            ];
        }
        result
    }
}
fn minimum(a: f32, b: f32) -> f32 {
    if a.is_nan() || b.is_nan() {
        f32::NAN
    } else {
        a.min(b)
    }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a.is_nan() || b.is_nan() {
        f32::NAN
    } else {
        a.max(b)
    }
}
