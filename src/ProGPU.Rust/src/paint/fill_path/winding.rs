//! Original dominant-contour winding and structural segment reversal.
use super::*;

pub(super) fn normalize(
    source: &RetainedPath,
    stroke: &RetainedPath,
) -> Result<RetainedPath, Error> {
    let source = source.geometry()?;
    let original = stroke.geometry()?;
    if source.deferred.is_some() || original.deferred.is_some() {
        return Err(Error::UnsupportedExecution);
    }
    let mut budget = Budget(MAX_WORK);
    let mut dominant: f32 = 0.0;
    for figure in &source.figures {
        budget.take(1)?;
        let points = flatten::figure(figure, 1.0, &mut budget)?;
        let area = area(&points);
        if area.abs() > dominant.abs() {
            dominant = area;
        }
    }
    let negative = dominant < 0.0;
    let mut geometry = PathGeometry::default();
    geometry.fill_rule = original.fill_rule;
    geometry
        .figures
        .try_reserve_exact(original.figures.len())
        .map_err(|_| Error::Capacity)?;
    for figure in &original.figures {
        budget.take(1)?;
        let points = flatten::figure(figure, 1.0, &mut budget)?;
        let area = area(&points);
        geometry
            .figures
            .push(if area.abs() > 0.0001 && (area < 0.0) != negative {
                reverse(figure, &mut budget)?
            } else {
                figure.clone()
            });
    }
    Ok(RetainedPath::from_geometry(
        Arc::new(geometry),
        stroke.is_inverse(),
    ))
}
fn area(points: &[[f32; 2]]) -> f32 {
    if points.len() < 3 {
        return 0.0;
    }
    let mut twice = 0.0;
    let mut previous = points[points.len() - 1];
    // The original ordered accumulation is dependent; changing its reduction
    // order would change dominant-contour selection near cancellation.
    for &current in points {
        twice += previous[0] * current[1] - current[0] * previous[1];
        previous = current;
    }
    twice * 0.5
}
fn reverse(source: &PathFigure, budget: &mut Budget) -> Result<PathFigure, Error> {
    if source.segments.is_empty() {
        return Ok(source.clone());
    }
    budget.take(source.segments.len())?;
    let mut starts = Vec::new();
    starts
        .try_reserve_exact(source.segments.len())
        .map_err(|_| Error::Capacity)?;
    let mut current = source.start;
    for segment in &source.segments {
        starts.push(current);
        current = segment.end();
    }
    let mut result = PathFigure::new(current, source.is_closed);
    result.is_filled = source.is_filled;
    result.start_cap = source.end_cap;
    result.end_cap = source.start_cap;
    result
        .segments
        .try_reserve_exact(source.segments.len())
        .map_err(|_| Error::Capacity)?;
    for (segment, &end) in source.segments.iter().zip(&starts).rev() {
        let kind = match segment.kind {
            PathSegmentKind::Line(_) => PathSegmentKind::Line(end),
            PathSegmentKind::Quadratic([control, _]) => PathSegmentKind::Quadratic([control, end]),
            PathSegmentKind::Cubic([c1, c2, _]) => PathSegmentKind::Cubic([c2, c1, end]),
            PathSegmentKind::Arc(mut arc) => {
                arc.end = end;
                arc.sweep = stroke::opposite(arc.sweep);
                PathSegmentKind::Arc(arc)
            }
            PathSegmentKind::RationalQuadratic { .. } | PathSegmentKind::RationalCubic { .. } => {
                return Err(Error::UnsupportedExecution);
            }
        };
        // Original ReverseFigure creates new polynomial segments. Conic source
        // records belong to the untouched source generation and are not relabeled
        // as witnesses for this newly derived reversed contour.
        let mut reversed = PathSegment::new(kind);
        reversed.is_smooth_join = segment.is_smooth_join;
        reversed.is_stroked = segment.is_stroked;
        result.segments.push(reversed);
    }
    Ok(result)
}
