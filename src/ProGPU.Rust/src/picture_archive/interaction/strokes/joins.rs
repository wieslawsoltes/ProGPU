//! Original explicit WPF/clipped/miter-or-bevel body and join input ownership.
use super::*;
#[derive(Clone)]
struct Run {
    start: [f32; 2],
    end: [f32; 2],
    segment: PathSegment,
    incoming: Option<[f32; 2]>,
    outgoing: Option<[f32; 2]>,
}
impl ArchiveHitTestCache {
    pub(super) fn source_stroke(
        &mut self,
        path: &PathGeometry,
        m: Matrix4,
        id: i32,
        z: f32,
        pen: &Pen,
        width: f32,
    ) -> Result<bool> {
        let primitive_start = self.primitives.len();
        let segment_start = self.segments.len();
        let bounds = self.bounds;
        let result = (|| {
            if path.deferred.is_some() {
                return Err(ArchiveError::Unsupported);
            }
            let mut run = Vec::new();
            for f in &path.figures {
                run.clear();
                let mut current = f.start;
                let selected = [
                    f.start_cap.unwrap_or(pen.options.start_cap),
                    f.end_cap.unwrap_or(pen.options.end_cap),
                ];
                let mut degenerate = None;
                for segment in &f.segments {
                    let end = segment.end();
                    if !finite(current) || !finite(end) {
                        return Err(ArchiveError::Unsupported);
                    }
                    if !segment.is_stroked {
                        self.source_run(
                            &mut run,
                            false,
                            !f.is_closed,
                            f.start,
                            current,
                            selected,
                            m,
                            id,
                            z,
                            pen,
                            width,
                        )?;
                        if run.is_empty() && !f.is_closed {
                            if let Some(point) = degenerate {
                                self.degenerate_caps(point, selected, pen, width, m, id, z)?;
                            }
                        }
                        run.clear();
                        degenerate = None;
                        current = end;
                        continue;
                    }
                    let incoming = tangent(segment.kind, current, true);
                    let outgoing = tangent(segment.kind, current, false);
                    let delta = sub(current, end);
                    if incoming.is_some()
                        || outgoing.is_some()
                        || delta[0] * delta[0] + delta[1] * delta[1] > 0.00000001
                    {
                        push(
                            &mut run,
                            Run {
                                start: current,
                                end,
                                segment: segment.clone(),
                                incoming,
                                outgoing,
                            },
                        )?;
                        degenerate = None;
                    } else if run.is_empty() && !f.is_closed {
                        degenerate = Some(current);
                    }
                    current = end;
                }
                self.source_run(
                    &mut run,
                    f.is_closed,
                    !f.is_closed,
                    f.start,
                    current,
                    selected,
                    m,
                    id,
                    z,
                    pen,
                    width,
                )?;
                if run.is_empty() && !f.is_closed {
                    if let Some(point) = degenerate {
                        self.degenerate_caps(point, selected, pen, width, m, id, z)?;
                    }
                }
            }
            Ok(true)
        })();
        if result.is_err() {
            self.primitives.truncate(primitive_start);
            self.segments.truncate(segment_start);
            self.bounds = bounds;
        }
        result
    }
    fn degenerate_caps(
        &mut self,
        mut point: [f32; 2],
        caps: [PenLineCap; 2],
        pen: &Pen,
        mut width: f32,
        mut m: Matrix4,
        id: i32,
        z: f32,
    ) -> Result<()> {
        if device(pen) || !requires_affine(m) {
            point = m.transform_point(point);
            if !device(pen) {
                width *= scale(m);
            }
            m = Matrix4::IDENTITY;
        }
        self.append(HitPrimitive::line_stroke(
            id, point, point, width, caps, 0.0, m, z,
        ))
    }
    fn source_run(
        &mut self,
        run: &mut Vec<Run>,
        close: bool,
        with_caps: bool,
        start: [f32; 2],
        current: [f32; 2],
        caps: [PenLineCap; 2],
        m: Matrix4,
        id: i32,
        z: f32,
        pen: &Pen,
        width: f32,
    ) -> Result<()> {
        if run.is_empty() {
            if close && current != start {
                self.source_body(
                    current,
                    PathSegment::new(PathSegmentKind::Line(start)),
                    [PenLineCap::Flat; 2],
                    pen,
                    width,
                    m,
                    id,
                    z,
                )?;
            }
            return Ok(());
        }
        let source_count = run.len();
        if close && run.last().is_some_and(|r| r.end != start) {
            let previous = run.last().ok_or(ArchiveError::InvalidData)?.end;
            let direction = sub(start, previous);
            push(
                run,
                Run {
                    start: previous,
                    end: start,
                    segment: PathSegment::new(PathSegmentKind::Line(start)),
                    incoming: Some(direction),
                    outgoing: Some(direction),
                },
            )?;
        }
        for (i, s) in run.iter().enumerate() {
            self.source_body(
                s.start,
                s.segment.clone(),
                [
                    if with_caps && i == 0 {
                        caps[0]
                    } else {
                        PenLineCap::Flat
                    },
                    if with_caps && i == run.len() - 1 {
                        caps[1]
                    } else {
                        PenLineCap::Flat
                    },
                ],
                pen,
                width,
                m,
                id,
                z,
            )?;
            if i > 0 {
                if let (Some(incoming), Some(outgoing)) = (run[i - 1].outgoing, s.incoming) {
                    self.source_join(
                        pen,
                        width,
                        s.start,
                        incoming,
                        outgoing,
                        s.segment.is_smooth_join,
                        m,
                        id,
                        z,
                    )?;
                }
            }
        }
        if close {
            if let (Some(incoming), Some(outgoing)) =
                (run.last().and_then(|s| s.outgoing), run[0].incoming)
            {
                self.source_join(
                    pen,
                    width,
                    start,
                    incoming,
                    outgoing,
                    run[0].segment.is_smooth_join,
                    m,
                    id,
                    z,
                )?;
            }
        }
        run.truncate(source_count);
        Ok(())
    }
    fn source_body(
        &mut self,
        start: [f32; 2],
        segment: PathSegment,
        caps: [PenLineCap; 2],
        pen: &Pen,
        mut width: f32,
        mut m: Matrix4,
        id: i32,
        z: f32,
    ) -> Result<()> {
        let mut figure = PathFigure::new(start, false);
        figure.is_filled = false;
        figure.start_cap = Some(caps[0]);
        figure.end_cap = Some(caps[1]);
        push(&mut figure.segments, segment)?;
        let mut body = PathGeometry::default();
        push(&mut body.figures, figure)?;
        if device(pen) {
            if !self.device_stroke(&body, m, id, z, pen, false)? {
                return Err(ArchiveError::Unsupported);
            }
            return Ok(());
        }
        if !requires_affine(m) {
            body = body.transformed(m)?;
            width *= scale(m);
            m = Matrix4::IDENTITY;
        }
        let Some(compiled) = self
            .compile_path(&body)?
            .filter(|p| p.bounds.iter().all(|v| v.is_finite()))
        else {
            return Err(ArchiveError::Unsupported);
        };
        self.path_stroke(compiled, m, id, z, width, caps)
    }
    fn source_join(
        &mut self,
        pen: &Pen,
        mut width: f32,
        mut center: [f32; 2],
        mut incoming: [f32; 2],
        mut outgoing: [f32; 2],
        smooth: bool,
        m: Matrix4,
        id: i32,
        z: f32,
    ) -> Result<()> {
        let affine = !device(pen) && requires_affine(m);
        if !affine {
            center = m.transform_point(center);
            incoming = direction(m, incoming);
            outgoing = direction(m, outgoing);
            if !device(pen) {
                width *= scale(m);
            }
        }
        let frame = if pen.options.use_wpf_join_semantics || device(pen) {
            StrokeJoinFrame {
                point: center,
                incoming,
                outgoing,
            }
        } else {
            StrokeJoinFrame::from_line(sub(center, incoming), center, add(center, outgoing))
        };
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = pen.write_join(
            &mut triangles,
            frame,
            width,
            smooth,
            MAX_STROKE_TRIANGLES as i32,
        )?;
        for triangle in &triangles[..count] {
            let points = triangle.points;
            if points.iter().any(|p| !finite(*p)) {
                return Err(ArchiveError::Unsupported);
            }
            let points = if affine {
                points.map(|p| m.transform_point(p))
            } else {
                points
            };
            let a = sub(points[1], points[0]);
            let b = sub(points[2], points[0]);
            if (a[0] * b[1] - a[1] * b[0]).abs() <= 0.0001 {
                continue;
            }
            let Some(compiled) = self.compile_path(&polygon(&points, true)?)? else {
                return Err(ArchiveError::Unsupported);
            };
            self.path_fill(compiled, Matrix4::IDENTITY, id, z)?;
        }
        Ok(())
    }
}
fn scale(m: Matrix4) -> f32 {
    m.stroke_scales().map_or(1.0, |s| s[0])
}
fn direction(m: Matrix4, p: [f32; 2]) -> [f32; 2] {
    sub(m.transform_point(p), m.transform_point([0.0; 2]))
}
fn requires_affine(m: Matrix4) -> bool {
    let x = [m.0[0][0], m.0[0][1]];
    let y = [m.0[1][0], m.0[1][1]];
    let a = length(x);
    let b = length(y);
    if !a.is_finite() || !b.is_finite() || a <= 0.0001 || b <= 0.0001 {
        return false;
    }
    (a - b).abs() > a.max(b) * 0.0001 || (x[0] * y[0] + x[1] * y[1]).abs() > a * b * 0.0001
}
fn tangent(segment: PathSegmentKind, start: [f32; 2], first: bool) -> Option<[f32; 2]> {
    let select = |p: &[[f32; 2]]| {
        p.iter().copied().find(|v| {
            let n = length(*v);
            n.is_finite() && n > 0.0001
        })
    };
    match segment {
        PathSegmentKind::Line(end) => select(&[sub(end, start)]),
        PathSegmentKind::Quadratic([c, end])
        | PathSegmentKind::RationalQuadratic {
            points: [c, end], ..
        } => select(&[
            if first { sub(c, start) } else { sub(end, c) },
            sub(end, start),
        ]),
        PathSegmentKind::Cubic([a, b, end])
        | PathSegmentKind::RationalCubic {
            points: [a, b, end],
            ..
        } => select(&if first {
            [sub(a, start), sub(b, start), sub(end, start)]
        } else {
            [sub(end, b), sub(end, a), sub(end, start)]
        }),
        PathSegmentKind::Arc(arc) => {
            let c = arc.center(start)?;
            let phi = arc.rotation * std::f32::consts::PI / 180.0;
            let x = [c.radii[0] * phi.cos(), c.radii[0] * phi.sin()];
            let y = [-c.radii[1] * phi.sin(), c.radii[1] * phi.cos()];
            let theta = if first { c.theta } else { c.theta + c.delta };
            let v = mul(
                add(mul(mul(x, -1.0), theta.sin()), mul(y, theta.cos())),
                if c.delta < 0.0 { -1.0 } else { 1.0 },
            );
            select(&[v])
        }
    }
}
