//! Original legacy path topology, raw point inventory and SVG command output.
use super::*;
use crate::vector::{GeometryTolerance, PathVerb, evaluate_cubic, evaluate_quadratic};
use std::fmt::Write;

/// Original CPU convexity classification, independent of raster coverage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PathConvexity {
    /// All original sampled turns have one sign, or a recognized simple shape.
    Convex,
    /// Multiple contours or opposite original sampled turns.
    Concave,
}
/// Original rectangle recognizer output, including explicit/implicit closure.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PathRectangle {
    /// Original corner envelope.
    pub bounds: [f32; 4],
    /// Explicit contour closure or final endpoint equal to the first point.
    pub closed: bool,
    /// Original signed-area direction.
    pub direction: PathDirection,
}
/// One original raw inventory traversal; points omit repeated segment starts.
#[derive(Clone, Debug)]
pub struct PathInventory {
    /// Actual original move/end/control slots, preserving source conics.
    pub points: Vec<[f32; 2]>,
    /// Raw verb count including Move and Close, excluding Done.
    pub verb_count: usize,
    /// Original Line=1, Quadratic=2, Conic=4, Cubic=8 flags.
    pub segment_mask: u8,
}
impl RetainedPath {
    /// Original empty classification, including known deferred Boolean results.
    /// An unresolved Boolean requires its actual solver rather than a bounds proxy.
    pub fn is_empty(&self) -> Result<bool, Error> {
        match &*self.source.borrow() {
            Source::Packed(p) => Ok(p.is_empty()),
            Source::Geometry(p) => {
                if let Some(deferred) = &p.deferred {
                    deferred.known_empty().ok_or(Error::UnsupportedExecution)
                } else {
                    Ok(p.figures.is_empty())
                }
            }
        }
    }
    /// Original final actual segment endpoint, even when the contour is closed.
    pub fn last_point(&self) -> Result<[f32; 2], Error> {
        let geometry = ordinary(self)?;
        Ok(geometry.figures.last().map_or([0.0; 2], |f| {
            f.segments.last().map_or(f.start, PathSegment::end)
        }))
    }
    /// Snapshot all raw points/verbs once in O(N + Q) time.
    pub fn inventory(&self) -> Result<PathInventory, Error> {
        let mut iterator = self.raw_iterator()?;
        let mut output = PathInventory {
            points: Vec::new(),
            verb_count: 0,
            segment_mask: 0,
        };
        let mut p = [[0.0; 2]; 4];
        loop {
            let (range, mask) = match iterator.next_points(&mut p) {
                PathVerb::Move => (0..1, 0),
                PathVerb::Line => (1..2, 1),
                PathVerb::Quadratic => (1..3, 2),
                PathVerb::Conic => (1..3, 4),
                PathVerb::Cubic => (1..4, 8),
                PathVerb::Close => (0..0, 0),
                PathVerb::Done => break,
            };
            output
                .points
                .try_reserve(range.len())
                .map_err(|_| Error::Capacity)?;
            output.points.extend_from_slice(&p[range]);
            output.verb_count = output.verb_count.checked_add(1).ok_or(Error::Capacity)?;
            output.segment_mask |= mask;
        }
        Ok(output)
    }
    /// Copy at most `maximum` and actual destination capacity, retaining unused
    /// tails. Returns the complete source point count even for an empty output.
    pub fn copy_points(
        &self,
        destination: &mut [[f32; 2]],
        maximum: usize,
    ) -> Result<usize, Error> {
        let source = self.inventory()?;
        let count = maximum.min(destination.len()).min(source.points.len());
        destination[..count].copy_from_slice(&source.points[..count]);
        Ok(source.points.len())
    }
    /// Original single open line recognition, preserving exact endpoint bits.
    pub fn line(&self) -> Result<Option<[[f32; 2]; 2]>, Error> {
        {
            let path = ordinary(self)?;
            Ok(line(&path))
        }
    }
    /// Original four-corner/equal-final-point rectangle recognizer.
    pub fn rectangle(&self) -> Result<Option<PathRectangle>, Error> {
        {
            let path = ordinary(self)?;
            Ok(rectangle(&path))
        }
    }
    /// Original two-arc closed oval recognition and exact analytic bounds.
    pub fn oval(&self) -> Result<Option<[f32; 4]>, Error> {
        let path = ordinary(self)?;
        oval(&path)
    }
    /// Original eight-segment/four-arc corner-radius recognizer.
    pub fn round_rectangle(&self) -> Result<Option<RoundRect>, Error> {
        let path = ordinary(self)?;
        round_rect(&path)
    }
    /// Original fixed-sample topology classification. This does not flatten the
    /// retained source or alter the analytic inventory consumed by rendering.
    pub fn convexity(&self) -> Result<PathConvexity, Error> {
        let path = ordinary(self)?;
        if path.figures.len() > 1 {
            return Ok(PathConvexity::Concave);
        }
        let Some(figure) = path.figures.first() else {
            return Ok(PathConvexity::Convex);
        };
        if rectangle(&path).is_some()
            || oval(&path)?.is_some()
            || round_rect(&path)?.is_some()
            || line(&path).is_some()
            || figure.segments.is_empty()
        {
            return Ok(PathConvexity::Convex);
        }
        if !figure.is_closed
            && arc_sweep(figure).is_some_and(|v| v.abs() > std::f32::consts::PI + EPS)
        {
            return Ok(PathConvexity::Concave);
        }
        let samples = samples(figure)?;
        if samples.len() < 3 {
            return Ok(PathConvexity::Convex);
        }
        let mut sign = 0.0;
        for i in 0..samples.len() {
            let a = samples[i];
            let b = samples[(i + 1) % samples.len()];
            let c = samples[(i + 2) % samples.len()];
            let cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]);
            if cross.abs() <= EPS {
                continue;
            }
            // MathF.Sign throws for NaN; retain rejection instead of assigning a sign.
            if cross.is_nan() {
                return Err(Error::InvalidInput);
            }
            let current = if cross > 0.0 { 1.0 } else { -1.0 };
            if sign == 0.0 {
                sign = current;
            } else if sign != current {
                return Ok(PathConvexity::Concave);
            }
        }
        Ok(PathConvexity::Convex)
    }
    /// Original zero-tolerance input query, including its explicit control-bound
    /// fallback on rejected geometry and independent inverse-fill application.
    pub fn contains(&self, point: [f32; 2]) -> Result<bool, Error> {
        let geometry = self.geometry()?;
        let contains = geometry
            .contains_fill(point, GeometryTolerance::default())?
            .unwrap_or_else(|| {
                let [l, t, r, b] = self.bounds();
                point[0] >= l && point[0] <= r && point[1] >= t && point[1] <= b
            });
        Ok(if self.inverse { !contains } else { contains })
    }
    /// Original SVG command inventory: exact arcs, 32 recursively chopped conic
    /// quadratics, explicit closing line when needed, and move-only filtering.
    /// Rust's round-trip decimal formatting is separately qualified against the
    /// source invariant-culture `R` spelling; this does not claim string identity.
    pub fn to_svg_path_data(&self) -> Result<String, Error> {
        let path = ordinary(self)?;
        let mut out = String::new();
        for figure in &path.figures {
            if figure.segments.is_empty() && !figure.is_closed {
                continue;
            }
            command(&mut out, 'M', &[figure.start])?;
            let mut current = figure.start;
            let mut index = 0;
            while index < figure.segments.len() {
                let segment = &figure.segments[index];
                if let Some(conic) = &segment.source_conic {
                    let points = super::super::conic_to_quadratics(
                        [conic.start(), conic.control(), conic.end()],
                        conic.weight(),
                        5,
                    )?;
                    for pair in points[1..].chunks_exact(2) {
                        command(&mut out, 'Q', pair)?;
                    }
                    current = conic.end();
                    index += conic.span_count();
                    continue;
                }
                match segment.kind {
                    PathSegmentKind::Line(p) => command(&mut out, 'L', &[p])?,
                    PathSegmentKind::Quadratic(p) => command(&mut out, 'Q', &p)?,
                    PathSegmentKind::Cubic(p) => command(&mut out, 'C', &p)?,
                    PathSegmentKind::Arc(a) => {
                        out.try_reserve(160).map_err(|_| Error::Capacity)?;
                        write!(
                            out,
                            "A{} {} {} {} {} {} {}",
                            Scalar(a.size[0].abs()),
                            Scalar(a.size[1].abs()),
                            Scalar(a.rotation),
                            u8::from(a.large),
                            u8::from(a.sweep == SweepDirection::Clockwise),
                            Scalar(a.end[0]),
                            Scalar(a.end[1])
                        )
                        .map_err(|_| Error::Capacity)?;
                    }
                    _ => return Err(Error::UnsupportedExecution),
                }
                current = segment.end();
                index += 1;
            }
            if figure.is_closed {
                if !near(current, figure.start) {
                    command(&mut out, 'L', &[figure.start])?;
                }
                out.try_reserve(1).map_err(|_| Error::Capacity)?;
                out.push('Z');
            }
        }
        Ok(out)
    }
}
fn ordinary(path: &RetainedPath) -> Result<Arc<PathGeometry>, Error> {
    let geometry = path.geometry()?;
    if geometry.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    Ok(geometry)
}
fn line(path: &PathGeometry) -> Option<[[f32; 2]; 2]> {
    if path.figures.len() != 1 {
        return None;
    }
    let f = &path.figures[0];
    if f.is_closed || f.segments.len() != 1 {
        return None;
    }
    if let PathSegmentKind::Line(end) = f.segments[0].kind {
        Some([f.start, end])
    } else {
        None
    }
}
fn rectangle(path: &PathGeometry) -> Option<PathRectangle> {
    if path.figures.len() != 1 {
        return None;
    }
    let f = &path.figures[0];
    if !(3..=4).contains(&f.segments.len()) {
        return None;
    }
    let mut points = [[0.0; 2]; 5];
    points[0] = f.start;
    for (i, s) in f.segments.iter().enumerate() {
        let PathSegmentKind::Line(p) = s.kind else {
            return None;
        };
        points[i + 1] = p;
    }
    let count = f.segments.len() + 1;
    if count != 4 && !(count == 5 && near(points[0], points[4])) {
        return None;
    }
    let mut min = points[0];
    let mut max = min;
    for &point in &points[1..4] {
        for axis in 0..2 {
            min[axis] = super::super::minimum(min[axis], point[axis]);
            max[axis] = super::super::maximum(max[axis], point[axis]);
        }
    }
    if max[0] - min[0] <= EPS || max[1] - min[1] <= EPS {
        return None;
    }
    if !points[..4].iter().all(|p| {
        ((p[0] - min[0]).abs() <= EPS || (p[0] - max[0]).abs() <= EPS)
            && ((p[1] - min[1]).abs() <= EPS || (p[1] - max[1]).abs() <= EPS)
    }) {
        return None;
    }
    let mut area = 0.0;
    for i in 0..4 {
        let next = (i + 1) % 4;
        area += points[i][0] * points[next][1] - points[next][0] * points[i][1];
    }
    Some(PathRectangle {
        bounds: [min[0], min[1], max[0], max[1]],
        closed: f.is_closed || (count == 5 && near(points[0], points[4])),
        direction: if area >= 0.0 {
            PathDirection::Clockwise
        } else {
            PathDirection::Counterclockwise
        },
    })
}
fn oval(path: &PathGeometry) -> Result<Option<[f32; 4]>, Error> {
    if path.figures.len() != 1 {
        return Ok(None);
    }
    let f = &path.figures[0];
    if !f.is_closed || f.segments.len() != 2 {
        return Ok(None);
    }
    let (PathSegmentKind::Arc(first), PathSegmentKind::Arc(second)) =
        (f.segments[0].kind, f.segments[1].kind)
    else {
        return Ok(None);
    };
    if !near(first.size, second.size) || !near(second.end, f.start) {
        return Ok(None);
    }
    let b = path.tight_bounds()?;
    Ok((b[2] - b[0] > EPS && b[3] - b[1] > EPS).then_some(b))
}
fn round_rect(path: &PathGeometry) -> Result<Option<RoundRect>, Error> {
    if path.figures.len() != 1 {
        return Ok(None);
    }
    let f = &path.figures[0];
    if !f.is_closed
        || f.segments.len() != 8
        || f.segments
            .iter()
            .filter(|s| matches!(s.kind, PathSegmentKind::Arc(_)))
            .count()
            != 4
        || f.segments
            .iter()
            .filter(|s| matches!(s.kind, PathSegmentKind::Line(_)))
            .count()
            != 4
    {
        return Ok(None);
    }
    let b = path.tight_bounds()?;
    let center = [(b[0] + b[2]) * 0.5, (b[1] + b[3]) * 0.5];
    let mut radii = [[0.0; 2]; 4];
    let mut found = [false; 4];
    let mut current = f.start;
    for s in &f.segments {
        if let PathSegmentKind::Arc(a) = s.kind
            && a.rotation.abs() <= EPS
            && !a.large
        {
            let mid = add(current, a.end).map(|v| v * 0.5);
            let corner = if mid[0] < center[0] {
                if mid[1] < center[1] { 0 } else { 3 }
            } else if mid[1] < center[1] {
                1
            } else {
                2
            };
            radii[corner] = a.size.map(f32::abs);
            found[corner] = true;
        }
        current = s.end();
    }
    Ok(found
        .into_iter()
        .all(|v| v)
        .then(|| RoundRect::from_radii(b, radii)))
}
fn arc_sweep(f: &PathFigure) -> Option<f32> {
    let mut sweep = 0.0;
    let mut current = f.start;
    for s in &f.segments {
        let PathSegmentKind::Arc(a) = s.kind else {
            return None;
        };
        sweep += a.center(current)?.delta;
        current = a.end;
    }
    (!f.segments.is_empty()).then_some(sweep)
}
fn samples(f: &PathFigure) -> Result<Vec<[f32; 2]>, Error> {
    let mut out = Vec::new();
    out.try_reserve(
        f.segments
            .len()
            .checked_mul(8)
            .ok_or(Error::Capacity)?
            .max(4),
    )
    .map_err(|_| Error::Capacity)?;
    out.push(f.start);
    let mut current = f.start;
    for s in &f.segments {
        match s.kind {
            PathSegmentKind::Line(p) => {
                out.try_reserve(1).map_err(|_| Error::Capacity)?;
                out.push(p);
            }
            PathSegmentKind::Quadratic(p) => {
                out.try_reserve(8).map_err(|_| Error::Capacity)?;
                for i in 1..=8 {
                    out.push(evaluate_quadratic([current, p[0], p[1]], i as f32 / 8.0));
                }
            }
            PathSegmentKind::Cubic(p) => {
                out.try_reserve(12).map_err(|_| Error::Capacity)?;
                for i in 1..=12 {
                    out.push(evaluate_cubic([current, p[0], p[1], p[2]], i as f32 / 12.0));
                }
            }
            PathSegmentKind::Arc(a) => {
                let points = a.flattened(current, std::f32::consts::PI / 16.0)?;
                out.try_reserve(points.len().saturating_sub(1))
                    .map_err(|_| Error::Capacity)?;
                out.extend_from_slice(points.get(1..).unwrap_or_default());
            }
            _ => return Err(Error::UnsupportedExecution),
        }
        current = s.end();
    }
    if out.len() > 1 && near(out[0], out[out.len() - 1]) {
        out.pop();
    }
    Ok(out)
}
struct Scalar(f32);
impl std::fmt::Display for Scalar {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.0.is_nan() {
            f.write_str("NaN")
        } else if self.0 == f32::INFINITY {
            f.write_str("Infinity")
        } else if self.0 == f32::NEG_INFINITY {
            f.write_str("-Infinity")
        } else {
            write!(f, "{}", self.0)
        }
    }
}
fn command(out: &mut String, verb: char, points: &[[f32; 2]]) -> Result<(), Error> {
    out.try_reserve(
        points
            .len()
            .checked_mul(96)
            .and_then(|v| v.checked_add(1))
            .ok_or(Error::Capacity)?,
    )
    .map_err(|_| Error::Capacity)?;
    out.push(verb);
    for (i, p) in points.iter().enumerate() {
        if i != 0 {
            out.push(' ');
        }
        write!(out, "{} {}", Scalar(p[0]), Scalar(p[1])).map_err(|_| Error::Capacity)?;
    }
    Ok(())
}
