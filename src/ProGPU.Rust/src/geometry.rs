//! Analytic quad expansion ported from ProGPU.Native's geometry headers.
//! Each primitive takes O(1) work and four vertices/six indices. Recording is
//! amortized O(N); no tessellation or GPU initialization occurs here.

use crate::Error;
use bytemuck::{Pod, Zeroable};

/// Row-vector affine transform in the native `m11,m12,m21,m22,m31,m32` order.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Affine(pub [f32; 6]);

impl Affine {
    /// Identity transform.
    pub const IDENTITY: Self = Self([1.0, 0.0, 0.0, 1.0, 0.0, 0.0]);

    /// Transform a point using the original native operation order (no FMA).
    pub fn transform_point(self, [x, y]: [f32; 2]) -> [f32; 2] {
        let [a, b, c, d, tx, ty] = self.0;
        [x * a + y * c + tx, x * b + y * d + ty]
    }

    /// Original native maximum/minimum singular scales, calculated in double
    /// precision and narrowed once. Singular and near-singular frames reject.
    pub fn scales(self) -> Result<[f32; 2], Error> {
        if self.0.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let [m11, m12, m21, m22, _, _] = self.0.map(f64::from);
        let sum = m11 * m11 + m12 * m12 + m21 * m21 + m22 * m22;
        let determinant = m11 * m22 - m12 * m21;
        let discriminant = (sum * sum - 4.0 * determinant * determinant).max(0.0);
        let maximum = ((sum + discriminant.sqrt()) * 0.5).max(0.0).sqrt();
        let minimum = ((sum - discriminant.sqrt()) * 0.5).max(0.0).sqrt();
        if [maximum, minimum]
            .iter()
            .any(|v| !v.is_finite() || *v <= 0.000001)
        {
            return Err(Error::InvalidInput);
        }
        let scales = [maximum as f32, minimum as f32];
        if scales.iter().any(|v| !v.is_finite() || *v <= 0.000001) {
            return Err(Error::InvalidInput);
        }
        Ok(scales)
    }
}

impl Default for Affine {
    fn default() -> Self {
        Self::IDENTITY
    }
}

/// Canonical vector shader vertex; offsets are shared with C# and C++.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct VectorVertex {
    /// Target logical coordinates.
    pub position: [f32; 2],
    /// Straight RGBA color.
    pub color: [f32; 4],
    /// Primitive-local coordinates.
    pub texture_coordinate: [f32; 2],
    /// Retained material index.
    pub brush_index: f32,
    /// Original unpadded width and height.
    pub shape_size: [f32; 2],
    /// Clamped round-rectangle radius.
    pub corner_radius: f32,
    /// Local stroke thickness (zero means fill).
    pub stroke_thickness: f32,
    /// Native primitive kind, with the original aliased-edge encoding.
    pub shape_type: f32,
}

const _: () = {
    assert!(size_of::<VectorVertex>() == 56);
    assert!(std::mem::offset_of!(VectorVertex, color) == 8);
    assert!(std::mem::offset_of!(VectorVertex, texture_coordinate) == 24);
    assert!(std::mem::offset_of!(VectorVertex, brush_index) == 32);
    assert!(std::mem::offset_of!(VectorVertex, shape_size) == 36);
    assert!(std::mem::offset_of!(VectorVertex, shape_type) == 52);
};

/// Analytic shape discriminants consumed by canonical Vector.wgsl.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum Shape {
    /// Axis-aligned rectangle before the primitive transform.
    Rectangle = 0,
    /// Ellipse inscribed in the primitive bounds.
    Ellipse = 1,
    /// Rectangle with a circular corner radius.
    RoundedRectangle = 2,
}

/// A native analytic primitive in its original local frame.
#[derive(Clone, Copy, Debug)]
pub struct AnalyticPrimitive {
    /// Shape family.
    pub shape: Shape,
    /// Local x, y, width and height; sizes must be nonnegative.
    pub bounds: [f32; 4],
    /// Straight RGBA color; finite HDR values are preserved.
    pub color: [f32; 4],
    /// Local-to-target transform.
    pub transform: Affine,
    /// Radius used only for rounded rectangles; absolute value is clamped.
    pub corner_radius: f32,
    /// Nonnegative stroke width; zero fills the shape.
    pub stroke_thickness: f32,
    /// Select the native binary edge contract.
    pub aliased: bool,
}

impl AnalyticPrimitive {
    /// Construct a filled rectangle with identity transform and antialiasing.
    pub fn rectangle(bounds: [f32; 4], color: [f32; 4]) -> Self {
        Self {
            shape: Shape::Rectangle,
            bounds,
            color,
            transform: Affine::IDENTITY,
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            aliased: false,
        }
    }

    /// Expand the original four vertices without allocation.
    ///
    /// `antialias_padding` is measured in the primitive's local coordinates.
    /// It must be finite and positive, including for explicitly aliased fills.
    pub fn vertices(&self, antialias_padding: f32) -> Result<[VectorVertex; 4], Error> {
        let [x, y, width, height] = self.bounds;
        if !self
            .bounds
            .iter()
            .chain(self.color.iter())
            .chain(self.transform.0.iter())
            .all(|v| v.is_finite())
            || width < 0.0
            || height < 0.0
            || !self.corner_radius.is_finite()
            || !self.stroke_thickness.is_finite()
            || self.stroke_thickness < 0.0
            || !antialias_padding.is_finite()
            || antialias_padding <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let radius = if self.shape == Shape::RoundedRectangle {
            self.corner_radius.abs().min(width.min(height) * 0.5)
        } else {
            0.0
        };
        let stroke_padding = self.stroke_thickness * 0.5;
        let aliased_fill_rectangle =
            self.shape == Shape::Rectangle && self.stroke_thickness == 0.0 && self.aliased;
        let padding = if aliased_fill_rectangle {
            0.0
        } else {
            antialias_padding + stroke_padding
        };
        let half_width = width * 0.5;
        let half_height = height * 0.5;
        let left = x - padding;
        let top = y - padding;
        let right = x + width + padding;
        let bottom = y + height + padding;
        let local_left = -half_width - padding;
        let local_top = -half_height - padding;
        let local_right = half_width + padding;
        let local_bottom = half_height + padding;
        let make = |x, y, u, v| VectorVertex {
            position: self.transform.transform_point([x, y]),
            color: self.color,
            texture_coordinate: [u, v],
            brush_index: 0.0,
            shape_size: [width, height],
            corner_radius: radius,
            stroke_thickness: self.stroke_thickness,
            shape_type: self.shape as u32 as f32 + if self.aliased { 1000.0 } else { 0.0 },
        };
        Ok([
            make(left, top, local_left, local_top),
            make(right, top, local_right, local_top),
            make(right, bottom, local_right, local_bottom),
            make(left, bottom, local_left, local_bottom),
        ])
    }
}

/// Contiguous immutable-on-borrow geometry storage, reusable across frames.
#[derive(Default, Debug)]
pub struct Mesh {
    vertices: Vec<VectorVertex>,
    indices: Vec<u32>,
}

impl Mesh {
    /// Append one primitive transactionally, retaining original quad/triangle order.
    pub fn push(
        &mut self,
        primitive: &AnalyticPrimitive,
        antialias_padding: f32,
    ) -> Result<(), Error> {
        let vertices = primitive.vertices(antialias_padding)?;
        self.push_quad(vertices)
    }

    pub(crate) fn push_quad(&mut self, vertices: [VectorVertex; 4]) -> Result<(), Error> {
        let base = u32::try_from(self.vertices.len()).map_err(|_| Error::Capacity)?;
        base.checked_add(4).ok_or(Error::Capacity)?;
        self.vertices.try_reserve(4).map_err(|_| Error::Capacity)?;
        self.indices.try_reserve(6).map_err(|_| Error::Capacity)?;
        self.vertices.extend_from_slice(&vertices);
        self.indices
            .extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
        Ok(())
    }
    /// Read the original vertex records.
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    /// Read ordered triangle indices.
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
    /// Empty the recorder while retaining capacity.
    pub fn clear(&mut self) {
        self.vertices.clear();
        self.indices.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aliased_rectangle_owns_exact_unpadded_edges() {
        let mut p = AnalyticPrimitive::rectangle([0.5, -2.5, 4.0, 3.0], [1.0; 4]);
        p.aliased = true;
        let vertices = p.vertices(1.5).unwrap();
        assert_eq!(vertices[0].position, [0.5, -2.5]);
        assert_eq!(vertices[2].position, [4.5, 0.5]);
        assert_eq!(vertices[0].shape_type, 1000.0);
    }

    #[test]
    fn rejected_append_keeps_previous_geometry() {
        let mut mesh = Mesh::default();
        let mut p = AnalyticPrimitive::rectangle([0.0, 0.0, 10.0, 20.0], [1.0; 4]);
        mesh.push(&p, 1.5).unwrap();
        let before = mesh.vertices.clone();
        p.transform.0[4] = f32::NAN;
        assert!(mesh.push(&p, 1.5).is_err());
        assert_eq!(mesh.vertices(), before);
        assert_eq!(mesh.indices(), [0, 1, 2, 0, 2, 3]);
    }
}
