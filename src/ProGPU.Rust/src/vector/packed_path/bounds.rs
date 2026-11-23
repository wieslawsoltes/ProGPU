//! Original double derivative roots and independent component evaluation.
use super::super::{maximum, minimum};
use wide::{f32x4 as V, f64x2 as D};
const EPS: f64 = 1e-12;
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Bounds {
    value: Option<[f32; 4]>,
}
impl Bounds {
    pub(super) fn include(&mut self, p: [f32; 2]) {
        if p.iter().any(|v| !v.is_finite()) {
            return;
        }
        self.value = Some(self.value.map_or([p[0], p[1], p[0], p[1]], |b| {
            [
                minimum(b[0], p[0]),
                minimum(b[1], p[1]),
                maximum(b[2], p[0]),
                maximum(b[3], p[1]),
            ]
        }));
    }
    pub(super) fn rect(self) -> [f32; 4] {
        self.value.unwrap_or([0.0; 4])
    }
    pub(super) fn translate(&mut self, p: [f32; 2]) {
        if let Some(b) = self.value {
            self.value = Some((V::from(b) + V::from([p[0], p[1], p[0], p[1]])).to_array());
        }
    }
    pub(super) fn quadratic(&mut self, p0: [f32; 2], p1: [f32; 2], p2: [f32; 2]) {
        for axis in 0..2 {
            if p1[axis] < minimum(p0[axis], p2[axis]) || p1[axis] > maximum(p0[axis], p2[axis]) {
                let [a, b, c] = [p0[axis], p1[axis], p2[axis]].map(f64::from);
                let denominator = a - 2.0 * b + c;
                if denominator.abs() <= EPS {
                    continue;
                }
                let t = (a - b) / denominator;
                if t > 0.0 && t < 1.0 {
                    let u = 1.0 - t;
                    self.include(point(
                        D::splat(u * u) * pair(p0)
                            + D::splat(2.0 * u * t) * pair(p1)
                            + D::splat(t * t) * pair(p2),
                    ));
                }
            }
        }
    }
    pub(super) fn cubic(&mut self, p0: [f32; 2], p1: [f32; 2], p2: [f32; 2], p3: [f32; 2]) {
        for axis in 0..2 {
            let min = minimum(p0[axis], p3[axis]);
            let max = maximum(p0[axis], p3[axis]);
            if !(p1[axis] < min || p1[axis] > max || p2[axis] < min || p2[axis] > max) {
                continue;
            }
            let [v0, v1, v2, v3] = [p0[axis], p1[axis], p2[axis], p3[axis]].map(f64::from);
            let a = -v0 + 3.0 * v1 - 3.0 * v2 + v3;
            let b = 2.0 * (v0 - 2.0 * v1 + v2);
            let c = v1 - v0;
            if a.abs() <= EPS {
                if b.abs() > EPS {
                    self.cubic_at(-c / b, [p0, p1, p2, p3]);
                }
                continue;
            }
            let discriminant = b * b - 4.0 * a * c;
            if discriminant < 0.0 {
                continue;
            }
            let root = if discriminant.is_nan() {
                discriminant
            } else {
                discriminant.max(0.0)
            }
            .sqrt();
            let denominator = 2.0 * a;
            self.cubic_at((-b + root) / denominator, [p0, p1, p2, p3]);
            if root > EPS {
                self.cubic_at((-b - root) / denominator, [p0, p1, p2, p3]);
            }
        }
    }
    fn cubic_at(&mut self, t: f64, p: [[f32; 2]; 4]) {
        if !(t > 0.0 && t < 1.0 && t.is_finite()) {
            return;
        }
        let u = 1.0 - t;
        let u2 = u * u;
        let t2 = t * t;
        self.include(point(
            D::splat(u2 * u) * pair(p[0])
                + D::splat(3.0 * u2 * t) * pair(p[1])
                + D::splat(3.0 * u * t2) * pair(p[2])
                + D::splat(t2 * t) * pair(p[3]),
        ));
    }
}
fn pair(p: [f32; 2]) -> D {
    D::from(p.map(f64::from))
}
fn point(p: D) -> [f32; 2] {
    p.to_array().map(|v| v as f32)
}
