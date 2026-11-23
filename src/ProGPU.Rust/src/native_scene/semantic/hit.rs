//! Complete retained hit-index graph admission, including unreferenced records.
use super::*;
fn finite4(v: wire::Float4) -> bool {
    [v.x, v.y, v.z, v.w].into_iter().all(f32::is_finite)
}
fn bounds(min: wire::Point, max: wire::Point) -> bool {
    [min.x, min.y, max.x, max.y].into_iter().all(f32::is_finite) && min.x <= max.x && min.y <= max.y
}
pub(crate) fn primitive(v: &wire::HitTestPrimitive, segments: u32) -> bool {
    let mut path = true;
    if matches!(v.kind, 6 | 7) {
        let first = v.data1.x;
        let count = v.data1.y;
        path = first >= 0.0
            && count > 0.0
            && first <= u32::MAX as f32
            && count <= u32::MAX as f32
            && first.trunc() == first
            && count.trunc() == count;
        if path {
            path = (first as u64) + (count as u64) <= u64::from(segments)
                && (v.kind != 6 || v.data1.z == 0.0 || v.data1.z == 1.0)
                && (v.kind != 7
                    || v.data1.z >= 0.0
                        && v.data1.w >= 0.0
                        && (0.0..=3.0).contains(&v.data2.x)
                        && (0.0..=3.0).contains(&v.data2.y));
        }
    }
    bounds(v.bounds_min, v.bounds_max)
        && [
            v.data0,
            v.data1,
            v.data2,
            v.inverse_transform0,
            v.inverse_transform1,
        ]
        .into_iter()
        .all(finite4)
        && v.z_index.is_finite()
        && v.kind <= 7
        && v.flags & !15 == 0
        && v.flags & 12 != 12
        && v.clip_fill_rule <= 1
        && v.clip_flags <= 1
        && ((v.clip_flags == 0 && v.clip_segment_count == 0)
            || (v.clip_flags == 1 && v.clip_segment_count != 0))
        && u64::from(v.clip_start_segment) + u64::from(v.clip_segment_count) <= u64::from(segments)
        && path
}
pub(crate) fn node(v: &wire::HitTestNode, nodes: u32, indices: u32) -> bool {
    bounds(v.bounds_min, v.bounds_max)
        && v.child_count <= 4
        && u64::from(v.first_child) + u64::from(v.child_count) <= u64::from(nodes)
        && u64::from(v.first_primitive) + u64::from(v.primitive_count) <= u64::from(indices)
        && (v.child_count != 0 || v.first_child == 0)
}
pub(crate) fn contains(a: wire::Point, b: wire::Point, c: wire::Point, d: wire::Point) -> bool {
    !(c.x < a.x || c.y < a.y || d.x > b.x || d.y > b.y)
}
fn align(value: u64) -> u64 {
    (value + 15) & !15
}
pub(super) fn validate_hit_test_resource(
    input: &Input<'_>,
    r: &wire::Resource,
) -> TransportResult<()> {
    let bad = || error(E::Value, r.payload_offset);
    if r.payload_size != 40 || r.auxiliary_size == 0 {
        return Err(bad());
    }
    let p: wire::HitTestIndex = input.read(r.payload_offset)?;
    let node_offset = align(u64::from(p.primitive_count) * 128);
    let index_offset = align(node_offset + u64::from(p.node_count) * 32);
    let segment_offset = align(index_offset + u64::from(p.primitive_index_count) * 4);
    let total = segment_offset + u64::from(p.path_segment_count) * 48;
    if p.struct_size != 40
        || p.flags != 0
        || p.node_count == 0
        || p.primitive_index_count != p.primitive_count
        || total > u64::from(MAX_STREAM_BYTES)
        || p.primitive_offset != 0
        || u64::from(p.node_offset) != node_offset
        || u64::from(p.primitive_index_offset) != index_offset
        || u64::from(p.path_segment_offset) != segment_offset
        || u64::from(r.auxiliary_size) != total
    {
        return Err(bad());
    }
    let mut seen =
        crate::native_scene::reserve::<u8>(p.primitive_count as usize, r.payload_offset)?;
    seen.resize(p.primitive_count as usize, 0);
    let mut parents = crate::native_scene::reserve::<u8>(p.node_count as usize, r.payload_offset)?;
    parents.resize(p.node_count as usize, 0);
    let base = r.auxiliary_offset;
    let mut last = r.payload_offset;
    for index in 0..p.primitive_count {
        last = base + p.primitive_offset + index * 128;
        if !primitive(&input.read(last)?, p.path_segment_count) {
            return Err(error(E::Value, last));
        }
    }
    for index in 0..p.node_count {
        last = base + p.node_offset + index * 32;
        let n: wire::HitTestNode = input.read(last)?;
        if !node(&n, p.node_count, p.primitive_index_count)
            || n.child_count != 0 && n.first_child <= index
        {
            return Err(error(E::Value, last));
        }
        for child in n.first_child..n.first_child + n.child_count {
            if parents[child as usize] != 0 {
                return Err(error(E::Value, last));
            }
            parents[child as usize] = 1;
            let c: wire::HitTestNode = input.read(base + p.node_offset + child * 32)?;
            if !contains(n.bounds_min, n.bounds_max, c.bounds_min, c.bounds_max) {
                return Err(error(E::Value, last));
            }
        }
        for slot in n.first_primitive..n.first_primitive + n.primitive_count {
            last = base + p.primitive_index_offset + slot * 4;
            let index = input.read::<u32>(last)?;
            if index >= p.primitive_count || seen[index as usize] != 0 {
                return Err(error(E::Value, last));
            }
            seen[index as usize] = 1;
            let c: wire::HitTestPrimitive = input.read(base + p.primitive_offset + index * 128)?;
            if !contains(n.bounds_min, n.bounds_max, c.bounds_min, c.bounds_max) {
                return Err(error(E::Value, last));
            }
        }
    }
    if parents[1..].iter().any(|&v| v != 1) || seen.iter().any(|&v| v != 1) {
        return Err(error(E::Value, last));
    }
    for index in 0..p.path_segment_count {
        let offset = base + p.path_segment_offset + index * 48;
        if !values::segment(&input.read(offset)?, true) {
            return Err(error(E::Value, offset));
        }
    }
    Ok(())
}
