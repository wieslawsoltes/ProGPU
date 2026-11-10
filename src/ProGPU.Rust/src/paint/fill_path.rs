//! Original SKPaint.GetFillPath CPU path producer. This explicit geometry query
//! is separate from retained GPU stroke rendering and its source raster policy.
use super::*;
use crate::vector::{
    MAX_STROKE_TRIANGLES, PathAddMode, PathBuilder, PathFigure, PathGeometry, PathSegment,
    PathSegmentKind, RetainedPath, StrokeCap, StrokeJoin, StrokeJoinFrame, StrokeTriangle,
    SweepDirection,
};
use wide::f32x4 as V;
mod flatten;
mod stroke;
mod winding;
const MAX_WORK: usize = 4_194_304;
struct Budget(usize);
impl Budget {
    fn take(&mut self, count: usize) -> Result<(), Error> {
        self.0 = self.0.checked_sub(count).ok_or(Error::Capacity)?;
        Ok(())
    }
}
impl Paint {
    /// Original complete fill-path producer. Hairline Stroke returns None;
    /// StrokeAndFill retains the original fill even with a hairline. Finite
    /// resolution values above one tighten only original curve subdivision.
    pub fn fill_path(
        &self,
        source: &RetainedPath,
        resolution_scale: f32,
    ) -> Result<Option<RetainedPath>, Error> {
        create(
            self,
            source,
            normalize_scale(resolution_scale),
            &mut Budget(MAX_WORK),
            0,
        )
    }
    /// Original matrix overload selects source maximum stroke scale; it does not
    /// transform the produced geometry or substitute the minimum singular value.
    pub fn fill_path_for_matrix(
        &self,
        source: &RetainedPath,
        matrix: crate::canvas::CanvasMatrix,
    ) -> Result<Option<RetainedPath>, Error> {
        self.fill_path(
            source,
            matrix.matrix4().stroke_scales().map_or(1.0, |s| s[0]),
        )
    }
    /// Original cull-rectangle overload deliberately delegates unchanged.
    pub fn fill_path_with_cull(
        &self,
        source: &RetainedPath,
        _cull: [f32; 4],
        resolution_scale: f32,
    ) -> Result<Option<RetainedPath>, Error> {
        self.fill_path(source, resolution_scale)
    }
    /// Original immutable destination replacement: unsuccessful hairline output
    /// leaves the caller's destination generation intact.
    pub fn replace_fill_path(
        &self,
        source: &RetainedPath,
        destination: &mut RetainedPath,
        resolution_scale: f32,
    ) -> Result<bool, Error> {
        let Some(result) = self.fill_path(source, resolution_scale)? else {
            return Ok(false);
        };
        *destination = result;
        Ok(true)
    }
    /// Original builder overload APPENDS successful output after selecting its
    /// fill metadata. Hairline failure replaces the builder with a source copy.
    /// Capacity/source admission occurs on a private candidate before publication.
    pub fn append_fill_path(
        &self,
        source: &RetainedPath,
        destination: &mut PathBuilder,
        resolution_scale: f32,
    ) -> Result<bool, Error> {
        let Some(result) = self.fill_path(source, resolution_scale)? else {
            *destination = PathBuilder::from_path(source)?;
            return Ok(false);
        };
        let mut candidate = PathBuilder::from_path(&destination.snapshot()?)?;
        candidate.set_fill(result.fill_rule(), result.is_inverse());
        candidate.add_path(&result, [0.0; 2], PathAddMode::Append)?;
        *destination = candidate;
        Ok(true)
    }
    /// Original source-font contour winding normalization. It uses the dominant
    /// flattened source contour and actual per-figure reversal, never a fill-rule
    /// relabel or a rewritten scan polynomial.
    pub fn normalize_stroke_winding(
        source: &RetainedPath,
        stroke: &RetainedPath,
    ) -> Result<RetainedPath, Error> {
        winding::normalize(source, stroke)
    }
}
fn normalize_scale(scale: f32) -> f32 {
    if scale.is_finite() && scale > 1.0 {
        scale
    } else {
        1.0
    }
}
fn create(
    paint: &Paint,
    source: &RetainedPath,
    scale: f32,
    budget: &mut Budget,
    depth: u16,
) -> Result<Option<RetainedPath>, Error> {
    if depth >= 256 {
        return Err(Error::Capacity);
    }
    if let Some(effect) = &paint.path_effect
        && !matches!(effect.kind(), PathEffectKind::Dash { .. })
    {
        let geometry = source.geometry()?;
        let original =
            crate::vector::PathEffectSource::from_geometry(&geometry, source.is_inverse())?;
        let output = effect.apply(&original, scale)?;
        if output.applied {
            let source = RetainedPath::from_geometry(
                Arc::new(output.output.geometry()?),
                output.output.is_inverse(),
            );
            let mut paint = paint.clone();
            paint.path_effect = None;
            if let Some(style) = output.paint.style {
                paint.style = match style {
                    crate::vector::EffectPaintStyle::Fill => PaintStyle::Fill,
                    crate::vector::EffectPaintStyle::Stroke => PaintStyle::Stroke,
                };
            }
            if let Some(width) = output.paint.stroke_width {
                paint.set_stroke_width(width);
            }
            return create(&paint, &source, scale, budget, depth + 1);
        }
    }
    if paint.style == PaintStyle::Fill {
        return Ok(Some(source.clone()));
    }
    let mut destination = PathBuilder::new();
    if paint.style == PaintStyle::StrokeAndFill {
        destination.add_path(source, [0.0; 2], PathAddMode::Append)?;
    }
    if paint.stroke_width == 0.0 {
        return Ok((paint.style == PaintStyle::StrokeAndFill).then(|| destination.detach()));
    }
    if !paint.stroke_width.is_finite() {
        return Ok(Some(destination.detach()));
    }
    let geometry = source.geometry()?;
    if geometry.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    let half = paint.stroke_width / 2.0;
    for figure in &geometry.figures {
        budget.take(1)?;
        if stroke::oval(&mut destination, figure, half)? {
            continue;
        }
        let mut points = flatten::figure(figure, scale, budget)?;
        flatten::deduplicate(&mut points);
        if figure.is_closed
            && points.len() > 1
            && distance_squared(points[0], points[points.len() - 1]) <= 0.0000001
        {
            points.pop();
        }
        if !figure.is_closed
            && !figure.segments.is_empty()
            && !points
                .iter()
                .skip(1)
                .any(|p| distance_squared(points[0], *p) > 0.0000001)
        {
            match paint.stroke_cap {
                PenLineCap::Round => {
                    destination.add_circle(figure.start, half, SweepDirection::Clockwise)?
                }
                PenLineCap::Square => destination.add_rectangle(
                    [
                        figure.start[0] - half,
                        figure.start[1] - half,
                        figure.start[0] + half,
                        figure.start[1] + half,
                    ],
                    SweepDirection::Clockwise,
                    0,
                )?,
                _ => {}
            }
            continue;
        }
        if let Some(effect) = &paint.path_effect
            && let PathEffectKind::Dash { intervals, phase } = effect.kind()
            && !intervals.values().is_empty()
        {
            stroke::dashed(
                paint,
                &mut destination,
                &points,
                figure.is_closed,
                half,
                intervals.values(),
                *phase,
                budget,
            )?;
            continue;
        }
        for pair in points.windows(2) {
            stroke::segment(&mut destination, pair[0], pair[1], half, budget)?;
        }
        if figure.is_closed && points.len() > 1 {
            stroke::segment(
                &mut destination,
                points[points.len() - 1],
                points[0],
                half,
                budget,
            )?;
        }
        stroke::joins(
            paint,
            &mut destination,
            &points,
            figure.is_closed,
            half * 2.0,
            budget,
        )?;
        if !figure.is_closed {
            stroke::caps(paint, &mut destination, &points, half * 2.0, budget)?;
        }
    }
    Ok(Some(destination.detach()))
}
fn vector(p: [f32; 2]) -> V {
    V::from([p[0], p[1], 0.0, 0.0])
}
fn point(v: V) -> [f32; 2] {
    let v = v.to_array();
    [v[0], v[1]]
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    point(vector(a) + vector(b))
}
fn sub(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    point(vector(a) - vector(b))
}
fn mul(a: [f32; 2], scale: f32) -> [f32; 2] {
    point(vector(a) * V::splat(scale))
}
fn distance_squared(a: [f32; 2], b: [f32; 2]) -> f32 {
    let d = sub(a, b);
    d[0] * d[0] + d[1] * d[1]
}
fn maximum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else {
        a.max(b)
    }
}
fn minimum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else {
        a.min(b)
    }
}
