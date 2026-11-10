//! Native path batches with independent explicit physical-source policy.
use super::*;
use crate::native_scene::semantic::{source_draw, values};
impl NativeSceneBuilder {
    pub fn draw_paths(
        &mut self,
        paths: &[wire::PathFill],
        segments: &[wire::PathSegment],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
        boolean_nodes: &[wire::PathBooleanNode],
    ) -> BuildResult<()> {
        self.draw_paths_core(
            paths,
            segments,
            brush_indices,
            bounds,
            state_resource_index,
            boolean_nodes,
            None,
        )
    }
    pub fn draw_source_paths(
        &mut self,
        paths: &[wire::PathFill],
        segments: &[wire::PathSegment],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        frame: &wire::SourceCoverageFrame,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        self.draw_paths_core(
            paths,
            segments,
            brush_indices,
            bounds,
            state_resource_index,
            &[],
            Some(frame),
        )
    }
    fn draw_paths_core(
        &mut self,
        paths: &[wire::PathFill],
        segments: &[wire::PathSegment],
        brush_indices: &[u32],
        bounds: wire::ImageRect,
        state_resource_index: u32,
        boolean_nodes: &[wire::PathBooleanNode],
        frame: Option<&wire::SourceCoverageFrame>,
    ) -> BuildResult<()> {
        if frame.is_some_and(|frame| {
            !boolean_nodes.is_empty()
                || brush_indices.len() != paths.len()
                || !source_paths(paths, segments, frame)
        }) || paths.is_empty()
            || segments.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, true)
            || (!brush_indices.is_empty() && brush_indices.len() != paths.len())
            || self.resources.len() >= MAX_RESOURCES as usize
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut covered = 0u64;
        let mut expected_boolean = 0u64;
        for path in paths {
            if path.segment_offset > covered
                || (path.boolean_node_count != 0 && path.boolean_node_offset != expected_boolean)
                || values::path(path, segments.len() as u64, boolean_nodes).is_none()
            {
                return Err(B::InvalidArgument);
            }
            covered = covered.max(
                path.segment_offset
                    .checked_add(path.segment_count)
                    .ok_or(B::InvalidArgument)?,
            );
            expected_boolean = expected_boolean
                .checked_add(path.boolean_node_count)
                .ok_or(B::InvalidArgument)?;
        }
        if covered != segments.len() as u64
            || expected_boolean != boolean_nodes.len() as u64
            || !segments.iter().all(|s| values::segment(s, true))
            || brush_indices
                .iter()
                .any(|&b| b as usize >= self.brushes.len())
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        reserve_append(&mut self.commands, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_PATH_BATCH);
        resource.payload = encode(paths)?;
        resource.auxiliary = encode(segments)?;
        append_encoded(&mut resource.auxiliary, boolean_nodes)?;
        let mut command = self.new_command(
            if frame.is_some() {
                SCENE_COMMAND_DRAW_SOURCE_PATH
            } else {
                SCENE_COMMAND_DRAW_PATH
            },
            bounds,
            state_resource_index,
            self.resources.len() as u32,
        );
        command.payload = self.brush_payload(brush_indices, false)?;
        if let Some(frame) = frame {
            append_encoded(&mut command.payload, &[*frame])?;
        }
        self.resources.push(resource);
        self.commands.push(command);
        Ok(())
    }
}
fn source_paths(
    paths: &[wire::PathFill],
    segments: &[wire::PathSegment],
    frame: &wire::SourceCoverageFrame,
) -> bool {
    if !source_draw::valid_source_coverage_frame(frame)
        || paths.is_empty()
        || segments.len() > 1048576
    {
        return false;
    }
    let mut end = 0u64;
    for path in paths {
        if path.segment_offset != end
            || !source_draw::valid_source_path_contours(path, segments.len() as u64, |i| {
                segments.get(i as usize).copied()
            })
        {
            return false;
        }
        end += path.segment_count;
    }
    end == segments.len() as u64
}
