//! Original canonical contour recognition and analytic convex containment.
use super::*;
/// A proven original clockwise canonical rectangle/rounded-rectangle contour.
/// Construction retains the source recognition tolerances and full corner pairs.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RoundedRectangleContour {
    bounds: [f32; 4],
    radii_x: [f32; 4],
    radii_y: [f32; 4],
}
impl RoundedRectangleContour {
    /// Original left/top/right/bottom envelope.
    pub fn bounds(self) -> [f32; 4] {
        self.bounds
    }
    /// Original X radii in top-left/top-right/bottom-right/bottom-left order.
    pub fn radii_x(self) -> [f32; 4] {
        self.radii_x
    }
    /// Original Y radii in matching corner order.
    pub fn radii_y(self) -> [f32; 4] {
        self.radii_y
    }
    fn width(self) -> f32 {
        self.bounds[2] - self.bounds[0]
    }
    fn height(self) -> f32 {
        self.bounds[3] - self.bounds[1]
    }
    /// Read the complete original 4..8-segment canonical contour. Rotated start
    /// slots are admitted; arbitrary curves, reversed arcs and partial axes are not.
    pub fn from_figure(figure: &PathFigure) -> Option<Self> {
        let segments = &figure.segments;
        if !figure.is_closed
            || !figure.is_filled
            || !(4..=8).contains(&segments.len())
            || !finite(figure.start)
        {
            return None;
        }
        let mut bounds = [
            figure.start[0],
            figure.start[1],
            figure.start[0],
            figure.start[1],
        ];
        for segment in segments {
            let end = match segment.kind {
                PathSegmentKind::Line(p) => p,
                PathSegmentKind::Quadratic(p) => p[1],
                PathSegmentKind::Cubic(p) => p[2],
                PathSegmentKind::Arc(a) => a.end,
                _ => return None,
            };
            if !finite(end) {
                return None;
            }
            for axis in 0..2 {
                bounds[axis] = minimum(bounds[axis], end[axis]);
                bounds[axis + 2] = maximum(bounds[axis + 2], end[axis]);
            }
        }
        let [left, top, right, bottom] = bounds;
        let width = right - left;
        let height = bottom - top;
        let tolerance = tolerance(width, height);
        if !width.is_finite() || !height.is_finite() || width <= tolerance || height <= tolerance {
            return None;
        }
        let mut selected = None;
        for (index, segment) in segments.iter().enumerate() {
            let start = if index == 0 {
                figure.start
            } else {
                segments[index - 1].end()
            };
            if let PathSegmentKind::Line(end) = segment.kind
                && near(start[1], top, tolerance)
                && near(end[1], top, tolerance)
                && end[0] + tolerance >= start[0]
            {
                selected = Some((index, start));
                break;
            }
        }
        let (rotation, start) = selected?;
        let mut reader = Reader {
            segments,
            rotation,
            index: 0,
            current: start,
            tolerance,
            contour: Self {
                bounds,
                radii_x: [0.0; 4],
                radii_y: [0.0; 4],
            },
        };
        if !reader.line(true, top, true)
            || !reader.corner(1, [right, top])
            || !reader.line(false, right, true)
            || !reader.corner(2, [right, bottom])
            || !reader.line(true, bottom, false)
            || !reader.corner(3, [left, bottom])
            || !reader.line(false, left, false)
            || !reader.corner(0, [left, top])
            || reader.index != segments.len()
            || !equal(reader.current, start, tolerance)
        {
            return None;
        }
        let c = reader.contour;
        let x = c.radii_x;
        let y = c.radii_y;
        if x[0] + x[1] > width + tolerance
            || x[3] + x[2] > width + tolerance
            || y[0] + y[3] > height + tolerance
            || y[1] + y[2] > height + tolerance
        {
            return None;
        }
        Some(c)
    }
    /// Original convex containment proof, then exact paired-corner-center/radius
    /// admission. No boundary flattening or rectangular-envelope substitution.
    pub fn contains(self, inner: Self) -> bool {
        let t = tolerance(self.width(), self.height());
        let a = self.bounds;
        let b = inner.bounds;
        if b[0] < a[0] - t
            || b[1] < a[1] - t
            || b[2] > a[2] + t
            || b[3] > a[3] + t
            || inner.width() <= t
            || inner.height() <= t
        {
            return false;
        }
        if (0..4).all(|i| self.contains_point(inner.corner_point(i), t)) {
            return true;
        }
        for i in 0..4 {
            let x = inner.radii_x[i];
            let y = inner.radii_y[i];
            if x <= t && y <= t {
                if !self.contains_point(inner.corner_point(i), t) {
                    return false;
                }
                continue;
            }
            if self.radii_x[i] <= t
                || self.radii_y[i] <= t
                || x > self.radii_x[i] + t
                || y > self.radii_y[i] + t
                || !equal(self.corner_center(i), inner.corner_center(i), t)
            {
                return false;
            }
        }
        true
    }
    /// Original four-corner ownership test for an arbitrary source bounds pair.
    pub fn contains_bounds(self, b: [f32; 4]) -> bool {
        let t = tolerance(self.width(), self.height());
        let a = self.bounds;
        b[0] >= a[0] - t
            && b[1] >= a[1] - t
            && b[2] <= a[2] + t
            && b[3] <= a[3] + t
            && [[b[0], b[1]], [b[2], b[1]], [b[2], b[3]], [b[0], b[3]]]
                .into_iter()
                .all(|p| self.contains_point(p, t))
    }
    /// Original strictly positive two-axis corner count, independent of tolerance.
    pub fn has_partial_rounded_corners(self) -> bool {
        let count = (0..4)
            .filter(|&i| self.radii_x[i] > 0.0 && self.radii_y[i] > 0.0)
            .count();
        count > 0 && count < 4
    }
    fn contains_point(self, p: [f32; 2], t: f32) -> bool {
        let b = self.bounds;
        if p[0] < b[0] - t || p[0] > b[2] + t || p[1] < b[1] - t || p[1] > b[3] + t {
            return false;
        }
        for i in 0..4 {
            let rx = self.radii_x[i];
            let ry = self.radii_y[i];
            if rx <= t || ry <= t {
                continue;
            }
            let center = self.corner_center(i);
            let inside = (if matches!(i, 0 | 3) {
                p[0] < center[0]
            } else {
                p[0] > center[0]
            }) && (if matches!(i, 0 | 1) {
                p[1] < center[1]
            } else {
                p[1] > center[1]
            });
            if !inside {
                continue;
            }
            let x = (p[0] - center[0]) / rx;
            let y = (p[1] - center[1]) / ry;
            return x * x + y * y <= 1.0 + t;
        }
        true
    }
    fn corner_point(self, i: usize) -> [f32; 2] {
        let b = self.bounds;
        match i {
            0 => [b[0], b[1]],
            1 => [b[2], b[1]],
            2 => [b[2], b[3]],
            _ => [b[0], b[3]],
        }
    }
    fn corner_center(self, i: usize) -> [f32; 2] {
        let b = self.bounds;
        let x = self.radii_x[i];
        let y = self.radii_y[i];
        match i {
            0 => [b[0] + x, b[1] + y],
            1 => [b[2] - x, b[1] + y],
            2 => [b[2] - x, b[3] - y],
            _ => [b[0] + x, b[3] - y],
        }
    }
}
struct Reader<'a> {
    segments: &'a [PathSegment],
    rotation: usize,
    index: usize,
    current: [f32; 2],
    tolerance: f32,
    contour: RoundedRectangleContour,
}
impl Reader<'_> {
    fn segment(&self) -> Option<PathSegment> {
        (self.index < self.segments.len())
            .then(|| self.segments[(self.rotation + self.index) % self.segments.len()])
    }
    fn line(&mut self, horizontal: bool, fixed: f32, increasing: bool) -> bool {
        let Some(segment) = self.segment() else {
            return false;
        };
        self.index += 1;
        let PathSegmentKind::Line(end) = segment.kind else {
            return false;
        };
        if !finite(end) {
            return false;
        }
        let axis = usize::from(!horizontal);
        let other = 1 - axis;
        let t = self.tolerance;
        if !near(self.current[other], fixed, t)
            || !near(end[other], fixed, t)
            || increasing && end[axis] + t < self.current[axis]
            || !increasing && end[axis] - t > self.current[axis]
        {
            return false;
        }
        self.current = end;
        true
    }
    fn corner(&mut self, i: usize, corner: [f32; 2]) -> bool {
        let arc = match self.segment().map(|s| s.kind) {
            Some(PathSegmentKind::Arc(a)) => a,
            _ => return equal(self.current, corner, self.tolerance),
        };
        self.index += 1;
        let end = arc.end;
        let p = self.current;
        let [left, top, right, bottom] = self.contour.bounds;
        let t = self.tolerance;
        let (x, y, valid) = match i {
            0 => (
                end[0] - left,
                p[1] - top,
                near(p[0], left, t) && near(end[1], top, t),
            ),
            1 => (
                right - p[0],
                end[1] - top,
                near(p[1], top, t) && near(end[0], right, t),
            ),
            2 => (
                p[0] - end[0],
                bottom - p[1],
                near(p[0], right, t) && near(end[1], bottom, t),
            ),
            _ => (
                p[0] - left,
                bottom - end[1],
                near(p[1], bottom, t) && near(end[0], left, t),
            ),
        };
        let degenerate = x <= t && y <= t;
        if !valid
            || (!degenerate && (x <= t || y <= t))
            || !finite(arc.size)
            || (arc.size[0].abs() - x).abs() > t
            || (arc.size[1].abs() - y).abs() > t
            || !arc.rotation.is_finite()
            || arc.rotation.abs() > t
            || arc.large
            || arc.sweep != SweepDirection::Clockwise
        {
            return false;
        }
        if !degenerate {
            self.contour.radii_x[i] = x;
            self.contour.radii_y[i] = y;
        }
        self.current = end;
        true
    }
}
fn tolerance(width: f32, height: f32) -> f32 {
    maximum(0.0001, maximum(width.abs(), height.abs()) * 0.00001)
}
fn near(a: f32, b: f32, t: f32) -> bool {
    (a - b).abs() <= t
}
fn equal(a: [f32; 2], b: [f32; 2], t: f32) -> bool {
    near(a[0], b[0], t) && near(a[1], b[1], t)
}
