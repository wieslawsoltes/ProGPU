//! Full raw layer-mask families, including bounded nested picture streams.
use super::*;
use wire::WireRecord;

pub(super) struct ParsedLayerMask {
    /// Absolute owned-stream locations, in original component order.
    pub nested_streams: Vec<(u32, u32)>,
}
fn opacity(value: f32) -> bool {
    value.is_finite() && (0.0..=1.0).contains(&value)
}
fn bounds(b: wire::ImageRect) -> bool {
    [b.x, b.y, b.width, b.height]
        .into_iter()
        .all(f32::is_finite)
        && b.width > 0.0
        && b.height > 0.0
}
fn transform(t: wire::Affine2d) -> bool {
    let determinant = f64::from(t.m11) * f64::from(t.m22) - f64::from(t.m12) * f64::from(t.m21);
    if ![t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
        .into_iter()
        .all(f32::is_finite)
        || !determinant.is_finite()
        || determinant.abs() <= 0.000001
    {
        return false;
    }
    let inverse = 1.0 / determinant;
    [
        f64::from(t.m22) * inverse,
        -f64::from(t.m12) * inverse,
        -f64::from(t.m21) * inverse,
        f64::from(t.m11) * inverse,
        (f64::from(t.m21) * f64::from(t.m32) - f64::from(t.m22) * f64::from(t.m31)) * inverse,
        (f64::from(t.m12) * f64::from(t.m31) - f64::from(t.m11) * f64::from(t.m32)) * inverse,
    ]
    .into_iter()
    .all(|v| v.is_finite() && v >= -f64::from(f32::MAX) && v <= f64::from(f32::MAX))
}
fn analytic(m: &wire::LayerMask) -> bool {
    let exact_clip = m.flags & SCENE_LAYER_MASK_AXIS_CLIP_AREA == 0
        || m.transform.m11 == 1.0
            && m.transform.m12 == 0.0
            && m.transform.m21 == 0.0
            && m.transform.m22 == 1.0
            && m.transform.m31 == 0.0
            && m.transform.m32 == 0.0
            && m.opacity == 1.0
            && m.corner_radii_x.into_iter().all(|v| v == 0.0)
            && m.corner_radii_y.into_iter().all(|v| v == 0.0);
    m.struct_size == 104
        && m.kind == SCENE_LAYER_MASK_ROUNDED_RECTANGLE
        && m.flags & !SCENE_LAYER_MASK_AXIS_CLIP_AREA == 0
        && exact_clip
        && m.reserved == 0
        && m.reserved0 == 0
        && m.reserved1 == 0
        && m.reserved2 == 0
        && bounds(m.bounds)
        && transform(m.transform)
        && opacity(m.opacity)
        && m.corner_radii_x
            .into_iter()
            .chain(m.corner_radii_y)
            .all(|v| v.is_finite() && v >= 0.0)
}
fn coverage(m: &wire::LayerCoverageMask, size: u32) -> bool {
    let bytes = if m.height == 0 {
        0
    } else {
        u64::from(m.row_bytes) * u64::from(m.height - 1) + u64::from(m.width)
    };
    m.struct_size == 80
        && m.kind == SCENE_LAYER_MASK_COVERAGE_BITMAP
        && m.flags == 0
        && m.reserved0 == 0
        && m.reserved1 == 0
        && (1..=16384).contains(&m.width)
        && (1..=16384).contains(&m.height)
        && m.row_bytes >= m.width
        && matches!(m.sampling, IMAGE_SAMPLING_NEAREST | IMAGE_SAMPLING_LINEAR)
        && bytes == u64::from(size)
        && bounds(m.bounds)
        && transform(m.transform)
        && opacity(m.opacity)
}
fn picture(
    input: &Input<'_>,
    m: &wire::LayerPictureMask,
    streams: u32,
    size: u32,
) -> TransportResult<bool> {
    let extent = m.flags & SCENE_PICTURE_MASK_SOURCE_EXTENT != 0;
    if m.struct_size != 72
        || m.kind != SCENE_LAYER_MASK_PICTURE
        || m.flags & !SCENE_PICTURE_MASK_SOURCE_EXTENT != 0
        || if extent {
            !(1..=16384).contains(&m.reserved0) || !(1..=16384).contains(&m.reserved1)
        } else {
            m.reserved0 != 0 || m.reserved1 != 0
        }
        || m.stream_size == 0
        || m.stream_offset > size
        || m.stream_size > size - m.stream_offset
        || !bounds(m.bounds)
        || !transform(m.transform)
        || !opacity(m.opacity)
        || m.stream_size < wire::Header::WIRE_SIZE as u32
        || m.stream_size > SCENE_MAX_STREAM_BYTES
    {
        return Ok(false);
    }
    let h: wire::Header = input.read(streams + m.stream_offset)?;
    Ok(h.struct_size >= wire::Header::WIRE_SIZE as u32
        && h.magic == SCENE_STREAM_MAGIC
        && h.stream_version == SCENE_STREAM_VERSION
        && h.endian_marker == SCENE_STREAM_ENDIAN_MARKER
        && h.flags == 0
        && h.total_size == m.stream_size
        && h.scene_id != 0
        && h.generation != 0
        && h.reserved0 == 0
        && h.reserved1 == 0)
}
fn brush_mask(
    input: &Input<'_>,
    m: &wire::LayerBrushMask,
    stops: u32,
    count: u32,
) -> TransportResult<bool> {
    let stored = brush::stored_stop_count(&m.brush);
    if m.struct_size != 320
        || m.kind != SCENE_LAYER_MASK_BRUSH
        || m.flags != 0
        || m.gradient_stop_count != stored
        || !bounds(m.bounds)
        || !transform(m.transform)
        || !opacity(m.opacity)
        || m.reserved0 != 0
        || m.brush.kind != SCENE_BRUSH_TILE_PATTERN
            && (m.brush.stop_offset > count || stored > count - m.brush.stop_offset)
    {
        return Ok(false);
    }
    brush::valid_brush(input, &m.brush, stops, count)
}
fn geometry_mask(
    input: &Input<'_>,
    m: &wire::LayerGeometryMask,
    primitives: u32,
    primitive_count: u32,
    stops: u32,
    stop_count: u32,
) -> TransportResult<bool> {
    let stored = brush::stored_stop_count(&m.brush);
    if m.struct_size != 336
        || m.kind != SCENE_LAYER_MASK_GEOMETRY
        || m.flags != 0
        || m.reserved0 != 0
        || m.reserved1 != 0
        || m.reserved2 != 0
        || m.primitive_count == 0
        || m.primitive_offset > primitive_count
        || m.primitive_count > primitive_count - m.primitive_offset
        || m.gradient_stop_count != stored
        || !bounds(m.bounds)
        || !transform(m.transform)
        || !opacity(m.opacity)
        || m.brush.kind != SCENE_BRUSH_TILE_PATTERN
            && (m.brush.stop_offset > stop_count || stored > stop_count - m.brush.stop_offset)
        || !brush::valid_brush(input, &m.brush, stops, stop_count)?
    {
        return Ok(false);
    }
    for index in m.primitive_offset..m.primitive_offset + m.primitive_count {
        if !values::geometry(
            &input.read(primitives + index * wire::GeometryPrimitive::WIRE_SIZE as u32)?,
        ) {
            return Ok(false);
        }
    }
    Ok(true)
}
fn vector(
    input: &Input<'_>,
    m: &wire::LayerVectorMask,
    paths: u32,
    size: u32,
) -> TransportResult<bool> {
    if m.struct_size != 32
        || m.kind != SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
        || m.flags != 0
        || !(1..=64).contains(&m.path_count)
        || m.segment_count == 0
        || !opacity(m.opacity)
        || m.reserved1 != 0
        || m.boolean_node_count > 64 * 63
    {
        return Ok(false);
    }
    let path_bytes = u64::from(m.path_count) * 88;
    let segment_bytes = u64::from(m.segment_count) * 48;
    if path_bytes + segment_bytes + u64::from(m.boolean_node_count) * 48 != u64::from(size) {
        return Ok(false);
    }
    let segments = paths + path_bytes as u32;
    let nodes = segments + segment_bytes as u32;
    let mut expected = 0u64;
    // At most one original 63-node program is decoded at once. The complete
    // auxiliary inventory remains borrowed, with no unbounded scratch copy.
    let mut program = [wire::PathBooleanNode::default(); 63];
    for index in 0..m.path_count {
        let p: wire::ClipPath = input.read(paths + index * 88)?;
        if p.segment_count == 0
            || p.segment_offset > u64::from(m.segment_count)
            || p.segment_count > u64::from(m.segment_count) - p.segment_offset
            || ![p.min_x, p.min_y, p.max_x, p.max_y]
                .into_iter()
                .all(f32::is_finite)
            || p.max_x <= p.min_x
            || p.max_y <= p.min_y
            || !transform(p.transform)
            || p.fill_rule > FILL_RULE_EVEN_ODD
            || !matches!(p.sample_grid, 1 | 4 | 8)
            || p.operation > CLIP_DIFFERENCE
            || p.reserved != 0
            || p.boolean_node_count > 63
            || p.boolean_node_offset > u64::from(m.boolean_node_count)
            || p.boolean_node_count > u64::from(m.boolean_node_count) - p.boolean_node_offset
            || p.boolean_node_count != 0 && p.boolean_node_offset != expected
            || p.boolean_node_count == 0 && p.boolean_node_offset != 0
        {
            return Ok(false);
        }
        for item in 0..p.boolean_node_count as usize {
            program[item] =
                input.read(nodes + (p.boolean_node_offset as u32 + item as u32) * 48)?;
        }
        if !values::boolean(
            p.segment_offset,
            p.segment_count,
            0,
            p.boolean_node_count,
            p.fill_rule,
            &program[..p.boolean_node_count as usize],
        ) {
            return Ok(false);
        }
        expected += p.boolean_node_count;
    }
    if expected != u64::from(m.boolean_node_count) {
        return Ok(false);
    }
    for index in 0..m.segment_count {
        if !values::segment(&input.read(segments + index * 48)?, true) {
            return Ok(false);
        }
    }
    Ok(true)
}

pub(super) fn validate_layer_mask_resource(
    input: &Input<'_>,
    r: &wire::Resource,
) -> TransportResult<ParsedLayerMask> {
    // The native routine reports the descriptor's payload offset for every
    // family failure, even when the malformed record lives in auxiliary bytes.
    parse(input, r).map_err(|mut failure| {
        failure.offset = r.payload_offset;
        failure
    })
}
fn parse(input: &Input<'_>, r: &wire::Resource) -> TransportResult<ParsedLayerMask> {
    let bad = || error(E::Value, r.payload_offset);
    if r.payload_size < 8 {
        return Err(bad());
    }
    input.span(r.payload_offset, r.payload_size)?;
    input.span(r.auxiliary_offset, r.auxiliary_size)?;
    let mut nested_streams = Vec::new();
    match input.read::<u32>(r.payload_offset + 4)? {
        SCENE_LAYER_MASK_ROUNDED_RECTANGLE => {
            if r.payload_size != 104
                || r.auxiliary_size != 0
                || !analytic(&input.read(r.payload_offset)?)
            {
                return Err(bad());
            }
        }
        SCENE_LAYER_MASK_COVERAGE_BITMAP => {
            if r.payload_size != 80
                || r.auxiliary_size == 0
                || !coverage(&input.read(r.payload_offset)?, r.auxiliary_size)
            {
                return Err(bad());
            }
        }
        SCENE_LAYER_MASK_ANALYTIC_CHAIN => {
            if r.payload_size != 432 || r.auxiliary_size != 0 {
                return Err(bad());
            }
            let m: wire::LayerMaskChain = input.read(r.payload_offset)?;
            if m.struct_size != 432 || m.flags != 0 || !(2..=4).contains(&m.mask_count) {
                return Err(bad());
            }
            for index in 0..4 {
                if if index < m.mask_count {
                    !analytic(&m.masks[index as usize])
                } else {
                    input
                        .span(r.payload_offset + 16 + index * 104, 104)?
                        .iter()
                        .any(|&b| b != 0)
                } {
                    return Err(bad());
                }
            }
        }
        SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN => {
            if r.payload_size != 32
                || r.auxiliary_size == 0
                || !vector(
                    input,
                    &input.read(r.payload_offset)?,
                    r.auxiliary_offset,
                    r.auxiliary_size,
                )?
            {
                return Err(bad());
            }
        }
        SCENE_LAYER_MASK_BRUSH => {
            if r.payload_size != 320 || r.auxiliary_size % 32 != 0 {
                return Err(bad());
            }
            let m: wire::LayerBrushMask = input.read(r.payload_offset)?;
            let count = r.auxiliary_size / 32;
            if count > SCENE_MAX_GRADIENT_STOPS
                || m.gradient_stop_count != count
                || m.brush.kind != SCENE_BRUSH_TILE_PATTERN && m.brush.stop_offset != 0
                || !brush_mask(input, &m, r.auxiliary_offset, count)?
            {
                return Err(bad());
            }
        }
        SCENE_LAYER_MASK_GEOMETRY => {
            if r.payload_size != 336 {
                return Err(bad());
            }
            let m: wire::LayerGeometryMask = input.read(r.payload_offset)?;
            let bytes = u64::from(m.primitive_count) * wire::GeometryPrimitive::WIRE_SIZE as u64;
            if bytes + u64::from(m.gradient_stop_count) * 32 != u64::from(r.auxiliary_size)
                || m.primitive_offset != 0
                || !geometry_mask(
                    input,
                    &m,
                    r.auxiliary_offset,
                    m.primitive_count,
                    r.auxiliary_offset + bytes as u32,
                    m.gradient_stop_count,
                )?
            {
                return Err(bad());
            }
        }
        SCENE_LAYER_MASK_PICTURE => {
            if r.payload_size != 72 || r.auxiliary_size == 0 {
                return Err(bad());
            }
            let m: wire::LayerPictureMask = input.read(r.payload_offset)?;
            if m.stream_offset != 0
                || m.stream_size != r.auxiliary_size
                || !picture(input, &m, r.auxiliary_offset, r.auxiliary_size)?
            {
                return Err(bad());
            }
            nested_streams = reserve(1, r.payload_offset)?;
            nested_streams.push((r.auxiliary_offset, m.stream_size));
        }
        SCENE_LAYER_MASK_COMPOSITE => return composite(input, r),
        _ => return Err(bad()),
    }
    Ok(ParsedLayerMask { nested_streams })
}
fn composite(input: &Input<'_>, r: &wire::Resource) -> TransportResult<ParsedLayerMask> {
    let bad = || error(E::Value, r.payload_offset);
    if !matches!(r.payload_size, 48 | 64) {
        return Err(bad());
    }
    let mut raw = [0u8; 64];
    raw[..r.payload_size as usize].copy_from_slice(input.span(r.payload_offset, r.payload_size)?);
    let m = wire::LayerCompositeMask::read(&raw).ok_or_else(bad)?;
    let lengths = [
        u64::from(m.brush_mask_count) * 320,
        u64::from(m.geometry_mask_count) * 336,
        u64::from(m.geometry_primitive_count) * wire::GeometryPrimitive::WIRE_SIZE as u64,
        u64::from(m.picture_mask_count) * 72,
        u64::from(m.picture_stream_bytes),
        u64::from(m.path_count) * 88,
        u64::from(m.segment_count) * 48,
        u64::from(m.boolean_node_count) * 48,
        u64::from(m.gradient_stop_count) * 32,
    ];
    if lengths.into_iter().sum::<u64>() != u64::from(r.auxiliary_size) {
        return Err(bad());
    }
    let legacy = m.struct_size == 48
        && m.picture_mask_count == 0
        && m.picture_stream_bytes == 0
        && m.reserved0 == 0
        && m.reserved1 == 0;
    // The native count expression is uint32 arithmetic; preserve its wrapping
    // behavior, while the independent full byte inventory still bounds traversal.
    let components = m
        .brush_mask_count
        .wrapping_add(m.geometry_mask_count)
        .wrapping_add(m.picture_mask_count)
        .wrapping_add(u32::from(m.path_count != 0));
    if !legacy && m.struct_size != 64
        || m.kind != SCENE_LAYER_MASK_COMPOSITE
        || m.flags != 0
        || m.reserved0 != 0
        || m.reserved1 != 0
        || m.component_count != components
        || !(2..=64).contains(&m.component_count)
        || m.brush_mask_count == 0 && m.geometry_mask_count == 0 && m.picture_mask_count == 0
        || (m.geometry_mask_count == 0) != (m.geometry_primitive_count == 0)
        || (m.picture_mask_count == 0) != (m.picture_stream_bytes == 0)
        || m.gradient_stop_count > SCENE_MAX_GRADIENT_STOPS
        || !opacity(m.opacity)
        || m.path_count == 0 && (m.segment_count != 0 || m.boolean_node_count != 0)
        || m.path_count != 0 && (m.path_count > 64 || m.segment_count == 0)
    {
        return Err(bad());
    }
    let mut offsets = [0u32; 9];
    let mut cursor = r.auxiliary_offset;
    for (slot, len) in offsets.iter_mut().zip(lengths) {
        *slot = cursor;
        cursor += len as u32;
    }
    let [
        brushes,
        geometry_masks,
        primitives,
        pictures,
        streams,
        paths,
        _segments,
        _nodes,
        stops,
    ] = offsets;
    for index in 0..m.brush_mask_count {
        if !brush_mask(
            input,
            &input.read(brushes + index * 320)?,
            stops,
            m.gradient_stop_count,
        )? {
            return Err(bad());
        }
    }
    let mut expected = 0;
    for index in 0..m.geometry_mask_count {
        let g: wire::LayerGeometryMask = input.read(geometry_masks + index * 336)?;
        if g.primitive_offset != expected
            || !geometry_mask(
                input,
                &g,
                primitives,
                m.geometry_primitive_count,
                stops,
                m.gradient_stop_count,
            )?
        {
            return Err(bad());
        }
        expected += g.primitive_count;
    }
    if expected != m.geometry_primitive_count {
        return Err(bad());
    }
    expected = 0;
    let mut nested_streams = reserve(m.picture_mask_count as usize, r.payload_offset)?;
    for index in 0..m.picture_mask_count {
        let p: wire::LayerPictureMask = input.read(pictures + index * 72)?;
        if p.stream_offset != expected || !picture(input, &p, streams, m.picture_stream_bytes)? {
            return Err(bad());
        }
        nested_streams.push((streams + p.stream_offset, p.stream_size));
        expected += p.stream_size;
    }
    if expected != m.picture_stream_bytes {
        return Err(bad());
    }
    if m.path_count != 0 {
        let v = wire::LayerVectorMask {
            struct_size: 32,
            kind: SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
            flags: 0,
            path_count: m.path_count,
            segment_count: m.segment_count,
            opacity: 1.0,
            boolean_node_count: m.boolean_node_count,
            reserved1: 0,
        };
        if !vector(
            input,
            &v,
            paths,
            (lengths[5] + lengths[6] + lengths[7]) as u32,
        )? {
            return Err(bad());
        }
    }
    Ok(ParsedLayerMask { nested_streams })
}
