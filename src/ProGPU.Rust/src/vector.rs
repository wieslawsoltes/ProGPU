//! Original managed vector geometry and material records. These source-level
//! figures retain arcs, rational weights and stroke metadata; the independent
//! native GPU segment wire remains in `path`. Boolean reconstruction and final
//! GPU material/stroke lowering are separate consumers, not implicit flattening.
use crate::Error;
mod arc;
mod bezier;
mod boolean;
#[cfg(feature = "gpu")]
mod boolean_gpu;
mod brush;
mod dash;
mod hit_test;
mod material;
mod parse;
mod primitives;
mod rounded;
mod stroke;
mod stroke_path;
pub use arc::{ArcCenter, ArcShaderParameters};
pub use bezier::{DEFAULT_LENGTH_SEGMENTS, evaluate_cubic, evaluate_quadratic};
pub use boolean::{
    BooleanOperation, CombinedQueryKind, CompiledPath, DeferredPath, GpuPathRecord,
    reconstruct_figures,
};
#[cfg(feature = "gpu")]
pub use boolean_gpu::{BooleanGpuMetrics, BooleanSolver, BooleanWork, PathOperationError};
pub use brush::*;
pub use dash::{DashPattern, DashResult, DashState, PositionedSegment};
pub use hit_test::{
    GeometryTolerance, ellipse_contains_fill, ellipse_contains_stroke, line_contains_fill,
    line_contains_stroke, rectangle_contains_fill, rectangle_contains_stroke,
};
pub use material::{GpuBrush, GpuGradientStop, MaterialPage, MaterialTable};
pub use parse::PathParseError;
pub use rounded::RoundedRectangleContour;
pub use stroke::{MAX_STROKE_TRIANGLES, StrokeCap, StrokeJoin, StrokeJoinFrame, StrokeTriangle};
pub use stroke_path::WidenedPath;

/// Complete row-major, row-vector 4-by-4 source transform. No perspective or
/// unused component is discarded when retaining a source frame.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Matrix4(pub [[f32; 4]; 4]);
impl Matrix4 {
    /// Original identity coefficients.
    pub const IDENTITY: Self = Self([
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]);
    /// Embed existing actual affine coefficients without inferring primitive
    /// construction history or claiming source rotation/skew qualification.
    pub fn from_affine(value: crate::geometry::Affine) -> Self {
        let [a, b, c, d, x, y] = value.0;
        Self([
            [a, b, 0.0, 0.0],
            [c, d, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [x, y, 0.0, 1.0],
        ])
    }
    /// Source Vector2 projection, retaining its XY multiply/add order. This is
    /// not homogeneous division or an assertion that the full frame is affine.
    pub fn transform_point(self, point: [f32; 2]) -> [f32; 2] {
        [
            point[0] * self.0[0][0] + point[1] * self.0[1][0] + self.0[3][0],
            point[0] * self.0[0][1] + point[1] * self.0[1][1] + self.0[3][1],
        ]
    }
    fn direction(self, p: [f32; 2]) -> [f32; 2] {
        [
            p[0] * self.0[0][0] + p[1] * self.0[1][0],
            p[0] * self.0[0][1] + p[1] * self.0[1][1],
        ]
    }
}
impl Default for Matrix4 {
    fn default() -> Self {
        Self::IDENTITY
    }
}
impl std::ops::Mul for Matrix4 {
    type Output = Self;
    /// Compose complete source row-vector matrices, preserving all sixteen
    /// components. Default-runtime SIMD/FMA bit qualification remains separate.
    fn mul(self, rhs: Self) -> Self {
        let mut result = [[0.0; 4]; 4];
        for (row, output) in result.iter_mut().enumerate() {
            for (column, value) in output.iter_mut().enumerate() {
                *value = self.0[row][0] * rhs.0[0][column]
                    + self.0[row][1] * rhs.0[1][column]
                    + self.0[row][2] * rhs.0[2][column]
                    + self.0[row][3] * rhs.0[3][column];
            }
        }
        Self(result)
    }
}

/// Original managed fill-rule values, distinct from the native shader wire.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum FillRule {
    /// Odd crossing parity.
    EvenOdd = 0,
    /// Any nonzero signed winding; original geometry default.
    #[default]
    Nonzero = 1,
}
impl FillRule {
    /// Explicit semantic conversion, never a cast between opposite wire values.
    pub fn raster_rule(self) -> crate::path::FillRule {
        match self {
            Self::EvenOdd => crate::path::FillRule::EvenOdd,
            Self::Nonzero => crate::path::FillRule::NonZero,
        }
    }
}
/// Original screen-space arc direction values.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum SweepDirection {
    /// Counterclockwise source sweep.
    Counterclockwise = 0,
    /// Clockwise source sweep.
    Clockwise = 1,
}
/// Original neutral figure/pen cap values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum PenLineCap {
    /// Original flat endpoint.
    #[default]
    Flat = 0,
    /// Half-width square extension.
    Square = 1,
    /// Rounded endpoint.
    Round = 2,
    /// Triangular endpoint.
    Triangle = 3,
}
/// Original endpoint-form elliptical arc. Construction does not normalize or
/// validate its original coordinates, radii, rotation or large-arc intent.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ArcSegment {
    /// Original endpoint.
    pub end: [f32; 2],
    /// Original radius pair, including signed/nonfinite input until admission.
    pub size: [f32; 2],
    /// Original rotation in degrees.
    pub rotation: f32,
    /// Original large-arc selection.
    pub large: bool,
    /// Original direction.
    pub sweep: SweepDirection,
}
/// Original source segment families. Start points belong to the figure/previous
/// segment; all original controls/weights remain distinct from GPU transport.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PathSegmentKind {
    /// Line endpoint.
    Line([f32; 2]),
    /// Quadratic control and endpoint.
    Quadratic([[f32; 2]; 2]),
    /// Cubic controls and endpoint.
    Cubic([[f32; 2]; 3]),
    /// Rational quadratic control/endpoint and original middle weight.
    RationalQuadratic {
        /// Control and endpoint.
        points: [[f32; 2]; 2],
        /// Original weight, without clamping.
        weight: f32,
    },
    /// Rational cubic controls/endpoint and original two middle weights.
    RationalCubic {
        /// Controls and endpoint.
        points: [[f32; 2]; 3],
        /// Original weights, without clamping.
        weights: [f32; 2],
    },
    /// Endpoint-form ellipse retained until the actual target is known.
    Arc(ArcSegment),
}
/// Original segment plus independent stroke/smooth-join metadata.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PathSegment {
    /// Original geometry payload.
    pub kind: PathSegmentKind,
    /// Original source smooth-join flag.
    pub is_smooth_join: bool,
    /// Original stroke participation, independent of fill participation.
    pub is_stroked: bool,
}
impl PathSegment {
    /// Original constructors: rational segments default unstroked, others stroked;
    /// smooth-join defaults false. No geometry admission occurs at recording.
    pub fn new(kind: PathSegmentKind) -> Self {
        let is_stroked = !matches!(
            kind,
            PathSegmentKind::RationalQuadratic { .. } | PathSegmentKind::RationalCubic { .. }
        );
        Self {
            kind,
            is_smooth_join: false,
            is_stroked,
        }
    }
    /// Original endpoint selected without reconstructing a curve.
    pub fn end(self) -> [f32; 2] {
        match self.kind {
            PathSegmentKind::Line(p) => p,
            PathSegmentKind::Quadratic(p)
            | PathSegmentKind::RationalQuadratic { points: p, .. } => p[1],
            PathSegmentKind::Cubic(p) | PathSegmentKind::RationalCubic { points: p, .. } => p[2],
            PathSegmentKind::Arc(arc) => arc.end,
        }
    }
}
/// One original figure, retaining independent closure/fill and cap metadata.
#[derive(Clone, Debug, PartialEq)]
pub struct PathFigure {
    /// Original starting point.
    pub start: [f32; 2],
    /// Complete ordered original segment inventory.
    pub segments: Vec<PathSegment>,
    /// Original closed-contour flag; the final edge can remain implicit.
    pub is_closed: bool,
    /// Original fill participation, default true.
    pub is_filled: bool,
    /// Optional original start-cap override; None inherits the pen.
    pub start_cap: Option<PenLineCap>,
    /// Optional original end-cap override; closed contours ignore overrides.
    pub end_cap: Option<PenLineCap>,
}
impl PathFigure {
    /// Record an original figure with no source point/flag normalization.
    pub fn new(start: [f32; 2], is_closed: bool) -> Self {
        Self {
            start,
            segments: Vec::new(),
            is_closed,
            is_filled: true,
            start_cap: None,
            end_cap: None,
        }
    }
    /// Original finite control envelope with analytic arc extrema. This is not
    /// a tight Bezier extent, filled-area proof or stroke/material bound.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        let mut bounds = None;
        include(&mut bounds, self.start);
        let mut current = self.start;
        for segment in &self.segments {
            match segment.kind {
                PathSegmentKind::Line(p) => include(&mut bounds, p),
                PathSegmentKind::Quadratic(p)
                | PathSegmentKind::RationalQuadratic { points: p, .. } => {
                    for point in p {
                        include(&mut bounds, point);
                    }
                }
                PathSegmentKind::Cubic(p) | PathSegmentKind::RationalCubic { points: p, .. } => {
                    for point in p {
                        include(&mut bounds, point);
                    }
                }
                PathSegmentKind::Arc(arc) => {
                    if let Some(b) = arc.bounds(current) {
                        include(&mut bounds, [b[0], b[1]]);
                        include(&mut bounds, [b[2], b[3]]);
                    } else {
                        include(&mut bounds, arc.end);
                    }
                }
            }
            current = segment.end();
        }
        if self.is_closed {
            include(&mut bounds, self.start);
        }
        bounds
    }
}
impl Default for PathFigure {
    fn default() -> Self {
        Self::new([0.0; 2], false)
    }
}
/// Owned source path geometry with optional immutable deferred Boolean operands.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct PathGeometry {
    /// Complete original figure inventory, including move-only/nonfilled figures.
    pub figures: Vec<PathFigure>,
    /// Original managed fill rule, default nonzero.
    pub fill_rule: FillRule,
    /// Original deferred expression; ordinary figures remain a separate inventory.
    pub deferred: Option<DeferredPath>,
}
impl PathGeometry {
    /// Original finite figure/control envelope, including analytic arc extrema.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        if let Some(deferred) = &self.deferred {
            return deferred.bounds();
        }
        let mut bounds = None;
        for figure in &self.figures {
            if let Some(b) = figure.bounds() {
                include(&mut bounds, [b[0], b[1]]);
                include(&mut bounds, [b[2], b[3]]);
            }
        }
        bounds
    }
    /// Transform the complete ordinary figure inventory. Arc transformation
    /// keeps the original similarity/axis/principal-axis routes and original
    /// line fallback when no transformed arc is admitted. No tessellation.
    pub fn transformed(&self, transform: Matrix4) -> Result<Self, Error> {
        if let Some(deferred) = &self.deferred {
            return deferred.transformed(transform, self.fill_rule);
        }
        let mut result = Self {
            fill_rule: self.fill_rule,
            ..Default::default()
        };
        result
            .figures
            .try_reserve_exact(self.figures.len())
            .map_err(|_| Error::Capacity)?;
        for source in &self.figures {
            let mut figure =
                PathFigure::new(transform.transform_point(source.start), source.is_closed);
            figure.is_filled = source.is_filled;
            figure.start_cap = source.start_cap;
            figure.end_cap = source.end_cap;
            figure
                .segments
                .try_reserve_exact(source.segments.len())
                .map_err(|_| Error::Capacity)?;
            let mut current = source.start;
            for segment in &source.segments {
                let kind = match segment.kind {
                    PathSegmentKind::Line(p) => PathSegmentKind::Line(transform.transform_point(p)),
                    PathSegmentKind::Quadratic(p) => {
                        PathSegmentKind::Quadratic(p.map(|p| transform.transform_point(p)))
                    }
                    PathSegmentKind::Cubic(p) => {
                        PathSegmentKind::Cubic(p.map(|p| transform.transform_point(p)))
                    }
                    PathSegmentKind::RationalQuadratic { points, weight } => {
                        PathSegmentKind::RationalQuadratic {
                            points: points.map(|p| transform.transform_point(p)),
                            weight,
                        }
                    }
                    PathSegmentKind::RationalCubic { points, weights } => {
                        PathSegmentKind::RationalCubic {
                            points: points.map(|p| transform.transform_point(p)),
                            weights,
                        }
                    }
                    PathSegmentKind::Arc(arc) => arc.transformed(current, transform).map_or_else(
                        || PathSegmentKind::Line(transform.transform_point(arc.end)),
                        |(_, arc)| PathSegmentKind::Arc(arc),
                    ),
                };
                figure.segments.push(PathSegment { kind, ..*segment });
                current = segment.end();
            }
            result.figures.push(figure);
        }
        Ok(result)
    }
}
fn finite(p: [f32; 2]) -> bool {
    p.iter().all(|v| v.is_finite())
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] + b[0], a[1] + b[1]]
}
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] - b[0], a[1] - b[1]]
}
fn mul(a: [f32; 2], n: f32) -> [f32; 2] {
    [a[0] * n, a[1] * n]
}
fn dot(a: [f32; 2], b: [f32; 2]) -> f32 {
    a[0] * b[0] + a[1] * b[1]
}
fn length(p: [f32; 2]) -> f32 {
    dot(p, p).sqrt()
}
fn minimum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() | b.to_bits())
    } else {
        a.min(b)
    }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() & b.to_bits())
    } else {
        a.max(b)
    }
}
fn include(bounds: &mut Option<[f32; 4]>, p: [f32; 2]) {
    if !finite(p) {
        return;
    }
    if let Some(b) = bounds {
        for axis in 0..2 {
            b[axis] = minimum(b[axis], p[axis]);
            b[axis + 2] = maximum(b[axis + 2], p[axis]);
        }
    } else {
        *bounds = Some([p[0], p[1], p[0], p[1]]);
    }
}
