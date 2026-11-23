//! Original shared PathAtlas input compilation and independent fill/stroke order.
use super::*;
impl ArchiveHitTestCache {
    pub(super) fn compile_path(&mut self, path: &PathGeometry) -> Result<Option<Compiled>> {
        let compiled = match path.compile_path() {
            Ok(c) => c,
            Err(Error::InvalidInput | Error::UnsupportedExecution) => return Ok(None),
            Err(e) => return Err(e.into()),
        };
        let Some(record) = compiled.record.filter(|_| !compiled.segments.is_empty()) else {
            return Ok(None);
        };
        let count = u32::try_from(compiled.segments.len()).map_err(|_| ArchiveError::Capacity)?;
        let start = u32::try_from(self.segments.len()).map_err(|_| ArchiveError::Capacity)?;
        start.checked_add(count).ok_or(ArchiveError::Capacity)?;
        reserve(&mut self.segments, compiled.segments.len())?;
        self.segments.extend(compiled.segments);
        Ok(Some(Compiled {
            bounds: record.bounds,
            start,
            count,
            rule: if record.fill_rule == 0 {
                FillRule::Nonzero
            } else {
                FillRule::EvenOdd
            },
        }))
    }
    pub(super) fn path_fill(&mut self, p: Compiled, m: Matrix4, id: i32, z: f32) -> Result<()> {
        self.append(HitPrimitive::path_fill(
            id, p.bounds, p.start, p.count, p.rule, m, z,
        ))
    }
    pub(super) fn path_stroke(
        &mut self,
        p: Compiled,
        m: Matrix4,
        id: i32,
        z: f32,
        width: f32,
        caps: [PenLineCap; 2],
    ) -> Result<()> {
        self.append(HitPrimitive::path_stroke(
            id, p.bounds, p.start, p.count, width, 0.0, caps, m, z,
        ))
    }
    pub(super) fn draw_path(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        mut z: f32,
        pen: Option<&Pen>,
    ) -> Result<()> {
        let Some(path) = c
            .path
            .as_ref()
            .filter(|_| c.brush.is_some() || pen.is_some())
        else {
            return Ok(());
        };
        let m = if c.transform == Matrix4([[0.0; 4]; 4]) {
            m
        } else {
            c.transform * m
        };
        if !invertible(m) {
            return Ok(());
        }
        let path = path.to_geometry()?;
        let mut fill = None;
        if c.brush.is_some() {
            if let Some(p) = self.compile_path(&path)? {
                fill = Some(p);
                self.path_fill(p, m, id, z)?;
                z += 0.25;
            }
        }
        if let Some(pen) = pen {
            if let Some(width) = local_width(c, pen) {
                if !pen.has_dash_pattern() && !device(pen) {
                    self.stroke_path(&path, m, id, z, pen, width, fill)?;
                } else {
                    self.prepared_stroke(&path, m, id, z, pen, width)?;
                }
            }
        }
        Ok(())
    }
    pub(super) fn prepared_stroke(
        &mut self,
        path: &PathGeometry,
        m: Matrix4,
        id: i32,
        z: f32,
        pen: &Pen,
        width: f32,
    ) -> Result<()> {
        if pen.has_dash_pattern() {
            let linear = !pen.is_hairline()
                && pen.options.transform == PenStrokeTransform::Normal
                && path.deferred.is_none()
                && path.figures.iter().all(|f| {
                    f.segments
                        .iter()
                        .all(|s| matches!(s.kind, PathSegmentKind::Line(_)))
                });
            if linear {
                let mut local = pen.clone();
                local.options.thickness = width;
                if let Some(coverage) = path.prepare_linear_stroke(&local)? {
                    if let Some(fill) = coverage.fill_coverage {
                        if let Some(p) = self.compile_path(&fill)? {
                            self.path_fill(p, m, id, z)?;
                        }
                    } else {
                        self.stroke_path(
                            &coverage.stroke_path,
                            m,
                            id,
                            z,
                            &coverage.pen,
                            width,
                            None,
                        )?;
                    }
                }
                return Ok(());
            }
            let Some(dashed) = path.dashed_stroke(pen, width, false)? else {
                return Ok(());
            };
            let undashed = pen.undashed(width)?;
            if device(pen) {
                self.device_stroke(&dashed.path, m, id, z, &undashed, true)?;
            } else {
                self.stroke_path(&dashed.path, m, id, z, &undashed, width, None)?;
            }
        } else if device(pen) {
            self.device_stroke(path, m, id, z, pen, true)?;
        } else {
            self.stroke_path(path, m, id, z, pen, width, None)?;
        }
        Ok(())
    }
}
pub(super) fn figure_count(f: &PathFigure) -> Result<usize> {
    let mut count = 0usize;
    let mut current = f.start;
    for s in &f.segments {
        if let PathSegmentKind::Arc(a) = s.kind {
            if a.center(current).is_some() || current != a.end {
                count = count.checked_add(1).ok_or(ArchiveError::Capacity)?;
            }
        } else {
            count = count.checked_add(1).ok_or(ArchiveError::Capacity)?;
        }
        current = s.end();
    }
    if f.is_closed && current != f.start {
        count = count.checked_add(1).ok_or(ArchiveError::Capacity)?;
    }
    Ok(count)
}
