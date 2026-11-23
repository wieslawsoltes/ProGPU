//! Original native double affine source placement and physical quarter phase.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeAtlasHinting {
    #[default]
    Auto = 0,
    Fixed = 1,
    Animated = 2,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeAtlasFrame {
    /// Original row-vector double affine M11,M12,M21,M22,M31,M32.
    pub transform: [f64; 6],
    pub opacity: f32,
    pub hinting: NativeAtlasHinting,
    /// Actual original resolved baseline guideline policy, independent of X.
    pub preserve_baseline: bool,
    pub suppress_cleartype: bool,
}
impl Default for NativeAtlasFrame {
    fn default() -> Self {
        Self {
            transform: [1.0, 0.0, 0.0, 1.0, 0.0, 0.0],
            opacity: 1.0,
            hinting: NativeAtlasHinting::Auto,
            preserve_baseline: false,
            suppress_cleartype: false,
        }
    }
}
impl NativeAtlasFrame {
    pub fn validate(self) -> Result<(), Error> {
        if self.transform.into_iter().any(|v| !finite_float(v)) || !self.opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        self.scale()?;
        Ok(())
    }
    /// Compose the actual source target mapping in double before native snapping.
    pub fn then(self, next: [f64; 6]) -> Result<Self, Error> {
        let [a, b, c, d, x, y] = self.transform;
        let [e, f, g, h, u, v] = next;
        let frame = Self {
            transform: [
                a * e + b * g,
                a * f + b * h,
                c * e + d * g,
                c * f + d * h,
                x * e + y * g + u,
                x * f + y * h + v,
            ],
            ..self
        };
        frame.validate()?;
        Ok(frame)
    }
    pub(crate) fn scale(self) -> Result<f32, Error> {
        let [a, b, c, d, _, _] = self.transform;
        let xx = a * a + b * b;
        let xy = a * c + b * d;
        let yy = c * c + d * d;
        let half = (xx - yy) * 0.5;
        let maximum = (xx + yy) * 0.5 + half.hypot(xy);
        if !maximum.is_finite() || maximum <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let scale = maximum.sqrt();
        if !finite_float(scale) || scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        Ok(scale as f32)
    }
    pub(super) fn placement(self, position: [f32; 2], size: f32, dpi: f32) -> (u8, [f32; 2]) {
        let [a, b, c, d, _, _] = self.transform;
        if self.hinting == NativeAtlasHinting::Animated
            || b.abs() > 0.0001
            || c.abs() > 0.0001
            || a < 0.0
            || d < 0.0
        {
            return (0, position);
        }
        let Some(inverse) = inverse(self.transform) else {
            return (0, position);
        };
        let mut world = point(position, self.transform);
        world[0] *= dpi;
        world[1] *= dpi;
        let mut phase = 0;
        if size <= 24.0 {
            let mut integer = f64::from(world[0]).floor();
            let mut rounded = ((f64::from(world[0]) - integer) * 4.0).round_ties_even() as i32;
            if rounded == 4 {
                rounded = 0;
                integer += 1.0;
            }
            phase = rounded as u8;
            world[0] = integer as f32;
        } else {
            world[0] = world[0].round_ties_even();
        }
        if !self.preserve_baseline {
            world[1] = world[1].round_ties_even();
        }
        world[0] /= dpi;
        world[1] /= dpi;
        (phase, point(world, inverse))
    }
}
pub(super) fn finite_float(value: f64) -> bool {
    value.is_finite() && value.abs() <= f64::from(f32::MAX)
}
pub(super) fn point(p: [f32; 2], m: [f64; 6]) -> [f32; 2] {
    [
        (f64::from(p[0]) * m[0] + f64::from(p[1]) * m[2] + m[4]) as f32,
        (f64::from(p[0]) * m[1] + f64::from(p[1]) * m[3] + m[5]) as f32,
    ]
}
fn inverse(m: [f64; 6]) -> Option<[f64; 6]> {
    let [a, b, c, d, x, y] = m;
    let determinant = a * d - b * c;
    if !determinant.is_finite() || determinant == 0.0 {
        return None;
    }
    let reciprocal = 1.0 / determinant;
    let result = [
        d * reciprocal,
        -b * reciprocal,
        -c * reciprocal,
        a * reciprocal,
        (c * y - x * d) * reciprocal,
        (x * b - a * y) * reciprocal,
    ];
    result.into_iter().all(finite_float).then_some(result)
}
