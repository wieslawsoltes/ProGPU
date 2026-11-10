//! Original SKPath rational-conic source identity and its sixteen actual render
//! quadratics. Raw iteration and reversal consume the source; rendering does not.
use super::{PathFigure, PathSegment, PathSegmentKind, SweepDirection};
use crate::Error;
use std::sync::Arc;
use wide::f32x4 as V;
pub(super) const SPANS: usize = 16;

/// Original conic retained by the first polynomial rendering span. Private fields
/// prevent caller-created zero-length groups or unrelated source substitutions.
#[derive(Clone, Debug, PartialEq)]
pub struct RetainedConic {
    points: [[f32; 2]; 3],
    weight: f32,
}
impl RetainedConic {
    /// Actual original start, before render approximation.
    pub fn start(&self) -> [f32; 2] {
        self.points[0]
    }
    /// Actual original rational control.
    pub fn control(&self) -> [f32; 2] {
        self.points[1]
    }
    /// Actual original endpoint.
    pub fn end(&self) -> [f32; 2] {
        self.points[2]
    }
    /// Original unmodified middle weight.
    pub fn weight(&self) -> f32 {
        self.weight
    }
    /// Original fixed number of polynomial render spans.
    pub fn span_count(&self) -> usize {
        SPANS
    }
}
impl PathSegment {
    pub(crate) fn map_conic(
        &self,
        map: impl FnMut([f32; 2]) -> [f32; 2],
    ) -> Option<Arc<RetainedConic>> {
        self.source_conic.as_ref().map(|source| {
            Arc::new(RetainedConic {
                points: source.points.map(map),
                weight: source.weight,
            })
        })
    }
}
pub(super) fn append(
    figure: &mut PathFigure,
    points: [[f32; 2]; 3],
    weight: f32,
) -> Result<(), Error> {
    if !weight.is_finite() || weight <= 0.0 || (weight - 1.0).abs() <= 0.00001 {
        figure
            .segments
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        figure
            .segments
            .push(PathSegment::new(PathSegmentKind::Quadratic([
                points[1], points[2],
            ])));
        return Ok(());
    }
    figure
        .segments
        .try_reserve(SPANS)
        .map_err(|_| Error::Capacity)?;
    for index in 0..SPANS {
        let first = index as f32 / SPANS as f32;
        let last = (index as f32 + 1.0) / SPANS as f32;
        let start = evaluate(points, weight, first);
        let end = evaluate(points, weight, last);
        let mid = evaluate(points, weight, (first + last) * 0.5);
        let control = V::splat(2.0) * mid - V::splat(0.5) * (start + end);
        let mut segment = PathSegment::new(PathSegmentKind::Quadratic([xy(control), xy(end)]));
        if index == 0 {
            segment.source_conic = Some(Arc::new(RetainedConic { points, weight }));
        }
        figure.segments.push(segment);
    }
    Ok(())
}
fn evaluate(p: [[f32; 2]; 3], weight: f32, t: f32) -> V {
    let inverse = 1.0 - t;
    let a = inverse * inverse;
    let b = 2.0 * weight * inverse * t;
    let c = t * t;
    (V::splat(a) * vector(p[0]) + V::splat(b) * vector(p[1]) + V::splat(c) * vector(p[2]))
        / V::splat(a + b + c)
}
fn vector(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn xy(v: V) -> [f32; 2] {
    let [x, y, _, _] = v.to_array();
    [x, y]
}

/// Original recursive rational subdivision into `2^pow2` quadratic control/end
/// pairs, preceded by the exact original start. Depth is bounded to 0..=20;
/// weights retain the original arithmetic, including nonfinite results.
pub fn conic_to_quadratics(
    points: [[f32; 2]; 3],
    weight: f32,
    pow2: i32,
) -> Result<Vec<[f32; 2]>, Error> {
    if !(0..=20).contains(&pow2) {
        return Err(Error::InvalidInput);
    }
    let count = 1_usize << pow2;
    let mut output = Vec::new();
    output
        .try_reserve_exact(count * 2 + 1)
        .map_err(|_| Error::Capacity)?;
    output.resize(count * 2 + 1, [0.0; 2]);
    output[0] = points[0];
    chop(points.map(vector), weight, pow2 as u32, &mut output[1..]);
    Ok(output)
}
fn chop(p: [V; 3], weight: f32, depth: u32, output: &mut [[f32; 2]]) {
    if depth == 0 {
        output[0] = xy(p[1]);
        output[1] = xy(p[2]);
        return;
    }
    let scale = 1.0 / (1.0 + weight);
    let left = (p[0] + V::splat(weight) * p[1]) * V::splat(scale);
    let mid = (p[0] + V::splat(2.0 * weight) * p[1] + p[2]) * V::splat(scale * 0.5);
    let right = (V::splat(weight) * p[1] + p[2]) * V::splat(scale);
    let next_weight = ((1.0 + weight) * 0.5).sqrt();
    let half = output.len() / 2;
    let (first, last) = output.split_at_mut(half);
    chop([p[0], left, mid], next_weight, depth - 1, first);
    chop([mid, right, p[2]], next_weight, depth - 1, last);
}

pub(super) fn reverse(source: &PathFigure) -> Result<PathFigure, Error> {
    let mut starts = Vec::new();
    starts
        .try_reserve_exact(source.segments.len())
        .map_err(|_| Error::Capacity)?;
    let mut current = source.start;
    for segment in &source.segments {
        starts.push(current);
        current = segment.end();
    }
    let mut output = PathFigure::new(current, source.is_closed);
    output.is_filled = source.is_filled;
    output.start_cap = source.end_cap;
    output.end_cap = source.start_cap;
    output
        .segments
        .try_reserve_exact(source.segments.len())
        .map_err(|_| Error::Capacity)?;
    let mut remaining = source.segments.len();
    while remaining > 0 {
        let index = remaining - 1;
        let group = (index.saturating_sub(SPANS - 1)..=index)
            .rev()
            .find_map(|first| {
                source.segments[first]
                    .source_conic
                    .as_ref()
                    .filter(|conic| first + conic.span_count() > index)
                    .map(|conic| (first, conic))
            });
        if let Some((first, conic)) = group {
            append(
                &mut output,
                [conic.end(), conic.control(), conic.start()],
                conic.weight(),
            )?;
            remaining = first;
            continue;
        }
        let segment = &source.segments[index];
        let end = starts[index];
        let kind = match segment.kind {
            PathSegmentKind::Line(_) => PathSegmentKind::Line(end),
            PathSegmentKind::Quadratic([control, _]) => PathSegmentKind::Quadratic([control, end]),
            PathSegmentKind::Cubic([a, b, _]) => PathSegmentKind::Cubic([b, a, end]),
            PathSegmentKind::Arc(mut arc) => {
                arc.end = end;
                arc.sweep = match arc.sweep {
                    SweepDirection::Clockwise => SweepDirection::Counterclockwise,
                    SweepDirection::Counterclockwise => SweepDirection::Clockwise,
                };
                PathSegmentKind::Arc(arc)
            }
            _ => return Err(Error::UnsupportedExecution),
        };
        output.segments.push(PathSegment {
            kind,
            source_conic: None,
            ..segment.clone()
        });
        remaining -= 1;
    }
    Ok(output)
}
