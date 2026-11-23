//! Original bounded linear stroke preparation and material-domain measurement.
//! Independent XY arithmetic uses safe packed f64 lanes; traversal/root order
//! stays scalar because each decision depends on its predecessor.
use super::dashed_path::push;
use super::*;
use wide::f64x2 as Pair;
mod bounds;
mod outline;
use bounds::Bounds;
const BUDGET: usize = 1_000_000;
const KAPPA: f64 = 0.552_284_749_830_793_4;

/// Owned original stroke preparation with independent source material bounds.
#[derive(Clone, Debug, PartialEq)]
pub struct LinearStrokeCoverage {
    /// Gap-split and constant-compacted spine; dashed spans remain ordered.
    pub stroke_path: PathGeometry,
    /// Actual derived pen, including source join/frame policy and material.
    pub pen: Pen,
    /// Original x/y/width/height material bounds, zero for empty coverage.
    pub bounds: [f32; 4],
    /// When present, paint this compound nonzero fill instead of the spine.
    pub fill_coverage: Option<PathGeometry>,
}
impl PathGeometry {
    /// Original complete linear inventory admission, run splitting, dash-density
    /// bounds and optional directed-cap union. None rejects without publication.
    /// Source geometry transforms must already be applied; no curve flattening.
    pub fn prepare_linear_stroke(&self, pen: &Pen) -> Result<Option<LinearStrokeCoverage>, Error> {
        pen.validate_join_semantics()?;
        if self.deferred.is_some()
            || !pen.options.thickness.is_finite()
            || pen.options.thickness < 0.0
            || pen.options.transform != PenStrokeTransform::Normal
            || !pen.options.dash_offset.is_finite()
            || !pen.options.miter_limit.is_finite()
            || !admit(self, false)
        {
            return Ok(None);
        }
        match prepare(self, pen) {
            Ok(result) => Ok(Some(result)),
            Err(Error::InvalidInput) => Ok(None),
            Err(error) => Err(error),
        }
    }
}
fn prepare(source: &PathGeometry, pen: &Pen) -> Result<LinearStrokeCoverage, Error> {
    let mut prepared = PathGeometry {
        fill_rule: source.fill_rule,
        ..Default::default()
    };
    for figure in &source.figures {
        append_runs(figure, pen, &mut prepared)?;
    }
    let mut coverage = LinearStrokeCoverage {
        stroke_path: prepared,
        pen: pen.clone(),
        bounds: [0.0; 4],
        fill_coverage: None,
    };
    if pen.has_dash_pattern() {
        if pen.options.thickness == 0.0 {
            return Ok(coverage);
        }
        let mut total = 0.0;
        let mut records = coverage.stroke_path.figures.len();
        for figure in &coverage.stroke_path.figures {
            if figure.segments.len() > BUDGET.saturating_sub(records) {
                return Err(Error::InvalidInput);
            }
            records += figure.segments.len();
            let mut start = figure.start;
            for segment in &figure.segments {
                let end = segment.end();
                let distance = length(sub(end, start));
                if !distance.is_finite() || distance <= 0.0001 {
                    return Err(Error::InvalidInput);
                }
                total += f64::from(distance);
                start = end;
            }
        }
        if !admit_pattern(pen, total, records * 2) {
            return Err(Error::InvalidInput);
        }
        let dashed = coverage
            .stroke_path
            .dashed_stroke(pen, pen.options.thickness, true)?
            .ok_or(Error::InvalidInput)?;
        coverage.pen = pen.undashed(pen.options.thickness)?;
        coverage.fill_coverage = dashed
            .terminal_caps
            .as_ref()
            .map(|_| PathGeometry::default());
        coverage.bounds = outline::measure(
            &dashed.path,
            &coverage.pen,
            &mut coverage.fill_coverage,
            dashed.terminal_caps.as_deref().unwrap_or_default(),
        )?;
        coverage.stroke_path = dashed.path;
    } else {
        let mut state = Bounds::default();
        let mut any = false;
        if pen.options.thickness > 0.0 {
            for figure in &coverage.stroke_path.figures {
                any |= measure_run(figure, pen, &mut state)?;
            }
        }
        if any {
            coverage.bounds = state.rect()?;
        }
    }
    Ok(coverage)
}
fn admit(path: &PathGeometry, prepared: bool) -> bool {
    let mut records = path.figures.len();
    if records > BUDGET {
        return false;
    }
    for figure in &path.figures {
        if !finite(figure.start) || figure.segments.len() > BUDGET - records {
            return false;
        }
        records += figure.segments.len();
        let mut current = figure.start;
        for segment in &figure.segments {
            let PathSegmentKind::Line(end) = segment.kind else {
                return false;
            };
            if !finite(end) || (prepared && (!segment.is_stroked || end == current)) {
                return false;
            }
            current = end;
        }
        if prepared && figure.is_closed && current != figure.start {
            return false;
        }
    }
    true
}
fn append_runs(source: &PathFigure, pen: &Pen, output: &mut PathGeometry) -> Result<(), Error> {
    let count = source.segments.len();
    if count == 0 {
        return Ok(());
    }
    let last = source.segments[count - 1].end();
    let edges = count + usize::from(source.is_closed && last != source.start);
    let gap = source.segments.iter().position(|s| !s.is_stroked);
    let closed = source.is_closed && gap.is_none();
    let origin = if source.is_closed {
        gap.map_or(0, |gap| (gap + 1) % edges)
    } else {
        0
    };
    let mut consumed = 0;
    while consumed < edges {
        let mut index = (origin + consumed) % edges;
        if index < count && !source.segments[index].is_stroked {
            consumed += 1;
            continue;
        }
        let first = consumed;
        let start = if index == 0 {
            source.start
        } else {
            source.segments[index - 1].end()
        };
        let mut run = PathFigure::new(start, closed);
        run.is_filled = false;
        run.start_cap = Some(if !source.is_closed && index == 0 {
            source.start_cap.unwrap_or(pen.options.start_cap)
        } else {
            pen.options.dash_cap
        });
        let mut current = start;
        let mut pending_smooth = false;
        while consumed < edges {
            index = (origin + consumed) % edges;
            let line = source.segments.get(index);
            if line.is_some_and(|s| !s.is_stroked) {
                break;
            }
            let end = line.map_or(source.start, |s| s.end());
            let smooth = line.is_some_and(|s| s.is_smooth_join);
            if end != current {
                let mut segment = PathSegment::new(PathSegmentKind::Line(end));
                segment.is_smooth_join = pending_smooth || smooth;
                push(&mut run.segments, segment)?;
                pending_smooth = false;
            } else {
                pending_smooth |= smooth;
            }
            current = end;
            consumed += 1;
        }
        if run.segments.is_empty() && consumed != first {
            push(
                &mut run.segments,
                PathSegment::new(PathSegmentKind::Line(current)),
            )?;
        }
        if closed && pending_smooth {
            run.segments[0].is_smooth_join = true;
        }
        run.end_cap = Some(if !source.is_closed && consumed == edges {
            source.end_cap.unwrap_or(pen.options.end_cap)
        } else {
            pen.options.dash_cap
        });
        push(&mut output.figures, run)?;
    }
    Ok(())
}
fn admit_pattern(pen: &Pen, length: f64, records: usize) -> bool {
    let intervals = pen.dash_array().unwrap_or_default();
    if intervals.is_empty() || intervals.len() > BUDGET {
        return false;
    }
    let mut minimum = Pair::splat(f64::INFINITY);
    let mut sum = Pair::ZERO;
    let width = f64::from(pen.options.thickness);
    let mut chunks = intervals.chunks_exact(2);
    for pair in &mut chunks {
        let values = Pair::new([pair[0], pair[1]]) * Pair::splat(width);
        let lanes = values.to_array();
        if lanes
            .iter()
            .any(|v| !(*v == 0.0 || *v > 0.0001) || *v >= f64::from(f32::MAX))
        {
            return false;
        }
        minimum = minimum.min(Pair::new(
            lanes.map(|v| if v == 0.0 { f64::INFINITY } else { v }),
        ));
        sum += values;
    }
    let [a, b] = minimum.to_array();
    let mut smallest = a.min(b);
    let [a, b] = sum.to_array();
    let mut total = a + b;
    if let [tail] = chunks.remainder() {
        let tail = *tail * width;
        if !(tail == 0.0 || (tail > 0.0001 && tail < f64::from(f32::MAX))) {
            return false;
        }
        if tail > 0.0 {
            smallest = smallest.min(tail);
        }
        total += tail;
    }
    if !intervals.len().is_multiple_of(2) {
        total *= 2.0;
    }
    total > 0.0
        && total < f64::from(f32::MAX)
        && ((pen.options.dash_offset * width) as f32).is_finite()
        && length.is_finite()
        && length / smallest + records as f64 <= BUDGET as f64
        && ((length / total).ceil() + 1.0) * intervals.len() as f64 * 2.0 + records as f64
            <= BUDGET as f64
}
fn measure_run(figure: &PathFigure, pen: &Pen, state: &mut Bounds) -> Result<bool, Error> {
    let count = figure.segments.len();
    if count == 0 {
        return Ok(false);
    }
    let first = figure.start;
    let last = figure.segments[count - 1].end();
    let radius = f64::from(pen.options.thickness) * 0.5;
    if count == 1 && first == last {
        let mut point = Bounds::line(first, last, pen.options.thickness);
        let start_cap = if figure.is_closed {
            PenLineCap::Round
        } else {
            figure.start_cap.unwrap_or(pen.options.start_cap)
        };
        let end_cap = if figure.is_closed {
            PenLineCap::Round
        } else {
            figure.end_cap.unwrap_or(pen.options.end_cap)
        };
        if start_cap == PenLineCap::Flat && end_cap == PenLineCap::Flat {
            return Ok(false);
        }
        point.cap(first, -1.0, start_cap);
        point.cap(first, 1.0, end_cap);
        let (a, b) = point.points()?;
        state.include(wide(a));
        state.include(wide(b));
        return Ok(true);
    }
    let mut start = wide(first);
    let mut first_direction = Pair::ZERO;
    let mut previous_direction = Pair::ZERO;
    for (index, segment) in figure.segments.iter().enumerate() {
        let end = wide(segment.end());
        let delta = end - start;
        let length = magnitude(delta);
        if !length.is_finite() || length == 0.0 {
            return Err(Error::InvalidInput);
        }
        let direction = delta / Pair::splat(length);
        let normal = left(direction) * Pair::splat(radius);
        for p in [start + normal, start - normal, end + normal, end - normal] {
            state.include(narrow(p));
        }
        if index == 0 {
            first_direction = direction;
        } else if pen.options.use_wpf_join_semantics {
            let previous = if index == 1 {
                first
            } else {
                figure.segments[index - 2].end()
            };
            outline::source_join(
                state,
                pen,
                [previous, figure.segments[index - 1].end(), segment.end()],
                segment.is_smooth_join,
                &mut None,
            )?;
        } else {
            ideal_join(
                state,
                start,
                previous_direction,
                direction,
                radius,
                if segment.is_smooth_join {
                    PenLineJoin::Round
                } else {
                    pen.options.join
                },
                f64::from(pen.options.miter_limit),
            );
        }
        previous_direction = direction;
        start = end;
    }
    if figure.is_closed {
        if pen.options.use_wpf_join_semantics {
            let previous = if count == 1 {
                first
            } else {
                figure.segments[count - 2].end()
            };
            outline::source_join(
                state,
                pen,
                [previous, first, figure.segments[0].end()],
                figure.segments[0].is_smooth_join,
                &mut None,
            )?;
        } else {
            ideal_join(
                state,
                wide(first),
                previous_direction,
                first_direction,
                radius,
                if figure.segments[0].is_smooth_join {
                    PenLineJoin::Round
                } else {
                    pen.options.join
                },
                f64::from(pen.options.miter_limit),
            );
        }
    } else {
        ideal_cap(
            state,
            wide(first),
            -first_direction,
            radius,
            figure.start_cap.unwrap_or(pen.options.start_cap),
        );
        ideal_cap(
            state,
            wide(last),
            previous_direction,
            radius,
            figure.end_cap.unwrap_or(pen.options.end_cap),
        );
    }
    Ok(true)
}
fn ideal_cap(state: &mut Bounds, center: Pair, outward: Pair, radius: f64, cap: PenLineCap) {
    if cap == PenLineCap::Flat {
        return;
    }
    if cap == PenLineCap::Round {
        sector(state, center, outward, -outward, radius);
        return;
    }
    let extension = narrow(center + outward * Pair::splat(radius));
    if cap == PenLineCap::Triangle {
        state.include(extension);
        return;
    }
    let normal = left(outward) * Pair::splat(radius);
    state.include(narrow(extension + normal));
    state.include(narrow(extension - normal));
}
fn sector(state: &mut Bounds, center: Pair, incoming: Pair, outgoing: Pair, radius: f64) {
    state.include(center);
    for positive in [Pair::new([radius, 0.0]), Pair::new([0.0, radius])] {
        for p in [positive, -positive] {
            if pair_dot(p, incoming) >= 0.0 && pair_dot(p, outgoing) <= 0.0 {
                state.include(narrow(center + p));
            }
        }
    }
}
fn ideal_join(
    state: &mut Bounds,
    center: Pair,
    incoming: Pair,
    outgoing: Pair,
    radius: f64,
    join: PenLineJoin,
    limit: f64,
) {
    if join == PenLineJoin::Round {
        sector(state, center, incoming, outgoing, radius);
        return;
    }
    if join == PenLineJoin::Bevel {
        return;
    }
    let denominator = cross(incoming, outgoing);
    if denominator.abs() <= 1e-6 {
        return;
    }
    for side in [-1.0, 1.0] {
        let a = narrow(center + left(incoming) * Pair::splat(radius * side));
        let b = narrow(center + left(outgoing) * Pair::splat(radius * side));
        let miter = narrow(a + incoming * Pair::splat(cross(b - a, outgoing) / denominator));
        let delta = miter - center;
        let length = magnitude(delta);
        if length <= limit.max(1.0) * radius {
            state.include(miter);
            continue;
        }
        if length == 0.0 || join == PenLineJoin::MiterOrBevel {
            continue;
        }
        let bisector = delta / Pair::splat(length);
        for offset in [a, b] {
            let projection = pair_dot(offset - center, bisector);
            let divisor = length - projection;
            if divisor <= 0.0 {
                continue;
            }
            let amount = (limit.max(1.0) * radius - projection) / divisor;
            if amount.is_finite() && (0.0..=1.0).contains(&amount) {
                state.include(narrow(offset + (miter - offset) * Pair::splat(amount)));
            }
        }
    }
}
fn wide(p: [f32; 2]) -> Pair {
    Pair::new(p.map(f64::from))
}
fn point(p: Pair) -> [f32; 2] {
    p.to_array().map(|v| v as f32)
}
fn narrow(p: Pair) -> Pair {
    wide(point(p))
}
fn left(p: Pair) -> Pair {
    let [x, y] = p.to_array();
    Pair::new([-y, x])
}
fn magnitude(p: Pair) -> f64 {
    let [x, y] = p.to_array();
    x.hypot(y)
}
fn pair_dot(a: Pair, b: Pair) -> f64 {
    let [x, y] = (a * b).to_array();
    x + y
}
fn cross(a: Pair, b: Pair) -> f64 {
    let [x, y] = a.to_array();
    let [u, v] = b.to_array();
    x * v - y * u
}
