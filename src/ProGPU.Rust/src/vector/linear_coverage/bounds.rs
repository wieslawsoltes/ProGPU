use super::*;
pub(super) struct Bounds {
    min: Pair,
    max: Pair,
    normal: Pair,
    along: Pair,
    valid: bool,
}
impl Default for Bounds {
    fn default() -> Self {
        Self {
            min: Pair::splat(f64::INFINITY),
            max: Pair::splat(f64::NEG_INFINITY),
            normal: Pair::ZERO,
            along: Pair::ZERO,
            valid: true,
        }
    }
}
impl Bounds {
    pub(super) fn line(start: [f32; 2], end: [f32; 2], width: f32) -> Self {
        let first = wide(start);
        let last = wide(end);
        let delta = last - first;
        let length = magnitude(delta);
        let unit = if length == 0.0 {
            Pair::new([1.0, 0.0])
        } else {
            delta / Pair::splat(length)
        };
        let along = unit * Pair::splat(f64::from(width) * 0.5);
        let normal = left(along);
        let mut state = Self {
            along,
            normal,
            valid: length.is_finite(),
            ..Default::default()
        };
        for p in [first - normal, first + normal, last - normal, last + normal] {
            state.include(p);
        }
        state
    }
    pub(super) fn include(&mut self, p: Pair) {
        if p.to_array().iter().any(|v| !v.is_finite()) {
            self.valid = false;
            return;
        }
        self.min = self.min.min(p);
        self.max = self.max.max(p);
    }
    pub(super) fn cap(&mut self, center: [f32; 2], sign: f64, cap: PenLineCap) {
        let origin = wide(center);
        let along = self.along * Pair::splat(sign);
        let outer = origin + along;
        match cap {
            PenLineCap::Flat => {}
            PenLineCap::Square => {
                self.include(outer - self.normal);
                self.include(outer + self.normal);
            }
            PenLineCap::Triangle => self.include(outer),
            PenLineCap::Round => {
                let k = Pair::splat(KAPPA);
                let first = origin - self.normal;
                let last = origin + self.normal;
                self.cubic(first, first + along * k, outer - self.normal * k, outer);
                self.cubic(outer, outer + self.normal * k, last + along * k, last);
            }
        }
    }
    pub(super) fn cubic(&mut self, a: Pair, b: Pair, c: Pair, d: Pair) {
        let [a, b, c, d] = [a, b, c, d].map(narrow);
        self.include(a);
        self.include(d);
        let quadratic =
            (Pair::splat(3.0) * (-a + Pair::splat(3.0) * b - Pair::splat(3.0) * c + d)).to_array();
        let linear = (Pair::splat(6.0) * (a - Pair::splat(2.0) * b + c)).to_array();
        let constant = (Pair::splat(3.0) * (b - a)).to_array();
        for axis in 0..2 {
            let (q, l, k) = (quadratic[axis], linear[axis], constant[axis]);
            if q == 0.0 {
                if l != 0.0 {
                    self.at([a, b, c, d], -k / l);
                }
                continue;
            }
            let discriminant = l * l - 4.0 * q * k;
            if discriminant < 0.0 {
                continue;
            }
            let root = discriminant.sqrt();
            self.at([a, b, c, d], (-l - root) / (2.0 * q));
            self.at([a, b, c, d], (-l + root) / (2.0 * q));
        }
    }
    fn at(&mut self, [a, b, c, d]: [Pair; 4], t: f64) {
        if !t.is_finite() {
            self.valid = false;
            return;
        }
        if t <= 0.0 || t >= 1.0 {
            return;
        }
        let s = 1.0 - t;
        self.include(
            Pair::splat(s * s * s) * a
                + Pair::splat(3.0 * s * s * t) * b
                + Pair::splat(3.0 * s * t * t) * c
                + Pair::splat(t * t * t) * d,
        );
    }
    pub(super) fn points(&self) -> Result<([f32; 2], [f32; 2]), Error> {
        let a = point(self.min);
        let b = point(self.max);
        if !self.valid || !finite(a) || !finite(b) {
            return Err(Error::InvalidInput);
        }
        Ok((a, b))
    }
    pub(super) fn rect(&self) -> Result<[f32; 4], Error> {
        let (min, max) = self.points()?;
        let extent = sub(max, min);
        if !finite(extent) {
            return Err(Error::InvalidInput);
        }
        if extent[0] <= 0.0 || extent[1] <= 0.0 {
            return Ok([0.0; 4]);
        }
        if !finite(add(min, extent)) {
            return Err(Error::InvalidInput);
        }
        Ok([min[0], min[1], extent[0], extent[1]])
    }
}
