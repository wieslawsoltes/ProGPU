//! Original typed hit-index producer, retaining complete DAG inventories before
//! any resource publication. Source-owner boundaries remain CPU-only metadata.
use super::*;
use crate::native_scene::semantic::{hit, values};

impl NativeSceneBuilder {
    pub fn set_hit_test_owner(&mut self, owner: Option<i32>) -> BuildResult<()> {
        if (self.hit_test_owners.is_empty() && owner.is_none())
            || self
                .hit_test_owners
                .last()
                .is_some_and(|b| b.owner == owner)
        {
            return Ok(());
        }
        let first = self.commands.len();
        if let Some(last) = self
            .hit_test_owners
            .last_mut()
            .filter(|b| b.first_command == first)
        {
            last.owner = owner;
        } else {
            reserve_append(&mut self.hit_test_owners, 1)?;
            self.hit_test_owners.push(HitTestOwnerBoundary {
                first_command: first,
                owner,
            });
        }
        Ok(())
    }

    pub fn add_hit_test_index(
        &mut self,
        primitives: &[wire::HitTestPrimitive],
        nodes: &[wire::HitTestNode],
        indices: &[u32],
        segments: &[wire::PathSegment],
    ) -> BuildResult<u32> {
        if nodes.is_empty()
            || indices.len() != primitives.len()
            || self.resources.len() >= MAX_RESOURCES as usize
            || [primitives.len(), nodes.len(), indices.len(), segments.len()]
                .into_iter()
                .any(|n| n > u32::MAX as usize)
        {
            return Err(B::InvalidArgument);
        }
        if !primitives
            .iter()
            .all(|p| hit::primitive(p, segments.len() as u32))
            || !nodes
                .iter()
                .all(|n| hit::node(n, nodes.len() as u32, indices.len() as u32))
            || indices.iter().any(|&i| i as usize >= primitives.len())
            || !segments.iter().all(|s| values::segment(s, true))
        {
            return Err(B::InvalidArgument);
        }
        let align = |value: u64| (value + 15) & !15;
        let node_offset = align(primitives.len() as u64 * wire::HitTestPrimitive::WIRE_SIZE as u64);
        let index_offset =
            align(node_offset + nodes.len() as u64 * wire::HitTestNode::WIRE_SIZE as u64);
        let segment_offset = align(index_offset + indices.len() as u64 * 4);
        let auxiliary_size =
            segment_offset + segments.len() as u64 * wire::PathSegment::WIRE_SIZE as u64;
        if auxiliary_size > u64::from(MAX_STREAM_BYTES) || auxiliary_size > usize::MAX as u64 {
            return Err(B::CapacityExceeded);
        }
        let mut seen = zeroed(primitives.len())?;
        let mut parents = zeroed(nodes.len())?;
        for (index, node) in nodes.iter().enumerate() {
            if node.child_count != 0 && node.first_child as usize <= index {
                return Err(B::InvalidArgument);
            }
            for child in node.first_child..node.first_child + node.child_count {
                if parents[child as usize] != 0 {
                    return Err(B::InvalidArgument);
                }
                parents[child as usize] = 1;
                let child = &nodes[child as usize];
                if !hit::contains(
                    node.bounds_min,
                    node.bounds_max,
                    child.bounds_min,
                    child.bounds_max,
                ) {
                    return Err(B::InvalidArgument);
                }
            }
            for slot in node.first_primitive..node.first_primitive + node.primitive_count {
                let index = indices[slot as usize] as usize;
                if seen[index] != 0 {
                    return Err(B::InvalidArgument);
                }
                seen[index] = 1;
                let primitive = &primitives[index];
                if !hit::contains(
                    node.bounds_min,
                    node.bounds_max,
                    primitive.bounds_min,
                    primitive.bounds_max,
                ) {
                    return Err(B::InvalidArgument);
                }
            }
        }
        if parents[1..].iter().any(|&v| v != 1) || seen.iter().any(|&v| v != 1) {
            return Err(B::InvalidArgument);
        }
        let mut resource = self.new_resource(SCENE_RESOURCE_HIT_TEST_INDEX);
        resource.payload = encode(&[wire::HitTestIndex {
            struct_size: wire::HitTestIndex::WIRE_SIZE as u32,
            flags: 0,
            primitive_count: primitives.len() as u32,
            node_count: nodes.len() as u32,
            primitive_index_count: indices.len() as u32,
            path_segment_count: segments.len() as u32,
            primitive_offset: 0,
            node_offset: node_offset as u32,
            primitive_index_offset: index_offset as u32,
            path_segment_offset: segment_offset as u32,
        }])?;
        resource.auxiliary = zeroed(auxiliary_size as usize)?;
        write_records(primitives, &mut resource.auxiliary)?;
        write_records(nodes, &mut resource.auxiliary[node_offset as usize..])?;
        write_records(indices, &mut resource.auxiliary[index_offset as usize..])?;
        write_records(segments, &mut resource.auxiliary[segment_offset as usize..])?;
        reserve_append(&mut self.resources, 1)?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
}
