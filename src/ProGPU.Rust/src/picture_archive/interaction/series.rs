//! Original retained series/spline input; no GPU vertex envelope substitution.
use super::*;
impl ArchiveHitTestCache {
    pub(super) fn spline(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        z: f32,
        p: Option<&ArchivePicture>,
        pen: Option<&Pen>,
    ) -> Result<()> {
        let Some(pen) = pen else {
            return Ok(());
        };
        let Some(width) = local_width(c, pen) else {
            return Ok(());
        };
        let points = commands::points(c, p)?;
        let doubles = |offset, count, inline: Option<&[f64]>| -> Result<Vec<f64>> {
            if count > 0 {
                if let Some(p) = p {
                    return copy(p.doubles(offset, count)?);
                }
            }
            copy(inline.unwrap_or(&[]))
        };
        let knots = doubles(
            c.double_buffer_offset,
            c.double_buffer_count,
            c.spline_knots.as_deref(),
        )?;
        let weights = doubles(
            c.weight_buffer_offset,
            c.weight_buffer_count,
            c.spline_weights.as_deref(),
        )?;
        let path = super::super::SplineGeometry::create_path(
            points,
            &knots,
            &weights,
            c.spline_degree,
            c.is_closed,
        )?;
        self.prepared_stroke(&path, m, id, z, pen, width)
    }
    pub(super) fn line_series(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        z: f32,
        p: Option<&ArchivePicture>,
    ) -> Result<()> {
        let values = floats(c, p)?;
        let Ok(count) = usize::try_from(c.gpu_points_count) else {
            return Ok(());
        };
        if count < 2 || count.checked_mul(2).is_none_or(|n| values.len() < n) {
            return Ok(());
        }
        let m = series_transform(c, m);
        if !invertible(m) {
            return Ok(());
        }
        let width = 1.0f32.max(c.radius_x);
        let mut points = Vec::new();
        let mut previous = None;
        let mut chunk = 0;
        for i in 0..count {
            let Some(point) = point(c, values, i, 2) else {
                self.series_chunk(&mut points, m, id, z, width, &mut chunk)?;
                previous = None;
                continue;
            };
            if let Some(previous) = previous {
                if points.is_empty() {
                    push(&mut points, previous)?;
                }
                push(&mut points, point)?;
                if points.len() - 1 >= 128 {
                    self.series_chunk(&mut points, m, id, z, width, &mut chunk)?;
                }
            }
            previous = Some(point);
        }
        self.series_chunk(&mut points, m, id, z, width, &mut chunk)
    }
    fn series_chunk(
        &mut self,
        points: &mut Vec<[f32; 2]>,
        m: Matrix4,
        id: i32,
        z: f32,
        width: f32,
        chunk: &mut usize,
    ) -> Result<()> {
        if points.len() < 2 {
            points.clear();
            return Ok(());
        }
        if let Some(path) = self.compile_path(&polygon(points, false)?)? {
            self.path_stroke(
                path,
                m,
                id,
                z + *chunk as f32 * 0.0001,
                width,
                [PenLineCap::Round; 2],
            )?;
        }
        *chunk += 1;
        points.clear();
        Ok(())
    }
    pub(super) fn scatter_series(
        &mut self,
        c: &ArchiveCommand,
        m: Matrix4,
        id: i32,
        z: f32,
        p: Option<&ArchivePicture>,
    ) -> Result<()> {
        let values = floats(c, p)?;
        let Ok(count) = usize::try_from(c.gpu_points_count) else {
            return Ok(());
        };
        if count == 0 {
            return Ok(());
        }
        let stride = if count.checked_mul(3).is_some_and(|n| values.len() >= n) {
            3
        } else {
            2
        };
        if count.checked_mul(stride).is_none_or(|n| values.len() < n) {
            return Ok(());
        }
        let m = series_transform(c, m);
        if !invertible(m) {
            return Ok(());
        }
        for i in 0..count {
            let Some(center) = point(c, values, i, stride) else {
                continue;
            };
            let radius = if stride == 3 {
                values[i * stride + 2]
            } else {
                c.radius_x
            };
            if !radius.is_finite() || radius <= 0.0 {
                continue;
            }
            self.append(HitPrimitive::ellipse_fill(
                id,
                [
                    center[0] - radius,
                    center[1] - radius,
                    center[0] + radius,
                    center[1] + radius,
                ],
                m,
                z + i as f32 * 0.0001,
            ))?;
        }
        Ok(())
    }
}
fn floats<'a>(c: &'a ArchiveCommand, p: Option<&'a ArchivePicture>) -> Result<&'a [f32]> {
    if c.float_buffer_count > 0 {
        if let Some(p) = p {
            return p.floats(c.float_buffer_offset, c.float_buffer_count);
        }
    }
    Ok(c.gpu_points.as_deref().unwrap_or(&[]))
}
fn point(c: &ArchiveCommand, values: &[f32], i: usize, stride: usize) -> Option<[f32; 2]> {
    let offset = i.checked_mul(stride)?;
    let input = [*values.get(offset)?, *values.get(offset + 1)?];
    if !finite(input) {
        return None;
    }
    let scale = if c.scale == [0.0; 2] {
        [1.0; 2]
    } else {
        c.scale
    };
    let point = [
        input[0] * scale[0] + c.translate[0],
        input[1] * scale[1] + c.translate[1],
    ];
    finite(point).then_some(point)
}
fn series_transform(c: &ArchiveCommand, m: Matrix4) -> Matrix4 {
    if c.transform == Matrix4([[0.0; 4]; 4]) || c.transform == Matrix4::IDENTITY {
        m
    } else {
        c.transform * m
    }
}
