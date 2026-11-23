//! Precise native input primitives and original stroke union construction.
use super::*;
impl Capture<'_> {
    pub(super) fn rectangle(
        &mut self,
        r: wire::ImageRect,
        t: wire::Affine2d,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        if (r.width == 0.0 || r.height == 0.0) && self.query != HIT_TEST_POINT_ONLY {
            return Ok(());
        }
        if finite_transform(t)
            && f64::from(t.m11) * f64::from(t.m22) == f64::from(t.m12) * f64::from(t.m21)
        {
            return Ok(());
        }
        let mut hit = wire::HitTestPrimitive {
            kind: HIT_TEST_RECTANGLE_FILL,
            data0: f4(r.x, r.y, r.x + r.width, r.y + r.height),
            ..Default::default()
        };
        if !math::place(&mut hit, p2(r.x, r.y), p2(r.x + r.width, r.y + r.height), t) {
            return Err(B::UnsupportedHitTest);
        }
        self.append(hit, state, index)
    }
    fn ellipse(
        &mut self,
        min: wire::Point,
        max: wire::Point,
        width: f32,
        t: wire::Affine2d,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        let rx = (max.x - min.x) * 0.5;
        let ry = (max.y - min.y) * 0.5;
        let mut hit = wire::HitTestPrimitive {
            kind: if width > 0.0 {
                HIT_TEST_ELLIPSE_STROKE
            } else {
                HIT_TEST_ELLIPSE_FILL
            },
            data0: f4(min.x, min.y, max.x, max.y),
            data1: f4(width, 0.0, 0.0, 0.0),
            data2: f4(
                (min.x + max.x) * 0.5,
                (min.y + max.y) * 0.5,
                if rx.abs() > 0.0001 { 1.0 / rx } else { 0.0 },
                if ry.abs() > 0.0001 { 1.0 / ry } else { 0.0 },
            ),
            ..Default::default()
        };
        let pad = width * 0.5;
        if !math::place(
            &mut hit,
            p2(min.x - pad, min.y - pad),
            p2(max.x + pad, max.y + pad),
            t,
        ) {
            return Err(B::UnsupportedHitTest);
        }
        self.append(hit, state, index)
    }
    fn line(
        &mut self,
        a: wire::Point,
        b: wire::Point,
        width: f32,
        start: u32,
        end: u32,
        t: wire::Affine2d,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        let mut hit = wire::HitTestPrimitive {
            kind: HIT_TEST_LINE_STROKE,
            data0: f4(a.x, a.y, b.x, b.y),
            data1: f4(width, 0.0, start as f32, end as f32),
            data2: math::line(a, b).ok_or(B::UnsupportedHitTest)?,
            ..Default::default()
        };
        let mut pad = width * 0.5;
        if start == STROKE_CAP_SQUARE || end == STROKE_CAP_SQUARE {
            pad *= 2.0f32.sqrt();
        }
        if !math::place(
            &mut hit,
            p2(minimum(a.x, b.x) - pad, minimum(a.y, b.y) - pad),
            p2(maximum(a.x, b.x) + pad, maximum(a.y, b.y) + pad),
            t,
        ) {
            return Err(B::UnsupportedHitTest);
        }
        self.append(hit, state, index)
    }
    fn triangle(
        &mut self,
        points: triangles::Triangle,
        t: wire::Affine2d,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        if !points.into_iter().all(finite_point) || self.segments.len() > EXACT_LIMIT - 3 {
            return Err(B::UnsupportedHitTest);
        }
        let min = p2(
            points.map(|p| p.x).into_iter().reduce(minimum).unwrap(),
            points.map(|p| p.y).into_iter().reduce(minimum).unwrap(),
        );
        let max = p2(
            points.map(|p| p.x).into_iter().reduce(maximum).unwrap(),
            points.map(|p| p.y).into_iter().reduce(maximum).unwrap(),
        );
        let mut hit = wire::HitTestPrimitive {
            kind: HIT_TEST_PATH_FILL,
            data0: f4(min.x, min.y, max.x, max.y),
            data1: f4(self.segments.len() as f32, 3.0, 1.0, 0.0),
            ..Default::default()
        };
        if !math::place(&mut hit, min, max, t) {
            return Err(B::UnsupportedHitTest);
        }
        for i in 0..3 {
            push(
                &mut self.segments,
                wire::PathSegment {
                    kind: PATH_SEGMENT_LINE,
                    p0: points[i],
                    p1: points[(i + 1) % 3],
                    ..Default::default()
                },
            )?;
        }
        self.append(hit, state, index)
    }
    fn triangles(
        &mut self,
        triangles: triangles::Triangles,
        t: wire::Affine2d,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        for triangle in &triangles.0[..triangles.1] {
            self.triangle(*triangle, t, state, index)?;
        }
        Ok(())
    }
    fn stroke_join(
        &mut self,
        source: &wire::Stroke,
        corner: wire::Point,
        incoming: wire::Point,
        outgoing: wire::Point,
        t: wire::Affine2d,
        scale: f32,
        affine: bool,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        let triangles = triangles::join(
            source.line_join,
            if affine {
                source.stroke_thickness
            } else {
                source.stroke_thickness * scale
            },
            source.miter_limit,
            if affine {
                corner
            } else {
                math::point(t, corner)
            },
            if affine {
                incoming
            } else {
                math::direction(t, incoming)
            },
            if affine {
                outgoing
            } else {
                math::direction(t, outgoing)
            },
            source.flags & POLYLINE_FLAG_WPF_JOIN_SEMANTICS != 0,
            source.flags & POLYLINE_FLAG_CLIP_MITER_AT_LIMIT != 0,
        );
        self.triangles(
            triangles,
            if affine {
                t
            } else {
                NativeSceneBuilder::identity_transform()
            },
            state,
            index,
        )
    }
    pub(super) fn geometry(
        &mut self,
        resource: &ResourceEntry,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        for i in 0..resource.payload.len() / wire::GeometryPrimitive::WIRE_SIZE {
            let s: wire::GeometryPrimitive = record(&resource.payload, i)?;
            let t = math::compose(s.transform, state.transform);
            if s.kind == GEOMETRY_ARC {
                if s.flags & !PRIMITIVE_FLAG_EDGE_ALIASED != 0
                    || s.p1.x <= 0.0
                    || s.p2.y <= 0.0
                    || s.p1.y != 0.0
                    || s.p2.x != 0.0
                    || s.p3.x != 0.0
                    || s.p3.y != std::f32::consts::PI * 2.0
                {
                    return Err(B::UnsupportedHitTest);
                }
                if s.stroke_thickness <= 0.0 {
                    continue;
                }
                self.ellipse(
                    p2(s.p0.x - s.p1.x, s.p0.y - s.p2.y),
                    p2(s.p0.x + s.p1.x, s.p0.y + s.p2.y),
                    s.stroke_thickness,
                    t,
                    state,
                    index,
                )?;
                continue;
            }
            let allowed =
                PRIMITIVE_FLAG_EDGE_ALIASED | PRIMITIVE_START_CAP_MASK | PRIMITIVE_END_CAP_MASK;
            if s.kind == GEOMETRY_PATH_JOIN {
                if s.flags
                    & !(PRIMITIVE_FLAG_EDGE_ALIASED
                        | PRIMITIVE_START_CAP_MASK
                        | PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT
                        | PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS)
                    != 0
                {
                    return Err(B::UnsupportedHitTest);
                }
                let (scale, _) = values::stroke_scales(&t).ok_or(B::UnsupportedHitTest)?;
                let affine = math::affine(t);
                let triangles = triangles::join(
                    (s.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT,
                    if affine {
                        s.stroke_thickness
                    } else {
                        s.stroke_thickness * scale
                    },
                    s.p3.x,
                    if affine { s.p0 } else { math::point(t, s.p0) },
                    if affine {
                        s.p1
                    } else {
                        math::direction(t, s.p1)
                    },
                    if affine {
                        s.p2
                    } else {
                        math::direction(t, s.p2)
                    },
                    s.flags & PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS != 0,
                    s.flags & PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT != 0,
                );
                self.triangles(
                    triangles,
                    if affine {
                        t
                    } else {
                        NativeSceneBuilder::identity_transform()
                    },
                    state,
                    index,
                )?;
                continue;
            }
            if s.kind == GEOMETRY_QUADRATIC_BEZIER || s.kind == GEOMETRY_CUBIC_BEZIER {
                if s.flags & !allowed != 0 {
                    return Err(B::UnsupportedHitTest);
                }
                if s.stroke_thickness <= 0.0 {
                    continue;
                }
                if self.segments.len() >= EXACT_LIMIT {
                    return Err(B::UnsupportedHitTest);
                }
                let cubic = s.kind == GEOMETRY_CUBIC_BEZIER;
                let segment = wire::PathSegment {
                    kind: if cubic {
                        PATH_SEGMENT_CUBIC
                    } else {
                        PATH_SEGMENT_QUADRATIC
                    },
                    p0: s.p0,
                    p1: s.p1,
                    p2: s.p2,
                    p3: if cubic { s.p3 } else { wire::Point::default() },
                    ..Default::default()
                };
                let min = p2(
                    minimum(minimum(s.p0.x, s.p1.x), s.p2.x),
                    minimum(minimum(s.p0.y, s.p1.y), s.p2.y),
                );
                let max = p2(
                    maximum(maximum(s.p0.x, s.p1.x), s.p2.x),
                    maximum(maximum(s.p0.y, s.p1.y), s.p2.y),
                );
                let min = if cubic {
                    p2(minimum(min.x, s.p3.x), minimum(min.y, s.p3.y))
                } else {
                    min
                };
                let max = if cubic {
                    p2(maximum(max.x, s.p3.x), maximum(max.y, s.p3.y))
                } else {
                    max
                };
                let start = (s.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT;
                let end = (s.flags & PRIMITIVE_END_CAP_MASK) >> PRIMITIVE_END_CAP_SHIFT;
                let mut hit = wire::HitTestPrimitive {
                    kind: HIT_TEST_PATH_STROKE,
                    data0: f4(min.x, min.y, max.x, max.y),
                    data1: f4(self.segments.len() as f32, 1.0, s.stroke_thickness, 0.0),
                    data2: f4(start as f32, end as f32, 0.0, 0.0),
                    ..Default::default()
                };
                let pad = s.stroke_thickness
                    * 0.5
                    * if start == STROKE_CAP_SQUARE || end == STROKE_CAP_SQUARE {
                        std::f32::consts::SQRT_2
                    } else {
                        1.0
                    };
                if !math::place(
                    &mut hit,
                    p2(min.x - pad, min.y - pad),
                    p2(max.x + pad, max.y + pad),
                    t,
                ) {
                    return Err(B::UnsupportedHitTest);
                }
                push(&mut self.segments, segment)?;
                self.append(hit, state, index)?;
                continue;
            }
            if s.kind != GEOMETRY_LINE || s.flags & !allowed != 0 {
                return Err(B::UnsupportedHitTest);
            }
            if s.stroke_thickness <= 0.0 {
                continue;
            }
            self.line(
                s.p0,
                s.p1,
                s.stroke_thickness,
                (s.flags & PRIMITIVE_START_CAP_MASK) >> PRIMITIVE_START_CAP_SHIFT,
                (s.flags & PRIMITIVE_END_CAP_MASK) >> PRIMITIVE_END_CAP_SHIFT,
                t,
                state,
                index,
            )?;
        }
        Ok(())
    }
    pub(super) fn strokes(
        &mut self,
        resource: &ResourceEntry,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        let count = resource.payload.len() / wire::Stroke::WIRE_SIZE;
        for i in 0..count {
            let s: wire::Stroke = record(&resource.payload, i)?;
            let allowed = POLYLINE_FLAG_EDGE_ALIASED
                | POLYLINE_FLAG_CLOSED
                | POLYLINE_FLAG_WPF_JOIN_SEMANTICS
                | POLYLINE_FLAG_CLIP_MITER_AT_LIMIT;
            let closed = s.flags & POLYLINE_FLAG_CLOSED != 0;
            if s.kind != SCENE_STROKE_POLYLINE
                || s.flags & !allowed != 0
                || s.point_count < if closed { 3 } else { 2 }
                || s.stroke_thickness <= 0.0001
            {
                return Err(B::UnsupportedHitTest);
            }
            let t = math::compose(s.transform, state.transform);
            let (scale, _) = values::stroke_scales(&t).ok_or(B::UnsupportedHitTest)?;
            let affine = math::affine(t);
            if s.dash_interval_count != 0 {
                let last: wire::Stroke = record(&resource.payload, count - 1)?;
                let double_base =
                    (last.point_offset + last.point_count) as usize * wire::Point::WIRE_SIZE;
                let mut points = std::mem::take(&mut self.dash_points);
                let mut intervals = std::mem::take(&mut self.dash_intervals);
                reserve_total(&mut points, s.point_count as usize)?;
                points.clear();
                for j in 0..s.point_count {
                    points.push(record(&resource.auxiliary, (s.point_offset + j) as usize)?);
                }
                reserve_total(&mut intervals, s.dash_interval_count as usize)?;
                intervals.clear();
                let doubles = resource
                    .auxiliary
                    .get(double_base..)
                    .ok_or(B::UnsupportedHitTest)?;
                for j in 0..s.dash_interval_count {
                    intervals.push(record(doubles, (s.dash_interval_offset + j) as usize)?);
                }
                let result = dash::walk(&s, t, &points, &intervals, |event| match event {
                    dash::Event::Body(first, last) => self.line(
                        first,
                        last,
                        s.stroke_thickness,
                        STROKE_CAP_FLAT,
                        STROKE_CAP_FLAT,
                        t,
                        state,
                        index,
                    ),
                    dash::Event::Cap(kind, center, direction, start) => self.triangles(
                        triangles::cap(
                            kind,
                            if affine {
                                s.stroke_thickness
                            } else {
                                s.stroke_thickness * scale
                            },
                            if affine {
                                center
                            } else {
                                math::point(t, center)
                            },
                            if affine {
                                direction
                            } else {
                                math::direction(t, direction)
                            },
                            start,
                        ),
                        if affine {
                            t
                        } else {
                            NativeSceneBuilder::identity_transform()
                        },
                        state,
                        index,
                    ),
                    dash::Event::Join(corner, incoming, outgoing) => self.stroke_join(
                        &s, corner, incoming, outgoing, t, scale, affine, state, index,
                    ),
                });
                self.dash_points = points;
                self.dash_intervals = intervals;
                result?;
                continue;
            }
            let edges = if closed {
                s.point_count
            } else {
                s.point_count - 1
            };
            for edge in 0..edges {
                let first: wire::Point =
                    record(&resource.auxiliary, (s.point_offset + edge) as usize)?;
                let corner: wire::Point = record(
                    &resource.auxiliary,
                    (s.point_offset + (edge + 1) % s.point_count) as usize,
                )?;
                self.line(
                    first,
                    corner,
                    s.stroke_thickness,
                    if !closed && edge == 0 {
                        s.start_cap
                    } else {
                        STROKE_CAP_FLAT
                    },
                    if !closed && edge + 1 == edges {
                        s.end_cap
                    } else {
                        STROKE_CAP_FLAT
                    },
                    t,
                    state,
                    index,
                )?;
                if !closed && edge + 1 == edges {
                    continue;
                }
                let last: wire::Point = record(
                    &resource.auxiliary,
                    (s.point_offset + (edge + 2) % s.point_count) as usize,
                )?;
                self.stroke_join(
                    &s,
                    corner,
                    p2(corner.x - first.x, corner.y - first.y),
                    p2(last.x - corner.x, last.y - corner.y),
                    t,
                    scale,
                    affine,
                    state,
                    index,
                )?;
            }
        }
        Ok(())
    }
    pub(super) fn ordinary(
        &mut self,
        kind: u32,
        resource: &ResourceEntry,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        if kind == SCENE_COMMAND_DRAW_ANALYTIC {
            for i in 0..resource.payload.len() / wire::AnalyticPrimitive::WIRE_SIZE {
                let s: wire::AnalyticPrimitive = record(&resource.payload, i)?;
                if s.flags & !PRIMITIVE_FLAG_EDGE_ALIASED != 0 || s.width <= 0.0 || s.height <= 0.0
                {
                    return Err(B::UnsupportedHitTest);
                }
                let t = math::compose(s.transform, state.transform);
                if s.kind == PRIMITIVE_ELLIPSE {
                    self.ellipse(
                        p2(s.x, s.y),
                        p2(s.x + s.width, s.y + s.height),
                        s.stroke_thickness,
                        t,
                        state,
                        index,
                    )?;
                    continue;
                }
                let radius = if s.kind == PRIMITIVE_ROUNDED_RECTANGLE {
                    s.corner_radius.clamp(0.0, minimum(s.width, s.height) * 0.5)
                } else {
                    0.0
                };
                let mut hit = wire::HitTestPrimitive {
                    kind: if s.stroke_thickness > 0.0 {
                        HIT_TEST_RECTANGLE_STROKE
                    } else {
                        HIT_TEST_RECTANGLE_FILL
                    },
                    data0: f4(s.x, s.y, s.x + s.width, s.y + s.height),
                    data1: f4(radius, radius, s.stroke_thickness, 0.0),
                    ..Default::default()
                };
                let pad = s.stroke_thickness * 0.5;
                if !math::place(
                    &mut hit,
                    p2(s.x - pad, s.y - pad),
                    p2(s.x + s.width + pad, s.y + s.height + pad),
                    t,
                ) {
                    return Err(B::UnsupportedHitTest);
                }
                self.append(hit, state, index)?;
            }
        } else if kind == SCENE_COMMAND_DRAW_PATH {
            for i in 0..resource.payload.len() / wire::PathFill::WIRE_SIZE {
                let s: wire::PathFill = record(&resource.payload, i)?;
                if s.boolean_node_count != 0
                    || s.segment_count == 0
                    || s.segment_count > EXACT_LIMIT as u64
                    || self.segments.len() > EXACT_LIMIT - s.segment_count as usize
                {
                    return Err(B::UnsupportedHitTest);
                }
                let mut hit = wire::HitTestPrimitive {
                    kind: HIT_TEST_PATH_FILL,
                    data0: f4(s.min_x, s.min_y, s.max_x, s.max_y),
                    data1: f4(
                        self.segments.len() as f32,
                        s.segment_count as f32,
                        if s.fill_rule == FILL_RULE_EVEN_ODD {
                            0.0
                        } else {
                            1.0
                        },
                        0.0,
                    ),
                    ..Default::default()
                };
                if !math::place(
                    &mut hit,
                    p2(s.min_x, s.min_y),
                    p2(s.max_x, s.max_y),
                    math::compose(s.transform, state.transform),
                ) {
                    return Err(B::UnsupportedHitTest);
                }
                reserve_append(&mut self.segments, s.segment_count as usize)?;
                for j in 0..s.segment_count {
                    self.segments.push(record(
                        &resource.auxiliary,
                        (s.segment_offset + j) as usize,
                    )?);
                }
                self.append(hit, state, index)?;
            }
        } else {
            return Err(B::UnsupportedHitTest);
        }
        Ok(())
    }
    pub(super) fn image(
        &mut self,
        command: &CommandEntry,
        resource: &ResourceEntry,
        state: &wire::State,
        index: u32,
    ) -> BuildResult<()> {
        let source: wire::ImageDraw = record(&command.payload, 0)?;
        if source.flags & SCENE_IMAGE_EFFECT != 0 {
            return Err(B::UnsupportedHitTest);
        }
        let bpp = if resource.r8_image { 1 } else { 4 };
        let bytes = (u64::from(source.row_bytes) * u64::from(source.image_height.wrapping_sub(1)))
            .wrapping_add(u64::from(source.image_width) * u64::from(bpp));
        let options = image_payload::read_image_payload(&command.payload, &source, bytes, bpp)
            .ok_or(B::UnsupportedHitTest)?;
        let transform = math::compose(source.transform, state.transform);
        if options.patch_count == 0 {
            self.rectangle(source.destination_rect, transform, state, index)?;
        } else {
            for j in 0..options.patch_count {
                let patch = options.patch(j).ok_or(B::UnsupportedHitTest)?;
                self.rectangle(
                    patch.destination_rect,
                    math::compose(patch.transform, transform),
                    state,
                    index,
                )?;
            }
        }
        Ok(())
    }
}
