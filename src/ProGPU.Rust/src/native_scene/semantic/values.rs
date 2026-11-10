//! Original native semantic scalar admission; not managed rendering policy.
use crate::native_scene::wire::{self, constants::*};
mod geometry;
mod glyph;
mod image;
mod layer;
mod path;
mod three_d;
pub(crate) use geometry::*;
pub(crate) use glyph::*;
pub(crate) use image::*;
pub(crate) use layer::*;
pub(crate) use path::*;
pub(crate) use three_d::*;

trait Finite {
    fn finite(self) -> bool;
}
fn finite<T: Finite>(value: T) -> bool {
    value.finite()
}
fn binary_flag(value: f32) -> bool {
    value == 0.0 || value == 1.0
}
impl Finite for wire::Point {
    fn finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite()
    }
}
impl Finite for wire::Color {
    fn finite(self) -> bool {
        [self.r, self.g, self.b, self.a]
            .into_iter()
            .all(f32::is_finite)
    }
}
impl Finite for wire::Affine2d {
    fn finite(self) -> bool {
        [self.m11, self.m12, self.m21, self.m22, self.m31, self.m32]
            .into_iter()
            .all(f32::is_finite)
    }
}

pub(crate) fn stroke_scales(transform: &wire::Affine2d) -> Option<(f32, f32)> {
    if !finite(*transform) {
        return None;
    }
    let (m11, m12, m21, m22) = (
        f64::from(transform.m11),
        f64::from(transform.m12),
        f64::from(transform.m21),
        f64::from(transform.m22),
    );
    let sum = m11 * m11 + m12 * m12 + m21 * m21 + m22 * m22;
    let determinant = m11 * m22 - m12 * m21;
    let discriminant = (sum * sum - 4.0 * determinant * determinant).max(0.0);
    let maximum = ((sum + discriminant.sqrt()) * 0.5).max(0.0).sqrt();
    let minimum = ((sum - discriminant.sqrt()) * 0.5).max(0.0).sqrt();
    if !maximum.is_finite() || maximum <= 0.000001 || !minimum.is_finite() || minimum <= 0.000001 {
        return None;
    }
    let (maximum, minimum) = (maximum as f32, minimum as f32);
    (maximum.is_finite() && maximum > 0.000001 && minimum.is_finite() && minimum > 0.000001)
        .then_some((maximum, minimum))
}
pub(crate) fn analytic(primitive: &wire::AnalyticPrimitive) -> bool {
    let Some((_, minimum)) = stroke_scales(&primitive.transform) else {
        return false;
    };
    let padding = 1.5 / minimum;
    primitive.kind <= PRIMITIVE_ROUNDED_RECTANGLE
        && primitive.flags & !PRIMITIVE_FLAG_EDGE_ALIASED == 0
        && [
            primitive.x,
            primitive.y,
            primitive.width,
            primitive.height,
            primitive.corner_radius,
            primitive.stroke_thickness,
        ]
        .into_iter()
        .all(f32::is_finite)
        && primitive.width >= 0.0
        && primitive.height >= 0.0
        && primitive.stroke_thickness >= 0.0
        && finite(primitive.color)
        && finite(primitive.transform)
        && padding.is_finite()
        && padding > 0.0
}
fn coverage(width: f32, height: f32) -> Option<u64> {
    if !width.is_finite()
        || !height.is_finite()
        || width <= 0.0
        || height <= 0.0
        || width > 4092.0
        || height > 4092.0
    {
        return None;
    }
    let width = width as u32;
    Some(u64::from((width + 255) / 256 * 256) * u64::from(height as u32))
}
