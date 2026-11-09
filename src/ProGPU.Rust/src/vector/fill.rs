//! Original PathAtlas figure compilation and bounded Boolean raster programs.
//! Source records retain raw coordinates; the immutable GPU Path constructor
//! separately enforces the native finite-input contract before publication.
use super::*;
use crate::path::{Path, RasterProgram, Segment};

const MAX_INSTRUCTIONS: usize = 63;
const MAX_STACK: usize = 16;
const OPERATION: u32 = 0x8000_0000;
const EMPTY: u32 = 0x4000_0000;
const QUERY_BUDGET: usize = 1 << 20;

/// One original query figure, including empty, unfilled and open source slots.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PathQueryFigure {
    /// Original move point, without normalization.
    pub start: [f32; 2],
    /// First emitted segment in the complete query inventory.
    pub first_segment: usize,
    /// Actual emitted count, including an original implicit closing edge.
    pub segment_count: usize,
    /// Original figure closure, independent of coincident endpoints.
    pub closed: bool,
    /// Original fill participation, independent of stroke flags.
    pub filled: bool,
}

/// Complete original stroke-query transport; no widening or flattening occurs.
#[derive(Clone, Debug, PartialEq)]
pub struct CompiledStrokeQuery {
    /// Every original figure slot, in source order.
    pub figures: Vec<PathQueryFigure>,
    /// Original six-family GPU segment records.
    pub segments: Vec<Segment>,
    /// Per emitted segment: bit 0 stroked, bit 1 incoming forced-round join.
    pub segment_flags: Vec<u8>,
}

impl PathGeometry {
    /// Compile all original figures, closing only explicitly closed figures.
    /// Deferred results requiring reconstruction return UnsupportedExecution;
    /// materialize them with BooleanSolver before using this synchronous API.
    pub fn compile_path(&self) -> Result<CompiledPath, Error> {
        compile(self, false, None)
    }

    /// Compile filled figures and implicitly close every admitted open figure.
    /// Stroke participation does not exclude edges from fill. Empty geometry
    /// has no record; invalid rational weights reject without partial output.
    pub fn compile_fill_path(&self) -> Result<CompiledPath, Error> {
        compile(self, true, None)
    }

    /// Retain original figure slots, gaps, smooth joins and degenerate arcs.
    /// Combined geometry and per-figure cap overrides require separate source
    /// materialization/pens, matching the original stroke-query admission.
    pub fn compile_stroke_query(&self) -> Result<CompiledStrokeQuery, Error> {
        if self.deferred.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let mut budget = QUERY_BUDGET
            .checked_sub(self.figures.len())
            .ok_or(Error::Capacity)?;
        for figure in &self.figures {
            budget = budget
                .checked_sub(
                    figure
                        .segments
                        .len()
                        .checked_add(1)
                        .ok_or(Error::Capacity)?,
                )
                .ok_or(Error::Capacity)?;
            if figure.start_cap.is_some() || figure.end_cap.is_some() {
                return Err(Error::UnsupportedExecution);
            }
        }
        let mut query = CompiledStrokeQuery {
            figures: Vec::new(),
            segments: Vec::new(),
            segment_flags: Vec::new(),
        };
        query
            .figures
            .try_reserve(self.figures.len())
            .map_err(|_| Error::Capacity)?;
        query.segments = compile(self, false, Some(&mut query))?.segments;
        Ok(query)
    }

    /// Original complete-definition check used before accepting an empty hit
    /// result. This is independent of GPU raster or platform qualification.
    pub fn has_complete_hit_test_definition(&self) -> bool {
        let mut pending = vec![(self, 0usize)];
        let mut budget = QUERY_BUDGET;
        while let Some((path, depth)) = pending.pop() {
            if depth > 64 || budget == 0 {
                return false;
            }
            budget -= 1;
            if let Some(deferred) = &path.deferred {
                let (a, b) = deferred.operands();
                pending.push((b, depth + 1));
                pending.push((a, depth + 1));
            } else {
                let Ok(query) = path.compile_stroke_query() else {
                    return false;
                };
                let Some(remaining) =
                    budget.checked_sub(query.figures.len() + query.segments.len())
                else {
                    return false;
                };
                budget = remaining;
                if query.figures.iter().any(|f| !finite(f.start))
                    || query.segments.iter().any(|s| {
                        s.points.iter().any(|p| !finite(*p))
                            || (s.kind == 3
                                && s.parameters.iter().any(|p| !f32::from_bits(*p).is_finite()))
                    })
                {
                    return false;
                }
            }
        }
        true
    }

    /// Compile original fill geometry into an immutable retained raster input.
    /// Selects direct two-operand evaluation, then the original 63-instruction /
    /// 16-value postfix program, then ordinary fill compilation. Empty source
    /// results return None. UnsupportedExecution requires GPU reconstruction;
    /// BooleanSolver::prepare_raster_path supplies that asynchronous fallback.
    /// Native finite-input admission is applied only after source compilation.
    pub fn compile_raster_path(&self) -> Result<Option<Path>, Error> {
        if let Some(deferred) = &self.deferred
            && !matches!(
                deferred.query_kind(),
                CombinedQueryKind::Empty
                    | CombinedQueryKind::ResultOperandA
                    | CombinedQueryKind::ResultOperandB
            )
        {
            let (a, b) = deferred.operands();
            if a.deferred.is_none()
                && b.deferred.is_none()
                && let Some(mut a) = filled(a)?
                && let Some(b) = filled(b)?
            {
                let first = a.record.ok_or(Error::InvalidInput)?;
                let mut second = b.record.ok_or(Error::InvalidInput)?;
                second.start_segment =
                    u32::try_from(a.segments.len()).map_err(|_| Error::Capacity)?;
                a.segments
                    .try_reserve(b.segments.len())
                    .map_err(|_| Error::Capacity)?;
                a.segments.extend(b.segments);
                let bounds = self.bounds().ok_or(Error::InvalidInput)?;
                return Path::with_program(
                    a.segments,
                    bounds,
                    self.fill_rule.raster_rule(),
                    RasterProgram {
                        records: vec![first, second].into_boxed_slice(),
                        tokens: Box::default(),
                        operation: deferred.operation() as u32 + 1,
                        index_b: 1,
                    },
                )
                .map(Some);
            }
            if let Some((segments, program)) = boolean_program(self)? {
                let bounds = self.bounds().ok_or(Error::InvalidInput)?;
                return Path::with_program(segments, bounds, self.fill_rule.raster_rule(), program)
                    .map(Some);
            }
        }
        let Some(compiled) = filled(self)? else {
            return Ok(None);
        };
        let record = compiled.record.ok_or(Error::InvalidInput)?;
        Path::new(
            compiled.segments,
            record.bounds,
            self.fill_rule_for_compilation()?.raster_rule(),
        )
        .map(Some)
    }

    fn fill_rule_for_compilation(&self) -> Result<FillRule, Error> {
        Ok(compilation_source(self)?.fill_rule)
    }
}

// Source TryGetCompiledFillPath catches invalid rational-weight compilation.
// Do not catch native Path admission errors or turn capacity errors into empty.
fn filled(path: &PathGeometry) -> Result<Option<CompiledPath>, Error> {
    match path.compile_fill_path() {
        Ok(path) => Ok(path.record.is_some().then_some(path)),
        Err(Error::InvalidInput) => Ok(None),
        Err(error) => Err(error),
    }
}

fn compilation_source(mut path: &PathGeometry) -> Result<&PathGeometry, Error> {
    while let Some(deferred) = &path.deferred {
        let (a, b) = deferred.operands();
        let operand = match deferred.query_kind() {
            CombinedQueryKind::Empty => return Ok(path),
            CombinedQueryKind::ResultOperandA => a,
            CombinedQueryKind::ResultOperandB => b,
            _ => return Err(Error::UnsupportedExecution),
        };
        if path.fill_rule != operand.fill_rule && operand.figures.len() != 1 {
            return Err(Error::UnsupportedExecution);
        }
        path = operand;
    }
    Ok(path)
}

fn compile(
    source: &PathGeometry,
    fill_only: bool,
    mut query: Option<&mut CompiledStrokeQuery>,
) -> Result<CompiledPath, Error> {
    let path = compilation_source(source)?;
    if path.deferred.is_some() {
        return Ok(CompiledPath {
            record: None,
            segments: Vec::new(),
        });
    }
    let capacity = path.figures.iter().try_fold(0usize, |n, figure| {
        if (fill_only && !figure.is_filled) || figure.segments.is_empty() {
            return Ok(n);
        }
        n.checked_add(figure.segments.len())
            .and_then(|n| n.checked_add(usize::from(fill_only || figure.is_closed)))
            .filter(|n| *n <= u32::MAX as usize)
            .ok_or(Error::Capacity)
    })?;
    let mut segments = Vec::new();
    segments
        .try_reserve(capacity)
        .map_err(|_| Error::Capacity)?;
    if let Some(query) = &mut query {
        query
            .segment_flags
            .try_reserve(capacity)
            .map_err(|_| Error::Capacity)?;
    }
    let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    let mut update = |p: [f32; 2]| {
        bounds = [
            minimum(bounds[0], p[0]),
            minimum(bounds[1], p[1]),
            maximum(bounds[2], p[0]),
            maximum(bounds[3], p[1]),
        ];
    };
    for figure in &path.figures {
        let first_segment = segments.len();
        if (!fill_only || figure.is_filled) && !figure.segments.is_empty() {
            let mut current = figure.start;
            update(current);
            for source in &figure.segments {
                let segment = match source.kind {
                    PathSegmentKind::Line(end) => {
                        update(end);
                        Some(Segment::line(current, end))
                    }
                    PathSegmentKind::Quadratic([control, end]) => {
                        update(control);
                        update(end);
                        Some(Segment::quadratic([current, control, end]))
                    }
                    PathSegmentKind::Cubic([c1, c2, end]) => {
                        update(c1);
                        update(c2);
                        update(end);
                        Some(Segment::cubic([current, c1, c2, end]))
                    }
                    PathSegmentKind::RationalQuadratic {
                        points: [control, end],
                        weight,
                    } => {
                        let magnitude = maximum(
                            point_scale(current),
                            maximum(point_scale(control), point_scale(end)),
                        );
                        check_weights(&[weight], magnitude, 4.0)?;
                        update(control);
                        update(end);
                        Some(Segment {
                            kind: 4,
                            parameters: [weight.to_bits(), 0, 0],
                            ..Segment::quadratic([current, control, end])
                        })
                    }
                    PathSegmentKind::RationalCubic {
                        points: [c1, c2, end],
                        weights,
                    } => {
                        let magnitude = maximum(
                            point_scale(current),
                            maximum(point_scale(c1), maximum(point_scale(c2), point_scale(end))),
                        );
                        check_weights(&weights, magnitude, 8.0)?;
                        update(c1);
                        update(c2);
                        update(end);
                        Some(Segment {
                            kind: 5,
                            parameters: [weights[0].to_bits(), weights[1].to_bits(), 0],
                            ..Segment::cubic([current, c1, c2, end])
                        })
                    }
                    PathSegmentKind::Arc(arc) => {
                        if let Some(center) = arc.center(current) {
                            if let Some(b) = arc.bounds(current) {
                                update([b[0], b[1]]);
                                update([b[2], b[3]]);
                            } else {
                                update(current);
                                update(arc.end);
                            }
                            Some(Segment {
                                points: [current, arc.end, center.center, center.radii],
                                kind: 3,
                                parameters: [
                                    center.theta.to_bits(),
                                    center.delta.to_bits(),
                                    (arc.rotation * std::f32::consts::PI / 180.0).to_bits(),
                                ],
                            })
                        } else {
                            update(arc.end);
                            (current != arc.end || query.is_some())
                                .then(|| Segment::line(current, arc.end))
                        }
                    }
                };
                if let Some(segment) = segment {
                    segments.push(segment);
                }
                if let Some(query) = &mut query {
                    query
                        .segment_flags
                        .push(u8::from(source.is_stroked) | (u8::from(source.is_smooth_join) << 1));
                }
                current = source.end();
            }
            if (fill_only || figure.is_closed) && current != figure.start {
                segments.push(Segment::line(current, figure.start));
                update(figure.start);
                if let Some(query) = &mut query {
                    query.segment_flags.push(1);
                }
            }
        }
        if let Some(query) = &mut query {
            query.figures.push(PathQueryFigure {
                start: figure.start,
                first_segment,
                segment_count: segments.len() - first_segment,
                closed: figure.is_closed,
                filled: figure.is_filled,
            });
        }
    }
    let record = (!segments.is_empty() && query.is_none()).then(|| GpuPathRecord {
        start_segment: 0,
        segment_count: segments.len() as u32,
        bounds,
        fill_rule: path.fill_rule.raster_rule() as u32,
        pad: 0,
    });
    Ok(CompiledPath { record, segments })
}

fn point_scale(point: [f32; 2]) -> f32 {
    maximum(point[0].abs(), point[1].abs())
}
fn check_weights(weights: &[f32], magnitude: f32, factor: f64) -> Result<(), Error> {
    // Math.Max propagates NaN here; native finite-point admission is separate.
    let scale = if magnitude.is_nan() {
        f64::NAN
    } else {
        f64::from(magnitude).max(1.0)
    };
    let limit = f64::from(f32::MAX) / (factor * scale);
    if weights
        .iter()
        .any(|w| !w.is_finite() || *w <= 0.0 || f64::from(*w) > limit)
    {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}

fn boolean_program(root: &PathGeometry) -> Result<Option<(Vec<Segment>, RasterProgram)>, Error> {
    enum Task<'a> {
        Visit(&'a PathGeometry),
        Emit(BooleanOperation),
    }
    let mut pending = vec![Task::Visit(root)];
    let mut records = Vec::new();
    let mut segments = Vec::new();
    let mut tokens = Vec::with_capacity(MAX_INSTRUCTIONS);
    let mut depth = 0usize;
    while let Some(task) = pending.pop() {
        if tokens.len() >= MAX_INSTRUCTIONS {
            return Ok(None);
        }
        let token = match task {
            Task::Emit(op) => {
                if depth < 2 {
                    return Ok(None);
                }
                depth -= 1;
                OPERATION | (op as u32 + 1)
            }
            Task::Visit(path) => {
                if let Some(deferred) = &path.deferred {
                    let (a, b) = deferred.operands();
                    match deferred.query_kind() {
                        CombinedQueryKind::Empty => {
                            depth += 1;
                            EMPTY
                        }
                        CombinedQueryKind::ResultOperandA => {
                            pending.push(Task::Visit(a));
                            continue;
                        }
                        CombinedQueryKind::ResultOperandB => {
                            pending.push(Task::Visit(b));
                            continue;
                        }
                        _ => {
                            pending.try_reserve(3).map_err(|_| Error::Capacity)?;
                            pending.push(Task::Emit(deferred.operation()));
                            pending.push(Task::Visit(b));
                            pending.push(Task::Visit(a));
                            continue;
                        }
                    }
                } else {
                    let Some(compiled) = filled(path)? else {
                        return Ok(None);
                    };
                    let mut record = compiled.record.ok_or(Error::InvalidInput)?;
                    record.start_segment =
                        u32::try_from(segments.len()).map_err(|_| Error::Capacity)?;
                    segments
                        .try_reserve(compiled.segments.len())
                        .map_err(|_| Error::Capacity)?;
                    segments.extend(compiled.segments);
                    let index = records.len() as u32;
                    records.push(record);
                    depth += 1;
                    index
                }
            }
        };
        if depth > MAX_STACK {
            return Ok(None);
        }
        tokens.push(GpuPathRecord {
            start_segment: token,
            segment_count: 0,
            bounds: [0.0; 4],
            fill_rule: 0,
            pad: 0,
        });
    }
    if depth != 1 || records.is_empty() || segments.is_empty() || tokens.is_empty() {
        return Ok(None);
    }
    Ok(Some((
        segments,
        RasterProgram {
            index_b: records.len() as u32,
            operation: OPERATION | tokens.len() as u32,
            records: records.into_boxed_slice(),
            tokens: tokens.into_boxed_slice(),
        },
    )))
}
