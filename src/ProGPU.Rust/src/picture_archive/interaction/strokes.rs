//! Original curve/cap lowering and actual device-width stroke coordinates.
use super::*;
mod joins;
impl ArchiveHitTestCache {
    pub(super) fn stroke_path(
        &mut self,
        path: &PathGeometry,
        m: Matrix4,
        id: i32,
        z: f32,
        pen: &Pen,
        width: f32,
        precompiled: Option<Compiled>,
    ) -> Result<bool> {
        pen.validate_join_semantics()?;
        if explicit(pen) {
            return self.source_stroke(path, m, id, z, pen, width);
        }
        if !device(pen)
            && (path
                .figures
                .iter()
                .any(|f| f.start_cap.is_some() || f.end_cap.is_some())
                || pen.options.start_cap != PenLineCap::Round
                || pen.options.end_cap != PenLineCap::Round)
        {
            if !path.figures.is_empty()
                && path.figures.iter().all(|f| {
                    !f.is_closed
                        && f.segments.len() == 1
                        && f.segments[0].is_stroked
                        && matches!(f.segments[0].kind, PathSegmentKind::Line(_))
                })
            {
                for f in &path.figures {
                    self.append(HitPrimitive::line_stroke(
                        id,
                        f.start,
                        f.segments[0].end(),
                        width,
                        [
                            f.start_cap.unwrap_or(pen.options.start_cap),
                            f.end_cap.unwrap_or(pen.options.end_cap),
                        ],
                        0.0,
                        m,
                        z,
                    ))?;
                }
                return Ok(true);
            }
            let mut expected = 0usize;
            for f in &path.figures {
                expected = expected
                    .checked_add(paths::figure_count(f)?)
                    .ok_or(ArchiveError::Capacity)?;
            }
            if expected != 0 {
                let compiled = if let Some(p) = precompiled {
                    Some(p)
                } else {
                    self.compile_path(path)?
                };
                if let Some(compiled) = compiled.filter(|p| p.count as usize == expected) {
                    let mut start = compiled.start;
                    let mut successful = true;
                    for f in &path.figures {
                        let count = paths::figure_count(f)?;
                        if count == 0 {
                            continue;
                        }
                        let Some(bounds) = f.bounds() else {
                            successful = false;
                            break;
                        };
                        let p = Compiled {
                            bounds,
                            start,
                            count: u32::try_from(count).map_err(|_| ArchiveError::Capacity)?,
                            rule: compiled.rule,
                        };
                        self.path_stroke(p, m, id, z, width, caps(f, pen))?;
                        start = start.checked_add(p.count).ok_or(ArchiveError::Capacity)?;
                    }
                    if successful {
                        return Ok(true);
                    }
                }
            }
        }
        let p = if let Some(p) = precompiled {
            Some(p)
        } else {
            self.compile_path(path)?
        };
        if let Some(p) = p {
            self.path_stroke(p, m, id, z, width, [PenLineCap::Round; 2])?;
            Ok(true)
        } else {
            Ok(false)
        }
    }
    pub(super) fn device_stroke(
        &mut self,
        path: &PathGeometry,
        m: Matrix4,
        id: i32,
        z: f32,
        pen: &Pen,
        retain_joins: bool,
    ) -> Result<bool> {
        pen.validate_join_semantics()?;
        if retain_joins && explicit(pen) {
            return self.source_stroke(path, m, id, z, pen, device_width(pen));
        }
        let segment_checkpoint = self.segments.len();
        let primitive_checkpoint = self.primitives.len();
        let Some(local) = self.compile_path(path)? else {
            return Ok(false);
        };
        let start = local.start as usize;
        let end = start
            .checked_add(local.count as usize)
            .ok_or(ArchiveError::Capacity)?;
        let expected = path.figures.iter().try_fold(0usize, |n, f| {
            n.checked_add(paths::figure_count(f)?)
                .ok_or(ArchiveError::Capacity)
        })?;
        if start != segment_checkpoint
            || end != self.segments.len()
            || expected != local.count as usize
        {
            self.segments.truncate(segment_checkpoint);
            self.primitives.truncate(primitive_checkpoint);
            return Ok(false);
        }
        let transformed_start = self.segments.len();
        let mut local_start = start;
        for f in &path.figures {
            let count = paths::figure_count(f)?;
            if count == 0 {
                continue;
            }
            let figure_start = self.segments.len();
            let mut bounds = [
                f32::INFINITY,
                f32::INFINITY,
                f32::NEG_INFINITY,
                f32::NEG_INFINITY,
            ];
            for i in local_start..local_start + count {
                let mut s = self.segments[i];
                match s.kind {
                    0..=2 => {
                        let points = s.kind as usize + 2;
                        for p in &mut s.points[..points] {
                            *p = device_point(*p, m, &mut bounds);
                        }
                        push(&mut self.segments, s)?;
                    }
                    3 => self.device_arc(s, m, &mut bounds)?,
                    // The original device-path switch has no rational arm.
                    _ => {}
                }
            }
            let count = self.segments.len() - figure_start;
            if count == 0 || bounds.iter().any(|v| !v.is_finite()) {
                self.segments.truncate(segment_checkpoint);
                self.primitives.truncate(primitive_checkpoint);
                // Original compatibility rollback removes payloads, retaining
                // its already accumulated conservative bounds state.
                return Ok(false);
            }
            let final_start = start + (figure_start - transformed_start);
            self.path_stroke(
                Compiled {
                    bounds,
                    start: u32::try_from(final_start).map_err(|_| ArchiveError::Capacity)?,
                    count: u32::try_from(count).map_err(|_| ArchiveError::Capacity)?,
                    rule: local.rule,
                },
                Matrix4::IDENTITY,
                id,
                z,
                device_width(pen),
                caps(f, pen),
            )?;
            local_start += paths::figure_count(f)?;
        }
        self.segments.drain(start..end);
        Ok(true)
    }
    fn device_arc(&mut self, s: Segment, m: Matrix4, bounds: &mut [f32; 4]) -> Result<()> {
        let count = device_arc_count(s, m);
        let [theta, delta, rotation] = s.parameters.map(f32::from_bits);
        let cos = rotation.cos();
        let sin = rotation.sin();
        let evaluate = |theta: f32| {
            let p = [theta.cos() * s.points[3][0], theta.sin() * s.points[3][1]];
            add(
                s.points[2],
                [p[0] * cos - p[1] * sin, p[0] * sin + p[1] * cos],
            )
        };
        let mut previous = device_point(evaluate(theta), m, bounds);
        for i in 1..=count {
            let angle = theta + delta * (i as f32 / count as f32);
            let next = device_point(evaluate(angle), m, bounds);
            push(&mut self.segments, Segment::line(previous, next))?;
            previous = next;
        }
        Ok(())
    }
}
fn explicit(p: &Pen) -> bool {
    p.options.use_wpf_join_semantics
        || p.options.join == PenLineJoin::MiterOrBevel
        || (p.options.clip_miter_at_limit && p.options.join == PenLineJoin::Miter)
}
fn caps(f: &PathFigure, p: &Pen) -> [PenLineCap; 2] {
    if f.is_closed {
        [PenLineCap::Round; 2]
    } else {
        [
            f.start_cap.unwrap_or(p.options.start_cap),
            f.end_cap.unwrap_or(p.options.end_cap),
        ]
    }
}
fn device_point(p: [f32; 2], m: Matrix4, b: &mut [f32; 4]) -> [f32; 2] {
    let p = m.transform_point(p);
    *b = union(*b, [p[0], p[1], p[0], p[1]]);
    p
}
fn device_arc_count(s: Segment, m: Matrix4) -> usize {
    let [_, delta, rotation] = s.parameters.map(f32::from_bits);
    let cos = rotation.cos();
    let sin = rotation.sin();
    let linear = |p: [f32; 2]| {
        [
            p[0] * m.0[0][0] + p[1] * m.0[1][0],
            p[0] * m.0[0][1] + p[1] * m.0[1][1],
        ]
    };
    let x = linear([s.points[3][0] * cos, s.points[3][0] * sin]);
    let y = linear([-s.points[3][1] * sin, s.points[3][1] * cos]);
    let matrix = Matrix4([
        [x[0], x[1], 0.0, 0.0],
        [y[0], y[1], 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]);
    let Some([maximum, _]) = matrix.stroke_scales().filter(|_| delta.is_finite()) else {
        return 32;
    };
    let sweep = f64::from(delta).abs();
    if sweep == 0.0 || maximum <= 0.25 {
        return 32;
    }
    let angle = 2.0 * (1.0 - 0.25 / f64::from(maximum)).clamp(-1.0, 1.0).acos();
    if !angle.is_finite() || angle <= 0.0 {
        return 4096;
    }
    let required = (sweep / angle).ceil();
    if !required.is_finite() || required >= 4096.0 {
        4096
    } else {
        (required as usize).max(32)
    }
}
