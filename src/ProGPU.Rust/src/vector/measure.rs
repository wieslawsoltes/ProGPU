//! Original contour measurements retain analytic output and bounded sample evidence.
use super::{Matrix4, PathFigure, PathGeometry, PathSegment, PathSegmentKind};
use crate::Error;
use std::{cell::RefCell, sync::Arc};
use wide::f32x4 as V;
mod curve;
use curve::Curve;
const EPS: f32 = 0.00001;
/// Original raw path verbs, including move-only and post-close command slots.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum MeasureCommand {
    /// Begin one original contour.
    Move([f32; 2]),
    /// Original line endpoint.
    Line([f32; 2]),
    /// Original quadratic control and endpoint.
    Quadratic([[f32; 2]; 2]),
    /// Original rational quadratic control/endpoint and unmodified weight.
    Conic { points: [[f32; 2]; 2], weight: f32 },
    /// Original cubic controls and endpoint.
    Cubic([[f32; 2]; 3]),
    /// Close the current contour without inventing a new Move command.
    Close,
}
/// Immutable raw source command generation, independent of path raster planning.
#[derive(Clone, Debug, Default)]
pub struct MeasuredPath {
    commands: Arc<[MeasureCommand]>,
}
impl MeasuredPath {
    /// Copy the complete original inventory once, including nonfinite/degenerate
    /// geometry whose original measurement has separate segment admission.
    pub fn new(commands: impl Into<Arc<[MeasureCommand]>>) -> Self {
        Self {
            commands: commands.into(),
        }
    }
    /// Original owned command inventory.
    pub fn commands(&self) -> &[MeasureCommand] {
        &self.commands
    }
    /// Capture original line/quad/conic/cubic figures and coalesced analytic arc
    /// runs. Rational cubics/deferred Boolean paths have no matching raw contract.
    pub fn from_geometry(source: &PathGeometry) -> Result<Self, Error> {
        Ok(Self::new(super::raw_path::commands(source)?))
    }
    /// Publish analytic original command geometry; measurement samples are never
    /// used as drawn line segments. Close remains independent figure metadata.
    pub fn to_geometry(&self, fill_rule: super::FillRule) -> Result<PathGeometry, Error> {
        let mut result = PathGeometry {
            fill_rule,
            ..Default::default()
        };
        let mut current = None;
        let mut last = [0.0; 2];
        for command in self.commands.iter() {
            if let MeasureCommand::Move(point) = *command {
                result.figures.try_reserve(1).map_err(|_| Error::Capacity)?;
                result.figures.push(PathFigure::new(point, false));
                current = Some(result.figures.len() - 1);
                last = point;
                continue;
            }
            if matches!(command, MeasureCommand::Close) {
                if let Some(index) = current.take() {
                    let f = &mut result.figures[index];
                    f.is_closed = true;
                    last = f.start;
                }
                continue;
            }
            let index = if let Some(index) = current {
                index
            } else {
                result.figures.try_reserve(1).map_err(|_| Error::Capacity)?;
                result.figures.push(PathFigure::new(last, false));
                let index = result.figures.len() - 1;
                current = Some(index);
                index
            };
            let kind = match *command {
                MeasureCommand::Line(p) => PathSegmentKind::Line(p),
                MeasureCommand::Quadratic(p) => PathSegmentKind::Quadratic(p),
                MeasureCommand::Conic { points, weight } => {
                    PathSegmentKind::RationalQuadratic { points, weight }
                }
                MeasureCommand::Cubic(p) => PathSegmentKind::Cubic(p),
                _ => unreachable!(),
            };
            let segment = PathSegment::new(kind);
            last = segment.end();
            result.figures[index]
                .segments
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            result.figures[index].segments.push(segment);
        }
        Ok(result)
    }
}
/// Reusable raw command builder for measurement/effect output.
#[derive(Clone, Debug, Default)]
pub struct MeasuredPathBuilder {
    commands: Vec<MeasureCommand>,
}
impl MeasuredPathBuilder {
    /// Append one actual raw verb without GPU work or source normalization.
    pub fn push(&mut self, command: MeasureCommand) -> Result<(), Error> {
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(command);
        Ok(())
    }
    /// Append an immutable complete source command inventory once.
    pub fn append(&mut self, path: &MeasuredPath) -> Result<(), Error> {
        self.commands
            .try_reserve(path.commands.len())
            .map_err(|_| Error::Capacity)?;
        self.commands.extend_from_slice(&path.commands);
        Ok(())
    }
    /// Whether no raw command, including Move, has been recorded.
    pub fn is_empty(&self) -> bool {
        self.commands.is_empty()
    }
    /// Raw output command count, including Move and Close slots.
    pub fn command_count(&self) -> usize {
        self.commands.len()
    }
    /// Transfer all freshly allocated storage into an immutable owner.
    pub fn finish(self) -> MeasuredPath {
        MeasuredPath::new(self.commands)
    }
}
#[derive(Clone, Copy)]
struct Sample {
    point: [f32; 2],
    distance: f32,
    segment: usize,
    parameter: f32,
}
#[derive(Clone, Copy, Default)]
struct Location {
    segment: usize,
    parameter: f32,
}
#[derive(Default)]
struct Contour {
    start: [f32; 2],
    segments: Vec<Curve>,
    samples: Vec<Sample>,
    closed: bool,
}
impl Contour {
    fn reset(&mut self, start: [f32; 2]) {
        self.start = start;
        self.segments.clear();
        self.samples.clear();
        self.closed = false;
    }
    fn length(&self) -> f32 {
        self.samples.last().map_or(0.0, |s| s.distance)
    }
    fn add(&mut self, segment: Curve, subdivisions: u32) -> Result<(), Error> {
        self.segments.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.samples
            .try_reserve(subdivisions as usize)
            .map_err(|_| Error::Capacity)?;
        let index = self.segments.len();
        self.segments.push(segment);
        let mut previous = self.samples.last().map_or(segment.start(), |s| s.point);
        let mut distance = self.length();
        for step in 1..=subdivisions {
            let parameter = step as f32 / subdivisions as f32;
            let point = segment.evaluate(parameter);
            let length = length(sub(point, previous));
            if length.is_finite() && length > EPS {
                distance += length;
                self.samples.push(Sample {
                    point,
                    distance,
                    segment: index,
                    parameter,
                });
                previous = point;
            }
        }
        Ok(())
    }
    fn close(&mut self) -> Result<(), Error> {
        if self.closed {
            return Ok(());
        }
        let current = self.segments.last().map_or(self.start, |s| s.end());
        if length_squared(sub(current, self.start)) > EPS * EPS {
            self.add(Curve::line(current, self.start), 1)?;
        }
        self.closed = true;
        Ok(())
    }
    fn locate(&self, distance: f32) -> Location {
        if self.samples.is_empty() {
            return Location::default();
        }
        if distance <= 0.0 {
            return Location {
                segment: self.samples[0].segment,
                parameter: 0.0,
            };
        }
        if distance >= self.length() {
            return Location {
                segment: self.samples.last().unwrap().segment,
                parameter: 1.0,
            };
        }
        let mut low = 0;
        let mut high = self.samples.len() - 1;
        while low < high {
            let middle = low + (high - low) / 2;
            if self.samples[middle].distance < distance {
                low = middle + 1;
            } else {
                high = middle;
            }
        }
        let upper = self.samples[low];
        let lower_distance = if low == 0 {
            0.0
        } else {
            self.samples[low - 1].distance
        };
        let lower_parameter = if low > 0 && self.samples[low - 1].segment == upper.segment {
            self.samples[low - 1].parameter
        } else {
            0.0
        };
        let span = upper.distance - lower_distance;
        let amount = if span <= EPS {
            0.0
        } else {
            (distance - lower_distance) / span
        };
        // Original float.Lerp public weighted-sum contract. Runtime fused
        // arithmetic remains an independent original-source qualification gate.
        Location {
            segment: upper.segment,
            parameter: (lower_parameter * (1.0 - amount) + upper.parameter * amount)
                .clamp(0.0, 1.0),
        }
    }
}
#[derive(Default)]
struct Storage {
    contours: Vec<Contour>,
    count: usize,
}
impl Storage {
    fn reset(&mut self) {
        for c in &mut self.contours[..self.count] {
            c.reset([0.0; 2]);
        }
        self.count = 0;
    }
    fn contour(&mut self, start: [f32; 2]) -> Result<usize, Error> {
        let index = self.count;
        if index == self.contours.len() {
            self.contours.try_reserve(1).map_err(|_| Error::Capacity)?;
            self.contours.push(Contour::default());
        }
        self.contours[index].reset(start);
        self.count += 1;
        Ok(index)
    }
    fn can_retain(&self) -> bool {
        self.contours.capacity() <= 64
            && self
                .contours
                .iter()
                .map(|c| c.segments.capacity())
                .sum::<usize>()
                <= 2048
            && self
                .contours
                .iter()
                .map(|c| c.samples.capacity())
                .sum::<usize>()
                <= 8192
    }
}
std::thread_local! {static STORAGE:RefCell<Option<Storage>>=const{RefCell::new(None)};}
/// Original contour-local measurement, binary-search lookup and analytic slicing.
/// CPU work is bounded samples per segment; cumulative lengths remain sequential,
/// while independent XY/homogeneous lanes use SIMD. No GPU is initialized.
pub struct PathMeasure {
    storage: Storage,
    current: Option<usize>,
    subdivisions: u32,
}
impl PathMeasure {
    /// Use the original sqrt precision schedule, bounded to 2..=64 samples per curve.
    pub fn new(
        path: Option<&MeasuredPath>,
        force_closed: bool,
        resolution_scale: f32,
    ) -> Result<Self, Error> {
        let scale = if resolution_scale.is_finite() && resolution_scale > 0.0 {
            resolution_scale
        } else {
            1.0
        };
        let subdivisions = (1.0 + (4.0 * scale.sqrt()).ceil()).clamp(2.0, 64.0) as u32;
        let storage = STORAGE
            .with(|pool| pool.borrow_mut().take())
            .unwrap_or_default();
        let mut result = Self {
            storage,
            current: None,
            subdivisions,
        };
        result.set_path(path, force_closed)?;
        Ok(result)
    }
    /// Replace the active source while reusing bounded contour/sample capacity.
    /// On allocation failure no partial contour becomes the active published one.
    pub fn set_path(
        &mut self,
        path: Option<&MeasuredPath>,
        force_closed: bool,
    ) -> Result<(), Error> {
        self.current = None;
        self.storage.reset();
        let Some(path) = path else {
            return Ok(());
        };
        let mut contour = None;
        let mut current = [0.0; 2];
        for command in path.commands.iter() {
            if let MeasureCommand::Move(point) = *command {
                if force_closed {
                    if let Some(index) = contour {
                        self.storage.contours[index].close()?;
                    }
                }
                current = point;
                contour = Some(self.storage.contour(point)?);
                continue;
            }
            if matches!(command, MeasureCommand::Close) {
                if let Some(index) = contour {
                    self.storage.contours[index].close()?;
                }
                continue;
            }
            let index = match contour {
                Some(index) => index,
                None => {
                    let i = self.storage.contour(current)?;
                    contour = Some(i);
                    i
                }
            };
            let curve = Curve::from_command(current, *command).ok_or(Error::InvalidInput)?;
            let count = if matches!(command, MeasureCommand::Line(_)) {
                1
            } else {
                self.subdivisions
            };
            self.storage.contours[index].add(curve, count)?;
            current = curve.end();
        }
        if force_closed {
            if let Some(index) = contour {
                self.storage.contours[index].close()?;
            }
        }
        self.current = (self.storage.count != 0).then_some(0);
        Ok(())
    }
    /// Original current contour's measured cumulative length, or zero.
    pub fn length(&self) -> f32 {
        self.current
            .map_or(0.0, |i| self.storage.contours[i].length())
    }
    /// Original current closure flag, including explicit force-close.
    pub fn is_closed(&self) -> bool {
        self.current
            .is_some_and(|i| self.storage.contours[i].closed)
    }
    /// Advance in original contour order; exhaustion clears the current contour.
    pub fn next_contour(&mut self) -> bool {
        self.current = self
            .current
            .and_then(|i| (i + 1 < self.storage.count).then_some(i + 1));
        self.current.is_some()
    }
    /// Original clamped-distance evaluation and normalized nondegenerate tangent.
    pub fn position_and_tangent(&self, distance: f32) -> Option<([f32; 2], [f32; 2])> {
        let contour = self.storage.contours.get(self.current?)?;
        if contour.length() <= EPS || !distance.is_finite() {
            return None;
        }
        let location = contour.locate(distance.clamp(0.0, contour.length()));
        let curve = contour.segments[location.segment];
        let point = curve.evaluate(location.parameter);
        let tangent = curve.tangent(location.parameter);
        let square = length_squared(tangent);
        if square <= EPS * EPS {
            return None;
        }
        let tangent = div(tangent, square.sqrt());
        (length_squared(tangent) > EPS * EPS).then_some((point, tangent))
    }
    /// Original position query; failed sampling remains distinct from [0,0].
    pub fn position(&self, distance: f32) -> Option<[f32; 2]> {
        self.position_and_tangent(distance).map(|v| v.0)
    }
    /// Original tangent query; degenerate input remains unavailable.
    pub fn tangent(&self, distance: f32) -> Option<[f32; 2]> {
        self.position_and_tangent(distance).map(|v| v.1)
    }
    /// Original flag bits 1=position, 2=tangent; unknown bits are ignored.
    pub fn matrix(&self, distance: f32, flags: u32) -> Option<Matrix4> {
        let (point, tangent) = self.position_and_tangent(distance)?;
        let mut matrix = Matrix4::IDENTITY;
        if flags & 2 != 0 {
            matrix.0[0][0] = tangent[0];
            matrix.0[0][1] = tangent[1];
            matrix.0[1][0] = -tangent[1];
            matrix.0[1][1] = tangent[0];
        }
        if flags & 1 != 0 {
            matrix.0[3][0] = point[0];
            matrix.0[3][1] = point[1];
        }
        Some(matrix)
    }
    /// Original open analytic interval, including equal-range point semantics.
    pub fn segment(
        &self,
        start: f32,
        stop: f32,
        start_with_move: bool,
    ) -> Result<Option<MeasuredPath>, Error> {
        let mut output = MeasuredPathBuilder::default();
        if self.append_segment(start, stop, &mut output, start_with_move)? {
            Ok(Some(output.finish()))
        } else {
            Ok(None)
        }
    }
    /// Append original sliced verbs; failure preserves the existing output bytes.
    pub fn append_segment(
        &self,
        start: f32,
        stop: f32,
        output: &mut MeasuredPathBuilder,
        start_with_move: bool,
    ) -> Result<bool, Error> {
        let Some(index) = self.current else {
            return Ok(false);
        };
        let contour = &self.storage.contours[index];
        let length = contour.length();
        if length <= EPS
            || !start.is_finite()
            || !stop.is_finite()
            || start > stop
            || stop < 0.0
            || start > length
        {
            return Ok(false);
        }
        let start = start.clamp(0.0, length);
        let stop = stop.clamp(0.0, length);
        let first = contour.locate(start);
        let last = contour.locate(stop);
        let start_point = contour.segments[first.segment].evaluate(first.parameter);
        let checkpoint = output.commands.len();
        let result = (|| {
            let empty = output.is_empty();
            if start_with_move {
                output.push(MeasureCommand::Move(start_point))?;
            } else if !empty {
                output.push(MeasureCommand::Line(start_point))?;
            }
            if (stop - start).abs() <= EPS {
                if start_with_move || empty {
                    output.push(MeasureCommand::Line(start_point))?;
                }
                return Ok(true);
            }
            let mut first = first;
            if first.parameter >= 1.0 - EPS && first.segment + 1 < contour.segments.len() {
                first.segment += 1;
                first.parameter = 0.0;
            }
            for index in first.segment..=last.segment {
                let a = if index == first.segment {
                    first.parameter
                } else {
                    0.0
                };
                let b = if index == last.segment {
                    last.parameter
                } else {
                    1.0
                };
                if b > a + EPS {
                    output.push(contour.segments[index].slice(a, b))?;
                }
            }
            Ok(true)
        })();
        if result.is_err() {
            output.commands.truncate(checkpoint);
        }
        result
    }
}
impl Drop for PathMeasure {
    fn drop(&mut self) {
        let mut storage = std::mem::take(&mut self.storage);
        storage.reset();
        if storage.can_retain() {
            STORAGE.with(|pool| {
                if let Ok(mut pool) = pool.try_borrow_mut() {
                    if pool.is_none() {
                        *pool = Some(storage);
                    }
                }
            });
        }
    }
}
fn vector(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn xy(v: V) -> [f32; 2] {
    let [x, y, _, _] = v.to_array();
    [x, y]
}
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    xy(vector(a) - vector(b))
}
fn div(a: [f32; 2], b: f32) -> [f32; 2] {
    xy(vector(a) / V::splat(b))
}
fn length_squared(p: [f32; 2]) -> f32 {
    let v = (vector(p) * vector(p)).to_array();
    v[0] + v[1]
}
fn length(p: [f32; 2]) -> f32 {
    length_squared(p).sqrt()
}
