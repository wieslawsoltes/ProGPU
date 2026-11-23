use super::*;
use engine_resources::path::{material, resource_counts, source_coverage_placement};
pub(super) fn preflight(
    frame: &NativeCommandFrame,
    materials: &NativeBrushPage,
    per_point: bool,
    cost: &mut NativeDrawCommandCost,
    metrics: &mut NativeDrawPlanMetrics,
) -> Result<(), Error> {
    let command = frame.command();
    let resource = frame
        .scene
        .envelope()
        .resource(command.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    let source = command.record.kind == SCENE_COMMAND_DRAW_SOURCE_PATH;
    if source {
        let at = command
            .payload
            .len()
            .checked_sub(wire::SourceCoverageFrame::WIRE_SIZE)
            .ok_or(Error::InvalidInput)?;
        let captured =
            wire::SourceCoverageFrame::read(&command.payload[at..]).ok_or(Error::InvalidInput)?;
        source_coverage_placement(
            &captured,
            &frame.source_state,
            frame.target,
            frame.presentation,
        )?;
    }
    let (paths, segments, nodes) = resource_counts(resource.payload, resource.auxiliary)?;
    if paths > 1 << 20 || segments > 1 << 24 || nodes > 1 << 22 || paths > u32::MAX as usize / 6 {
        return Err(Error::InvalidInput);
    }
    if per_point && nodes != 0 {
        return Err(Error::UnsupportedExecution);
    }
    let raw_nodes = &resource.auxiliary[segments * wire::PathSegment::WIRE_SIZE..];
    cost.vertex_bytes = paths as u64 * 4 * 56;
    cost.index_bytes = paths as u64 * 6 * 4;
    for i in 0..segments {
        let s: wire::PathSegment = read(resource.auxiliary, i)?;
        if !values::segment(&s, true) {
            return Err(Error::InvalidInput);
        }
        if per_point && s.kind == PATH_SEGMENT_ARC {
            return Err(Error::UnsupportedExecution);
        }
    }
    let mut end = 0u64;
    for i in 0..paths {
        let mut p: wire::PathFill = read(resource.payload, i)?;
        if let Some((_, brush)) = materials.draw_brush(frame.command_index, i as u32)? {
            if !source {
                apply_path_transform(&mut p, &frame.state);
            }
            material(&mut p, brush);
        } else {
            apply_path_state(&mut p, &frame.state);
        }
        let bytes = values::path_with(&p, segments as u64, nodes as u64, |index| {
            wire::PathBooleanNode::read(&raw_nodes[index * wire::PathBooleanNode::WIRE_SIZE..])
                .expect("complete admitted original Boolean slot")
        })
        .ok_or(Error::InvalidInput)?;
        if per_point {
            if p.segment_offset < end {
                return Err(Error::UnsupportedExecution);
            }
            end = p.segment_offset + p.segment_count;
        }
        if bytes > MAX_COVERAGE_BYTES - cost.coverage_bytes {
            return Err(Error::Capacity);
        }
        cost.coverage_bytes += bytes;
    }
    metrics.path_draw_count += 1;
    add(&mut metrics.path_count, paths as u64)?;
    add(&mut metrics.path_segment_count, segments as u64)?;
    add(&mut metrics.path_boolean_node_count, nodes as u64)?;
    Ok(())
}
