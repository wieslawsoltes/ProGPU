//! Original raw/force-close iterators and compatible analytic arc-run conversion.
use super::{MeasureCommand, PathFigure, PathGeometry, PathSegment, PathSegmentKind};
use crate::Error;
use std::f32::consts::PI;
use wide::f32x4 as V;
const EPS: f32 = 0.00001;
/// Original raw verb order; Done does not change caller point slots.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PathVerb {
    /// Original contour start.
    Move,
    /// Straight endpoint.
    Line,
    /// Quadratic controls.
    Quadratic,
    /// Rational quadratic controls and independent weight.
    Conic,
    /// Cubic controls.
    Cubic,
    /// Contour closure.
    Close,
    /// End of immutable command inventory.
    Done,
}
#[derive(Clone, Copy)]
struct Operation {
    verb: PathVerb,
    points: [[f32; 2]; 4],
    weight: f32,
    closed: bool,
    synthetic: bool,
}
impl Operation {
    fn new(verb: PathVerb, points: [[f32; 2]; 4], closed: bool) -> Self {
        Self {
            verb,
            points,
            weight: 0.0,
            closed,
            synthetic: false,
        }
    }
    fn command(self) -> MeasureCommand {
        match self.verb {
            PathVerb::Move => MeasureCommand::Move(self.points[0]),
            PathVerb::Line => MeasureCommand::Line(self.points[1]),
            PathVerb::Quadratic => MeasureCommand::Quadratic([self.points[1], self.points[2]]),
            PathVerb::Conic => MeasureCommand::Conic {
                points: [self.points[1], self.points[2]],
                weight: self.weight,
            },
            PathVerb::Cubic => {
                MeasureCommand::Cubic([self.points[1], self.points[2], self.points[3]])
            }
            PathVerb::Close | PathVerb::Done => MeasureCommand::Close,
        }
    }
    fn write(self, points: &mut [[f32; 2]; 4], write_close: bool) {
        let count = match self.verb {
            PathVerb::Move => 1,
            PathVerb::Line => 2,
            PathVerb::Quadratic | PathVerb::Conic => 3,
            PathVerb::Cubic => 4,
            PathVerb::Close if write_close => 1,
            _ => 0,
        };
        points[..count].copy_from_slice(&self.points[..count]);
    }
}
fn push(out: &mut Vec<Operation>, operation: Operation) -> Result<(), Error> {
    out.try_reserve(1).map_err(|_| Error::Capacity)?;
    out.push(operation);
    Ok(())
}
// SKPath.CloneFigure performs the addition even for a zero offset. Retain that
// arithmetic (including signed zero) and all independent figure/segment flags.
pub(super) fn copy_figure(figure: &PathFigure, offset: [f32; 2]) -> Result<PathFigure, Error> {
    let map = |p: [f32; 2]| {
        xy(V::from([p[0], p[1], 0.0, 0.0]) + V::from([offset[0], offset[1], 0.0, 0.0]))
    };
    let mut result = PathFigure::new(map(figure.start), figure.is_closed);
    result.is_filled = figure.is_filled;
    result.start_cap = figure.start_cap;
    result.end_cap = figure.end_cap;
    result
        .segments
        .try_reserve_exact(figure.segments.len())
        .map_err(|_| Error::Capacity)?;
    for segment in &figure.segments {
        let kind = match segment.kind {
            PathSegmentKind::Line(p) => PathSegmentKind::Line(map(p)),
            PathSegmentKind::Quadratic(p) => PathSegmentKind::Quadratic(p.map(map)),
            PathSegmentKind::Cubic(p) => PathSegmentKind::Cubic(p.map(map)),
            PathSegmentKind::RationalQuadratic { points, weight } => {
                PathSegmentKind::RationalQuadratic {
                    points: points.map(map),
                    weight,
                }
            }
            PathSegmentKind::Arc(mut arc) => {
                arc.end = map(arc.end);
                PathSegmentKind::Arc(arc)
            }
            PathSegmentKind::RationalCubic { .. } => return Err(Error::UnsupportedExecution),
        };
        result.segments.push(PathSegment { kind, ..*segment });
    }
    Ok(result)
}
fn raw_figure(figure: &PathFigure, out: &mut Vec<Operation>) -> Result<(), Error> {
    let mut current = figure.start;
    let closed = figure.is_closed;
    push(
        out,
        Operation::new(
            PathVerb::Move,
            [current, [0.0; 2], [0.0; 2], [0.0; 2]],
            closed,
        ),
    )?;
    let mut index = 0;
    while index < figure.segments.len() {
        let command = figure.segments[index];
        let mut operation = match command.kind {
            PathSegmentKind::Line(p) => {
                Operation::new(PathVerb::Line, [current, p, [0.0; 2], [0.0; 2]], closed)
            }
            PathSegmentKind::Quadratic([control, end]) => Operation::new(
                PathVerb::Quadratic,
                [current, control, end, [0.0; 2]],
                closed,
            ),
            PathSegmentKind::RationalQuadratic {
                points: [control, end],
                weight,
            } => {
                let mut v =
                    Operation::new(PathVerb::Conic, [current, control, end, [0.0; 2]], closed);
                v.weight = weight;
                v
            }
            PathSegmentKind::Cubic([a, b, end]) => {
                Operation::new(PathVerb::Cubic, [current, a, b, end], closed)
            }
            PathSegmentKind::Arc(_) => {
                let (consumed, end) = arc_run(figure, index, current, out)?;
                index += consumed;
                current = end;
                continue;
            }
            PathSegmentKind::RationalCubic { .. } => return Err(Error::UnsupportedExecution),
        };
        operation.closed = closed;
        push(out, operation)?;
        current = command.end();
        index += 1;
    }
    if closed {
        push(
            out,
            Operation::new(
                PathVerb::Close,
                [current, figure.start, [0.0; 2], [0.0; 2]],
                true,
            ),
        )?;
    }
    Ok(())
}
fn arc_run(
    figure: &PathFigure,
    first: usize,
    start: [f32; 2],
    out: &mut Vec<Operation>,
) -> Result<(usize, [f32; 2]), Error> {
    let PathSegmentKind::Arc(arc) = figure.segments[first].kind else {
        return Err(Error::ResourceDomain);
    };
    let Some(center) = arc.center(start) else {
        push(
            out,
            Operation::new(
                PathVerb::Line,
                [start, arc.end, [0.0; 2], [0.0; 2]],
                figure.is_closed,
            ),
        )?;
        return Ok((1, arc.end));
    };
    let mut delta = center.delta;
    let mut consumed = 1;
    let mut end = arc.end;
    while let Some(segment) = figure.segments.get(first + consumed) {
        let PathSegmentKind::Arc(next) = segment.kind else {
            break;
        };
        if next.sweep != arc.sweep || !((next.rotation - arc.rotation).abs() <= 0.0001) {
            break;
        }
        let Some(other) = next.center(end) else {
            break;
        };
        if !(squared(center.center, other.center) <= 0.0001
            && (center.radii[0] - other.radii[0]).abs() <= 0.0001
            && (center.radii[1] - other.radii[1]).abs() <= 0.0001)
        {
            break;
        }
        delta += other.delta;
        end = next.end;
        consumed += 1;
    }
    let quarters = delta.abs() / (PI * 0.5);
    let count = (quarters - 0.00001).ceil().max(1.0);
    if !count.is_finite() || count > 16_777_216.0 {
        return Err(Error::Capacity);
    }
    out.try_reserve(count as usize)
        .map_err(|_| Error::Capacity)?;
    let rotation = arc.rotation * (PI / 180.0);
    let cosine = rotation.cos();
    let sine = rotation.sin();
    let axis_x = V::from([cosine * center.radii[0], sine * center.radii[0], 0.0, 0.0]);
    let axis_y = V::from([-sine * center.radii[1], cosine * center.radii[1], 0.0, 0.0]);
    let c = V::from([center.center[0], center.center[1], 0.0, 0.0]);
    let mut current = start;
    let mut theta = center.theta;
    let mut remaining = delta;
    for index in 0..count as usize {
        let step = remaining.abs().min(PI * 0.5).copysign(remaining);
        let last = theta + step;
        let mid = theta + step * 0.5;
        let weight = (step * 0.5).cos();
        let control = xy(c
            + axis_x * V::splat(snap(mid.cos() / weight))
            + axis_y * V::splat(snap(mid.sin() / weight)));
        let endpoint = if index + 1 == count as usize {
            end
        } else {
            xy(c + axis_x * V::splat(snap(last.cos())) + axis_y * V::splat(snap(last.sin())))
        };
        let mut operation = Operation::new(
            PathVerb::Conic,
            [current, control, endpoint, [0.0; 2]],
            figure.is_closed,
        );
        operation.weight = weight;
        out.push(operation);
        current = endpoint;
        theta = last;
        remaining -= step;
    }
    Ok((consumed, end))
}
fn snap(v: f32) -> f32 {
    if v.abs() <= EPS {
        0.0
    } else if (v - 1.0).abs() <= EPS {
        1.0
    } else if (v + 1.0).abs() <= EPS {
        -1.0
    } else {
        v
    }
}
fn squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = (V::from([a[0], a[1], 0.0, 0.0]) - V::from([b[0], b[1], 0.0, 0.0])).to_array();
    d[0] * d[0] + d[1] * d[1]
}
fn xy(v: V) -> [f32; 2] {
    let [x, y, _, _] = v.to_array();
    [x, y]
}
fn raw(source: &PathGeometry) -> Result<Vec<Operation>, Error> {
    if source.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    let mut operations = Vec::new();
    for figure in &source.figures {
        raw_figure(figure, &mut operations)?;
    }
    Ok(operations)
}
pub(super) fn commands(source: &PathGeometry) -> Result<Vec<MeasureCommand>, Error> {
    let operations = raw(source)?;
    let mut commands = Vec::new();
    commands
        .try_reserve_exact(operations.len())
        .map_err(|_| Error::Capacity)?;
    commands.extend(operations.into_iter().map(Operation::command));
    Ok(commands)
}
/// Original immutable raw iterator: Close writes no points, and conic weight is
/// retained across all following nonconic verbs and Done.
pub struct RawPathIterator {
    operations: Vec<Operation>,
    index: usize,
    weight: f32,
}
impl RawPathIterator {
    /// Snapshot source figures and coalesce compatible arcs into exact conics.
    pub fn new(source: &PathGeometry) -> Result<Self, Error> {
        Ok(Self {
            operations: raw(source)?,
            index: 0,
            weight: 0.0,
        })
    }
    /// Original current conic weight, initially zero.
    pub fn conic_weight(&self) -> f32 {
        self.weight
    }
    /// Inspect the next verb without advancing or changing caller state.
    pub fn peek(&self) -> PathVerb {
        self.operations
            .get(self.index)
            .map_or(PathVerb::Done, |o| o.verb)
    }
    /// Write only the original verb's defined point slots into four caller slots.
    pub fn next_points(&mut self, points: &mut [[f32; 2]; 4]) -> PathVerb {
        let Some(operation) = self.operations.get(self.index).copied() else {
            return PathVerb::Done;
        };
        self.index += 1;
        operation.write(points, false);
        if operation.verb == PathVerb::Conic {
            self.weight = operation.weight;
        }
        operation.verb
    }
}
/// Original filtered iterator with explicit/synthetic close-line ownership.
pub struct PathIterator {
    operations: Vec<Operation>,
    index: usize,
    weight: f32,
    close_line: bool,
    close_contour: bool,
}
impl PathIterator {
    /// Preserve original move-only filtering and force-close contour policy.
    pub fn new(source: &PathGeometry, force_close: bool) -> Result<Self, Error> {
        if source.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let mut operations = Vec::new();
        for figure in &source.figures {
            if figure.segments.is_empty() {
                if figure.is_closed {
                    push(
                        &mut operations,
                        Operation::new(
                            PathVerb::Move,
                            [figure.start, [0.0; 2], [0.0; 2], [0.0; 2]],
                            true,
                        ),
                    )?;
                    push(
                        &mut operations,
                        Operation::new(
                            PathVerb::Close,
                            [figure.start, figure.start, [0.0; 2], [0.0; 2]],
                            true,
                        ),
                    )?;
                }
                continue;
            }
            let first = operations.len();
            raw_figure(&copy_figure(figure, [0.0; 2])?, &mut operations)?;
            if operations.last().is_some_and(|o| o.verb == PathVerb::Close) {
                operations.pop();
            }
            let closes = figure.is_closed || force_close;
            if force_close {
                for operation in &mut operations[first..] {
                    operation.closed = true;
                }
            }
            if closes {
                let end = figure.segments.last().map_or(figure.start, |s| s.end());
                let synthetic = !(squared(end, figure.start) <= EPS * EPS);
                if synthetic {
                    let mut operation = Operation::new(
                        PathVerb::Line,
                        [end, figure.start, [0.0; 2], [0.0; 2]],
                        true,
                    );
                    operation.synthetic = true;
                    push(&mut operations, operation)?;
                }
                let mut operation = Operation::new(
                    PathVerb::Close,
                    [figure.start, figure.start, [0.0; 2], [0.0; 2]],
                    true,
                );
                operation.synthetic = synthetic;
                push(&mut operations, operation)?;
            }
        }
        Ok(Self {
            operations,
            index: 0,
            weight: 0.0,
            close_line: false,
            close_contour: false,
        })
    }
    /// Original retained conic weight.
    pub fn conic_weight(&self) -> f32 {
        self.weight
    }
    /// Whether the last operation is the original synthetic close line/Close pair.
    pub fn is_close_line(&self) -> bool {
        self.close_line
    }
    /// Original close-contour flag; Close itself reports false.
    pub fn is_close_contour(&self) -> bool {
        self.close_contour
    }
    /// Write exactly the defined slots; Done clears only the close-status flags.
    pub fn next_points(&mut self, points: &mut [[f32; 2]; 4]) -> PathVerb {
        let Some(operation) = self.operations.get(self.index).copied() else {
            self.close_line = false;
            self.close_contour = false;
            return PathVerb::Done;
        };
        self.index += 1;
        operation.write(points, true);
        if operation.verb == PathVerb::Conic {
            self.weight = operation.weight;
        }
        self.close_line = operation.synthetic;
        self.close_contour = operation.closed && operation.verb != PathVerb::Close;
        operation.verb
    }
}
