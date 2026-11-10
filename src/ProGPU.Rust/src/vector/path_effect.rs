//! Original nine-family path effects; generated output stays analytic and owned.
use super::{
    FillRule, GeometryTolerance, Matrix4, MeasureCommand as C, MeasuredPath,
    MeasuredPathBuilder as Builder, PathGeometry, PathMeasure,
};
use crate::Error;
use std::sync::Arc;
use wide::f32x4 as V;
mod lattice;
pub use lattice::EffectMatrix;
const EPS: f32 = 0.00001;
const LIMIT: usize = 1_000_000;
/// Original one-dimensional stamp placement.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PathStampStyle {
    /// Translate without rotating controls.
    Translate,
    /// Rotate using the original measured tangent.
    Rotate,
    /// Map every original control independently onto the contour.
    Morph,
}
/// Original trim selection policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrimMode {
    /// Retain the requested possibly wrapping interval.
    Normal,
    /// Retain its original complement.
    Inverted,
}
/// Independent original paint-style adjustment from generated effect geometry.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EffectPaintStyle {
    /// Draw generated filled stamps.
    Fill,
    /// Stroke generated lattice spans.
    Stroke,
}
/// Original optional style/width overrides; composition merges outer after inner.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PaintAdjustment {
    /// Optional selected paint style.
    pub style: Option<EffectPaintStyle>,
    /// Optional original stroke width, including zero.
    pub stroke_width: Option<f32>,
}
/// Original small-inline dash inventory; immutable overflow only above four slots.
#[derive(Clone, Debug)]
pub struct DashIntervals {
    small: [f32; 4],
    count: usize,
    large: Option<Arc<[f32]>>,
}
impl DashIntervals {
    /// Copy original values without preempting evaluator rejection policy.
    pub fn new(values: &[f32]) -> Self {
        let mut result = Self {
            small: [0.0; 4],
            count: values.len(),
            large: None,
        };
        if values.len() <= 4 {
            result.small[..values.len()].copy_from_slice(values);
        } else {
            result.large = Some(Arc::from(values));
        }
        result
    }
    /// Original exact interval inventory.
    pub fn values(&self) -> &[f32] {
        self.large
            .as_deref()
            .unwrap_or(&self.small[..self.count.min(4)])
    }
}
/// Immutable original effect descriptors. Scalar parameters retain invalid values
/// so factories and evaluation preserve their separate original admission rules.
#[derive(Clone, Debug)]
pub enum PathEffectKind {
    /// Original ordered on/off lengths and cyclic phase.
    Dash {
        /// Complete interval generation.
        intervals: DashIntervals,
        /// Original phase.
        phase: f32,
    },
    /// Independent immutable stamp owner and traversal parameters.
    Path1D {
        /// Stamp source.
        stamp: PathEffectSource,
        /// Original advance.
        advance: f32,
        /// Original phase.
        phase: f32,
        /// Original placement policy.
        style: PathStampStyle,
    },
    /// Original full lattice matrix and stroke width.
    Line2D {
        /// Original width.
        width: f32,
        /// Original lattice.
        matrix: EffectMatrix,
    },
    /// Original lattice-stamped filled path.
    Path2D {
        /// Original lattice.
        matrix: EffectMatrix,
        /// Original stamp.
        stamp: PathEffectSource,
    },
    /// Evaluate inner, then outer on its generated output.
    Compose {
        /// Outer effect.
        outer: PathEffect,
        /// Inner effect.
        inner: PathEffect,
    },
    /// Original line-figure corner trimming.
    Corner(f32),
    /// Original sequential xorshift displacement.
    Discrete {
        /// Requested segment length.
        segment_length: f32,
        /// Maximum normal displacement.
        deviation: f32,
        /// Original optional seed.
        seed: u32,
    },
    /// Preserve first then second output order and source fill rule.
    Sum {
        /// First effect.
        first: PathEffect,
        /// Second effect.
        second: PathEffect,
    },
    /// Original contour-relative trim.
    Trim {
        /// Original normalized start.
        start: f32,
        /// Original normalized stop.
        stop: f32,
        /// Original complement selection.
        mode: TrimMode,
    },
}
/// Complete immutable path owner, retaining inverse fill independently of geometry.
#[derive(Clone, Debug)]
pub struct PathEffectSource {
    /// Original analytic command storage.
    path: MeasuredPath,
    geometry: Option<Arc<PathGeometry>>,
    /// Original nonzero/evenodd selection.
    fill_rule: FillRule,
    /// Original inverse-fill policy for source hit admission.
    inverse: bool,
}
impl PathEffectSource {
    fn from_owned_geometry(geometry: PathGeometry, inverse: bool) -> Result<Self, Error> {
        Ok(Self {
            path: MeasuredPath::from_geometry(&geometry)?,
            fill_rule: geometry.fill_rule,
            geometry: Some(Arc::new(geometry)),
            inverse,
        })
    }
    /// Retain original raw verbs and independent fill policy.
    pub fn new(path: MeasuredPath, fill_rule: FillRule, inverse: bool) -> Self {
        Self {
            path,
            fill_rule,
            inverse,
            geometry: None,
        }
    }
    /// Original immutable raw verb owner.
    pub fn path(&self) -> &MeasuredPath {
        &self.path
    }
    /// Original source fill rule.
    pub fn fill_rule(&self) -> FillRule {
        self.fill_rule
    }
    /// Original independent inverse-fill policy.
    pub fn is_inverse(&self) -> bool {
        self.inverse
    }
    /// Snapshot ordinary typed figures once, retaining original analytic arc-run
    /// conics. Rational cubics/deferred Boolean paths have no matching raw contract.
    pub fn from_geometry(geometry: &PathGeometry, inverse: bool) -> Result<Self, Error> {
        Ok(Self {
            path: MeasuredPath::from_geometry(geometry)?,
            geometry: Some(Arc::new(geometry.clone())),
            fill_rule: geometry.fill_rule,
            inverse,
        })
    }
    /// Retain generated analytic geometry. Inverse fill stays in this source owner
    /// and must be applied by an actual bounded consumer, never lost in this view.
    pub fn geometry(&self) -> Result<PathGeometry, Error> {
        self.geometry.as_ref().map_or_else(
            || self.path.to_geometry(self.fill_rule),
            |value| Ok(value.as_ref().clone()),
        )
    }
    fn empty_like(&self) -> Self {
        Self {
            path: MeasuredPath::default(),
            geometry: None,
            fill_rule: self.fill_rule,
            inverse: self.inverse,
        }
    }
}
#[derive(Debug)]
struct Effect {
    kind: PathEffectKind,
    depth: u16,
}
/// Immutable owned path-effect graph; clones retain the exact stamp generations.
#[derive(Clone, Debug)]
pub struct PathEffect(Arc<Effect>);
/// Original evaluation status, output and independent paint metadata.
#[derive(Clone, Debug)]
pub struct PathEffectResult {
    /// Original success flag, separate from a valid empty output.
    pub applied: bool,
    /// Actual output generation, including original failure fallback.
    pub output: PathEffectSource,
    /// Style/width policy evaluated even when geometry rejects.
    pub paint: PaintAdjustment,
}
impl PathEffect {
    /// Retain descriptor and whole child graph under a fixed 256-level bound.
    pub fn new(kind: PathEffectKind) -> Result<Self, Error> {
        let child = match &kind {
            PathEffectKind::Compose { outer, inner } => outer.0.depth.max(inner.0.depth),
            PathEffectKind::Sum { first, second } => first.0.depth.max(second.0.depth),
            _ => 0,
        };
        let depth = child.checked_add(1).ok_or(Error::Capacity)?;
        if depth > 256 {
            return Err(Error::Capacity);
        }
        Ok(Self(Arc::new(Effect { kind, depth })))
    }
    /// Original immutable descriptor inventory.
    pub fn kind(&self) -> &PathEffectKind {
        &self.0.kind
    }
    /// Original implicit small-inline dash factory.
    pub fn dash(intervals: &[f32], phase: f32) -> Result<Self, Error> {
        Self::new(PathEffectKind::Dash {
            intervals: DashIntervals::new(intervals),
            phase,
        })
    }
    /// Evaluate with original per-contour generation guards and no GPU/readback.
    /// Structural control and cumulative distances are sequential; independent
    /// vector components use SIMD. No performance/quality qualification is implied.
    pub fn apply(
        &self,
        source: &PathEffectSource,
        resolution_scale: f32,
    ) -> Result<PathEffectResult, Error> {
        let scale = if resolution_scale.is_finite() && resolution_scale > 0.0 {
            resolution_scale
        } else {
            1.0
        };
        let (applied, output) = self.run(source, scale)?;
        Ok(PathEffectResult {
            applied,
            output,
            paint: self.paint_adjustment(),
        })
    }
    fn paint_adjustment(&self) -> PaintAdjustment {
        match self.kind() {
            PathEffectKind::Line2D { width, .. } => PaintAdjustment {
                style: Some(EffectPaintStyle::Stroke),
                stroke_width: Some(*width),
            },
            PathEffectKind::Path2D { .. } => PaintAdjustment {
                style: Some(EffectPaintStyle::Fill),
                stroke_width: None,
            },
            PathEffectKind::Compose { outer, inner } => {
                let a = inner.paint_adjustment();
                let b = outer.paint_adjustment();
                PaintAdjustment {
                    style: b.style.or(a.style),
                    stroke_width: b.stroke_width.or(a.stroke_width),
                }
            }
            PathEffectKind::Sum { first, second } => {
                let a = first.paint_adjustment();
                if a == second.paint_adjustment() {
                    a
                } else {
                    Default::default()
                }
            }
            _ => Default::default(),
        }
    }
    fn run(
        &self,
        source: &PathEffectSource,
        scale: f32,
    ) -> Result<(bool, PathEffectSource), Error> {
        match self.kind() {
            PathEffectKind::Compose { outer, inner } => {
                let (ok, result) = inner.run(source, scale)?;
                if !ok {
                    return Ok((false, source.clone()));
                }
                outer.run(&result, scale)
            }
            PathEffectKind::Sum { first, second } => {
                let (a, first) = first.run(source, scale)?;
                if !a {
                    return Ok((false, source.clone()));
                }
                let (b, second) = second.run(source, scale)?;
                if !b {
                    return Ok((false, source.clone()));
                }
                let mut out = Builder::default();
                out.append(&first.path)?;
                out.append(&second.path)?;
                let (applied, mut output) = finish(source, out);
                let mut geometry = first.geometry()?;
                let second = second.geometry()?;
                geometry
                    .figures
                    .try_reserve(second.figures.len())
                    .map_err(|_| Error::Capacity)?;
                geometry.figures.extend(second.figures);
                geometry.fill_rule = source.fill_rule;
                output.geometry = Some(Arc::new(geometry));
                Ok((applied, output))
            }
            PathEffectKind::Dash { intervals, phase } => {
                dash(source, intervals.values(), *phase, scale)
            }
            PathEffectKind::Trim { start, stop, mode } => trim(source, *start, *stop, *mode, scale),
            PathEffectKind::Corner(radius) => corner(source, *radius),
            PathEffectKind::Discrete {
                segment_length,
                deviation,
                seed,
            } => discrete(source, *segment_length, *deviation, *seed, scale),
            PathEffectKind::Path1D {
                stamp,
                advance,
                phase,
                style,
            } => stamps(source, stamp, *advance, *phase, *style, scale),
            PathEffectKind::Line2D { width, matrix } => {
                lattice::lines(source, *width, *matrix, scale)
            }
            PathEffectKind::Path2D { matrix, stamp } => lattice::paths(source, stamp, *matrix),
        }
    }
}
fn finish(source: &PathEffectSource, out: Builder) -> (bool, PathEffectSource) {
    (
        true,
        PathEffectSource {
            path: out.finish(),
            geometry: None,
            fill_rule: source.fill_rule,
            inverse: source.inverse,
        },
    )
}
fn rejected(source: &PathEffectSource) -> (bool, PathEffectSource) {
    (false, source.empty_like())
}
fn advance(intervals: &[f32], index: &mut usize, remaining: &mut f32, increment: bool) -> bool {
    if increment {
        *index = (*index + 1) % intervals.len();
        *remaining = intervals[*index];
    }
    for _ in 0..intervals.len() {
        if *remaining > EPS {
            return true;
        }
        *index = (*index + 1) % intervals.len();
        *remaining = intervals[*index];
    }
    false
}
fn dash(
    source: &PathEffectSource,
    intervals: &[f32],
    phase: f32,
    scale: f32,
) -> Result<(bool, PathEffectSource), Error> {
    if intervals.is_empty() || intervals.len() % 2 != 0 || !phase.is_finite() {
        return Ok(rejected(source));
    }
    let mut total = 0.0;
    for value in intervals {
        if !value.is_finite() || *value < 0.0 {
            return Ok(rejected(source));
        }
        total += value;
    }
    if !total.is_finite() || total <= EPS {
        return Ok(rejected(source));
    }
    let mut phase = phase % total;
    if phase < 0.0 {
        phase += total;
    }
    let mut initial = 0;
    let mut initial_remaining = intervals[0];
    if !advance(intervals, &mut initial, &mut initial_remaining, false) {
        return Ok(rejected(source));
    }
    while phase >= initial_remaining - EPS {
        phase -= initial_remaining;
        if !advance(intervals, &mut initial, &mut initial_remaining, true) {
            return Ok(rejected(source));
        }
    }
    initial_remaining -= phase;
    if initial_remaining <= EPS {
        return Ok(rejected(source));
    }
    let mut measure = PathMeasure::new(Some(&source.path), false, scale)?;
    let mut out = Builder::default();
    loop {
        let length = measure.length();
        if length > EPS {
            let mut distance = 0.0;
            let mut index = initial;
            let mut remaining = initial_remaining;
            let mut generated = 0;
            while distance < length - EPS {
                let step = remaining.min(length - distance);
                if index & 1 == 0 && step > EPS {
                    append_range(&measure, &mut out, distance, distance + step)?;
                }
                distance += step;
                remaining -= step;
                if remaining <= EPS && !advance(intervals, &mut index, &mut remaining, true) {
                    return Ok((false, source.clone()));
                }
                generated += 1;
                if generated > LIMIT {
                    return Ok((false, source.clone()));
                }
            }
        }
        if !measure.next_contour() {
            break;
        }
    }
    Ok(finish(source, out))
}
fn append_range(measure: &PathMeasure, out: &mut Builder, a: f32, b: f32) -> Result<(), Error> {
    if b - a > EPS {
        measure.append_segment(a, b, out, true)?;
    }
    Ok(())
}
fn trim(
    source: &PathEffectSource,
    start: f32,
    stop: f32,
    mode: TrimMode,
    scale: f32,
) -> Result<(bool, PathEffectSource), Error> {
    if !start.is_finite() || !stop.is_finite() {
        return Ok(rejected(source));
    }
    let a = start.clamp(0.0, 1.0);
    let b = stop.clamp(0.0, 1.0);
    let mut measure = PathMeasure::new(Some(&source.path), false, scale)?;
    let mut out = Builder::default();
    loop {
        let length = measure.length();
        if length > EPS {
            match mode {
                TrimMode::Normal if a <= b => {
                    append_range(&measure, &mut out, a * length, b * length)?
                }
                TrimMode::Normal => {
                    append_range(&measure, &mut out, a * length, length)?;
                    append_range(&measure, &mut out, 0.0, b * length)?;
                }
                TrimMode::Inverted if a <= b => {
                    append_range(&measure, &mut out, 0.0, a * length)?;
                    append_range(&measure, &mut out, b * length, length)?;
                }
                TrimMode::Inverted => append_range(&measure, &mut out, b * length, a * length)?,
            }
        }
        if !measure.next_contour() {
            break;
        }
    }
    Ok(finish(source, out))
}
fn discrete(
    source: &PathEffectSource,
    segment: f32,
    deviation: f32,
    seed: u32,
    scale: f32,
) -> Result<(bool, PathEffectSource), Error> {
    if !segment.is_finite() || segment <= EPS || !deviation.is_finite() || deviation < 0.0 {
        return Ok(rejected(source));
    }
    let mut state = if seed == 0 { 0x9e3779b9 } else { seed };
    let mut measure = PathMeasure::new(Some(&source.path), false, scale)?;
    let mut out = Builder::default();
    loop {
        let length = measure.length();
        let exact = (length / segment).ceil();
        if !exact.is_finite() || exact > LIMIT as f32 {
            return Ok((false, source.clone()));
        }
        let count = exact as i32;
        if count > 0 {
            for index in 0..=count {
                if let Some((position, tangent)) =
                    measure.position_and_tangent(length.min(index as f32 * segment))
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    let offset =
                        (((state >> 8) as f32 * (1.0 / 16777215.0)) * 2.0 - 1.0) * deviation;
                    let point = normal_offset(position, tangent, offset);
                    out.push(if index == 0 {
                        C::Move(point)
                    } else {
                        C::Line(point)
                    })?;
                }
            }
            if measure.is_closed() {
                out.push(C::Close)?;
            }
        }
        if !measure.next_contour() {
            break;
        }
    }
    Ok(finish(source, out))
}
fn stamps(
    source: &PathEffectSource,
    stamp: &PathEffectSource,
    advance: f32,
    phase: f32,
    style: PathStampStyle,
    scale: f32,
) -> Result<(bool, PathEffectSource), Error> {
    if !advance.is_finite() || advance <= EPS || !phase.is_finite() {
        return Ok(rejected(source));
    }
    let mut phase = phase % advance;
    if phase < 0.0 {
        phase += advance;
    }
    let mut measure = PathMeasure::new(Some(&source.path), false, scale)?;
    let mut out = Builder::default();
    let stamp_geometry = stamp.geometry()?;
    let mut placed = PathGeometry {
        fill_rule: source.fill_rule,
        ..Default::default()
    };
    loop {
        let length = measure.length();
        let exact = if length <= phase {
            0.0
        } else {
            ((length - phase) / advance).floor() + 1.0
        };
        if !exact.is_finite() || exact > LIMIT as f32 {
            return Ok((false, source.clone()));
        }
        for index in 0..exact as usize {
            let distance = phase + index as f32 * advance;
            let Some((position, tangent)) = measure.position_and_tangent(distance) else {
                continue;
            };
            let matrix = if style == PathStampStyle::Rotate {
                EffectMatrix([
                    tangent[0],
                    -tangent[1],
                    position[0],
                    tangent[1],
                    tangent[0],
                    position[1],
                    0.0,
                    0.0,
                    1.0,
                ])
            } else {
                EffectMatrix::translation(position)
            };
            if style != PathStampStyle::Morph {
                let mut copy = PathGeometry {
                    fill_rule: stamp.fill_rule,
                    ..Default::default()
                };
                append_figures(&mut copy, &stamp_geometry, [0.0; 2])?;
                if matrix != EffectMatrix::IDENTITY {
                    let [a, b, x, c, d, y, p, q, w] = matrix.0;
                    copy = copy.transformed(Matrix4([
                        [a, c, 0.0, p],
                        [b, d, 0.0, q],
                        [0.0, 0.0, 1.0, 0.0],
                        [x, y, 0.0, w],
                    ]))?;
                }
                append_figures(&mut placed, &copy, [0.0; 2])?;
                continue;
            }
            for command in stamp.path.commands() {
                out.push(map_command(*command, |local| {
                    let p = measure
                        .position_and_tangent(distance + local[0])
                        .map_or([0.0; 2], |(p, t)| normal_offset(p, t, local[1]));
                    [p[0] + 0.0, p[1] + 0.0]
                }))?;
            }
        }
        if !measure.next_contour() {
            break;
        }
    }
    if style == PathStampStyle::Morph {
        Ok(finish(source, out))
    } else {
        Ok((
            true,
            PathEffectSource::from_owned_geometry(placed, source.inverse)?,
        ))
    }
}
fn append_figures(
    target: &mut PathGeometry,
    source: &PathGeometry,
    offset: [f32; 2],
) -> Result<(), Error> {
    if source.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    target
        .figures
        .try_reserve(source.figures.len())
        .map_err(|_| Error::Capacity)?;
    for figure in &source.figures {
        target
            .figures
            .push(super::raw_path::copy_figure(figure, offset)?);
    }
    Ok(())
}
fn normal_offset(point: [f32; 2], tangent: [f32; 2], offset: f32) -> [f32; 2] {
    let [x, y, _, _] = (V::from([tangent[1], tangent[0], 0.0, 0.0]) * V::splat(offset)).to_array();
    [point[0] - x, point[1] + y]
}
fn map_command(command: C, mut map: impl FnMut([f32; 2]) -> [f32; 2]) -> C {
    match command {
        C::Move(p) => C::Move(map(p)),
        C::Line(p) => C::Line(map(p)),
        C::Quadratic(p) => C::Quadratic(p.map(map)),
        C::Conic { points, weight } => C::Conic {
            points: points.map(map),
            weight,
        },
        C::Cubic(p) => C::Cubic(p.map(map)),
        C::Close => C::Close,
    }
}
fn corner(source: &PathEffectSource, radius: f32) -> Result<(bool, PathEffectSource), Error> {
    if !radius.is_finite() || radius <= 0.0 {
        return Ok((radius == 0.0, source.clone()));
    }
    let geometry = source.geometry()?;
    let mut out = Builder::default();
    let mut retained = Vec::new();
    for (figure_index, figure) in geometry.figures.iter().enumerate() {
        let mut points = Vec::new();
        points
            .try_reserve(figure.segments.len() + 1)
            .map_err(|_| Error::Capacity)?;
        points.push(figure.start);
        let all_lines = !figure.segments.is_empty()
            && figure
                .segments
                .iter()
                .all(|s| matches!(s.kind, super::PathSegmentKind::Line(_)));
        if all_lines {
            for s in &figure.segments {
                points.push(s.end());
            }
        }
        if figure.is_closed
            && points.len() > 1
            && squared(points[0], *points.last().unwrap()) <= EPS * EPS
        {
            points.pop();
        }
        if !all_lines || points.len() < if figure.is_closed { 3 } else { 2 } {
            let original = PathGeometry {
                figures: vec![figure.clone()],
                fill_rule: source.fill_rule,
                ..Default::default()
            };
            out.append(&MeasuredPath::from_geometry(&original)?)?;
            retained.try_reserve(1).map_err(|_| Error::Capacity)?;
            retained.push((figure_index, figure.clone()));
            continue;
        }
        if !figure.is_closed {
            out.push(C::Move(points[0]))?;
            for i in 1..points.len() - 1 {
                let (a, b) = rounded(points[i - 1], points[i], points[i + 1], radius);
                out.push(C::Line(a))?;
                out.push(C::Quadratic([points[i], b]))?;
            }
            out.push(C::Line(*points.last().unwrap()))?;
        } else {
            let (first_before, first_after) =
                rounded(*points.last().unwrap(), points[0], points[1], radius);
            out.push(C::Move(first_after))?;
            for i in 1..points.len() {
                let (a, b) = rounded(
                    points[i - 1],
                    points[i],
                    points[(i + 1) % points.len()],
                    radius,
                );
                out.push(C::Line(a))?;
                out.push(C::Quadratic([points[i], b]))?;
            }
            out.push(C::Line(first_before))?;
            out.push(C::Quadratic([points[0], first_after]))?;
            out.push(C::Close)?;
        }
    }
    let (applied, mut output) = finish(source, out);
    if !retained.is_empty() {
        let mut geometry = output.geometry()?;
        for (index, figure) in retained {
            geometry.figures[index] = figure;
        }
        output.geometry = Some(Arc::new(geometry));
    }
    Ok((applied, output))
}
fn squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let v = (V::from([a[0], a[1], 0.0, 0.0]) - V::from([b[0], b[1], 0.0, 0.0])).to_array();
    v[0] * v[0] + v[1] * v[1]
}
fn rounded(
    previous: [f32; 2],
    point: [f32; 2],
    next: [f32; 2],
    radius: f32,
) -> ([f32; 2], [f32; 2]) {
    let before =
        V::from([point[0], point[1], 0.0, 0.0]) - V::from([previous[0], previous[1], 0.0, 0.0]);
    let after = V::from([next[0], next[1], 0.0, 0.0]) - V::from([point[0], point[1], 0.0, 0.0]);
    let a = before.to_array();
    let b = after.to_array();
    let la = (a[0] * a[0] + a[1] * a[1]).sqrt();
    let lb = (b[0] * b[0] + b[1] * b[1]).sqrt();
    if la <= EPS || lb <= EPS {
        return (point, point);
    }
    let trim = radius.min(la.min(lb) * 0.5);
    let p = V::from([point[0], point[1], 0.0, 0.0]);
    let a = (p - before * V::splat(trim / la)).to_array();
    let b = (p + after * V::splat(trim / lb)).to_array();
    ([a[0], a[1]], [b[0], b[1]])
}
