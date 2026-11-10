//! Original typed circular-corner recognizer and balanced analytic triangle writer.
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    vector::{
        ArcSegment, BooleanOperation, FillRule, Matrix4, PathFigure, PathGeometry, PathSegmentKind,
        SweepDirection,
    },
};
const EPS: f32 = 0.0001;
#[derive(Clone, Copy, Debug)]
struct Contour {
    bounds: [f32; 4],
    radii: [f32; 4],
}
impl Contour {
    fn width(self) -> f32 {
        self.bounds[2] - self.bounds[0]
    }
    fn height(self) -> f32 {
        self.bounds[3] - self.bounds[1]
    }
    fn rounded(self) -> bool {
        self.radii.iter().any(|v| *v > 0.0)
    }
    fn partial(self) -> bool {
        let n = self.radii.iter().filter(|v| **v > 0.0).count();
        n > 0 && n < 4
    }
}
#[derive(Clone, Debug)]
pub(crate) struct DirectRoundedPath {
    outer: Contour,
    inner: Option<Contour>,
}
impl DirectRoundedPath {
    pub(super) fn read(path: &PathGeometry) -> Option<Self> {
        if let Some(deferred) = &path.deferred {
            let (a, b) = deferred.operands();
            if deferred.operation() != BooleanOperation::Difference
                || a.deferred.is_some()
                || b.deferred.is_some()
                || a.figures.len() != 1
                || b.figures.len() != 1
            {
                return None;
            }
            return pair(contour(&a.figures[0])?, contour(&b.figures[0])?);
        }
        if !(1..=2).contains(&path.figures.len()) {
            return None;
        }
        let first = contour(&path.figures[0])?;
        if path.figures.len() == 1 {
            return Some(Self {
                outer: first,
                inner: None,
            });
        }
        if path.fill_rule != FillRule::EvenOdd {
            return None;
        }
        let second = contour(&path.figures[1])?;
        if first.width() * first.height() >= second.width() * second.height() {
            pair(first, second)
        } else {
            pair(second, first)
        }
    }
    pub(crate) fn has_inner(&self) -> bool {
        self.inner.is_some()
    }
    pub(crate) fn has_rounding(&self) -> bool {
        self.outer.rounded() || self.inner.is_some_and(Contour::rounded)
    }
    pub(crate) fn rectangle(&self) -> [f32; 4] {
        let [l, t, _, _] = self.outer.bounds;
        [l, t, self.outer.width(), self.outer.height()]
    }
    fn segment_count(&self, m: Matrix4) -> usize {
        let maximum = |r: [f32; 4]| r[0].max(r[1]).max(r[2].max(r[3]));
        let mut radius = maximum(self.outer.radii);
        if let Some(inner) = self.inner {
            radius = radius.max(maximum(inner.radii));
        }
        let m = m.0;
        let sx = (m[0][0] * m[0][0] + m[0][1] * m[0][1]).sqrt();
        let sy = (m[1][0] * m[1][0] + m[1][1] * m[1][1]).sqrt();
        let device = radius * sx.max(sy);
        if !device.is_finite() || device <= 0.25 {
            return 8;
        }
        let cosine = 1.0 - 0.25 / device;
        let angle = cosine.clamp(-1.0, 1.0).acos();
        if !angle.is_finite() || angle <= EPS {
            return 128;
        }
        ((std::f32::consts::PI / (4.0 * angle)).ceil() as usize).clamp(8, 128)
    }
    pub(crate) fn append(
        &self,
        mesh: &mut Mesh,
        index: f32,
        transform: Matrix4,
        aliased: bool,
    ) -> Result<(), Error> {
        let count = self.segment_count(transform);
        let mut outer = points(self.outer, count)?;
        if let Some(inner) = self.inner {
            let inner = points(inner, count)?;
            for i in 0..outer.len() {
                let next = (i + 1) % outer.len();
                triangle(
                    mesh,
                    index,
                    [outer[i], outer[next], inner[next]],
                    1,
                    4,
                    transform,
                    aliased,
                )?;
                triangle(
                    mesh,
                    index,
                    [outer[i], inner[next], inner[i]],
                    2,
                    4,
                    transform,
                    aliased,
                )?;
            }
        } else {
            compact(&mut outer);
            if outer.len() >= 3 {
                balanced(mesh, index, &outer, 0, outer.len() - 1, transform, aliased)?;
            }
        }
        Ok(())
    }
}
fn pair(outer: Contour, inner: Contour) -> Option<DirectRoundedPath> {
    let t = tolerance(outer.width(), outer.height());
    let a = outer.bounds;
    let b = inner.bounds;
    if !outer.partial()
        || b[0] < a[0] - t
        || b[1] < a[1] - t
        || b[2] > a[2] + t
        || b[3] > a[3] + t
        || inner.width() <= t
        || inner.height() <= t
        || (inner.width() >= outer.width() - t && inner.height() >= outer.height() - t)
        || !contains(outer, inner, t)
    {
        return None;
    }
    Some(DirectRoundedPath {
        outer,
        inner: Some(inner),
    })
}
fn contains(a: Contour, b: Contour, t: f32) -> bool {
    let center = |c: Contour, i: usize| {
        let r = c.radii[i];
        let [l, top, right, bottom] = c.bounds;
        match i {
            0 => [l + r, top + r],
            1 => [right - r, top + r],
            2 => [right - r, bottom - r],
            _ => [l + r, bottom - r],
        }
    };
    (0..4).all(|i| {
        if a.radii[i] <= t {
            return true;
        }
        let ac = center(a, i);
        let bc = center(b, i);
        let sign = match i {
            0 => [-1.0, -1.0],
            1 => [1.0, -1.0],
            2 => [1.0, 1.0],
            _ => [-1.0, 1.0],
        };
        let x = sign[0] * (bc[0] - ac[0]);
        let y = sign[1] * (bc[1] - ac[1]);
        let maximum = if x > 0.0 && y > 0.0 {
            (x * x + y * y).sqrt()
        } else {
            x.max(y)
        };
        maximum + b.radii[i] <= a.radii[i] + t
    })
}
fn contour(f: &PathFigure) -> Option<Contour> {
    if !f.is_closed || !f.is_filled || !(3..=12).contains(&f.segments.len()) || !finite(f.start) {
        return None;
    }
    let mut bounds = [f.start[0], f.start[1], f.start[0], f.start[1]];
    for s in &f.segments {
        let p = s.end();
        if !finite(p) {
            return None;
        }
        bounds[0] = bounds[0].min(p[0]);
        bounds[1] = bounds[1].min(p[1]);
        bounds[2] = bounds[2].max(p[0]);
        bounds[3] = bounds[3].max(p[1]);
    }
    let w = bounds[2] - bounds[0];
    let h = bounds[3] - bounds[1];
    let t = tolerance(w, h);
    if !w.is_finite() || !h.is_finite() || w <= t || h <= t {
        return None;
    }
    let mut radii = [0.0; 4];
    let mut mask = 0;
    let mut current = f.start;
    for s in &f.segments {
        let end = s.end();
        match s.kind {
            PathSegmentKind::Line(_) => {
                if !boundary(current, end, bounds, t) {
                    return None;
                }
            }
            PathSegmentKind::Arc(arc) => {
                let (corner, radius) = classify(current, end, arc, bounds, t)?;
                if mask & (1 << corner) != 0 {
                    return None;
                }
                mask |= 1 << corner;
                radii[corner] = radius;
            }
            _ => return None,
        }
        current = end;
    }
    if !near(current, f.start, t) && !boundary(current, f.start, bounds, t) {
        return None;
    }
    if radii[0] + radii[1] > w + t
        || radii[3] + radii[2] > w + t
        || radii[0] + radii[3] > h + t
        || radii[1] + radii[2] > h + t
        || !topology(f, bounds, radii, t)
    {
        return None;
    }
    Some(Contour { bounds, radii })
}
fn classify(
    start: [f32; 2],
    end: [f32; 2],
    arc: ArcSegment,
    b: [f32; 4],
    t: f32,
) -> Option<(usize, f32)> {
    let r = arc.size[0];
    if !r.is_finite()
        || r <= t
        || !arc.size[1].is_finite()
        || (arc.size[1] - r).abs() > t
        || !arc.rotation.is_finite()
        || arc.rotation.abs() > t
        || arc.large
        || arc.sweep != SweepDirection::Clockwise
    {
        return None;
    }
    let [l, top, right, bottom] = b;
    let pairs = [
        ([l, top + r], [l + r, top]),
        ([right - r, top], [right, top + r]),
        ([right, bottom - r], [right - r, bottom]),
        ([l + r, bottom], [l, bottom - r]),
    ];
    pairs
        .iter()
        .position(|(a, b)| near(start, *a, t) && near(end, *b, t))
        .map(|i| (i, r))
}
fn topology(f: &PathFigure, b: [f32; 4], r: [f32; 4], t: f32) -> bool {
    let [l, top, right, bottom] = b;
    if !near(f.start, [l + r[0], top], t) {
        return false;
    }
    let mut i = 0;
    let line = |i: &mut usize, end: [f32; 2], implicit: bool| {
        if implicit && *i == f.segments.len() && near(end, f.start, t) {
            return true;
        }
        let Some(s) = f.segments.get(*i) else {
            return false;
        };
        *i += 1;
        matches!(s.kind,PathSegmentKind::Line(p) if near(p,end,t))
    };
    let arc = |i: &mut usize, radius: f32, end: [f32; 2]| {
        if radius <= t {
            return true;
        }
        let Some(s) = f.segments.get(*i) else {
            return false;
        };
        *i += 1;
        matches!(s.kind,PathSegmentKind::Arc(a) if near(a.end,end,t))
    };
    line(&mut i, [right - r[1], top], false)
        && arc(&mut i, r[1], [right, top + r[1]])
        && line(&mut i, [right, bottom - r[2]], false)
        && arc(&mut i, r[2], [right - r[2], bottom])
        && line(&mut i, [l + r[3], bottom], false)
        && arc(&mut i, r[3], [l, bottom - r[3]])
        && line(&mut i, [l, top + r[0]], r[0] <= t)
        && arc(&mut i, r[0], f.start)
        && i == f.segments.len()
}
fn boundary(start: [f32; 2], end: [f32; 2], b: [f32; 4], t: f32) -> bool {
    if near(start, end, t) {
        return true;
    }
    let [l, top, right, bottom] = b;
    let horizontal = (start[1] - end[1]).abs() <= t
        && ((start[1] - top).abs() <= t || (start[1] - bottom).abs() <= t)
        && start[0] >= l - t
        && start[0] <= right + t
        && end[0] >= l - t
        && end[0] <= right + t;
    let vertical = (start[0] - end[0]).abs() <= t
        && ((start[0] - l).abs() <= t || (start[0] - right).abs() <= t)
        && start[1] >= top - t
        && start[1] <= bottom + t
        && end[1] >= top - t
        && end[1] <= bottom + t;
    horizontal || vertical
}
fn tolerance(w: f32, h: f32) -> f32 {
    EPS.max(w.abs().max(h.abs()) * 0.00001)
}
fn near(a: [f32; 2], b: [f32; 2], t: f32) -> bool {
    (a[0] - b[0]).abs() <= t && (a[1] - b[1]).abs() <= t
}
fn finite(p: [f32; 2]) -> bool {
    p.iter().all(|v| v.is_finite())
}
fn points(c: Contour, n: usize) -> Result<Vec<[f32; 2]>, Error> {
    let mut out = Vec::new();
    out.try_reserve_exact(4 * (n + 1))
        .map_err(|_| Error::Capacity)?;
    let [l, t, r, b] = c.bounds;
    let p = std::f32::consts::PI;
    let radii = c.radii;
    let corners = [
        ([l + radii[0], t + radii[0]], p, p * 1.5),
        ([r - radii[1], t + radii[1]], p * 1.5, p * 2.0),
        ([r - radii[2], b - radii[2]], 0.0, p * 0.5),
        ([l + radii[3], b - radii[3]], p * 0.5, p),
    ];
    for (i, (center, start, end)) in corners.into_iter().enumerate() {
        for j in 0..=n {
            let fraction = j as f32 / n as f32;
            let angle = start + (end - start) * fraction;
            out.push([
                center[0] + angle.cos() * radii[i],
                center[1] + angle.sin() * radii[i],
            ]);
        }
    }
    Ok(out)
}
fn distance(a: [f32; 2], b: [f32; 2]) -> f32 {
    let x = a[0] - b[0];
    let y = a[1] - b[1];
    x * x + y * y
}
fn compact(points: &mut Vec<[f32; 2]>) {
    let mut out = 0;
    for i in 0..points.len() {
        let p = points[i];
        if out > 0 && distance(points[out - 1], p) <= EPS * EPS {
            continue;
        }
        points[out] = p;
        out += 1;
    }
    if out > 1 && distance(points[0], points[out - 1]) <= EPS * EPS {
        out -= 1;
    }
    points.truncate(out);
}
fn balanced(
    mesh: &mut Mesh,
    index: f32,
    p: &[[f32; 2]],
    start: usize,
    end: usize,
    m: Matrix4,
    aliased: bool,
) -> Result<(), Error> {
    if end - start < 2 {
        return Ok(());
    }
    let mid = start + (end - start) / 2;
    let mut exterior = 0;
    let mut owned = 0;
    for (a, b, bit) in [(start, mid, 1), (mid, end, 2), (end, start, 4)] {
        if (a + 1) % p.len() == b || (b + 1) % p.len() == a {
            exterior |= bit;
        } else if a < b {
            owned |= bit;
        }
    }
    triangle(
        mesh,
        index,
        [p[start], p[mid], p[end]],
        exterior,
        owned,
        m,
        aliased,
    )?;
    balanced(mesh, index, p, start, mid, m, aliased)?;
    balanced(mesh, index, p, mid, end, m, aliased)
}
fn triangle(
    mesh: &mut Mesh,
    index: f32,
    p: [[f32; 2]; 3],
    exterior: u32,
    owned: u32,
    m: Matrix4,
    aliased: bool,
) -> Result<(), Error> {
    let e0 = [p[1][0] - p[0][0], p[1][1] - p[0][1]];
    let e1 = [p[2][0] - p[0][0], p[2][1] - p[0][1]];
    let area = e0[0] * e1[1] - e0[1] * e1[0];
    if !area.is_finite() || area.abs() <= EPS {
        return Ok(());
    }
    let v = m.0;
    let sx = (v[0][0] * v[0][0] + v[0][1] * v[0][1]).sqrt();
    let sy = (v[1][0] * v[1][0] + v[1][1] * v[1][1]).sqrt();
    let minimum = sx.min(sy);
    if !minimum.is_finite() || minimum <= EPS {
        return Ok(());
    }
    let pad = 1.5 / minimum;
    let lo = [
        p[0][0].min(p[1][0].min(p[2][0])) - pad,
        p[0][1].min(p[1][1].min(p[2][1])) - pad,
    ];
    let hi = [
        p[0][0].max(p[1][0].max(p[2][0])) + pad,
        p[0][1].max(p[1][1].max(p[2][1])) + pad,
    ];
    if !finite(lo) || !finite(hi) {
        return Ok(());
    }
    mesh.push_quad(
        [
            [lo[0], lo[1]],
            [hi[0], lo[1]],
            [hi[0], hi[1]],
            [lo[0], hi[1]],
        ]
        .map(|q| VectorVertex {
            position: m.transform_point(q),
            color: [p[0][0], p[0][1], p[1][0], p[1][1]],
            texture_coordinate: q,
            brush_index: index,
            shape_size: p[2],
            corner_radius: exterior as f32,
            stroke_thickness: owned as f32,
            shape_type: if aliased { 1013.0 } else { 13.0 },
        }),
    )
}
