//! Original bounded lattice intersections/stamps and independent source fill query.
use super::*;
/// Exact original nine-coefficient row-major source matrix. Path effects reject
/// perspective before inversion; original raw values remain available to callers.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EffectMatrix(pub [f32; 9]);
impl EffectMatrix {
    /// Original identity matrix, including its exact zero/one components.
    pub const IDENTITY: Self = Self([1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]);
    /// Original pure-translation construction.
    pub fn translation(p: [f32; 2]) -> Self {
        Self([1.0, 0.0, p[0], 0.0, 1.0, p[1], 0.0, 0.0, 1.0])
    }
    fn perspective(self) -> bool {
        self.0[6] != 0.0 || self.0[7] != 0.0 || self.0[8] != 1.0
    }
    /// Original complete point mapping, including zero-W policy.
    pub fn point(self, p: [f32; 2]) -> [f32; 2] {
        let m = self.0;
        let v = V::splat(p[0]) * V::from([m[0], m[3], 0.0, 0.0])
            + V::splat(p[1]) * V::from([m[1], m[4], 0.0, 0.0])
            + V::from([m[2], m[5], 0.0, 0.0]);
        let v = if self.perspective() {
            let w = p[0] * m[6] + p[1] * m[7] + m[8];
            v * V::splat(if w == 0.0 { 0.0 } else { 1.0 / w })
        } else {
            v
        };
        let [x, y, _, _] = v.to_array();
        [x, y]
    }
    fn inverse(self) -> Option<Self> {
        if self.perspective() {
            return None;
        }
        if self == Self::IDENTITY {
            return Some(self);
        }
        let [a, b, x, c, d, y, _, _, _] = self.0;
        if b == 0.0 && c == 0.0 {
            let sx = 1.0 / a;
            let sy = 1.0 / d;
            let tx = -x * sx;
            let ty = -y * sy;
            return [sx, sy, tx, ty]
                .iter()
                .all(|v| v.is_finite())
                .then_some(Self([sx, 0.0, tx, 0.0, sy, ty, 0.0, 0.0, 1.0]));
        }
        let cross = |a: f32, b: f32, c: f32, d: f32| {
            f64::from(a) * f64::from(b) - f64::from(c) * f64::from(d)
        };
        let determinant = cross(a, d, b, c);
        if !determinant.is_finite() || (determinant as f32).abs() <= 1.0 / 68719476736.0 {
            return None;
        }
        let scale = 1.0 / determinant;
        let result = Self([
            (f64::from(d) * scale) as f32,
            (f64::from(-b) * scale) as f32,
            (cross(b, y, d, x) * scale) as f32,
            (f64::from(-c) * scale) as f32,
            (f64::from(a) * scale) as f32,
            (cross(c, x, a, y) * scale) as f32,
            0.0,
            0.0,
            1.0,
        ]);
        result.0.iter().all(|v| v.is_finite()).then_some(result)
    }
    fn bounds(self, b: [f32; 4]) -> [f32; 4] {
        let m = self.0;
        let x0 = b[0] * m[0];
        let x1 = b[2] * m[0];
        let sy0 = b[1] * m[1];
        let sy1 = b[3] * m[1];
        let sx0 = b[0] * m[3];
        let sx1 = b[2] * m[3];
        let y0 = b[1] * m[4];
        let y1 = b[3] * m[4];
        [
            x0.min(x1) + sy0.min(sy1) + m[2],
            sx0.min(sx1) + y0.min(y1) + m[5],
            x0.max(x1) + sy0.max(sy1) + m[2],
            sx0.max(sx1) + y0.max(y1) + m[5],
        ]
    }
}
fn range(a: f32, b: f32, padding: i32) -> Option<(i32, i32)> {
    let floor = a.floor();
    let ceiling = b.ceil();
    if !floor.is_finite()
        || !ceiling.is_finite()
        || floor < (i32::MIN + padding) as f32
        || ceiling > (i32::MAX - padding) as f32
    {
        return None;
    }
    let first = (floor as i32).checked_sub(padding)?;
    let last = (ceiling as i32).checked_add(padding)?;
    (last >= first).then_some((first, last))
}
fn contains(
    source: &PathEffectSource,
    geometry: &PathGeometry,
    p: [f32; 2],
) -> Result<bool, Error> {
    let inside = geometry
        .contains_fill(p, GeometryTolerance::default())?
        .unwrap_or_else(|| {
            let b = geometry.bounds().unwrap_or([0.0; 4]);
            p[0] >= b[0] && p[0] <= b[2] && p[1] >= b[1] && p[1] <= b[3]
        });
    Ok(if source.inverse { !inside } else { inside })
}
fn contours(
    source: &PathEffectSource,
    inverse: EffectMatrix,
    scale: f32,
) -> Result<Option<(Vec<Vec<[f32; 2]>>, [f32; 2], [f32; 2])>, Error> {
    let mut result = Vec::new();
    let mut min = [f32::INFINITY; 2];
    let mut max = [f32::NEG_INFINITY; 2];
    let mut generated = 0usize;
    let mut measure = PathMeasure::new(Some(&source.path), true, scale)?;
    loop {
        let length = measure.length();
        if length > EPS {
            let exact = (length * scale.max(1.0) * 0.5).ceil();
            if !exact.is_finite() || exact > LIMIT as f32 {
                return Ok(None);
            }
            let count = (exact as usize).max(2);
            generated = generated.checked_add(count + 1).ok_or(Error::Capacity)?;
            if generated > LIMIT {
                return Ok(None);
            }
            let mut contour = Vec::new();
            contour
                .try_reserve_exact(count + 1)
                .map_err(|_| Error::Capacity)?;
            for index in 0..=count {
                let p = measure
                    .position(length * index as f32 / count as f32)
                    .unwrap_or([0.0; 2]);
                let p = inverse.point(p);
                contour.push(p);
                min = [min[0].min(p[0]), min[1].min(p[1])];
                max = [max[0].max(p[0]), max[1].max(p[1])];
            }
            result.try_reserve(1).map_err(|_| Error::Capacity)?;
            result.push(contour);
        }
        if !measure.next_contour() {
            break;
        }
    }
    if result.is_empty() || min.iter().chain(&max).any(|v| !v.is_finite()) {
        Ok(None)
    } else {
        Ok(Some((result, min, max)))
    }
}
pub(super) fn lines(
    source: &PathEffectSource,
    width: f32,
    matrix: EffectMatrix,
    scale: f32,
) -> Result<(bool, PathEffectSource), Error> {
    if !width.is_finite() || width < 0.0 {
        return Ok(rejected(source));
    }
    let Some(inverse) = matrix.inverse() else {
        return Ok(rejected(source));
    };
    let Some((contours, min, max)) = contours(source, inverse, scale)? else {
        return Ok(rejected(source));
    };
    let Some((first, last)) = range(min[1], max[1], 0) else {
        return Ok(rejected(source));
    };
    if i64::from(last) - i64::from(first) + 1 > LIMIT as i64 {
        return Ok(rejected(source));
    }
    let geometry = source.geometry()?;
    let mut out = Builder::default();
    let mut intersections = Vec::new();
    let mut generated = 0;
    for row in first..=last {
        intersections.clear();
        let y = row as f32;
        for contour in &contours {
            for pair in contour.windows(2) {
                let [a, b] = [pair[0], pair[1]];
                if (a[1] <= y && b[1] > y) || (b[1] <= y && a[1] > y) {
                    let amount = (y - a[1]) / (b[1] - a[1]);
                    intersections.try_reserve(1).map_err(|_| Error::Capacity)?;
                    intersections.push(a[0] + (b[0] - a[0]) * amount);
                }
            }
        }
        intersections
            .sort_unstable_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        for pair in intersections.windows(2) {
            let [left, right] = [pair[0], pair[1]];
            if right - left <= EPS {
                continue;
            }
            if !contains(source, &geometry, matrix.point([(left + right) * 0.5, y]))? {
                continue;
            }
            out.push(C::Move(matrix.point([left, y])))?;
            out.push(C::Line(matrix.point([right, y])))?;
            generated += 1;
            if generated > LIMIT {
                return Ok((false, source.clone()));
            }
        }
    }
    Ok(finish(source, out))
}
pub(super) fn paths(
    source: &PathEffectSource,
    stamp: &PathEffectSource,
    matrix: EffectMatrix,
) -> Result<(bool, PathEffectSource), Error> {
    let empty = stamp.empty_like();
    let Some(inverse) = matrix.inverse() else {
        return Ok((false, empty));
    };
    let geometry = source.geometry()?;
    let bounds = inverse.bounds(geometry.bounds().unwrap_or([0.0; 4]));
    if bounds.iter().any(|v| !v.is_finite()) {
        return Ok((false, empty));
    }
    let Some((x0, x1)) = range(bounds[0], bounds[2], 1) else {
        return Ok((false, empty));
    };
    let Some((y0, y1)) = range(bounds[1], bounds[3], 1) else {
        return Ok((false, empty));
    };
    let cols = i64::from(x1) - i64::from(x0) + 1;
    let rows = i64::from(y1) - i64::from(y0) + 1;
    if cols > LIMIT as i64 || rows > LIMIT as i64 || cols * rows > LIMIT as i64 {
        return Ok((false, empty));
    }
    let mut out = Builder::default();
    let mut generated = 0usize;
    for row in y0..=y1 {
        for column in x0..=x1 {
            let p = matrix.point([column as f32, row as f32]);
            if !contains(source, &geometry, p)? {
                continue;
            }
            generated = generated
                .checked_add(stamp.path.commands().len())
                .ok_or(Error::Capacity)?;
            if generated > LIMIT {
                return Ok((false, source.clone()));
            }
            let translation = EffectMatrix::translation(p);
            for command in stamp.path.commands() {
                out.push(map_command(*command, |p| translation.point(p)))?;
            }
        }
    }
    Ok(finish(stamp, out))
}
