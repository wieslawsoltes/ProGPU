//! Original compositor dash topology. Hidden intervals terminate runs even if
//! their final coordinate equals an earlier endpoint; analytic curves survive.
use super::*;
const EPS: f32 = 0.0001;

/// Two directed caps belonging to an original zero-length terminal interval.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DirectedStrokeCaps {
    /// Original terminal point.
    pub point: [f32; 2],
    /// Original adjacent source point defining its direction.
    pub adjacent: [f32; 2],
    /// Cap facing against the source direction.
    pub start_cap: PenLineCap,
    /// Cap facing along the source direction.
    pub end_cap: PenLineCap,
}
/// Atomically published dash generation, retaining separate terminal metadata.
#[derive(Clone, Debug, PartialEq)]
pub struct DashedStrokePath {
    /// Analytic visible contours, with source endpoint overrides and seams.
    pub path: PathGeometry,
    /// Present only when explicitly captured and at least one terminal exists.
    pub terminal_caps: Option<Vec<DirectedStrokeCaps>>,
}
impl PathGeometry {
    /// Original line/Bezier/arc dash traversal. Rational curves reject instead
    /// of flattening. None is source rejection; no partial output is published.
    pub fn dashed_stroke(
        &self,
        pen: &Pen,
        local_thickness: f32,
        capture_terminal_caps: bool,
    ) -> Result<Option<DashedStrokePath>, Error> {
        if self.deferred.is_some() {
            return Ok(None);
        }
        let thickness = if pen.is_hairline() {
            1.0
        } else {
            local_thickness
        };
        let Some(pattern) = DashPattern::new(
            pen.dash_array().unwrap_or_default(),
            pen.options.dash_offset,
            f64::from(thickness),
        )?
        else {
            return Ok(None);
        };
        let mut output = DashedStrokePath {
            path: PathGeometry {
                fill_rule: self.fill_rule,
                ..Default::default()
            },
            terminal_caps: None,
        };
        for source in &self.figures {
            if !finite(source.start) {
                return Ok(None);
            }
            let first = output.path.figures.len();
            let mut writer = Writer {
                output: &mut output.path,
                pattern: &pattern,
                state: pattern.initial_state(),
                active: None,
                end: [0.0; 2],
            };
            let mut start_eligible = writer.state.index & 1 == 0;
            let mut traversed = false;
            let mut current = source.start;
            for segment in &source.segments {
                let start = current;
                let starts_visible = writer.state.index & 1 == 0;
                if !segment.is_stroked {
                    if writer.output.figures.len() == first {
                        start_eligible = false;
                    }
                    traversed = false;
                    current = segment.end();
                    writer.state = pattern.initial_state();
                    writer.active = None;
                    writer.end = [0.0; 2];
                    continue;
                }
                let spans = match segment.kind {
                    PathSegmentKind::Line(end) => {
                        if near(start, end) {
                            current = end;
                            continue;
                        }
                        writer.line(start, end, segment.is_smooth_join)?;
                        None
                    }
                    PathSegmentKind::Quadratic(points) => {
                        if points.iter().all(|p| *p == start) {
                            continue;
                        }
                        Some(segment.bezier_dashes(
                            start,
                            pattern.intervals(),
                            writer.state,
                            DEFAULT_LENGTH_SEGMENTS,
                        )?)
                    }
                    PathSegmentKind::Cubic(points) => {
                        if points.iter().all(|p| *p == start) {
                            continue;
                        }
                        Some(segment.bezier_dashes(
                            start,
                            pattern.intervals(),
                            writer.state,
                            DEFAULT_LENGTH_SEGMENTS,
                        )?)
                    }
                    PathSegmentKind::Arc(arc) => {
                        if arc.end == start
                            && arc.size.iter().all(|v| v.is_finite() && *v >= 0.0)
                            && arc.rotation.is_finite()
                        {
                            continue;
                        }
                        Some(segment.arc_dashes(
                            start,
                            pattern.intervals(),
                            writer.state,
                            ArcSegment::DEFAULT_DASH_ANGLE,
                        )?)
                    }
                    _ => return Ok(None),
                };
                if let Some(result) = spans {
                    let Some(spans) = result.segments else {
                        return Ok(None);
                    };
                    if spans.is_empty() {
                        writer.active = None;
                    }
                    for (index, mut span) in spans.into_iter().enumerate() {
                        let at_start = index == 0 && starts_visible;
                        if !at_start {
                            writer.active = None;
                        }
                        span.segment.is_smooth_join = at_start && segment.is_smooth_join;
                        writer.append(span.start, span.segment)?;
                    }
                    writer.state = result.final_state;
                }
                current = segment.end();
                traversed = true;
                if !matches!(segment.kind, PathSegmentKind::Line(_))
                    && (writer.state.index & 1 != 0 || writer.state.distance <= EPS)
                {
                    writer.active = None;
                }
            }
            if source.is_closed && distance_squared(current, source.start) > EPS * EPS {
                writer.line(current, source.start, false)?;
                traversed = true;
            }
            let end_eligible = traversed
                && if writer.state.index & 1 == 0 {
                    writer.state.distance > EPS
                } else {
                    writer.state.distance <= EPS
                };
            if source.is_closed {
                if start_eligible && end_eligible {
                    merge_seam(
                        writer.output,
                        first,
                        source.start,
                        source.segments.first().is_some_and(|s| s.is_smooth_join),
                    )?;
                }
            } else {
                let start_cap = source.start_cap.unwrap_or(pen.options.start_cap);
                let end_cap = source.end_cap.unwrap_or(pen.options.end_cap);
                if capture_terminal_caps
                    && traversed
                    && writer.state.index & 1 == 0
                    && writer.state.distance <= EPS
                    && (pen.options.dash_cap != PenLineCap::Flat || end_cap != PenLineCap::Flat)
                {
                    if !source
                        .segments
                        .last()
                        .is_some_and(|s| matches!(s.kind, PathSegmentKind::Line(_)))
                    {
                        return Ok(None);
                    }
                    let count = source.segments.len();
                    let adjacent = if count > 1 {
                        source.segments[count - 2].end()
                    } else {
                        source.start
                    };
                    push(
                        output.terminal_caps.get_or_insert_with(Vec::new),
                        DirectedStrokeCaps {
                            point: current,
                            adjacent,
                            start_cap: pen.options.dash_cap,
                            end_cap,
                        },
                    )?;
                }
                if first < writer.output.figures.len() {
                    let figure = &mut writer.output.figures[first];
                    if start_eligible
                        && start_cap != pen.options.dash_cap
                        && near(figure.start, source.start)
                    {
                        figure.start_cap = Some(start_cap);
                    }
                    let figure = writer.output.figures.last_mut().unwrap();
                    if end_eligible
                        && end_cap != pen.options.dash_cap
                        && figure
                            .segments
                            .last()
                            .is_some_and(|s| near(s.end(), current))
                    {
                        figure.end_cap = Some(end_cap);
                    }
                }
            }
        }
        Ok(Some(output))
    }
}
impl Pen {
    /// Original normalized derived pen: dash caps become all three endpoint
    /// defaults, while the independent join and width-frame policies survive.
    pub fn undashed(&self, local_thickness: f32) -> Result<Self, Error> {
        Self::with_options(
            self.brush.clone(),
            PenOptions {
                thickness: if self.is_hairline() {
                    Self::HAIRLINE_THICKNESS
                } else {
                    local_thickness
                },
                start_cap: self.options.dash_cap,
                end_cap: self.options.dash_cap,
                dash_offset: 0.0,
                ..self.options
            },
            None,
        )
    }
}
pub(super) fn push<T>(values: &mut Vec<T>, value: T) -> Result<(), Error> {
    values.try_reserve(1).map_err(|_| Error::Capacity)?;
    values.push(value);
    Ok(())
}
fn distance_squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = sub(a, b);
    dot(d, d)
}
fn near(a: [f32; 2], b: [f32; 2]) -> bool {
    distance_squared(a, b) <= EPS * EPS
}
struct Writer<'a> {
    output: &'a mut PathGeometry,
    pattern: &'a DashPattern,
    state: DashState,
    active: Option<usize>,
    end: [f32; 2],
}
impl Writer<'_> {
    fn append(&mut self, start: [f32; 2], mut segment: PathSegment) -> Result<(), Error> {
        let end = segment.end();
        if matches!(segment.kind, PathSegmentKind::Line(_)) && near(start, end) {
            return Ok(());
        }
        segment.is_stroked = true;
        if let Some(index) = self.active
            && near(self.end, start)
        {
            push(&mut self.output.figures[index].segments, segment)?;
        } else {
            segment.is_smooth_join = false;
            let mut figure = PathFigure::new(start, false);
            figure.is_filled = false;
            push(&mut figure.segments, segment)?;
            let index = self.output.figures.len();
            push(&mut self.output.figures, figure)?;
            self.active = Some(index);
        }
        self.end = end;
        Ok(())
    }
    fn line(&mut self, start: [f32; 2], end: [f32; 2], smooth: bool) -> Result<(), Error> {
        let intervals = self.pattern.intervals();
        if !DashPattern::validate_state(intervals, self.state) {
            return Ok(());
        }
        let mut state = self.state;
        dash::normalize(intervals, &mut state)?;
        let delta = sub(end, start);
        let total = length(delta);
        if total <= EPS {
            return Ok(());
        }
        let direction = [delta[0] / total, delta[1] / total];
        let mut distance = 0.0;
        let mut stalled = 0usize;
        while distance < total - EPS {
            let remaining = intervals[state.index as usize] - state.distance;
            let step = minimum(remaining, total - distance);
            let visible = state.index & 1 == 0;
            if visible && step > EPS {
                let mut segment = PathSegment::new(PathSegmentKind::Line(add(
                    start,
                    mul(direction, distance + step),
                )));
                segment.is_smooth_join = distance <= EPS && smooth;
                self.append(add(start, mul(direction, distance)), segment)?;
            }
            if !visible || step >= remaining - EPS {
                self.active = None;
                self.end = [0.0; 2];
            }
            dash::advance(intervals, &mut state, remaining, step);
            let next = distance + step;
            if next == distance {
                stalled += 1;
                if stalled > intervals.len() {
                    return Err(Error::InvalidInput);
                }
            } else {
                stalled = 0;
            }
            distance = next;
        }
        self.state = state;
        Ok(())
    }
}
fn merge_seam(
    path: &mut PathGeometry,
    first: usize,
    seam: [f32; 2],
    smooth: bool,
) -> Result<(), Error> {
    if first >= path.figures.len()
        || distance_squared(path.figures[first].start, seam) > EPS * EPS
        || path
            .figures
            .last()
            .unwrap()
            .segments
            .last()
            .is_none_or(|s| distance_squared(s.end(), seam) > EPS * EPS)
    {
        return Ok(());
    }
    path.figures[first].segments[0].is_smooth_join = smooth;
    if first + 1 == path.figures.len() {
        let figure = &mut path.figures[first];
        figure.is_closed = true;
        figure.start_cap = None;
        figure.end_cap = None;
    } else {
        let count = path.figures[first].segments.len();
        path.figures
            .last_mut()
            .unwrap()
            .segments
            .try_reserve(count)
            .map_err(|_| Error::Capacity)?;
        let first = path.figures.remove(first);
        let last = path.figures.last_mut().unwrap();
        last.segments.extend(first.segments);
        last.start_cap = None;
        last.end_cap = None;
    }
    Ok(())
}
