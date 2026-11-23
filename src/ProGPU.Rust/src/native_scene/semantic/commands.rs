//! Exact original command reference, suffix and ordered scope admission.
use super::*;
pub(super) fn admit_commands(
    input: &Input<'_>,
    mut payload_bytes: u64,
) -> TransportResult<(u32, u32, u64)> {
    let h = input.header;
    // Duplicate IDs precede every command descriptor/payload check, after all
    // resource checks. Stable radix sorting preserves the original failure site.
    envelope::unique_command_ids(input.bytes, h)?;
    let mut stack = [0u8; MAX_STACK_DEPTH as usize];
    let (mut depth, mut materialized_depth, mut maximum_depth, mut draws) =
        (0usize, 0u32, 0u32, 0u32);
    for index in 0..h.command_count {
        let offset = h.command_offset + index * h.command_stride;
        let c: wire::Command = input.read(offset)?;
        if c.struct_size < wire::Command::WIRE_SIZE as u32
            || c.struct_size > h.command_stride
            || c.flags & !(RECORD_REQUIRED | GLYPH_STYLED) != 0
            || c.reserved != 0
            || c.command_id == 0
            || c.reserved0 != 0
            || c.reserved1 != 0
            || !envelope::arena(c.payload_offset, c.payload_size, &h)
            || ![c.bounds_x, c.bounds_y, c.bounds_width, c.bounds_height]
                .iter()
                .all(|v| v.is_finite())
            || c.bounds_width < 0.0
            || c.bounds_height < 0.0
        {
            return Err(error(E::Record, offset));
        }
        let Ok(kind) = NativeSceneCommandKind::try_from(c.kind) else {
            if c.flags & RECORD_REQUIRED != 0 {
                return Err(error(E::Unsupported, offset));
            }
            continue;
        };
        if c.flags & GLYPH_STYLED != 0 && kind != NativeSceneCommandKind::DrawGlyphRun {
            return Err(error(E::Record, offset));
        }
        if c.state_index != NO_INDEX {
            if matches!(
                kind,
                NativeSceneCommandKind::Restore | NativeSceneCommandKind::PopLayer
            ) {
                return Err(error(E::Record, offset));
            }
            if c.state_index >= h.resource_count {
                return Err(error(E::Range, offset));
            }
            let state_resource = input.resource(c.state_index)?;
            if state_resource.kind != SCENE_RESOURCE_STATE {
                return Err(error(E::Record, offset));
            }
            let state: wire::State = input.read(state_resource.payload_offset)?;
            if state.flags & SCENE_STATE_GUIDELINE_SET != 0 {
                let guideline_resource = input.resource(state.guideline_resource_index)?;
                let guidelines: wire::GuidelineSet =
                    input.read(guideline_resource.payload_offset)?;
                if guidelines.flags & SCENE_GUIDELINE_COMPOSITE_ONLY != 0 {
                    return Err(error(E::Value, offset));
                }
            }
        }
        if kind == NativeSceneCommandKind::ClearTarget {
            if c.resource_index != NO_INDEX
                || c.payload_size != wire::Color::WIRE_SIZE as u32
                || [c.bounds_x, c.bounds_y, c.bounds_width, c.bounds_height] != [0.0; 4]
                || c.flags & !RECORD_REQUIRED != 0
            {
                return Err(error(E::Record, offset));
            }
            let color: wire::Color = input.read(c.payload_offset)?;
            if ![color.r, color.g, color.b, color.a]
                .iter()
                .all(|v| v.is_finite())
                || color.a < 0.0
                || color.a > 1.0
            {
                return Err(error(E::Value, c.payload_offset));
            }
            draws += 1;
            payload_bytes += u64::from(c.payload_size);
            continue;
        }
        if kind.is_draw() {
            if c.resource_index >= h.resource_count {
                return Err(error(E::Range, offset));
            }
            let resource = input.resource(c.resource_index)?;
            if Some(resource.kind) != kind.expected_resource().map(|k| k as u32) {
                return Err(error(E::Record, offset));
            }
            if matches!(
                kind,
                NativeSceneCommandKind::DrawLine3DBatch | NativeSceneCommandKind::DrawMesh3DBatch
            ) {
                camera_and_materials(input, &c, &resource, offset)?;
            }
            let record_size = match kind {
                NativeSceneCommandKind::DrawAnalytic => Some(wire::AnalyticPrimitive::WIRE_SIZE),
                NativeSceneCommandKind::DrawPath | NativeSceneCommandKind::DrawSourcePath => {
                    Some(wire::PathFill::WIRE_SIZE)
                }
                NativeSceneCommandKind::DrawGeometry => Some(wire::GeometryPrimitive::WIRE_SIZE),
                NativeSceneCommandKind::DrawPointBatch => Some(wire::PointBatch::WIRE_SIZE),
                NativeSceneCommandKind::DrawVertexMesh
                | NativeSceneCommandKind::DrawSourceCoverage => Some(wire::VertexMesh::WIRE_SIZE),
                NativeSceneCommandKind::DrawStrokeBatch => Some(wire::Stroke::WIRE_SIZE),
                _ => None,
            };
            if let Some(stride) = record_size {
                if c.payload_size == 0
                    && !matches!(
                        kind,
                        NativeSceneCommandKind::DrawSourcePath
                            | NativeSceneCommandKind::DrawSourceCoverage
                    )
                {
                    draws += 1;
                    payload_bytes += u64::from(c.payload_size);
                    continue;
                }
                if resource.payload_size % stride as u32 != 0
                    || resource.payload_size / stride as u32 > SCENE_MAX_DRAW_BRUSH_INDICES
                {
                    return Err(error(E::Record, resource.payload_offset));
                }
                brush::validate_draw_brushes(input, &c, resource.payload_size / stride as u32)?;
            }
            match kind {
                NativeSceneCommandKind::DrawSourcePath => {
                    source_draw::validate_source_path_draw(input, &c)?
                }
                NativeSceneCommandKind::DrawSourceCoverage => {
                    source_draw::validate_source_coverage_draw(input, &c)?
                }
                NativeSceneCommandKind::DrawRgbGlyphRun => {
                    rgb_glyph::validate_rgb_glyph_draw(input, &c)?
                }
                NativeSceneCommandKind::DrawPaintedGlyphRun => {
                    if resource.flags & COLOR_GLYPH_BITMAPS != 0 {
                        return Err(error(E::Value, c.payload_offset));
                    }
                    text::validate_painted_glyph_draw(input, &c)?;
                }
                NativeSceneCommandKind::DrawGlyphRun if c.flags & GLYPH_STYLED != 0 => {
                    text::validate_styled_glyph_draw(input, &c)?
                }
                _ => {}
            }
            draws += 1;
            payload_bytes += u64::from(c.payload_size);
            continue;
        }
        if c.resource_index != NO_INDEX
            || kind != NativeSceneCommandKind::PushLayer
                && (c.payload_size != 0 || c.payload_offset != 0)
        {
            return Err(error(E::Record, offset));
        }
        let materialized = kind == NativeSceneCommandKind::PushLayer
            && c.payload_size != 0
            && layer(input, &c, offset)?;
        payload_bytes += u64::from(c.payload_size);
        if matches!(
            kind,
            NativeSceneCommandKind::Save | NativeSceneCommandKind::PushLayer
        ) {
            if depth == stack.len() || materialized && materialized_depth == MAX_MATERIALIZED_LAYERS
            {
                return Err(error(E::Stack, offset));
            }
            stack[depth] = if kind == NativeSceneCommandKind::Save {
                1
            } else if materialized {
                3
            } else {
                2
            };
            depth += 1;
            materialized_depth += u32::from(materialized);
            maximum_depth = maximum_depth.max(depth as u32);
        } else {
            let actual = if depth == 0 { 0 } else { stack[depth - 1] };
            if depth == 0
                || if kind == NativeSceneCommandKind::PopLayer {
                    actual != 2 && actual != 3
                } else {
                    actual != 1
                }
            {
                return Err(error(E::Stack, offset));
            }
            depth -= 1;
            materialized_depth -= u32::from(actual == 3);
        }
    }
    if depth != 0 {
        return Err(error(
            E::Stack,
            h.command_offset + h.command_count * h.command_stride,
        ));
    }
    Ok((draws, maximum_depth, payload_bytes))
}
fn camera_and_materials(
    input: &Input<'_>,
    c: &wire::Command,
    r: &wire::Resource,
    offset: u32,
) -> TransportResult<()> {
    let size = wire::Camera3d::WIRE_SIZE as u32;
    let has_materials = c.kind == SCENE_COMMAND_DRAW_MESH_3D_BATCH && c.payload_size > size;
    if c.payload_size < size || !has_materials && c.payload_size != size {
        return Err(error(E::Record, offset));
    }
    if !values::camera_3d(&input.read(c.payload_offset)?) {
        return Err(error(E::Value, c.payload_offset));
    }
    if !has_materials {
        return Ok(());
    }
    let at = c.payload_offset + size;
    if c.payload_size < size + wire::Mesh3dMaterials::WIRE_SIZE as u32 {
        return Err(error(E::Record, offset));
    }
    let m: wire::Mesh3dMaterials = input.read(at)?;
    if m.struct_size != wire::Mesh3dMaterials::WIRE_SIZE as u32
        || m.reserved0 != 0
        || m.brush_count != r.payload_size / wire::Mesh3d::WIRE_SIZE as u32
        || u64::from(size) + wire::Mesh3dMaterials::WIRE_SIZE as u64 + u64::from(m.brush_count) * 4
            != u64::from(c.payload_size)
        || m.brush_resource_index >= input.header.resource_count
    {
        return Err(error(E::Value, at));
    }
    let brushes = input.resource(m.brush_resource_index)?;
    if brushes.kind != SCENE_RESOURCE_BRUSH_TABLE
        || brushes.payload_size == 0
        || brushes.payload_size % wire::Brush::WIRE_SIZE as u32 != 0
    {
        return Err(error(E::Record, at));
    }
    let count = brushes.payload_size / wire::Brush::WIRE_SIZE as u32;
    let indices = at + wire::Mesh3dMaterials::WIRE_SIZE as u32;
    for i in 0..m.brush_count {
        let slot = indices + i * 4;
        let selected: u32 = input.read(slot)?;
        if selected >= count {
            return Err(error(E::Range, slot));
        }
        let address = brushes.payload_offset + selected * wire::Brush::WIRE_SIZE as u32;
        let brush: wire::Brush = input.read(address)?;
        if !matches!(
            brush.kind,
            SCENE_BRUSH_SOLID | SCENE_BRUSH_LINEAR_GRADIENT | SCENE_BRUSH_RADIAL_GRADIENT
        ) {
            return Err(error(E::Value, address));
        }
    }
    Ok(())
}
fn layer(input: &Input<'_>, c: &wire::Command, offset: u32) -> TransportResult<bool> {
    if c.payload_size != wire::Layer::WIRE_SIZE as u32 {
        return Err(error(E::Value, offset));
    }
    let l: wire::Layer = input.read(c.payload_offset)?;
    if !values::layer(&l) {
        return Err(error(E::Value, offset));
    }
    let valid_resource = |index: u32, kind: u32| -> bool {
        index == NO_INDEX
            || index < input.header.resource_count
                && input.resource(index).is_ok_and(|r| r.kind == kind)
    };
    if !valid_resource(l.mask_resource_index, SCENE_RESOURCE_LAYER_MASK)
        || !valid_resource(l.effect_resource_index, SCENE_RESOURCE_EFFECT_CHAIN)
            && !valid_resource(l.effect_resource_index, SCENE_RESOURCE_WPF_SHADER_EFFECT)
        || l.flags & (SCENE_LAYER_CACHE_LOCAL_SPACE | SCENE_LAYER_COMPOSITE_STATE) != 0
            && (l.reserved0 == NO_INDEX || !valid_resource(l.reserved0, SCENE_RESOURCE_STATE))
    {
        return Err(error(E::Record, offset));
    }
    if l.effect_resource_index != NO_INDEX
        && valid_resource(l.effect_resource_index, SCENE_RESOURCE_WPF_SHADER_EFFECT)
        && (l.flags & SCENE_LAYER_BOUNDS == 0
            || l.flags & (SCENE_LAYER_BACKDROP | SCENE_LAYER_CACHE_CONTENT) != 0)
    {
        return Err(error(E::Value, offset));
    }
    let local = l.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0;
    let tile = l.flags & SCENE_LAYER_CACHE_TILE != 0;
    if tile
        && (l.reserved1 == NO_INDEX || !valid_resource(l.reserved1, SCENE_RESOURCE_TILE_COMPOSITE))
    {
        return Err(error(E::Record, offset));
    }
    if local || l.flags & SCENE_LAYER_COMPOSITE_STATE != 0 {
        let resource = input.resource(l.reserved0)?;
        let state: wire::State = input.read(resource.payload_offset)?;
        let flags = if local {
            SCENE_STATE_CLIP_RECT | SCENE_STATE_GUIDELINE_SET
        } else {
            SCENE_STATE_CLIP_RECT
        };
        let t = state.transform;
        let canonical = local && !tile
            || [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32] == [1.0, 0.0, 0.0, 1.0, 0.0, 0.0];
        let mut valid_guidelines = true;
        if state.flags & SCENE_STATE_GUIDELINE_SET != 0 {
            let r = input.resource(state.guideline_resource_index)?;
            let g: wire::GuidelineSet = input.read(r.payload_offset)?;
            valid_guidelines = g.flags & SCENE_GUIDELINE_PER_POINT == 0;
        }
        if state.flags & !flags != 0
            || tile && state.flags & !SCENE_STATE_CLIP_RECT != 0
            || !canonical
            || !valid_guidelines
            || state.opacity != 1.0
            || state.mask_resource_index != 0
        {
            return Err(error(E::Value, offset));
        }
    }
    Ok(l.flags
        & (SCENE_LAYER_BACKDROP
            | SCENE_LAYER_FORCE_ISOLATION
            | SCENE_LAYER_CACHE_CONTENT
            | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | SCENE_LAYER_IGNORE_ALPHA)
        != 0
        || l.opacity != 1.0
        || l.blend_mode != BLEND_SRC_OVER
        || l.mask_resource_index != NO_INDEX
        || l.effect_resource_index != NO_INDEX)
}
