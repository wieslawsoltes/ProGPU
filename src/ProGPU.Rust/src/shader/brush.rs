use super::*;
pub(super) fn special(source: &FilterShaderSource, depth: u16) -> Result<bool, Error> {
    if depth >= 256 {
        return Err(Error::Capacity);
    }
    Ok(match source {
        FilterShaderSource::ColorFilter { .. }
        | FilterShaderSource::Image { .. }
        | FilterShaderSource::Picture { .. }
        | FilterShaderSource::Compose { .. } => true,
        FilterShaderSource::LocalMatrix { source, .. } => return special(source, depth + 1),
        _ => false,
    })
}
pub(super) fn to_brush(source: &FilterShaderSource, depth: u16) -> Result<Brush, Error> {
    if depth >= 256 {
        return Err(Error::Capacity);
    }
    match source {
        FilterShaderSource::Empty => Ok(Brush::default()),
        FilterShaderSource::Brush(brush) => Ok(brush.as_ref().clone()),
        FilterShaderSource::LocalMatrix { source, matrix } => {
            let mut brush = to_brush(source, depth + 1)?;
            if matches!(brush.kind, BrushKind::Solid(_)) {
                return Ok(brush);
            }
            let Some(inverse) = FilterTransform::new(*matrix).inverse_raw() else {
                return Ok(Brush::default());
            };
            coordinate_transform(&mut brush, inverse);
            Ok(brush)
        }
        FilterShaderSource::ColorFilter { source, filter } => {
            let mut brush = to_brush(source, depth + 1)?;
            filter_brush(&mut brush, filter);
            Ok(brush)
        }
        _ => Err(Error::UnsupportedExecution),
    }
}
fn coordinate_transform(brush: &mut Brush, inverse: Matrix4) {
    let target = match &mut brush.kind {
        BrushKind::Linear(v) => Some(&mut v.options.transform),
        BrushKind::Radial(v) => Some(&mut v.options.transform),
        BrushKind::TwoPointConical(v) => Some(&mut v.options.transform),
        BrushKind::Sweep(v) => Some(&mut v.options.transform),
        BrushKind::PerlinNoise(v) => Some(&mut v.transform),
        _ => None,
    };
    if let Some(target) = target {
        *target = inverse * *target;
    }
}
/// Exact original CPU material policy: convert each selected solid/stop through
/// RGBA8 before ColorFilter.Apply, leaving procedural noise/hatch families alone.
pub(crate) fn filter_brush(brush: &mut Brush, filter: &ColorFilter) {
    let stops = match &mut brush.kind {
        BrushKind::Solid(color) => {
            *color = filter_color(*color, filter);
            return;
        }
        BrushKind::Linear(v) => &mut v.stops,
        BrushKind::Radial(v) => &mut v.stops,
        BrushKind::TwoPointConical(v) => &mut v.stops,
        BrushKind::Sweep(v) => &mut v.stops,
        _ => return,
    };
    for stop in stops {
        stop.color = filter_color(stop.color, filter);
    }
}
fn filter_color(color: [f32; 4], filter: &ColorFilter) -> [f32; 4] {
    let scaled = (wide::f32x4::from(color) * wide::f32x4::splat(255.0)).to_array();
    let bytes = scaled.map(|v| v.round_ties_even().clamp(0.0, 255.0) as u8);
    (wide::f32x4::from(filter.apply_color(bytes).map(f32::from)) / wide::f32x4::splat(255.0))
        .to_array()
}
