//! Original bounded directional, point, ambient and spot light records.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum LightKind3D {
    #[default]
    Ambient = 0,
    Directional = 1,
    Point = 2,
    Spot = 3,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Light3D {
    pub kind: LightKind3D,
    pub color: [f32; 4],
    pub position: [f32; 3],
    pub direction: [f32; 3],
    pub range: f32,
    pub constant_attenuation: f32,
    pub linear_attenuation: f32,
    pub quadratic_attenuation: f32,
    pub inner_cone_cosine: f32,
    pub outer_cone_cosine: f32,
}
impl Default for Light3D {
    fn default() -> Self {
        Self {
            kind: Default::default(),
            color: [1.0; 4],
            position: [0.0; 3],
            direction: [0.0, 0.0, -1.0],
            range: f32::MAX,
            constant_attenuation: 1.0,
            linear_attenuation: 0.0,
            quadratic_attenuation: 0.0,
            inner_cone_cosine: 0.0,
            outer_cone_cosine: 0.0,
        }
    }
}
impl Light3D {
    pub fn record(self) -> Result<GpuLight3DRecord, Error> {
        if self.color.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let mut result = GpuLight3DRecord {
            metadata: [self.kind as u32 as f32, 0.0, 0.0, 0.0],
            color: self.color,
            ..Default::default()
        };
        if self.kind == LightKind3D::Ambient {
            return Ok(result);
        }
        let direction = || {
            let d =
                wide::f32x4::from([self.direction[0], self.direction[1], self.direction[2], 0.0]);
            let squared = (d * d).to_array();
            let length = squared[0] + squared[1] + squared[2];
            if self.direction.iter().any(|v| !v.is_finite()) || length <= 0.000001 {
                return Err(Error::InvalidInput);
            }
            let n = (d / wide::f32x4::splat(length.sqrt())).to_array();
            Ok([n[0], n[1], n[2]])
        };
        if self.kind == LightKind3D::Directional {
            result.direction_inner_cos = super::records::extend(direction()?, 0.0);
            return Ok(result);
        }
        let attenuation = [
            self.constant_attenuation,
            self.linear_attenuation,
            self.quadratic_attenuation,
        ];
        if self.position.iter().any(|v| !v.is_finite())
            || !self.range.is_finite()
            || self.range <= 0.0
            || attenuation.iter().any(|v| !v.is_finite() || *v < 0.0)
            || attenuation == [0.0; 3]
        {
            return Err(Error::InvalidInput);
        }
        result.position_range = super::records::extend(self.position, self.range);
        result.attenuation_outer_cos = super::records::extend(attenuation, 0.0);
        if self.kind == LightKind3D::Point {
            return Ok(result);
        }
        if !self.inner_cone_cosine.is_finite()
            || !self.outer_cone_cosine.is_finite()
            || !(-1.0..=1.0).contains(&self.inner_cone_cosine)
            || !(-1.0..=1.0).contains(&self.outer_cone_cosine)
            || self.inner_cone_cosine < self.outer_cone_cosine
        {
            return Err(Error::InvalidInput);
        }
        result.direction_inner_cos = super::records::extend(direction()?, self.inner_cone_cosine);
        result.attenuation_outer_cos[3] = self.outer_cone_cosine;
        Ok(result)
    }
}
