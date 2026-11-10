//! Original float homogeneous de Boor writer and target-size sampling policy.
use super::*;
pub(super) fn domain(s: &wire::Stroke, knots: &[f64]) -> Option<(f64, f64)> {
    let degree = s.degree as usize;
    let controls = s.point_count as usize;
    if controls < 2
        || knots.len() < controls.checked_add(degree)?.checked_add(1)?
        || degree >= knots.len()
    {
        return None;
    }
    let end = knots.len() - degree - 1;
    if end <= degree || end >= knots.len() {
        return None;
    }
    let (a, b) = (knots[degree], knots[end]);
    (a.is_finite() && b.is_finite() && b > a).then_some((a, b))
}
pub(super) fn count(s: &wire::Stroke, points: &[wire::Point]) -> Result<usize, Error> {
    if points.is_empty() || !finite_transform(s.transform) {
        return Err(Error::InvalidInput);
    }
    let (mut min, mut max) = (point(f32::MAX, f32::MAX), point(f32::MIN, f32::MIN));
    for &p in points {
        let screen = transform_point(s.transform, p);
        if !finite_point(p) || !finite_point(screen) {
            return Err(Error::InvalidInput);
        }
        min.x = min.x.min(screen.x);
        min.y = min.y.min(screen.y);
        max.x = max.x.max(screen.x);
        max.y = max.y.max(screen.y);
    }
    let extent = (max.x - min.x).hypot(max.y - min.y);
    if !extent.is_finite() {
        return Err(Error::InvalidInput);
    }
    Ok(if extent < 2.0 {
        0
    } else if extent < 20.0 {
        10
    } else if extent < 80.0 {
        25
    } else if extent < 250.0 {
        50
    } else {
        100
    })
}
fn evaluate(
    s: &wire::Stroke,
    points: &[wire::Point],
    knots: &[f64],
    weights: &[f64],
    parameter: f64,
    work: &mut [[f32; 3]],
) -> Result<wire::Point, Error> {
    let degree = s.degree as usize;
    let end = knots.len() - degree - 1;
    let parameter = parameter.clamp(knots[degree], knots[end]);
    let span = (degree..knots.len() - 1)
        .find(|&i| parameter >= knots[i] && parameter <= knots[i + 1])
        .unwrap_or(knots.len() - degree - 2);
    for (index, value) in work.iter_mut().enumerate() {
        let control = span as i64 - degree as i64 + index as i64;
        if control >= 0 && (control as usize) < points.len() {
            let weight = weights.get(control as usize).copied().unwrap_or(1.0) as f32;
            let p = points[control as usize];
            *value = [p.x * weight, p.y * weight, weight];
        } else {
            *value = [0.0; 3];
        }
    }
    for level in 1..=degree {
        for index in (level..=degree).rev() {
            let knot = span - degree + index;
            let denominator = knots[knot + degree + 1 - level] - knots[knot];
            let alpha = if denominator > 1.0e-9 {
                ((parameter - knots[knot]) / denominator) as f32
            } else {
                0.0
            };
            let inverse = 1.0 - alpha;
            let previous = work[index - 1];
            let current = work[index];
            work[index] = [
                inverse * previous[0] + alpha * current[0],
                inverse * previous[1] + alpha * current[1],
                inverse * previous[2] + alpha * current[2],
            ];
        }
    }
    let result = work[degree];
    let p = if result[2].abs() > 1.0e-9 {
        point(result[0] / result[2], result[1] / result[2])
    } else {
        point(result[0], result[1])
    };
    if !finite_point(p) {
        return Err(Error::InvalidInput);
    }
    Ok(p)
}
pub(super) fn sample(
    s: &wire::Stroke,
    points: &[wire::Point],
    knots: &[f64],
    weights: &[f64],
    count: usize,
    out: &mut [wire::Point; 101],
) -> Result<(), Error> {
    if count == 0 {
        return Ok(());
    }
    let (a, b) = domain(s, knots).ok_or(Error::InvalidInput)?;
    if count >= out.len() {
        return Err(Error::InvalidInput);
    }
    let mut work = allocate(s.degree as usize + 1)?;
    work.resize(s.degree as usize + 1, [0.0; 3]);
    let delta = (b - a) / count as f64;
    for (index, p) in out[..=count].iter_mut().enumerate() {
        *p = evaluate(
            s,
            points,
            knots,
            weights,
            a + index as f64 * delta,
            &mut work,
        )?;
    }
    Ok(())
}
