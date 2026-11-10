//! Original image-filter frame, lens, crop and light arithmetic.
use super::*;
use crate::vector::Matrix4;
use wide::f32x4 as V;
/// Original rectangle endpoints; widths are derived only where the producer does.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FilterRect(pub [f32; 4]);
impl FilterRect {
    /// Left/top/right/bottom source bits.
    pub fn new(left: f32, top: f32, right: f32, bottom: f32) -> Self {
        Self([left, top, right, bottom])
    }
    /// Original finite positive-extent admission.
    pub fn is_valid(self) -> bool {
        self.0.iter().all(|v| v.is_finite()) && self.width() > 0.0 && self.height() > 0.0
    }
    /// Original endpoint subtraction.
    pub fn width(self) -> f32 {
        self.0[2] - self.0[0]
    }
    /// Independent original endpoint subtraction.
    pub fn height(self) -> f32 {
        self.0[3] - self.0[1]
    }
    /// Original intersection, without normalizing disjoint endpoints.
    pub fn intersect(self, rhs: Self) -> Self {
        Self([
            self.0[0].max(rhs.0[0]),
            self.0[1].max(rhs.0[1]),
            self.0[2].min(rhs.0[2]),
            self.0[3].min(rhs.0[3]),
        ])
    }
    /// Original inclusive endpoint containment.
    pub fn contains(self, rhs: Self) -> bool {
        self.0[0] <= rhs.0[0]
            && self.0[1] <= rhs.0[1]
            && self.0[2] >= rhs.0[2]
            && self.0[3] >= rhs.0[3]
    }
    /// Original full-canvas tolerance, evaluated before applying the filter frame.
    pub fn is_full_canvas(self, canvas: [f32; 2]) -> bool {
        self.0[0].abs() < 0.0001
            && self.0[1].abs() < 0.0001
            && (self.width() - canvas[0]).abs() < 0.0001
            && (self.height() - canvas[1]).abs() < 0.0001
    }
    /// Actual zero-origin texture extent.
    pub fn texture(size: [u32; 2]) -> Self {
        Self([0.0, 0.0, size[0] as f32, size[1] as f32])
    }
}
/// Complete original row-vector filter frame; a raw all-zero frame has the
/// producer's default/identity interpretation where explicitly required.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FilterTransform(pub Matrix4);
impl FilterTransform {
    /// Original explicit identity frame.
    pub const IDENTITY: Self = Self(Matrix4::IDENTITY);
    /// Retain all source coefficients. Numeric publication remains separate.
    pub fn new(matrix: Matrix4) -> Self {
        Self(matrix)
    }
    /// Original default matrix sentinel handling.
    pub fn effective(self) -> Matrix4 {
        if self.0.0 == [[0.0; 4]; 4] {
            Matrix4::IDENTITY
        } else {
            self.0
        }
    }
    /// Original finite whole-frame check.
    pub fn is_finite(self) -> bool {
        self.0.0.iter().flatten().all(|v| v.is_finite())
    }
    /// Independent original Vector2 normal lengths with fallback one.
    pub fn axis_scales(self) -> [f32; 2] {
        let m = self.0.0;
        let v = V::from([m[0][0], m[0][1], m[1][0], m[1][1]]);
        let p = (v * v).to_array();
        [p[0] + p[1], p[2] + p[3]].map(|v| {
            let v = v.sqrt();
            if v.is_finite() && v > 0.0 { v } else { 1.0 }
        })
    }
    /// Original normal-vector transform, preserving the source on nonfinite output.
    pub fn vector(self, value: [f32; 2]) -> [f32; 2] {
        if self.0.0 == [[0.0; 4]; 4] {
            return value;
        }
        let m = self.0.0;
        let r = [
            value[0] * m[0][0] + value[1] * m[1][0],
            value[0] * m[0][1] + value[1] * m[1][1],
        ];
        if r.iter().all(|v| v.is_finite()) {
            r
        } else {
            value
        }
    }
    /// Original complete displacement basis; no scalar maximum-scale substitution.
    pub fn displacement(self, scale: f32) -> [f32; 4] {
        let x = self.vector([scale, 0.0]);
        let y = self.vector([0.0, scale]);
        [x[0], x[1], y[0], y[1]]
    }
    /// Four original transformed corners and nested min/max reductions.
    pub fn bounds(self, rect: FilterRect) -> FilterRect {
        let [l, t, r, b] = rect.0;
        let m = self.effective().0;
        let x = V::from([l, r, r, l]);
        let y = V::from([t, t, b, b]);
        let xx = (x * V::splat(m[0][0]) + y * V::splat(m[1][0]) + V::splat(m[3][0])).to_array();
        let yy = (x * V::splat(m[0][1]) + y * V::splat(m[1][1]) + V::splat(m[3][1])).to_array();
        FilterRect([
            xx[0].min(xx[1]).min(xx[2].min(xx[3])),
            yy[0].min(yy[1]).min(yy[2].min(yy[3])),
            xx[0].max(xx[1]).max(xx[2].max(xx[3])),
            yy[0].max(yy[1]).max(yy[2].max(yy[3])),
        ])
    }
    /// Original non-Decal convolution domain; invalid/nonintersecting crop is absent.
    pub fn crop_pixels(
        self,
        crop: Option<FilterRect>,
        size: [u32; 2],
    ) -> Option<([i32; 2], [i32; 2])> {
        let (origin, extent, valid) = self.convolution_domain(crop, size);
        valid.then_some((origin, extent))
    }
    /// Original complete out-parameter contract, including zero initialization
    /// and computed extents retained when a crop later fails domain admission.
    pub fn convolution_domain(
        self,
        crop: Option<FilterRect>,
        size: [u32; 2],
    ) -> ([i32; 2], [i32; 2], bool) {
        let Some(crop) = crop.filter(|r| r.is_valid()) else {
            return ([0; 2], [0; 2], false);
        };
        let r = self.bounds(crop);
        if !r.is_valid()
            || r.0[2] <= 0.0
            || r.0[3] <= 0.0
            || r.0[0] >= size[0] as f32
            || r.0[1] >= size[1] as f32
        {
            return ([0; 2], [0; 2], false);
        }
        let lo = [r.0[0], r.0[1]].map(|v| f64::from(v).floor().clamp(-1e9, 1e9) as i32);
        let hi = [r.0[2], r.0[3]].map(|v| f64::from(v).ceil().clamp(-1e9, 1e9) as i32);
        let extent = [hi[0] - lo[0], hi[1] - lo[1]];
        (
            lo,
            extent,
            extent.iter().all(|v| *v > 0 && *v <= 1_000_000_000),
        )
    }
    /// Actual inverse effective source frame, including the original all-zero
    /// sentinel. Procedural shader captures use this before their local frame.
    pub fn inverse_effective(self) -> Option<Matrix4> {
        inverse(self.effective())
    }
    /// Invert an explicit source-local matrix without applying the filter-frame
    /// all-zero sentinel. A zero shader local matrix remains singular.
    pub fn inverse_raw(self) -> Option<Matrix4> {
        inverse(self.0)
    }
    /// Ordered full row-vector frame product, retaining all sixteen components.
    pub fn concatenate(self, following: Matrix4) -> Matrix4 {
        multiply(self.effective(), following)
    }
    /// Original inverse-layer * local * layer composition. Singular or nonfinite
    /// frames produce absence, selecting the original transparent capture.
    pub fn device_matrix(self, local: Matrix4) -> Option<Matrix4> {
        let frame = self.effective();
        let inverse = inverse(frame)?;
        let result = multiply(multiply(inverse, local), frame);
        result
            .0
            .iter()
            .flatten()
            .all(|v| v.is_finite())
            .then_some(result)
    }
    /// Original transformed light XY, independent geometric-mean Z and surface scale.
    pub fn light(self, mut light: ImageLightingInput) -> ImageLightingInput {
        let m = self.effective().0;
        let x = (m[0][0] * m[0][0] + m[0][1] * m[0][1]).sqrt();
        let y = (m[1][0] * m[1][0] + m[1][1] * m[1][1]).sqrt();
        let mut z = (x * y).max(0.0).sqrt();
        if !z.is_finite() || z <= f32::from_bits(1) {
            z = 1.0;
        }
        let point = |p: [f32; 3]| {
            let xy = self.effective().transform_point([p[0], p[1]]);
            [xy[0], xy[1], p[2] * z]
        };
        if light.light_type == 0 {
            let p = light.light_position;
            let zz = p[2] * z;
            light.light_position =
                std::array::from_fn(|c| p[0] * m[0][c] + p[1] * m[1][c] + zz * m[2][c]);
        } else {
            light.light_position = point(light.light_position);
            if light.light_type == 2 {
                light.light_target = point(light.light_target);
            }
        }
        light.surface_scale *= z;
        light
    }
}
/// Original magnifier center clamp and visible-source fit.
pub fn magnifier_zoom(
    lens: FilterRect,
    visible: FilterRect,
    available: FilterRect,
    zoom: f32,
) -> [f32; 4] {
    let inverse = 1.0 / zoom;
    let center = [
        ((lens.0[0] + lens.0[2]) * 0.5).clamp(available.0[0], available.0[2]),
        ((lens.0[1] + lens.0[3]) * 0.5).clamp(available.0[1], available.0[3]),
    ];
    let mut translate = center.map(|v| v * (1.0 - inverse));
    if !available.contains(visible) {
        let sampled = FilterRect([
            translate[0] + inverse * visible.0[0],
            translate[1] + inverse * visible.0[1],
            translate[0] + inverse * visible.0[2],
            translate[1] + inverse * visible.0[3],
        ]);
        if available.width() >= sampled.width() && available.height() >= sampled.height() {
            let left = if sampled.0[0] < available.0[0] {
                available.0[0]
            } else {
                sampled.0[2].min(available.0[2]) - sampled.width()
            };
            let top = if sampled.0[1] < available.0[1] {
                available.0[1]
            } else {
                sampled.0[3].min(available.0[3]) - sampled.height()
            };
            translate = [left - inverse * visible.0[0], top - inverse * visible.0[1]];
        }
    }
    [translate[0], translate[1], inverse, inverse]
}
fn multiply(a: Matrix4, b: Matrix4) -> Matrix4 {
    let rows = b.0.map(V::from);
    Matrix4(a.0.map(|r| {
        (V::splat(r[0]) * rows[0]
            + V::splat(r[1]) * rows[1]
            + V::splat(r[2]) * rows[2]
            + V::splat(r[3]) * rows[3])
            .to_array()
    }))
}
fn inverse(m: Matrix4) -> Option<Matrix4> {
    if !m.0.iter().flatten().all(|v| v.is_finite()) {
        return None;
    }
    // Complete adjugate construction. Independent cofactor row lanes are kept
    // together; determinant reduction and inversion preserve their dependencies.
    let mut cofactors = [[0.0; 4]; 4];
    for (row, out) in cofactors.iter_mut().enumerate() {
        let rows: [usize; 3] = std::array::from_fn(|i| if i < row { i } else { i + 1 });
        let triples: [[f32; 9]; 4] = std::array::from_fn(|column| {
            let mut values = [0.0; 9];
            let mut i = 0;
            for r in &rows {
                for c in 0..4 {
                    if c != column {
                        values[i] = m.0[*r][c];
                        i += 1;
                    }
                }
            }
            values
        });
        let lane = |i| V::from(triples.map(|t| t[i]));
        let values = lane(0) * (lane(4) * lane(8) - lane(5) * lane(7))
            - lane(1) * (lane(3) * lane(8) - lane(5) * lane(6))
            + lane(2) * (lane(3) * lane(7) - lane(4) * lane(6));
        *out = (values
            * V::from(std::array::from_fn(|c| {
                if (row + c) % 2 == 0 { 1.0 } else { -1.0 }
            })))
        .to_array();
    }
    let terms = (V::from(m.0[0]) * V::from(cofactors[0])).to_array();
    let determinant = terms[0] + terms[1] + terms[2] + terms[3];
    if !determinant.is_finite() || determinant.abs() < f32::from_bits(1) {
        return None;
    }
    let inverse = 1.0 / determinant;
    let result = Matrix4(std::array::from_fn(|r| {
        (V::from(std::array::from_fn(|c| cofactors[c][r])) * V::splat(inverse)).to_array()
    }));
    result
        .0
        .iter()
        .flatten()
        .all(|v| v.is_finite())
        .then_some(result)
}
