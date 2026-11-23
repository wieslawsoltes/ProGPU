//! Original raw native material gates, including kind-specific auxiliary records.
use super::*;
use crate::native_scene::wire::WireRecord;
fn color(c: wire::Color) -> bool {
    [c.r, c.g, c.b, c.a].into_iter().all(f32::is_finite)
}
fn point(p: wire::Point) -> bool {
    p.x.is_finite() && p.y.is_finite()
}
fn finite(values: &[f32]) -> bool {
    values.iter().all(|v| v.is_finite())
}
fn gradient(kind: u32) -> bool {
    matches!(kind, 1 | 2 | 5 | 6 | 10)
}
pub(crate) fn stored_stop_count(brush: &wire::Brush) -> u32 {
    if brush.kind == 7 {
        if brush.stop_count == 0 || brush.color_interpolation_mode == 0 {
            0
        } else {
            512
        }
    } else if brush.kind == 8 || gradient(brush.kind) {
        brush.stop_count
    } else {
        0
    }
}
fn valid_stop(stop: wire::GradientStop) -> bool {
    color(stop.color)
        && stop.offset.is_finite()
        && stop.reserved0 == 0
        && stop.reserved1 == 0
        && stop.reserved2 == 0
}
pub(super) fn valid_brush(
    input: &Input<'_>,
    b: &wire::Brush,
    offset: u32,
    count: u32,
) -> TransportResult<bool> {
    valid_brush_with(b, count, |index| {
        input.read::<wire::GradientStop>(offset + index * 32)
    })
}
/// Shared original semantic predicate for owned builder records.
pub(crate) fn valid_brush_records(b: &wire::Brush, stops: &[wire::GradientStop]) -> bool {
    let Ok(count) = u32::try_from(stops.len()) else {
        return false;
    };
    valid_brush_with(b, count, |index| {
        stops
            .get(index as usize)
            .copied()
            .ok_or_else(|| error(E::Value, 0))
    })
    .unwrap_or(false)
}
fn valid_brush_with(
    b: &wire::Brush,
    count: u32,
    stop: impl Fn(u32) -> TransportResult<wire::GradientStop>,
) -> TransportResult<bool> {
    let spread = b.spread_method & 0x3fff_ffff;
    let outside = b.spread_method & 0x4000_0000 != 0;
    let conical = b.spread_method & 0x8000_0000 != 0;
    if b.kind > 10
        || !b.opacity.is_finite()
        || !(0.0..=1.0).contains(&b.opacity)
        || !point(b.start_point)
        || !point(b.end_point)
        || !point(b.center)
        || !b.radius.is_finite()
        || !b.radius_y.is_finite()
        || b.kind != 8 && (spread > 4 || spread == 4 && !matches!(b.kind, 1 | 2))
        || b.color_interpolation_mode > 1
        || conical && b.kind != 5
        || outside && (!gradient(b.kind) || spread != 0)
        || b.reserved0 != 0
        || b.reserved1 != 0
        || !finite(&b.offsets0)
        || !finite(&b.offsets1)
        || !finite(&b.coordinate_transform0)
        || !finite(&b.coordinate_transform1)
        || b.coordinate_transform0[3] != 0.0
        || b.coordinate_transform1[3] != 0.0
        || !b.colors.iter().copied().all(color)
    {
        return Ok(false);
    }
    if b.kind == 8 {
        if outside
            || conical
            || spread == 0
            || b.radius < 0.0
            || b.radius_y != 0.0
            || b.color_interpolation_mode != 0
            || spread > u32::MAX / 4
            || b.stop_count != spread * 4
            || b.stop_offset > count
            || b.stop_count > count - b.stop_offset
        {
            return Ok(false);
        }
        for family in 0..spread {
            let base = b.stop_offset + family * 4;
            let a = stop(base)?;
            let c = stop(base + 1)?;
            let d = stop(base + 2)?;
            let e = stop(base + 3)?;
            if ![a, c, d, e].into_iter().all(valid_stop)
                || a.offset <= 0.0
                || b.radius > a.offset
                || c.color.a != 0.0
                || c.offset != 0.0
                || e.color.g != 0.0
                || e.color.b != 0.0
                || e.color.a != 0.0
                || e.offset != 0.0
            {
                return Ok(false);
            }
            let direction = a.color.b * a.color.b + a.color.a * a.color.a;
            let n = c.color.b;
            if (direction - 1.0).abs() > 0.001 || n < 0.0 || n > 6.0 || n.floor() != n {
                return Ok(false);
            }
            let n = n as usize;
            let dashes = [
                d.color.r, d.color.g, d.color.b, d.color.a, d.offset, e.color.r,
            ];
            let mut period = 0.0;
            let mut draws = false;
            for &dash in &dashes[..n] {
                period += dash.abs();
                draws |= dash >= 0.0;
            }
            if dashes[n..].iter().any(|&v| v != 0.0) {
                return Ok(false);
            }
            let tolerance = if 1.0 < period { period } else { 1.0 } * 0.00001;
            if n == 0 && c.color.g != 0.0
                || n != 0 && (!draws || period <= 0.0 || (period - c.color.g).abs() > tolerance)
            {
                return Ok(false);
            }
        }
        return Ok(true);
    }
    if b.kind == 7 {
        let stored = stored_stop_count(b);
        if outside
            || conical
            || spread > 1
            || b.stop_count > 255
            || stored == 0 && b.stop_offset != 0
            || b.stop_offset > count
            || stored > count - b.stop_offset
        {
            return Ok(false);
        }
        for index in 0..stored {
            if !valid_stop(stop(b.stop_offset + index)?) {
                return Ok(false);
            }
        }
        return Ok(true);
    }
    if matches!(b.kind, 3 | 4) && (b.center.x <= 0.0 || b.center.y < 0.0) {
        return Ok(false);
    }
    if !gradient(b.kind) {
        return Ok(b.spread_method == 0
            && b.color_interpolation_mode == 0
            && (b.kind == 9 || b.stop_count == 0 && b.stop_offset == 0));
    }
    if b.stop_count == 0 || b.stop_offset > count || b.stop_count > count - b.stop_offset {
        return Ok(false);
    }
    if b.kind == 2 && (b.radius < 0.0 || b.radius_y < 0.0 || b.radius == 0.0 && b.radius_y == 0.0)
        || b.kind == 5 && (b.radius < 0.0 || b.radius_y < 0.0)
    {
        return Ok(false);
    }
    if b.kind == 10 {
        let boundary = b.radius as u32;
        let curves = b.radius_y as u32;
        if b.radius != boundary as f32
            || !(2..=128).contains(&boundary)
            || b.radius_y != curves as f32
            || curves == 0
            || u64::from(b.stop_count) != u64::from(boundary) * 2 + u64::from(curves)
            || b.colors[1].r != 0.0 && b.colors[1].r != 1.0
        {
            return Ok(false);
        }
    }
    let mut previous = f32::NEG_INFINITY;
    for index in 0..b.stop_count {
        let stop = stop(b.stop_offset + index)?;
        if !valid_stop(stop) || stop.offset < previous {
            return Ok(false);
        }
        previous = stop.offset;
    }
    Ok(true)
}
pub(super) fn validate_brush_table(input: &Input<'_>, r: &wire::Resource) -> TransportResult<()> {
    let bad = || error(E::Value, r.payload_offset);
    if r.payload_size == 0
        || r.payload_size % 256 != 0
        || r.auxiliary_size % 32 != 0
        || r.payload_size / 256 > 1 << 20
        || r.auxiliary_size / 32 > 64 * 1024
    {
        return Err(bad());
    }
    input.span(r.auxiliary_offset, r.auxiliary_size)?;
    for index in 0..r.payload_size / 256 {
        let offset = r.payload_offset + index * 256;
        if !valid_brush(
            input,
            &input.read(offset)?,
            r.auxiliary_offset,
            r.auxiliary_size / 32,
        )? {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
pub(super) fn validate_draw_brushes(
    input: &Input<'_>,
    c: &wire::Command,
    count: u32,
) -> TransportResult<()> {
    let bad = || error(E::Value, c.payload_offset);
    let source = matches!(c.kind, 28 | 29);
    if c.payload_size == 0 {
        return if source { Err(bad()) } else { Ok(()) };
    }
    if c.payload_size < 16 {
        return Err(bad());
    }
    let d: wire::DrawBrushes = input.read(c.payload_offset)?;
    let extra = if source {
        wire::SourceCoverageFrame::WIRE_SIZE as u64
    } else {
        0
    };
    if d.struct_size != 16
        || d.reserved != 0
        || d.brush_count == 0
        || d.brush_count != count
        || d.brush_count > 1 << 20
        || 16 + u64::from(d.brush_count) * 4 + extra != u64::from(c.payload_size)
        || d.brush_resource_index >= input.header.resource_count
    {
        return Err(bad());
    }
    let r = input.resource(d.brush_resource_index)?;
    if r.kind != 8 || r.payload_size % 256 != 0 {
        return Err(bad());
    }
    for index in 0..d.brush_count {
        let offset = c.payload_offset + 16 + index * 4;
        if input.read::<u32>(offset)? >= r.payload_size / 256 {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
