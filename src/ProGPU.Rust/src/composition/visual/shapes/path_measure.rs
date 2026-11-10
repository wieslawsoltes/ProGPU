//! Original CompositionPathData bounded length evidence. Raster output remains
//! analytic source subsegments; the measurement samples are never painted.
use super::*;
use crate::vector::{FillRule, evaluate_cubic, evaluate_quadratic};
const EPS: f32 = 0.00001;
#[derive(Debug)]
struct PathData {
    geometry: Arc<PathGeometry>,
    segments: Vec<MeasuredSegment>,
    length: f32,
}
/// Immutable typed composition path snapshot, including original bounded length
/// tables. Combined paths reject because their trim semantics are independent.
#[derive(Clone, Debug)]
pub struct CompositionPath(Arc<PathData>);
impl PartialEq for CompositionPath {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}
impl Eq for CompositionPath {}
impl CompositionPath {
    /// Snapshot through the original identity geometry transformation, preserving
    /// source figures and segment flags while rejecting combined source providers.
    pub fn new(source: &PathGeometry) -> Result<Self, Error> {
        if source.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let geometry = Arc::new(source.transformed(Matrix4::IDENTITY)?);
        Self::from_owned(geometry)
    }
    pub(super) fn from_owned(geometry: Arc<PathGeometry>) -> Result<Self, Error> {
        let mut segments = Vec::new();
        let mut length = 0.0;
        for (index, figure) in geometry.figures.iter().enumerate() {
            let mut current = figure.start;
            for segment in &figure.segments {
                let end = endpoint(*segment, current);
                if let Some((segment, table, segment_length)) = measure(current, *segment) {
                    segments.try_reserve(1).map_err(|_| Error::Capacity)?;
                    segments.push(MeasuredSegment {
                        figure: index,
                        filled: figure.is_filled,
                        start: current,
                        segment,
                        start_distance: length,
                        length: segment_length,
                        table,
                    });
                    length += segment_length;
                }
                current = end;
            }
            if figure.is_closed && distance_squared(current, figure.start) > EPS * EPS {
                let close_length = distance(current, figure.start);
                segments.try_reserve(1).map_err(|_| Error::Capacity)?;
                segments.push(MeasuredSegment {
                    figure: index,
                    filled: figure.is_filled,
                    start: current,
                    segment: line(figure.start),
                    start_distance: length,
                    length: close_length,
                    table: None,
                });
                length += close_length;
            }
        }
        Ok(Self(Arc::new(PathData {
            geometry,
            segments,
            length,
        })))
    }
    /// Actual immutable owned source geometry, not reconstructed measurement data.
    pub fn geometry(&self) -> Arc<PathGeometry> {
        self.0.geometry.clone()
    }
    /// Original measured total length, with source float accumulation order.
    pub fn measured_length(&self) -> f32 {
        self.0.length
    }
    /// Rebuild one wrapping trim interval with original open-figure semantics.
    pub fn trimmed(&self, mut origin: f32, mut length: f32) -> Result<PathGeometry, Error> {
        let mut result = PathGeometry {
            fill_rule: self.0.geometry.fill_rule,
            ..Default::default()
        };
        if self.0.length <= EPS || !origin.is_finite() || !length.is_finite() {
            return Ok(result);
        }
        origin -= origin.floor();
        length = length.clamp(0.0, 1.0);
        if length <= EPS {
            return Ok(result);
        }
        let start = origin * self.0.length;
        let end = start + length * self.0.length;
        self.append_interval(&mut result, start, end.min(self.0.length))?;
        if end > self.0.length + EPS {
            self.append_interval(&mut result, 0.0, end - self.0.length)?;
        }
        Ok(result)
    }
    fn append_interval(
        &self,
        result: &mut PathGeometry,
        first: f32,
        last: f32,
    ) -> Result<(), Error> {
        if last <= first + EPS {
            return Ok(());
        }
        let mut active = None;
        let mut output_end = [0.0; 2];
        let mut output_index = None;
        for measured in &self.0.segments {
            let segment_start = measured.start_distance;
            let segment_end = segment_start + measured.length;
            let overlap_start = first.max(segment_start);
            let overlap_end = last.min(segment_end);
            if overlap_end <= overlap_start + EPS {
                continue;
            }
            let first = measured.parameter(overlap_start - segment_start);
            let last = measured.parameter(overlap_end - segment_start);
            let Some((start, segment)) = slice(measured.start, measured.segment, first, last)
            else {
                continue;
            };
            if output_index.is_none()
                || active != Some(measured.figure)
                || distance_squared(output_end, start) > EPS * EPS
            {
                let mut figure = PathFigure::new(start, false);
                figure.is_filled = measured.filled;
                result.figures.try_reserve(1).map_err(|_| Error::Capacity)?;
                output_index = Some(result.figures.len());
                result.figures.push(figure);
                active = Some(measured.figure);
            }
            let output = &mut result.figures[output_index.expect("active original trim figure")];
            output
                .segments
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            output.segments.push(segment);
            output_end = endpoint(segment, start);
        }
        Ok(())
    }
    /// Original retained fill-rule metadata.
    pub fn fill_rule(&self) -> FillRule {
        self.0.geometry.fill_rule
    }
}
#[derive(Debug)]
struct MeasuredSegment {
    figure: usize,
    filled: bool,
    start: [f32; 2],
    segment: PathSegment,
    start_distance: f32,
    length: f32,
    table: Option<Box<[f32]>>,
}
impl MeasuredSegment {
    fn parameter(&self, distance: f32) -> f32 {
        let distance = distance.clamp(0.0, self.length);
        let Some(table) = &self.table else {
            return distance / self.length;
        };
        if distance <= EPS {
            return 0.0;
        }
        if distance >= self.length - EPS {
            return 1.0;
        }
        // Same midpoint binary search as the original Array.BinarySearch. An
        // exact duplicate length returns that selected midpoint, not a partition.
        let mut lower = 0_isize;
        let mut upper = table.len() as isize - 1;
        while lower <= upper {
            let middle = lower + ((upper - lower) >> 1);
            let value = table[middle as usize];
            if value == distance {
                return middle as f32 / (table.len() - 1) as f32;
            }
            if value < distance {
                lower = middle + 1;
            } else {
                upper = middle - 1;
            }
        }
        let upper = lower as usize;
        let lower = upper - 1;
        let lower_distance = table[lower];
        let span = table[upper] - lower_distance;
        let fraction = if span <= EPS {
            0.0
        } else {
            (distance - lower_distance) / span
        };
        (lower as f32 + fraction) / (table.len() - 1) as f32
    }
}
fn measure(
    start: [f32; 2],
    mut segment: PathSegment,
) -> Option<(PathSegment, Option<Box<[f32]>>, f32)> {
    let table = match segment.kind {
        PathSegmentKind::Line(_) => None,
        PathSegmentKind::Quadratic(points) => Some(length_table(32, |t| {
            evaluate_quadratic([start, points[0], points[1]], t)
        })),
        PathSegmentKind::Cubic(points) => Some(length_table(32, |t| {
            evaluate_cubic([start, points[0], points[1], points[2]], t)
        })),
        PathSegmentKind::Arc(arc) => {
            if let Some(center) = arc.center(start) {
                let count = ((center.delta.abs() / (std::f32::consts::PI / 64.0)).ceil() as i32)
                    .clamp(1, 128) as usize;
                Some(length_table(count, |t| {
                    center.evaluate(arc.rotation, center.theta + center.delta * t)
                }))
            } else {
                segment.kind = PathSegmentKind::Line(arc.end);
                None
            }
        }
        // The original composition measure deliberately does not add rational
        // curve evidence. Their full path generation remains independently owned.
        _ => return None,
    };
    let length = table.as_ref().map_or_else(
        || distance(start, endpoint(segment, start)),
        |table| table[table.len() - 1],
    );
    (length.is_finite() && length > EPS).then_some((segment, table, length))
}
fn length_table(count: usize, evaluate: impl Fn(f32) -> [f32; 2]) -> Box<[f32]> {
    let mut values = vec![0.0; count + 1];
    let mut previous = evaluate(0.0);
    let mut length = 0.0;
    for (index, value) in values.iter_mut().enumerate().skip(1) {
        let point = evaluate(index as f32 / count as f32);
        length += distance(previous, point);
        *value = length;
        previous = point;
    }
    values.into_boxed_slice()
}
fn slice(
    start: [f32; 2],
    segment: PathSegment,
    first: f32,
    last: f32,
) -> Option<([f32; 2], PathSegment)> {
    if last <= first + EPS {
        return None;
    }
    match segment.kind {
        PathSegmentKind::Line(end) => Some((
            lerp(start, end, first),
            PathSegment {
                kind: PathSegmentKind::Line(lerp(start, end, last)),
                ..segment
            },
        )),
        PathSegmentKind::Quadratic(_) | PathSegmentKind::Cubic(_) => segment
            .bezier_subsegment(start, first, last)
            .map(|v| (v.start, v.segment)),
        PathSegmentKind::Arc(arc) => arc.subarc(start, first, last).map(|(start, arc)| {
            (
                start,
                PathSegment {
                    kind: PathSegmentKind::Arc(arc),
                    ..segment
                },
            )
        }),
        _ => None,
    }
}
fn endpoint(segment: PathSegment, fallback: [f32; 2]) -> [f32; 2] {
    match segment.kind {
        PathSegmentKind::Line(p) => p,
        PathSegmentKind::Quadratic(p) => p[1],
        PathSegmentKind::Cubic(p) => p[2],
        PathSegmentKind::Arc(a) => a.end,
        _ => fallback,
    }
}
fn distance_squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let x = a[0] - b[0];
    let y = a[1] - b[1];
    x * x + y * y
}
fn distance(a: [f32; 2], b: [f32; 2]) -> f32 {
    distance_squared(a, b).sqrt()
}
