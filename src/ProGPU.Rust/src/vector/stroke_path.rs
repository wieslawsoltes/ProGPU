//! Original line-only retained widening and matching triangle hit traversal.
//! Curve flattening and expansion-frame selection belong to the owning API.
use super::stroke::{EPSILON, cross, distance_squared};
use super::*;

/// Original widening result, including earlier figures when later input rejects.
#[derive(Clone, Debug, PartialEq)]
pub struct WidenedPath {
    /// Ordered Nonzero polygons emitted by the original geometry sink.
    pub path: PathGeometry,
    /// Whether the entire original line-only inventory was admitted.
    pub complete: bool,
}
impl PathGeometry {
    /// Expand the original line-only stroke, retaining partial output on source
    /// admission failure. Callers must check `complete` before publishing it.
    /// Curves are not implicitly flattened; figure cap overrides are not used
    /// by this source utility. Hairlines use its original one-unit thickness.
    pub fn widen_stroke(&self, pen: &Pen) -> Result<WidenedPath, Error> {
        pen.validate_join_semantics()?;
        let mut sink = GeometrySink(PathGeometry::default());
        let complete = emit_stroke(self, pen, &mut sink)?;
        Ok(WidenedPath {
            path: sink.0,
            complete,
        })
    }
    /// Test the same emitted stroke triangles, retaining original early-hit
    /// short circuit. None means source rejection, distinct from a valid miss.
    pub fn contains_stroke(&self, pen: &Pen, point: [f32; 2]) -> Result<Option<bool>, Error> {
        pen.validate_join_semantics()?;
        if !finite(point) {
            return Ok(None);
        }
        let mut sink = HitSink {
            point,
            contains: false,
        };
        let admitted = emit_stroke(self, pen, &mut sink)?;
        Ok(admitted.then_some(sink.contains))
    }
}
fn emit_stroke(
    source: &PathGeometry,
    pen: &Pen,
    sink: &mut impl TriangleSink,
) -> Result<bool, Error> {
    let thickness = if pen.is_hairline() {
        1.0
    } else {
        pen.options.thickness
    };
    if source.deferred.is_some() || !thickness.is_finite() || thickness <= EPSILON {
        return Ok(false);
    }
    let pattern = if let Some(dashes) = pen.dash_array().filter(|d| !d.is_empty()) {
        let Some(pattern) =
            DashPattern::new(dashes, pen.options.dash_offset, f64::from(thickness))?
        else {
            return Ok(false);
        };
        Some(pattern)
    } else {
        None
    };
    let mut emitter = Emitter {
        pen,
        thickness,
        sink,
    };
    for figure in &source.figures {
        // Source admission is per figure; early hits need not visit later input.
        if !finite(figure.start)
            || !figure
                .segments
                .iter()
                .all(|s| matches!(s.kind, PathSegmentKind::Line(p) if finite(p)))
        {
            return Ok(false);
        }
        if let Some(pattern) = &pattern {
            emitter.dashed_figure(figure, pattern)?;
        } else {
            emitter.solid_figure(figure)?;
        }
        if emitter.sink.complete() {
            return Ok(true);
        }
    }
    Ok(true)
}
struct Emitter<'a, S> {
    pen: &'a Pen,
    thickness: f32,
    sink: &'a mut S,
}
#[derive(Clone, Copy)]
struct RunStart {
    cap: PenLineCap,
    suppress: bool,
    smooth: bool,
}
#[derive(Clone, Copy)]
struct StrokeRun {
    start: [f32; 2],
    first_next: [f32; 2],
    previous: [f32; 2],
    current: [f32; 2],
    metadata: RunStart,
}
#[derive(Clone, Copy)]
struct DelayedStart {
    start: [f32; 2],
    next: [f32; 2],
    cap: PenLineCap,
    smooth: bool,
}
struct DashedRun<'a> {
    run: Option<StrokeRun>,
    delayed: Option<DelayedStart>,
    pattern: &'a DashPattern,
    state: DashState,
}
impl<S: TriangleSink> Emitter<'_, S> {
    fn solid_figure(&mut self, figure: &PathFigure) -> Result<(), Error> {
        let mut run = None;
        let mut current = figure.start;
        for segment in &figure.segments {
            let end = segment.end();
            if segment.is_stroked {
                self.append(
                    &mut run,
                    current,
                    end,
                    RunStart {
                        cap: self.pen.options.start_cap,
                        suppress: false,
                        smooth: segment.is_smooth_join,
                    },
                )?;
            } else {
                self.finish(&mut run, self.pen.options.end_cap, &mut None)?;
            }
            current = end;
            if self.sink.complete() {
                return Ok(());
            }
        }
        if figure.is_closed {
            self.append(
                &mut run,
                current,
                figure.start,
                RunStart {
                    cap: self.pen.options.start_cap,
                    suppress: true,
                    smooth: false,
                },
            )?;
            self.finish_closed(&mut run)?;
        } else {
            self.finish(&mut run, self.pen.options.end_cap, &mut None)?;
        }
        Ok(())
    }
    fn dashed_figure(&mut self, figure: &PathFigure, pattern: &DashPattern) -> Result<(), Error> {
        let mut dash = DashedRun {
            run: None,
            delayed: None,
            pattern,
            state: pattern.initial_state(),
        };
        let mut current = figure.start;
        let mut at_start = true;
        for segment in &figure.segments {
            let end = segment.end();
            if segment.is_stroked {
                self.dashed_line(
                    &mut dash,
                    current,
                    end,
                    figure.is_closed,
                    at_start,
                    segment.is_smooth_join,
                )?;
            } else {
                self.finish(&mut dash.run, self.pen.options.dash_cap, &mut dash.delayed)?;
                dash.state = pattern.initial_state();
            }
            current = end;
            at_start = false;
            if self.sink.complete() {
                return Ok(());
            }
        }
        if figure.is_closed && distance_squared(current, figure.start) > EPSILON * EPSILON {
            self.dashed_line(&mut dash, current, figure.start, true, false, false)?;
        }
        if figure.is_closed {
            if dash.run.is_some_and(|r| r.metadata.suppress) {
                self.finish_closed(&mut dash.run)?;
            } else if let (Some(run), Some(delayed)) = (dash.run, dash.delayed)
                && distance_squared(run.current, figure.start) <= EPSILON * EPSILON
            {
                self.start_cap(run)?;
                self.join(run.previous, run.current, delayed.next, delayed.smooth)?;
            } else {
                self.finish(&mut dash.run, self.pen.options.dash_cap, &mut None)?;
                if let Some(delayed) = dash.delayed {
                    self.cap(delayed.cap, delayed.start, delayed.next, true)?;
                }
            }
        } else {
            self.finish(&mut dash.run, self.pen.options.end_cap, &mut None)?;
        }
        Ok(())
    }
    fn dashed_line(
        &mut self,
        dash: &mut DashedRun<'_>,
        start: [f32; 2],
        end: [f32; 2],
        closed: bool,
        at_start: bool,
        smooth: bool,
    ) -> Result<(), Error> {
        let delta = sub(end, start);
        let size = length(delta);
        if !size.is_finite() || size <= EPSILON {
            return Ok(());
        }
        let intervals = dash.pattern.intervals();
        dash::normalize(intervals, &mut dash.state)?;
        if dash.run.is_some() && dash.state.index & 1 != 0 {
            self.finish(&mut dash.run, self.pen.options.dash_cap, &mut dash.delayed)?;
        }
        let direction = [delta[0] / size, delta[1] / size];
        let mut distance = 0.0;
        let mut stalled = 0usize;
        while distance < size - EPSILON && !self.sink.complete() {
            let remaining = intervals[dash.state.index as usize] - dash.state.distance;
            let step = minimum(remaining, size - distance);
            let drawn = dash.state.index & 1 == 0;
            if drawn && step > EPSILON {
                let dash_start = add(start, mul(direction, distance));
                let dash_end = add(start, mul(direction, distance + step));
                let starts_at_figure = at_start && distance <= EPSILON;
                self.append(
                    &mut dash.run,
                    dash_start,
                    dash_end,
                    RunStart {
                        cap: if starts_at_figure {
                            self.pen.options.start_cap
                        } else {
                            self.pen.options.dash_cap
                        },
                        suppress: closed && starts_at_figure,
                        smooth: smooth && distance == 0.0,
                    },
                )?;
            }
            let completes_interval = step >= remaining - EPSILON;
            dash::advance(intervals, &mut dash.state, remaining, step);
            let next = distance + step;
            // Same bounded nonprogress rejection as the original-port dash walk:
            // allow one initial partial interval plus a complete ordered cycle.
            if next == distance {
                stalled += 1;
                if stalled > intervals.len() {
                    return Err(Error::InvalidInput);
                }
            } else {
                stalled = 0;
            }
            distance = next;
            if drawn && completes_interval && dash.state.index & 1 != 0 && distance < size - EPSILON
            {
                self.finish(&mut dash.run, self.pen.options.dash_cap, &mut dash.delayed)?;
            }
        }
        Ok(())
    }
    fn append(
        &mut self,
        run: &mut Option<StrokeRun>,
        start: [f32; 2],
        end: [f32; 2],
        metadata: RunStart,
    ) -> Result<(), Error> {
        if distance_squared(start, end) <= EPSILON * EPSILON {
            return Ok(());
        }
        if run.is_none_or(|previous| distance_squared(previous.current, start) > EPSILON * EPSILON)
        {
            self.finish(run, self.pen.options.dash_cap, &mut None)?;
            *run = Some(StrokeRun {
                start,
                first_next: end,
                previous: start,
                current: end,
                metadata,
            });
        } else if let Some(previous) = *run {
            self.join(previous.previous, previous.current, end, metadata.smooth)?;
            *run = Some(StrokeRun {
                previous: previous.current,
                current: end,
                ..previous
            });
        }
        self.body(start, end)
    }
    fn finish(
        &mut self,
        run: &mut Option<StrokeRun>,
        cap: PenLineCap,
        delayed: &mut Option<DelayedStart>,
    ) -> Result<(), Error> {
        let Some(value) = *run else { return Ok(()) };
        if value.metadata.suppress {
            *delayed = Some(DelayedStart {
                start: value.start,
                next: value.first_next,
                cap: value.metadata.cap,
                smooth: value.metadata.smooth,
            });
        } else {
            self.start_cap(value)?;
        }
        self.cap(cap, value.previous, value.current, false)?;
        *run = None;
        Ok(())
    }
    fn finish_closed(&mut self, run: &mut Option<StrokeRun>) -> Result<(), Error> {
        let Some(value) = *run else { return Ok(()) };
        if distance_squared(value.current, value.start) <= EPSILON * EPSILON {
            self.join(
                value.previous,
                value.current,
                value.first_next,
                value.metadata.smooth,
            )?;
        } else {
            self.start_cap(value)?;
            self.cap(
                self.pen.options.end_cap,
                value.previous,
                value.current,
                false,
            )?;
        }
        *run = None;
        Ok(())
    }
    fn start_cap(&mut self, run: StrokeRun) -> Result<(), Error> {
        self.cap(run.metadata.cap, run.start, run.first_next, true)
    }
    fn body(&mut self, start: [f32; 2], end: [f32; 2]) -> Result<(), Error> {
        let delta = sub(end, start);
        let size = length(delta);
        if !size.is_finite() || size <= EPSILON {
            return Ok(());
        }
        let n = mul([-delta[1] / size, delta[0] / size], self.thickness * 0.5);
        self.sink
            .quad([add(start, n), sub(start, n), sub(end, n), add(end, n)])
    }
    fn join(
        &mut self,
        previous: [f32; 2],
        point: [f32; 2],
        next: [f32; 2],
        smooth: bool,
    ) -> Result<(), Error> {
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        // Generic widening historically ignores smooth metadata, rather than
        // suppressing its joins. WPF alone selects smooth-to-Round here.
        let count = self.pen.write_join(
            &mut triangles,
            StrokeJoinFrame::from_line(previous, point, next),
            self.thickness,
            self.pen.options.use_wpf_join_semantics && smooth,
            MAX_STROKE_TRIANGLES as i32,
        )?;
        self.triangles(&triangles[..count])
    }
    fn cap(
        &mut self,
        cap: PenLineCap,
        start: [f32; 2],
        end: [f32; 2],
        is_start: bool,
    ) -> Result<(), Error> {
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = StrokeCap {
            cap,
            thickness: self.thickness,
            is_start,
            max_round_segments: MAX_STROKE_TRIANGLES as i32,
        }
        .write_line(&mut triangles, start, end)?;
        self.triangles(&triangles[..count])
    }
    fn triangles(&mut self, triangles: &[StrokeTriangle]) -> Result<(), Error> {
        for triangle in triangles {
            if self.sink.complete() {
                break;
            }
            self.sink.triangle(triangle.points)?;
        }
        Ok(())
    }
}
trait TriangleSink {
    fn complete(&self) -> bool;
    fn triangle(&mut self, points: [[f32; 2]; 3]) -> Result<(), Error>;
    fn quad(&mut self, points: [[f32; 2]; 4]) -> Result<(), Error>;
}
fn area(a: [f32; 2], b: [f32; 2], c: [f32; 2]) -> f32 {
    cross(sub(b, a), sub(c, a))
}
struct GeometrySink(PathGeometry);
impl GeometrySink {
    fn polygon(&mut self, points: &[[f32; 2]]) -> Result<(), Error> {
        let mut figure = PathFigure::new(points[0], true);
        figure
            .segments
            .try_reserve_exact(points.len() - 1)
            .map_err(|_| Error::Capacity)?;
        figure.segments.extend(
            points[1..]
                .iter()
                .map(|p| PathSegment::new(PathSegmentKind::Line(*p))),
        );
        self.0.figures.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.0.figures.push(figure);
        Ok(())
    }
}
impl TriangleSink for GeometrySink {
    fn complete(&self) -> bool {
        false
    }
    fn triangle(&mut self, mut points: [[f32; 2]; 3]) -> Result<(), Error> {
        let a = area(points[0], points[1], points[2]);
        if !a.is_finite() || a.abs() <= EPSILON {
            return Ok(());
        }
        if a < 0.0 {
            points.swap(1, 2);
        }
        self.polygon(&points)
    }
    fn quad(&mut self, mut points: [[f32; 2]; 4]) -> Result<(), Error> {
        let a = area(points[0], points[1], points[2]) + area(points[0], points[2], points[3]);
        if !a.is_finite() || a.abs() <= EPSILON {
            return Ok(());
        }
        if a < 0.0 {
            points.swap(1, 3);
        }
        self.polygon(&points)
    }
}
struct HitSink {
    point: [f32; 2],
    contains: bool,
}
impl TriangleSink for HitSink {
    fn complete(&self) -> bool {
        self.contains
    }
    fn triangle(&mut self, p: [[f32; 2]; 3]) -> Result<(), Error> {
        if !self.contains {
            let c0 = area(p[0], p[1], self.point);
            let c1 = area(p[1], p[2], self.point);
            let c2 = area(p[2], p[0], self.point);
            let negative = c0 < -EPSILON || c1 < -EPSILON || c2 < -EPSILON;
            let positive = c0 > EPSILON || c1 > EPSILON || c2 > EPSILON;
            self.contains = !(negative && positive);
        }
        Ok(())
    }
    fn quad(&mut self, p: [[f32; 2]; 4]) -> Result<(), Error> {
        self.triangle([p[0], p[1], p[2]])?;
        self.triangle([p[0], p[2], p[3]])
    }
}
