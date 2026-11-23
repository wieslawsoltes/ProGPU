//! Import the admitted native page into the shared GPU-query storage records.
//! Every source node/reference/coordinate bit and the original stream owner
//! survives; this path performs no geometry construction or quadtree rebuild.
use super::*;
use crate::picture_archive::{ArchiveHitIndex, HitNode, HitPrimitive};
use wire::WireRecord;

impl NativeScene {
    /// Obtain the original hit page for `GpuHitIndex::new`. Upload and execution
    /// still perform the existing device/provider/query-capacity admission.
    pub fn hit_index(&self, resource_index: u32) -> TransportResult<ArchiveHitIndex> {
        let view = self.envelope().resource(resource_index).ok_or_else(|| {
            error(
                NativeSceneValidationError::Range,
                self.envelope().header().resource_offset,
            )
        })?;
        if view.record.kind != wire::constants::SCENE_RESOURCE_HIT_TEST_INDEX {
            return Err(error(
                NativeSceneValidationError::Value,
                view.record.payload_offset,
            ));
        }
        let page = wire::HitTestIndex::read(view.payload).ok_or_else(|| {
            error(
                NativeSceneValidationError::Value,
                view.record.payload_offset,
            )
        })?;
        let primitives = copy_records::<wire::HitTestPrimitive, _>(
            view.auxiliary,
            page.primitive_offset,
            page.primitive_count,
            view.record.auxiliary_offset,
            |p| HitPrimitive {
                bounds_min: point(p.bounds_min),
                bounds_max: point(p.bounds_max),
                data0: float4(p.data0),
                data1: float4(p.data1),
                data2: float4(p.data2),
                inverse_transform0: float4(p.inverse_transform0),
                inverse_transform1: float4(p.inverse_transform1),
                kind: p.kind,
                flags: p.flags,
                id: p.id,
                z_index: p.z_index,
                clip_start_segment: p.clip_start_segment,
                clip_segment_count: p.clip_segment_count,
                clip_fill_rule: p.clip_fill_rule,
                clip_flags: p.clip_flags,
            },
        )?;
        let nodes = copy_records::<wire::HitTestNode, _>(
            view.auxiliary,
            page.node_offset,
            page.node_count,
            view.record.auxiliary_offset,
            |n| HitNode {
                bounds_min: point(n.bounds_min),
                bounds_max: point(n.bounds_max),
                first_child: n.first_child,
                child_count: n.child_count,
                first_primitive: n.first_primitive,
                primitive_count: n.primitive_count,
            },
        )?;
        let indices = copy_records::<u32, _>(
            view.auxiliary,
            page.primitive_index_offset,
            page.primitive_index_count,
            view.record.auxiliary_offset,
            |i| i,
        )?;
        let segments = copy_records::<wire::PathSegment, _>(
            view.auxiliary,
            page.path_segment_offset,
            page.path_segment_count,
            view.record.auxiliary_offset,
            |s| crate::path::Segment {
                points: [point(s.p0), point(s.p1), point(s.p2), point(s.p3)],
                kind: s.kind,
                parameters: [s.pad0, s.pad1, s.pad2],
            },
        )?;
        Ok(ArchiveHitIndex::from_native_scene(
            self.clone(),
            primitives.into(),
            segments.into(),
            nodes.into(),
            indices.into(),
        ))
    }
}
fn point(value: wire::Point) -> [f32; 2] {
    [value.x, value.y]
}
fn float4(value: wire::Float4) -> [f32; 4] {
    [value.x, value.y, value.z, value.w]
}
fn copy_records<T: WireRecord, U>(
    bytes: &[u8],
    offset: u32,
    count: u32,
    base: u32,
    convert: impl Fn(T) -> U,
) -> TransportResult<Vec<U>> {
    let failure = || error(NativeSceneValidationError::Value, base + offset);
    let size = (count as usize)
        .checked_mul(T::WIRE_SIZE)
        .ok_or_else(failure)?;
    let end = (offset as usize).checked_add(size).ok_or_else(failure)?;
    let source = bytes.get(offset as usize..end).ok_or_else(failure)?;
    let mut output = reserve(count as usize, base + offset)?;
    for record in source.chunks_exact(T::WIRE_SIZE) {
        output.push(convert(T::read(record).ok_or_else(failure)?));
    }
    Ok(output)
}
