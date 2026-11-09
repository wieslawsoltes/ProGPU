//! Original deferred Boolean query classification, immediate source results and
//! GPU reconstruction transport. No scalar replacement for pairwise GPU work.
use super::*;
use crate::path::Segment;
use std::sync::Arc;
/// Original Boolean operation values, independent of raster-operation encoding.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum BooleanOperation {
    /// A minus B.
    Difference = 0,
    /// Common interior.
    Intersect = 1,
    /// Combined interior.
    Union = 2,
    /// Exactly one operand's interior.
    ExclusiveOr = 3,
    /// B minus A.
    ReverseDifference = 4,
}
impl TryFrom<u32> for BooleanOperation {
    type Error = Error;
    fn try_from(value: u32) -> Result<Self, Error> {
        match value {
            0 => Ok(Self::Difference),
            1 => Ok(Self::Intersect),
            2 => Ok(Self::Union),
            3 => Ok(Self::ExclusiveOr),
            4 => Ok(Self::ReverseDifference),
            _ => Err(Error::InvalidInput),
        }
    }
}
/// Original cached query classification. Bounds ownership is distinct from an
/// exact operand result; neither implies reconstructed CPU contour inventory.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum CombinedQueryKind {
    /// Requires ordinary fallback query logic.
    Unknown = 0,
    /// Proven empty.
    Empty = 1,
    /// Exact bounds of A, not necessarily A's geometry.
    BoundsOperandA = 2,
    /// Exact bounds of B, not necessarily B's geometry.
    BoundsOperandB = 3,
    /// Union of the original operand bounds.
    UnionBounds = 4,
    /// Result is operand A.
    ResultOperandA = 5,
    /// Result is operand B.
    ResultOperandB = 6,
}
/// Immutable owned deferred expression. Typed operands prevent null/cyclic raw
/// object graphs; shared Arc generations replace mutable source snapshot marks.
#[derive(Clone, Debug, PartialEq)]
pub struct DeferredPath {
    a: Arc<PathGeometry>,
    b: Arc<PathGeometry>,
    operation: BooleanOperation,
    query: CombinedQueryKind,
}
impl DeferredPath {
    /// Original operation.
    pub fn operation(&self) -> BooleanOperation {
        self.operation
    }
    /// Actual immutable retained source operands.
    pub fn operands(&self) -> (&Arc<PathGeometry>, &Arc<PathGeometry>) {
        (&self.a, &self.b)
    }
    /// Original cached query decision.
    pub fn query_kind(&self) -> CombinedQueryKind {
        self.query
    }
    /// Original three-state emptiness knowledge, not materialization.
    pub fn known_empty(&self) -> Option<bool> {
        match self.query {
            CombinedQueryKind::Unknown => None,
            CombinedQueryKind::Empty => Some(true),
            _ => Some(false),
        }
    }
    /// Whether the original classifier knows exact result bounds.
    pub fn has_exact_bounds(&self) -> bool {
        self.query != CombinedQueryKind::Unknown
    }
    pub(super) fn bounds(&self) -> Option<[f32; 4]> {
        use CombinedQueryKind::*;
        match self.query {
            Empty => return None,
            BoundsOperandA | ResultOperandA => return self.a.bounds(),
            BoundsOperandB | ResultOperandB => return self.b.bounds(),
            UnionBounds => return union(self.a.bounds(), self.b.bounds()),
            Unknown => {}
        }
        let a = self.a.bounds();
        let b = self.b.bounds();
        match self.operation {
            BooleanOperation::Intersect => intersection(a?, b?),
            BooleanOperation::Difference => a,
            BooleanOperation::ReverseDifference => b,
            _ => union(a, b),
        }
    }
    pub(super) fn transformed(
        &self,
        matrix: Matrix4,
        fill_rule: FillRule,
    ) -> Result<PathGeometry, Error> {
        Ok(PathGeometry {
            fill_rule,
            deferred: Some(Self {
                a: Arc::new(self.a.transformed(matrix)?),
                b: Arc::new(self.b.transformed(matrix)?),
                operation: self.operation,
                query: self.query,
            }),
            ..Default::default()
        })
    }
}
impl PathGeometry {
    /// Retain and classify the original immutable Boolean expression without
    /// materializing contours, raster masks or GPU resources.
    pub fn combine_deferred(a: Arc<Self>, b: Arc<Self>, operation: BooleanOperation) -> Self {
        let fill_rule = output_fill(&a, &b, operation);
        let query = classify(&a, &b, operation);
        Self {
            fill_rule,
            deferred: Some(DeferredPath {
                a,
                b,
                operation,
                query,
            }),
            ..Default::default()
        }
    }
    /// Original immediate-result branches for resolved ordinary operands. None
    /// means the canonical GPU reconstruction is required; it is not empty.
    pub fn immediate_boolean(
        &self,
        b: &Self,
        operation: BooleanOperation,
    ) -> Result<Option<Self>, Error> {
        if self.deferred.is_some() || b.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        reject_rational(self)?;
        reject_rational(b)?;
        let mut result = Self {
            fill_rule: output_fill(self, b, operation),
            ..Default::default()
        };
        if matches!(
            operation,
            BooleanOperation::Intersect | BooleanOperation::Union
        ) {
            let selected = if contained(self, b) {
                Some(if operation == BooleanOperation::Intersect {
                    b
                } else {
                    self
                })
            } else if contained(b, self) {
                Some(if operation == BooleanOperation::Intersect {
                    self
                } else {
                    b
                })
            } else {
                None
            };
            if let Some(source) = selected {
                copy_figures(source, &mut result)?;
                return Ok(Some(result));
            }
        }
        if operation == BooleanOperation::Difference
            && (rectangle_contains(self, b) || rounded_contains(self, b))
        {
            result.fill_rule = FillRule::EvenOdd;
            copy_figures(self, &mut result)?;
            copy_figures(b, &mut result)?;
            return Ok(Some(result));
        }
        let empty_a = empty_figures(self);
        let empty_b = empty_figures(b);
        if empty_a && empty_b {
            return Ok(Some(result));
        }
        if empty_a {
            if matches!(
                operation,
                BooleanOperation::Union
                    | BooleanOperation::ExclusiveOr
                    | BooleanOperation::ReverseDifference
            ) {
                result.fill_rule = b.fill_rule;
                copy_figures(b, &mut result)?;
            }
            return Ok(Some(result));
        }
        if empty_b {
            if matches!(
                operation,
                BooleanOperation::Difference
                    | BooleanOperation::Union
                    | BooleanOperation::ExclusiveOr
            ) {
                result.fill_rule = self.fill_rule;
                copy_figures(self, &mut result)?;
            }
            return Ok(Some(result));
        }
        Ok(None)
    }
    /// Compile the original ordinary solver input. This retains the solver's
    /// independent figure policy (including unfilled/unstroked figures) and arc
    /// fallback, not a general retained-paint compiler. Rational/combined inputs
    /// reject before publication, matching the solver's separate admission gate.
    pub fn compile_boolean_input(&self) -> Result<CompiledPath, Error> {
        if self.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        reject_rational(self)?;
        let mut count = 0usize;
        for figure in &self.figures {
            if !figure.segments.is_empty() {
                count = count
                    .checked_add(figure.segments.len())
                    .and_then(|n| n.checked_add(usize::from(figure.is_closed)))
                    .ok_or(Error::Capacity)?;
            }
        }
        let mut segments = Vec::new();
        segments
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
        for figure in &self.figures {
            if figure.segments.is_empty() {
                continue;
            }
            let mut current = figure.start;
            update(&mut bounds, current);
            for segment in &figure.segments {
                match segment.kind {
                    PathSegmentKind::Line(p) => {
                        segments.push(Segment::line(current, p));
                        update(&mut bounds, p);
                        current = p;
                    }
                    PathSegmentKind::Quadratic(p) => {
                        segments.push(Segment::quadratic([current, p[0], p[1]]));
                        for point in p {
                            update(&mut bounds, point);
                        }
                        current = p[1];
                    }
                    PathSegmentKind::Cubic(p) => {
                        segments.push(Segment::cubic([current, p[0], p[1], p[2]]));
                        for point in p {
                            update(&mut bounds, point);
                        }
                        current = p[2];
                    }
                    PathSegmentKind::Arc(a) => {
                        if let Some(center) = a.center(current) {
                            segments.push(Segment {
                                points: [current, a.end, center.center, center.radii],
                                kind: 3,
                                parameters: [
                                    center.theta.to_bits(),
                                    center.delta.to_bits(),
                                    (a.rotation * std::f32::consts::PI / 180.0).to_bits(),
                                ],
                            });
                            if let Some(b) = a.bounds(current) {
                                update(&mut bounds, [b[0], b[1]]);
                                update(&mut bounds, [b[2], b[3]]);
                            } else {
                                update(&mut bounds, current);
                                update(&mut bounds, a.end);
                            }
                        } else {
                            if current != a.end {
                                segments.push(Segment::line(current, a.end));
                            }
                            update(&mut bounds, a.end);
                        }
                        current = a.end;
                    }
                    _ => return Err(Error::UnsupportedExecution),
                }
            }
            if figure.is_closed && current != figure.start {
                segments.push(Segment::line(current, figure.start));
                update(&mut bounds, figure.start);
            }
        }
        let record = if segments.is_empty() {
            None
        } else {
            Some(GpuPathRecord {
                start_segment: 0,
                segment_count: u32::try_from(segments.len()).map_err(|_| Error::Capacity)?,
                bounds,
                fill_rule: self.fill_rule.raster_rule() as u32,
                pad: 0,
            })
        };
        Ok(CompiledPath { record, segments })
    }
}
/// Exact original 32-byte solver/raster path record.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuPathRecord {
    /// Actual first segment index.
    pub start_segment: u32,
    /// Original segment count.
    pub segment_count: u32,
    /// Original minX/minY/maxX/maxY bounds.
    pub bounds: [f32; 4],
    /// Original shader encoding, zero nonzero / one evenodd.
    pub fill_rule: u32,
    /// Original zero padding.
    pub pad: u32,
}
const _: () = assert!(size_of::<GpuPathRecord>() == 32);
/// Complete original solver input, with no record for a zero-segment inventory.
#[derive(Clone, Debug, PartialEq)]
pub struct CompiledPath {
    /// Original single optional record.
    pub record: Option<GpuPathRecord>,
    /// Complete ordered original 48-byte segment inventory.
    pub segments: Vec<Segment>,
}
impl CompiledPath {
    /// Original max(64, 15*(A+B)) output bound with checked wire capacity.
    pub fn max_destination_segments(a: i32, b: i32) -> Result<u32, Error> {
        if a < 0 || b < 0 {
            return Err(Error::InvalidInput);
        }
        let count = (a as u64 + b as u64) * 15;
        u32::try_from(count.max(64)).map_err(|_| Error::Capacity)
    }
}
/// Original first-unused, forward-before-reversed contour reconstruction. Input
/// order and the original strict squared-distance threshold remain authoritative;
/// tiny/cancelled-area lobes are retained, including the source closure-loop rule.
pub fn reconstruct_figures(segments: &[Segment]) -> Result<Vec<PathFigure>, Error> {
    let mut figures = Vec::new();
    if segments.is_empty() {
        return Ok(figures);
    }
    let mut small = [false; 256];
    let mut large = Vec::new();
    let used = if segments.len() <= small.len() {
        &mut small[..segments.len()]
    } else {
        large
            .try_reserve_exact(segments.len())
            .map_err(|_| Error::Capacity)?;
        large.resize(segments.len(), false);
        &mut large[..]
    };
    let mut remaining = segments.len();
    while remaining > 0 {
        let first = used.iter().position(|v| !v).ok_or(Error::InvalidInput)?;
        let current_segment = segments[first];
        used[first] = true;
        remaining -= 1;
        let mut figure = PathFigure::new(current_segment.points[0], false);
        let mut current = figure.start;
        append(&mut figure, current_segment, &mut current)?;
        let mut found = true;
        while found && remaining > 0 {
            found = false;
            for (i, &segment) in segments.iter().enumerate() {
                if used[i] {
                    continue;
                }
                let next = if distance_squared(segment.points[0], current) < 0.25 {
                    Some(segment)
                } else if distance_squared(endpoint(segment), current) < 0.25 {
                    Some(reverse(segment))
                } else {
                    None
                };
                if let Some(segment) = next {
                    append(&mut figure, segment, &mut current)?;
                    used[i] = true;
                    remaining -= 1;
                    found = true;
                    break;
                }
            }
            if distance_squared(current, figure.start) < 0.25 {
                figure.is_closed = true;
                break;
            }
        }
        figures.try_reserve(1).map_err(|_| Error::Capacity)?;
        figures.push(figure);
    }
    Ok(figures)
}
fn endpoint(s: Segment) -> [f32; 2] {
    match s.kind {
        1 => s.points[2],
        2 => s.points[3],
        _ => s.points[1],
    }
}
fn reverse(mut s: Segment) -> Segment {
    match s.kind {
        0 => s.points.swap(0, 1),
        1 => s.points.swap(0, 2),
        2 => s.points.reverse(),
        3 => {
            s.points.swap(0, 1);
            let a = f32::from_bits(s.parameters[0]);
            let d = f32::from_bits(s.parameters[1]);
            s.parameters[0] = (a + d).to_bits();
            s.parameters[1] = (-d).to_bits();
        }
        _ => {}
    }
    s
}
fn append(figure: &mut PathFigure, s: Segment, current: &mut [f32; 2]) -> Result<(), Error> {
    let kind = match s.kind {
        0 => PathSegmentKind::Line(s.points[1]),
        1 => PathSegmentKind::Quadratic([s.points[1], s.points[2]]),
        2 => PathSegmentKind::Cubic([s.points[1], s.points[2], s.points[3]]),
        3 => {
            let delta = f32::from_bits(s.parameters[1]);
            PathSegmentKind::Arc(ArcSegment {
                end: s.points[1],
                size: s.points[3],
                rotation: f32::from_bits(s.parameters[2]) * 180.0 / std::f32::consts::PI,
                large: delta.abs() > std::f32::consts::PI,
                sweep: if delta > 0.0 {
                    SweepDirection::Clockwise
                } else {
                    SweepDirection::Counterclockwise
                },
            })
        }
        _ => return Ok(()),
    };
    let segment = PathSegment::new(kind);
    figure
        .segments
        .try_reserve(1)
        .map_err(|_| Error::Capacity)?;
    figure.segments.push(segment);
    *current = segment.end();
    Ok(())
}
fn distance_squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = sub(a, b);
    dot(d, d)
}
fn update(bounds: &mut [f32; 4], p: [f32; 2]) {
    for axis in 0..2 {
        bounds[axis] = minimum(bounds[axis], p[axis]);
        bounds[axis + 2] = maximum(bounds[axis + 2], p[axis]);
    }
}
fn empty_figures(path: &PathGeometry) -> bool {
    path.figures.iter().all(|f| f.segments.is_empty())
}
fn known_empty(path: &PathGeometry) -> bool {
    path.deferred
        .as_ref()
        .map_or_else(|| empty_figures(path), |d| d.known_empty() == Some(true))
}
pub(super) fn output_fill(a: &PathGeometry, b: &PathGeometry, op: BooleanOperation) -> FillRule {
    if a.fill_rule == FillRule::EvenOdd || b.fill_rule == FillRule::EvenOdd {
        FillRule::EvenOdd
    } else if op == BooleanOperation::ReverseDifference {
        b.fill_rule
    } else {
        a.fill_rule
    }
}
fn reject_rational(path: &PathGeometry) -> Result<(), Error> {
    if path.figures.iter().flat_map(|f| &f.segments).any(|s| {
        matches!(
            s.kind,
            PathSegmentKind::RationalQuadratic { .. } | PathSegmentKind::RationalCubic { .. }
        )
    }) {
        Err(Error::UnsupportedExecution)
    } else {
        Ok(())
    }
}
pub(super) fn copy_figures(source: &PathGeometry, target: &mut PathGeometry) -> Result<(), Error> {
    target
        .figures
        .try_reserve(source.figures.len())
        .map_err(|_| Error::Capacity)?;
    for original in &source.figures {
        let mut figure = PathFigure {
            start: original.start,
            segments: Vec::new(),
            is_closed: original.is_closed,
            is_filled: original.is_filled,
            start_cap: original.start_cap,
            end_cap: original.end_cap,
        };
        figure
            .segments
            .try_reserve_exact(original.segments.len())
            .map_err(|_| Error::Capacity)?;
        figure
            .segments
            .extend(original.segments.iter().copied().filter(|s| {
                !matches!(
                    s.kind,
                    PathSegmentKind::RationalQuadratic { .. }
                        | PathSegmentKind::RationalCubic { .. }
                )
            }));
        target.figures.push(figure);
    }
    Ok(())
}
fn contour(path: &PathGeometry) -> Option<RoundedRectangleContour> {
    if path.deferred.is_some() {
        return None;
    }
    let [figure] = path.figures.as_slice() else {
        return None;
    };
    RoundedRectangleContour::from_figure(figure)
}
fn rounded_contains(a: &PathGeometry, b: &PathGeometry) -> bool {
    contour(a)
        .zip(contour(b))
        .is_some_and(|(a, b)| a.contains(b))
}
fn rectangle_contains(a: &PathGeometry, b: &PathGeometry) -> bool {
    let Some((a, b)) = a
        .axis_aligned_rectangle_bounds()
        .zip(b.axis_aligned_rectangle_bounds())
    else {
        return false;
    };
    b[0] >= a[0] - 0.0001 && b[1] >= a[1] - 0.0001 && b[2] <= a[2] + 0.0001 && b[3] <= a[3] + 0.0001
}
fn contained(a: &PathGeometry, b: &PathGeometry) -> bool {
    rectangle_contains(a, b)
        || rounded_contains(a, b)
        || contour(a)
            .zip(b.bounds())
            .is_some_and(|(a, b)| a.contains_bounds(b))
}
fn strictly_contained(a: &PathGeometry, b: &PathGeometry, ab: [f32; 4], bb: [f32; 4]) -> bool {
    contained(a, b)
        && bb[0] > ab[0] + 0.0001
        && bb[1] > ab[1] + 0.0001
        && bb[2] < ab[2] - 0.0001
        && bb[3] < ab[3] - 0.0001
}
fn intersection(a: [f32; 4], b: [f32; 4]) -> Option<[f32; 4]> {
    let r = [
        maximum(a[0], b[0]),
        maximum(a[1], b[1]),
        minimum(a[2], b[2]),
        minimum(a[3], b[3]),
    ];
    (r[2] >= r[0] && r[3] >= r[1]).then_some(r)
}
fn union(a: Option<[f32; 4]>, b: Option<[f32; 4]>) -> Option<[f32; 4]> {
    match (a, b) {
        (Some(a), Some(b)) => Some([
            minimum(a[0], b[0]),
            minimum(a[1], b[1]),
            maximum(a[2], b[2]),
            maximum(a[3], b[3]),
        ]),
        (Some(a), None) => Some(a),
        (None, b) => b,
    }
}
fn classify(a: &PathGeometry, b: &PathGeometry, op: BooleanOperation) -> CombinedQueryKind {
    use CombinedQueryKind::*;
    let ea = known_empty(a);
    let eb = known_empty(b);
    let ab = a.bounds();
    let bb = b.bounds();
    if ea && eb {
        return Empty;
    }
    match op {
        BooleanOperation::Difference => {
            if ea {
                Empty
            } else if eb {
                ResultOperandA
            } else if ab
                .zip(bb)
                .is_some_and(|(ab, bb)| strictly_contained(a, b, ab, bb))
            {
                BoundsOperandA
            } else {
                Unknown
            }
        }
        BooleanOperation::Intersect => {
            if ea || eb || ab.zip(bb).and_then(|(a, b)| intersection(a, b)).is_none() {
                Empty
            } else if contained(a, b) {
                ResultOperandB
            } else if contained(b, a) {
                ResultOperandA
            } else {
                Unknown
            }
        }
        BooleanOperation::Union => {
            if ab.is_none() {
                ResultOperandB
            } else if bb.is_none() || contained(a, b) {
                ResultOperandA
            } else if contained(b, a) {
                ResultOperandB
            } else {
                UnionBounds
            }
        }
        BooleanOperation::ExclusiveOr => {
            if ea {
                ResultOperandB
            } else if eb {
                ResultOperandA
            } else if ab
                .zip(bb)
                .is_some_and(|(a, b)| intersection(a, b).is_none())
            {
                UnionBounds
            } else {
                Unknown
            }
        }
        BooleanOperation::ReverseDifference => {
            if eb {
                Empty
            } else if ea {
                ResultOperandB
            } else if ab
                .zip(bb)
                .is_some_and(|(ab, bb)| strictly_contained(b, a, bb, ab))
            {
                BoundsOperandB
            } else {
                Unknown
            }
        }
    }
}
