//! Original fixed four-corner SKRoundRect value, normalization and affine policy.
use super::{
    ArcSegment, EffectMatrix, PathFigure, PathGeometry, PathSegment, PathSegmentKind,
    SweepDirection, maximum, minimum,
};
use crate::Error;
use wide::f32x4 as V;

/// Original native rounded-rectangle classification values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum RoundRectType {
    /// Empty extent, with four square corners.
    #[default]
    Empty = 0,
    /// Positive rectangle with square corners.
    Rectangle = 1,
    /// Complete ellipse.
    Oval = 2,
    /// Four identical radius pairs.
    Simple = 3,
    /// Independent horizontal and vertical side radii.
    NinePatch = 4,
    /// Independent corner pairs.
    Complex = 5,
}
/// Original upper-left, upper-right, lower-right, lower-left corner order.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(usize)]
pub enum RoundRectCorner {
    /// Upper left.
    UpperLeft = 0,
    /// Upper right.
    UpperRight = 1,
    /// Lower right.
    LowerRight = 2,
    /// Lower left.
    LowerLeft = 3,
}
/// Fixed-work original source value. Bounds use left/top/right/bottom; snapshots
/// copy eight radius scalars without sharing mutable storage or allocating.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct RoundRect {
    bounds: [f32; 4],
    radii: [[f32; 2]; 4],
    kind: RoundRectType,
}
impl RoundRect {
    /// Original finite, standardized rectangle constructor.
    pub fn rectangle(bounds: [f32; 4]) -> Self {
        let mut result = Self::default();
        result.set_rectangle(bounds);
        result
    }
    /// Original uniform corner normalization and ellipse selection.
    pub fn uniform(bounds: [f32; 4], radii: [f32; 2]) -> Self {
        let mut result = Self::default();
        result.set_uniform(bounds, radii);
        result
    }
    /// Original complete ellipse constructor.
    pub fn oval(bounds: [f32; 4]) -> Self {
        let mut result = Self::default();
        result.set_oval(bounds);
        result
    }
    /// Original independent four-corner proportional normalization.
    pub fn from_radii(bounds: [f32; 4], radii: [[f32; 2]; 4]) -> Self {
        let mut result = Self::default();
        result.set_radii(bounds, radii);
        result
    }
    /// Original left/top/right/bottom side radii.
    pub fn nine_patch(bounds: [f32; 4], sides: [f32; 4]) -> Self {
        let mut result = Self::default();
        result.set_nine_patch(bounds, sides);
        result
    }
    /// Independent source bounds, including an empty rectangle's retained origin.
    pub fn bounds(self) -> [f32; 4] {
        self.bounds
    }
    /// Original computed source classification.
    pub fn kind(self) -> RoundRectType {
        self.kind
    }
    /// Independent fixed-size radius snapshot.
    pub fn radii(self) -> [[f32; 2]; 4] {
        self.radii
    }
    /// One exact stored corner pair.
    pub fn radius(self, corner: RoundRectCorner) -> [f32; 2] {
        self.radii[corner as usize]
    }
    /// Original floating width, without replacing it with double arithmetic.
    pub fn width(self) -> f32 {
        self.bounds[2] - self.bounds[0]
    }
    /// Original floating height.
    pub fn height(self) -> f32 {
        self.bounds[3] - self.bounds[1]
    }
    /// Original default circular tolerance, 1 / 4096.
    pub fn all_corners_circular(self) -> bool {
        self.check_circular(1.0 / 4096.0)
    }
    /// Original explicit circular test; negative/NaN tolerance admits nothing.
    pub fn check_circular(self, tolerance: f32) -> bool {
        self.radii.iter().all(|r| (r[0] - r[1]).abs() <= tolerance)
    }
    /// Reset both extent and radii to the original empty value.
    pub fn clear(&mut self) {
        *self = Self::default();
    }
    fn initialize(&mut self, bounds: [f32; 4]) -> bool {
        if !finite(bounds) {
            self.clear();
            return false;
        }
        self.bounds = [
            minimum(bounds[0], bounds[2]),
            minimum(bounds[1], bounds[3]),
            maximum(bounds[0], bounds[2]),
            maximum(bounds[1], bounds[3]),
        ];
        if empty(self.bounds) {
            self.radii = [[0.0; 2]; 4];
            self.kind = RoundRectType::Empty;
            return false;
        }
        true
    }
    /// Replace by the original standardized rectangle, or empty for invalid input.
    pub fn set_rectangle(&mut self, bounds: [f32; 4]) {
        if self.initialize(bounds) {
            self.radii = [[0.0; 2]; 4];
            self.kind = RoundRectType::Rectangle;
        }
    }
    /// Original simple radii handling: invalid pairs become square; excessive
    /// radii scale together, with the exact original float comparisons.
    pub fn set_uniform(&mut self, bounds: [f32; 4], mut radius: [f32; 2]) {
        if !self.initialize(bounds) {
            return;
        }
        if radius.iter().any(|v| !v.is_finite()) {
            radius = [0.0; 2];
        }
        if self.width() < radius[0] + radius[0] || self.height() < radius[1] + radius[1] {
            let scale = minimum(
                self.width() / (radius[0] + radius[0]),
                self.height() / (radius[1] + radius[1]),
            );
            radius = scaled(radius, scale);
        }
        if radius[0] <= 0.0 || radius[1] <= 0.0 {
            self.set_rectangle(bounds);
            return;
        }
        self.kind = RoundRectType::Simple;
        if radius[0] >= self.width() * 0.5 && radius[1] >= self.height() * 0.5 {
            self.kind = RoundRectType::Oval;
            radius = [self.width() * 0.5, self.height() * 0.5];
        }
        self.radii = [radius; 4];
    }
    /// Original ellipse selection, retaining the original underflow-to-rectangle case.
    pub fn set_oval(&mut self, bounds: [f32; 4]) {
        if !self.initialize(bounds) {
            return;
        }
        let radius = [self.width() * 0.5, self.height() * 0.5];
        if radius[0] == 0.0 || radius[1] == 0.0 {
            self.radii = [[0.0; 2]; 4];
            self.kind = RoundRectType::Rectangle;
        } else {
            self.radii = [radius; 4];
            self.kind = RoundRectType::Oval;
        }
    }
    /// Original side-radii normalization, square-corner collapse and classification.
    pub fn set_nine_patch(&mut self, bounds: [f32; 4], sides: [f32; 4]) {
        if !self.initialize(bounds) {
            return;
        }
        if !finite(sides) {
            self.set_rectangle(bounds);
            return;
        }
        let [mut l, mut t, mut r, mut b] = sides.map(|v| maximum(v, 0.0));
        let mut scale = 1.0;
        if l + r > self.width() {
            scale = self.width() / (l + r);
        }
        if t + b > self.height() {
            scale = minimum(scale, self.height() / (t + b));
        }
        if scale < 1.0 {
            [l, t, r, b] = (V::from([l, t, r, b]) * V::splat(scale)).to_array();
        }
        if l == r && t == b {
            if l >= self.width() * 0.5 && t >= self.height() * 0.5 {
                self.kind = RoundRectType::Oval;
                l = self.width() * 0.5;
                r = l;
                t = self.height() * 0.5;
                b = t;
            } else if l == 0.0 || t == 0.0 {
                self.kind = RoundRectType::Rectangle;
                l = 0.0;
                t = 0.0;
                r = 0.0;
                b = 0.0;
            } else {
                self.kind = RoundRectType::Simple;
            }
        } else {
            self.kind = RoundRectType::NinePatch;
        }
        self.radii = [[l, t], [r, t], [r, b], [l, b]];
        if self.clamp_corners() {
            self.set_rectangle(bounds);
        } else if self.kind == RoundRectType::NinePatch && !self.is_nine_patch() {
            self.kind = RoundRectType::Complex;
        }
    }
    /// Preserve complete input admission before publishing radius normalization.
    pub fn set_radii(&mut self, bounds: [f32; 4], radii: [[f32; 2]; 4]) {
        if !self.initialize(bounds) {
            return;
        }
        if radii.iter().flatten().any(|v| !v.is_finite()) {
            self.set_rectangle(bounds);
            return;
        }
        self.radii = radii;
        if self.clamp_corners() {
            self.set_rectangle(bounds);
            return;
        }
        self.scale_radii();
    }
    fn clamp_corners(&mut self) -> bool {
        let mut all_square = true;
        for radius in &mut self.radii {
            if radius[0] <= 0.0 || radius[1] <= 0.0 {
                *radius = [0.0; 2];
            } else {
                all_square = false;
            }
        }
        all_square
    }
    fn is_nine_patch(self) -> bool {
        let r = self.radii;
        r[0][0] == r[3][0] && r[0][1] == r[1][1] && r[1][0] == r[2][0] && r[3][1] == r[2][1]
    }
    fn computed_type(self) -> Option<RoundRectType> {
        if empty(self.bounds) {
            return self
                .radii
                .iter()
                .all(|r| *r == [0.0; 2])
                .then_some(RoundRectType::Empty);
        }
        if self.radii.iter().all(|r| r[0] == 0.0 || r[1] == 0.0) {
            return Some(RoundRectType::Rectangle);
        }
        if self.radii.windows(2).all(|p| p[0] == p[1]) {
            return Some(
                if self.radii[0][0] >= self.width() * 0.5 && self.radii[0][1] >= self.height() * 0.5
                {
                    RoundRectType::Oval
                } else {
                    RoundRectType::Simple
                },
            );
        }
        Some(if self.is_nine_patch() {
            RoundRectType::NinePatch
        } else {
            RoundRectType::Complex
        })
    }
    fn compute_type(&mut self) {
        if empty(self.bounds) {
            self.radii = [[0.0; 2]; 4];
            self.kind = RoundRectType::Empty;
            return;
        }
        self.kind = self.computed_type().unwrap_or(RoundRectType::Empty);
        if self.kind == RoundRectType::Oval {
            self.radii = [[self.width() * 0.5, self.height() * 0.5]; 4];
        }
    }
    fn scale_radii(&mut self) {
        let width = f64::from(self.bounds[2]) - f64::from(self.bounds[0]);
        let height = f64::from(self.bounds[3]) - f64::from(self.bounds[1]);
        let sides = [
            (0, 1, 0, width),
            (1, 2, 1, height),
            (2, 3, 0, width),
            (3, 0, 1, height),
        ];
        let fitting = self.radii.iter().flatten().all(|v| *v > 0.0)
            && sides.iter().all(|&(a, b, axis, limit)| {
                let x = self.radii[a][axis];
                let y = self.radii[b][axis];
                x + y != x && x + y != y && f64::from(x) + f64::from(y) <= limit
            });
        if fitting {
            self.compute_type();
            return;
        }
        let mut scale = 1.0_f64;
        for &(a, b, axis, limit) in &sides {
            let sum = f64::from(self.radii[a][axis]) + f64::from(self.radii[b][axis]);
            if sum > limit {
                scale = scale.min(limit / sum);
            }
        }
        // These adjacent-pair updates are deliberately ordered; each later side
        // observes the previous flush/adjust just as the original scalar routine.
        for &(a, b, axis, _) in &sides {
            let first = self.radii[a][axis];
            let second = self.radii[b][axis];
            if first + second == first {
                self.radii[b][axis] = 0.0;
            } else if first + second == second {
                self.radii[a][axis] = 0.0;
            }
        }
        if scale < 1.0 {
            for &(a, b, axis, limit) in &sides {
                let mut first = (f64::from(self.radii[a][axis]) * scale) as f32;
                let mut second = (f64::from(self.radii[b][axis]) * scale) as f32;
                if f64::from(first + second) > limit {
                    let first_min = first <= second;
                    let min = if first_min { first } else { second };
                    let mut max = (limit - f64::from(min)) as f32;
                    while f64::from(max + min) > limit {
                        max = max.next_down();
                    }
                    if first_min {
                        second = max;
                    } else {
                        first = max;
                    }
                }
                self.radii[a][axis] = first;
                self.radii[b][axis] = second;
            }
        }
        self.clamp_corners();
        self.compute_type();
    }
    /// Original rectangle containment: positive extents and all four exact
    /// ellipse corner equations, without flattening or envelope substitution.
    pub fn contains(self, bounds: [f32; 4]) -> bool {
        let a = self.bounds;
        if empty(bounds)
            || empty(a)
            || a[0] > bounds[0]
            || a[1] > bounds[1]
            || a[2] < bounds[2]
            || a[3] < bounds[3]
        {
            return false;
        }
        self.kind == RoundRectType::Rectangle
            || [
                [bounds[0], bounds[1]],
                [bounds[2], bounds[1]],
                [bounds[2], bounds[3]],
                [bounds[0], bounds[3]],
            ]
            .into_iter()
            .all(|p| self.contains_corner(p))
    }
    fn contains_corner(self, [x, y]: [f32; 2]) -> bool {
        let [l, t, r, b] = self.bounds;
        let rs = self.radii;
        let (index, p) = if self.kind == RoundRectType::Oval {
            (
                0,
                [x - (l + self.width() / 2.0), y - (t + self.height() / 2.0)],
            )
        } else if x < l + rs[0][0] && y < t + rs[0][1] {
            (0, [x - (l + rs[0][0]), y - (t + rs[0][1])])
        } else if x < l + rs[3][0] && y > b - rs[3][1] {
            (3, [x - (l + rs[3][0]), y - (b - rs[3][1])])
        } else if x > r - rs[1][0] && y < t + rs[1][1] {
            (1, [x - (r - rs[1][0]), y - (t + rs[1][1])])
        } else if x > r - rs[2][0] && y > b - rs[2][1] {
            (2, [x - (r - rs[2][0]), y - (b - rs[2][1])])
        } else {
            return true;
        };
        let radius = rs[index];
        let v = V::from([p[0], p[1], 0.0, 0.0]);
        let r = V::from([radius[1], radius[0], 0.0, 0.0]);
        let terms = (v * v * r * r).to_array();
        let product = radius[0] * radius[1];
        terms[0] + terms[1] <= product * product
    }
    /// Original in-place translation; metadata queries can still detect an
    /// invalid nonfinite result instead of silently replacing source input.
    pub fn offset(&mut self, offset: [f32; 2]) {
        self.bounds = (V::from(self.bounds)
            + V::from([offset[0], offset[1], offset[0], offset[1]]))
        .to_array();
    }
    /// Original centered collapse and independent nonzero-corner inset policy.
    pub fn inset(&mut self, inset: [f32; 2]) {
        let [dx, dy] = inset;
        let [l, t, r, b] = self.bounds;
        let mut rect = [l + dx, t + dy, r - dx, b - dy];
        let mut degenerate = false;
        if rect[2] <= rect[0] {
            degenerate = true;
            let m = rect[0] * 0.5 + rect[2] * 0.5;
            rect[0] = m;
            rect[2] = m;
        }
        if rect[3] <= rect[1] {
            degenerate = true;
            let m = rect[1] * 0.5 + rect[3] * 0.5;
            rect[1] = m;
            rect[3] = m;
        }
        if degenerate {
            self.bounds = rect;
            self.radii = [[0.0; 2]; 4];
            self.kind = RoundRectType::Empty;
            return;
        }
        if !finite(rect) {
            self.clear();
            return;
        }
        let radii = self.radii.map(|r| {
            [
                if r[0] == 0.0 { 0.0 } else { r[0] - dx },
                if r[1] == 0.0 { 0.0 } else { r[1] - dy },
            ]
        });
        self.set_radii(rect, radii);
    }
    /// Original outward inset counterpart.
    pub fn outset(&mut self, amount: [f32; 2]) {
        self.inset([-amount[0], -amount[1]]);
    }
    /// Original complete value validation, independent of metadata constructors.
    pub fn is_valid(self) -> bool {
        let [l, t, r, b] = self.bounds;
        finite(self.bounds)
            && l <= r
            && t <= b
            && self.radii.iter().all(|v| {
                v.iter().all(|x| x.is_finite())
                    && valid_radius(v[0], l, r)
                    && valid_radius(v[1], t, b)
                    && (v[0] == 0.0) == (v[1] == 0.0)
            })
            && self.computed_type() == Some(self.kind)
    }
    /// Original axis-preserving affine transformation. Radius mapping retains
    /// absolute anchors and their subtraction, including translation rounding.
    pub fn transformed(self, matrix: EffectMatrix) -> Option<Self> {
        if matrix == EffectMatrix::IDENTITY {
            return Some(self);
        }
        let m = matrix.0;
        let diagonal = m[1] == 0.0 && m[3] == 0.0;
        let anti_diagonal = m[0] == 0.0 && m[4] == 0.0;
        if (!diagonal && !anti_diagonal) || m[6] != 0.0 || m[7] != 0.0 || m[8] != 1.0 {
            return None;
        }
        let [l, t, r, b] = self.bounds;
        let p = [[l, t], [r, t], [r, b], [l, b]].map(|p| map(p, m));
        let rect = [
            minimum(minimum(p[0][0], p[1][0]), minimum(p[2][0], p[3][0])),
            minimum(minimum(p[0][1], p[1][1]), minimum(p[2][1], p[3][1])),
            maximum(maximum(p[0][0], p[1][0]), maximum(p[2][0], p[3][0])),
            maximum(maximum(p[0][1], p[1][1]), maximum(p[2][1], p[3][1])),
        ];
        if !finite(rect) {
            return None;
        }
        match self.kind {
            RoundRectType::Empty => return Some(Self::default()),
            RoundRectType::Rectangle => return Some(Self::rectangle(rect)),
            RoundRectType::Oval => return Some(Self::oval(rect)),
            _ => {}
        }
        let mut radii = [[0.0; 2]; 4];
        for corner in 0..4 {
            let [previous, control, next] = self.corner_contour(corner).map(|p| map(p, m));
            let d = (V::from([control[0], control[1], next[0], next[1]])
                - V::from([previous[0], previous[1], control[0], control[1]]))
            .to_array();
            let radius = if d[0] != 0.0 {
                [d[0].abs(), d[3].abs()]
            } else if d[1] == 0.0 {
                [d[2].abs(), d[3].abs()]
            } else {
                [d[2].abs(), d[1].abs()]
            };
            let target = if control[0] == rect[0] {
                if control[1] == rect[1] { 0 } else { 3 }
            } else if control[1] == rect[1] {
                1
            } else {
                2
            };
            radii[target] = radius;
        }
        Some(Self::from_radii(rect, radii))
    }
    fn corner_contour(self, corner: usize) -> [[f32; 2]; 3] {
        let [l, t, r, b] = self.bounds;
        let [x, y] = self.radii[corner];
        match corner {
            0 => [[l, t + y], [l, t], [l + x, t]],
            1 => [[r - x, t], [r, t], [r, t + y]],
            2 => [[r, b - y], [r, b], [r - x, b]],
            _ => [[l + x, b], [l, b], [l, b - y]],
        }
    }
    /// Original AddRoundRect contour with the explicit 0..7 starting slot.
    /// Both directions preserve original line/arc counts and final implicit edge.
    pub fn to_geometry(self, direction: SweepDirection, start: u32) -> Result<PathGeometry, Error> {
        if start > 7 {
            return Err(Error::InvalidInput);
        }
        let [l, t, r, b] = self.bounds;
        let mut radii = self.radii.map(|r| [r[0].abs(), r[1].abs()]);
        let mut scale = 1.0;
        for (available, requested) in [
            (self.width().abs(), radii[0][0] + radii[1][0]),
            (self.width().abs(), radii[3][0] + radii[2][0]),
            (self.height().abs(), radii[0][1] + radii[3][1]),
            (self.height().abs(), radii[1][1] + radii[2][1]),
        ] {
            if requested > available && requested > 0.00001 {
                scale = minimum(scale, available / requested);
            }
        }
        if scale < 1.0 {
            radii = radii.map(|r| scaled(r, scale));
        }
        let points = [
            [l + radii[0][0], t],
            [r - radii[1][0], t],
            [r, t + radii[1][1]],
            [r, b - radii[2][1]],
            [r - radii[2][0], b],
            [l + radii[3][0], b],
            [l, b - radii[3][1]],
            [l, t + radii[0][1]],
        ];
        let mut index = start as usize;
        let clockwise = direction == SweepDirection::Clockwise;
        let mut figure = PathFigure::new(points[index], true);
        figure
            .segments
            .try_reserve_exact(8)
            .map_err(|_| Error::Capacity)?;
        for edge in 1..=8 {
            let next = (index + if clockwise { 1 } else { 7 }) % 8;
            let arc = if clockwise {
                index % 2 == 1
            } else {
                index % 2 == 0
            };
            if edge == 8 && !arc {
                break;
            }
            let corner = if clockwise {
                (index + 1) / 2 % 4
            } else {
                index / 2
            };
            let radius = radii[corner];
            let kind = if arc && radius[0] > 0.00001 && radius[1] > 0.00001 {
                PathSegmentKind::Arc(ArcSegment {
                    end: points[next],
                    size: radius,
                    rotation: 0.0,
                    large: false,
                    sweep: direction,
                })
            } else {
                PathSegmentKind::Line(points[next])
            };
            figure.segments.push(PathSegment::new(kind));
            index = next;
        }
        let mut result = PathGeometry::default();
        result
            .figures
            .try_reserve_exact(1)
            .map_err(|_| Error::Capacity)?;
        result.figures.push(figure);
        Ok(result)
    }
}
fn finite(r: [f32; 4]) -> bool {
    r.iter().all(|v| v.is_finite())
}
fn empty(r: [f32; 4]) -> bool {
    r[0] >= r[2] || r[1] >= r[3]
}
fn valid_radius(radius: f32, min: f32, max: f32) -> bool {
    min <= max && radius <= max - min && min + radius <= max && max - radius >= min && radius >= 0.0
}
fn scaled(r: [f32; 2], scale: f32) -> [f32; 2] {
    let a = (V::from([r[0], r[1], 0.0, 0.0]) * V::splat(scale)).to_array();
    [a[0], a[1]]
}
fn map(p: [f32; 2], m: [f32; 9]) -> [f32; 2] {
    let a = (V::from([m[0], m[3], 0.0, 0.0]) * V::splat(p[0])
        + V::from([m[1], m[4], 0.0, 0.0]) * V::splat(p[1])
        + V::from([m[2], m[5], 0.0, 0.0]))
    .to_array();
    [a[0], a[1]]
}
