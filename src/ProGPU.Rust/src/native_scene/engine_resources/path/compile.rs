//! Native source inventory admission, material mapping and guideline deformation.
use super::*;
impl NativePathPage {
    pub fn compile(
        scene: Arc<NativeScene>,
        frames: &[NativeCommandFrame],
        materials: &NativeBrushPage,
    ) -> Result<Self, Error> {
        if !Arc::ptr_eq(&scene, materials.scene()) {
            return Err(Error::InvalidInput);
        }
        let (mut paths, mut segments, mut nodes, mut source_frames, mut brush_indices, mut draws) = (
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        );
        let mut metrics = NativePathPageMetrics::default();
        let mut last = None;
        for frame in frames {
            if !Arc::ptr_eq(&scene, &frame.scene)
                || last.is_some_and(|previous| frame.command_index <= previous)
            {
                return Err(Error::InvalidInput);
            }
            last = Some(frame.command_index);
            let command = frame.command();
            if !matches!(
                command.record.kind,
                SCENE_COMMAND_DRAW_PATH | SCENE_COMMAND_DRAW_SOURCE_PATH
            ) {
                return Err(Error::InvalidInput);
            }
            let resource = scene
                .envelope()
                .resource(command.record.resource_index)
                .ok_or(Error::InvalidInput)?;
            if resource.record.kind != SCENE_RESOURCE_PATH_BATCH {
                return Err(Error::InvalidInput);
            }
            let (path_count, segment_count, node_count) =
                counts(resource.payload, resource.auxiliary)?;
            if path_count > 1 << 20
                || segment_count > 1 << 24
                || node_count > 1 << 22
                || path_count > u32::MAX as usize / 6
            {
                return Err(Error::InvalidInput);
            }
            let cursor = NativeStateCursor::new(scene.clone(), frame.source_state_dpi());
            let per_point = cursor.has_per_point_guidelines(&frame.source_state)?;
            if per_point && node_count != 0 {
                return Err(Error::UnsupportedExecution);
            }
            let mut source_frame = NativeSourcePathFrame::default();
            if command.record.kind == SCENE_COMMAND_DRAW_SOURCE_PATH {
                let captured = command
                    .payload
                    .len()
                    .checked_sub(wire::SourceCoverageFrame::WIRE_SIZE)
                    .and_then(|i| wire::SourceCoverageFrame::read(&command.payload[i..]))
                    .ok_or(Error::InvalidInput)?;
                let [x, y] = source_coverage_placement(
                    &captured,
                    &frame.source_state,
                    frame.target,
                    frame.presentation,
                )?;
                source_frame = NativeSourcePathFrame {
                    physical: true,
                    offset_x: x,
                    offset_y: y,
                    dpi_x: captured.dpi_scale_x,
                    dpi_y: captured.dpi_scale_y,
                };
            }
            let (path_start, segment_start, node_start) =
                (paths.len(), segments.len(), nodes.len());
            let raw_nodes = &resource.auxiliary[segment_count * wire::PathSegment::WIRE_SIZE..];
            let mut local_nodes = allocate(node_count)?;
            for index in 0..node_count {
                local_nodes.push(read::<wire::PathBooleanNode>(raw_nodes, index)?);
            }
            for index in 0..segment_count {
                let segment: wire::PathSegment = read(resource.auxiliary, index)?;
                if !values::segment(&segment, true) {
                    return Err(Error::InvalidInput);
                }
                if per_point && segment.kind == PATH_SEGMENT_ARC {
                    return Err(Error::UnsupportedExecution);
                }
                push(&mut segments, segment)?;
            }
            for original in &local_nodes {
                let mut node = *original;
                if matches!(node.kind, PATH_BOOLEAN_LEAF | PATH_BOOLEAN_WINDING_LEAF) {
                    node.segment_offset = node
                        .segment_offset
                        .checked_add(segment_start as u64)
                        .ok_or(Error::Capacity)?;
                }
                push(&mut nodes, node)?;
            }
            let mut per_point_end = 0u64;
            for index in 0..path_count {
                let mut path: wire::PathFill = read(resource.payload, index)?;
                let brush = materials.draw_brush(frame.command_index, count_u32(index)?)?;
                // Keep original preflight arithmetic independent of the subsequent
                // per-point deformation and explicit physical-source substitution.
                let mut admitted = path;
                if let Some((_, brush)) = brush {
                    if !source_frame.physical {
                        execution::apply_path_transform(&mut admitted, &frame.state);
                    }
                    material(&mut admitted, brush);
                } else {
                    execution::apply_path_state(&mut admitted, &frame.state);
                }
                let coverage = values::path(&admitted, segment_count as u64, &local_nodes)
                    .ok_or(Error::InvalidInput)?;
                metrics.coverage_bytes = metrics
                    .coverage_bytes
                    .checked_add(coverage)
                    .filter(|v| *v <= 256 * 1024 * 1024)
                    .ok_or(Error::Capacity)?;
                if per_point {
                    if path.segment_offset < per_point_end {
                        return Err(Error::UnsupportedExecution);
                    }
                    per_point_end = path.segment_offset + path.segment_count;
                }
                if source_frame.physical {
                    path.transform = wire::Affine2d {
                        m31: source_frame.offset_x,
                        m32: source_frame.offset_y,
                        ..identity()
                    };
                    material(&mut path, brush.ok_or(Error::InvalidInput)?.1);
                } else if let Some((_, brush)) = brush {
                    if !per_point {
                        execution::apply_path_transform(&mut path, &frame.state);
                    }
                    material(&mut path, brush);
                } else if !per_point {
                    execution::apply_path_state(&mut path, &frame.state);
                } else {
                    path.color.a *= frame.state.opacity;
                }
                if per_point {
                    let transform =
                        execution::compose_affine(path.transform, frame.source_state.transform);
                    let mut min = [f32::INFINITY; 2];
                    let mut max = [f32::NEG_INFINITY; 2];
                    let mut deform = |point: &mut wire::Point| -> Result<(), Error> {
                        *point = execution::transform_point(transform, *point);
                        cursor.snap_draw_point(&frame.source_state, point)?;
                        *point = execution::localize_point(
                            *point,
                            frame.target,
                            frame.presentation,
                            frame.dpi_scale,
                        );
                        min[0] = min[0].min(point.x);
                        min[1] = min[1].min(point.y);
                        max[0] = max[0].max(point.x);
                        max[1] = max[1].max(point.y);
                        Ok(())
                    };
                    let first = segment_start + path.segment_offset as usize;
                    let end = first + path.segment_count as usize;
                    for segment in &mut segments[first..end] {
                        deform(&mut segment.p0)?;
                        deform(&mut segment.p1)?;
                        if matches!(segment.kind, PATH_SEGMENT_QUADRATIC | PATH_SEGMENT_CUBIC) {
                            deform(&mut segment.p2)?;
                        }
                        if segment.kind == PATH_SEGMENT_CUBIC {
                            deform(&mut segment.p3)?;
                        }
                    }
                    path.min_x = min[0];
                    path.min_y = min[1];
                    path.max_x = if max[0] == min[0] {
                        min[0].next_up()
                    } else {
                        max[0]
                    };
                    path.max_y = if max[1] == min[1] {
                        min[1].next_up()
                    } else {
                        max[1]
                    };
                    path.transform = identity();
                }
                path.segment_offset = path
                    .segment_offset
                    .checked_add(segment_start as u64)
                    .ok_or(Error::Capacity)?;
                if path.boolean_node_count != 0 {
                    path.boolean_node_offset = path
                        .boolean_node_offset
                        .checked_add(node_start as u64)
                        .ok_or(Error::Capacity)?;
                }
                push(&mut paths, path)?;
                push(&mut source_frames, source_frame)?;
                push(&mut brush_indices, brush.map_or(0, |v| v.0))?;
            }
            push(
                &mut draws,
                NativePathDrawRange {
                    command_index: frame.command_index,
                    draw: NativePathDraw {
                        first_index: count_u32(path_start.checked_mul(6).ok_or(Error::Capacity)?)?,
                        index_count: count_u32(path_count * 6)?,
                    },
                },
            )?;
        }
        metrics.draw_count = draws.len() as u64;
        metrics.path_count = paths.len() as u64;
        metrics.segment_count = segments.len() as u64;
        metrics.boolean_node_count = nodes.len() as u64;
        metrics.vertex_bytes = metrics
            .path_count
            .checked_mul(4 * 56)
            .filter(|v| *v <= 256 * 1024 * 1024)
            .ok_or(Error::Capacity)?;
        metrics.index_bytes = metrics
            .path_count
            .checked_mul(6 * 4)
            .filter(|v| *v <= 64 * 1024 * 1024)
            .ok_or(Error::Capacity)?;
        let mut retained = allocate(frames.len())?;
        retained.extend_from_slice(frames);
        Ok(Self {
            scene,
            frames: retained.into(),
            materials: materials.clone(),
            paths: paths.into(),
            segments: segments.into(),
            nodes: nodes.into(),
            source_frames: source_frames.into(),
            brush_indices: brush_indices.into(),
            draws: draws.into(),
            metrics,
        })
    }
}
fn counts(payload: &[u8], auxiliary: &[u8]) -> Result<(usize, usize, usize), Error> {
    if payload.is_empty() || payload.len() % wire::PathFill::WIRE_SIZE != 0 {
        return Err(Error::InvalidInput);
    }
    let count = payload.len() / wire::PathFill::WIRE_SIZE;
    let (mut segments, mut nodes) = (0u64, 0u64);
    for index in 0..count {
        let path: wire::PathFill = read(payload, index)?;
        if path.segment_offset > segments
            || (path.boolean_node_count != 0 && path.boolean_node_offset != nodes)
        {
            return Err(Error::InvalidInput);
        }
        segments = segments.max(
            path.segment_offset
                .checked_add(path.segment_count)
                .ok_or(Error::InvalidInput)?,
        );
        nodes = nodes
            .checked_add(path.boolean_node_count)
            .ok_or(Error::InvalidInput)?;
    }
    let bytes = segments
        .checked_mul(wire::PathSegment::WIRE_SIZE as u64)
        .and_then(|n| {
            nodes
                .checked_mul(wire::PathBooleanNode::WIRE_SIZE as u64)
                .and_then(|m| n.checked_add(m))
        })
        .ok_or(Error::InvalidInput)?;
    if bytes != auxiliary.len() as u64 {
        return Err(Error::InvalidInput);
    }
    Ok((
        count,
        usize::try_from(segments).map_err(|_| Error::Capacity)?,
        usize::try_from(nodes).map_err(|_| Error::Capacity)?,
    ))
}
