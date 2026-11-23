//! Original per-resource order: descriptor, payload and dependency gates stay interleaved.
use super::*;
pub(super) fn admit_resources(input: &Input<'_>, depth: u32) -> TransportResult<u64> {
    let h = input.header;
    let mut previous_id = 0;
    let mut payload_bytes = 0;
    let mut brushes = 0u64;
    let mut stops = 0u64;
    let mut styles = 0u64;
    for index in 0..h.resource_count {
        let offset = h.resource_offset + index * h.resource_stride;
        let r = input.resource(index)?;
        if r.struct_size < wire::Resource::WIRE_SIZE as u32
            || r.struct_size > h.resource_stride
            || r.flags
                & !(RECORD_REQUIRED
                    | COLOR_GLYPH_BITMAPS
                    | EXTERNAL_IMAGE
                    | IMAGE_BGRA8
                    | IMAGE_R8
                    | IMAGE_PICTURE)
                != 0
            || r.reserved != 0
            || r.resource_id == 0
            || !envelope::arena(r.payload_offset, r.payload_size, &h)
            || !envelope::arena(r.auxiliary_offset, r.auxiliary_size, &h)
        {
            return Err(error(E::Record, offset));
        }
        if r.flags & COLOR_GLYPH_BITMAPS != 0 && r.kind != SCENE_RESOURCE_GLYPH_RUN {
            return Err(error(E::Record, offset));
        }
        let external = r.flags & EXTERNAL_IMAGE != 0;
        let bgra = r.flags & IMAGE_BGRA8 != 0;
        let r8 = r.flags & IMAGE_R8 != 0;
        if r.flags & IMAGE_PICTURE != 0 {
            if external
                || bgra
                || r8
                || r.kind != SCENE_RESOURCE_IMAGE
                || r.auxiliary_size < wire::Header::WIRE_SIZE as u32
                || values::read_picture_image(input.span(r.payload_offset, r.payload_size)?)
                    .is_none()
                || admit(input.span(r.auxiliary_offset, r.auxiliary_size)?, depth).is_err()
            {
                return Err(error(E::Value, offset));
            }
        }
        if external
            && (r.kind != SCENE_RESOURCE_IMAGE
                || r.payload_size != 0
                || r.auxiliary_size != 0
                || bgra
                || r8)
            || bgra && r8
            || (bgra || r8) && r.kind != SCENE_RESOURCE_IMAGE
        {
            return Err(error(E::Record, offset));
        }
        if r.resource_id <= previous_id {
            return Err(error(E::Id, offset));
        }
        if r.generation == 0 {
            return Err(error(E::Generation, offset));
        }
        let known = NativeSceneResourceKind::try_from(r.kind).is_ok();
        if !known && r.flags & RECORD_REQUIRED != 0 {
            return Err(error(E::Unsupported, offset));
        }
        if known && r.payload_size == 0 && !external {
            return Err(error(E::Record, offset));
        }
        if r.kind == SCENE_RESOURCE_STATE {
            if r.payload_size != wire::State::WIRE_SIZE as u32 || r.auxiliary_size != 0 {
                return Err(error(E::Record, offset));
            }
            let state: wire::State = input.read(r.payload_offset)?;
            if !valid_state(&state) {
                return Err(error(E::Value, r.payload_offset));
            }
            for (flag, dependency, kind) in [
                (
                    SCENE_STATE_MASK,
                    state.mask_resource_index,
                    SCENE_RESOURCE_LAYER_MASK,
                ),
                (
                    SCENE_STATE_GUIDELINE_SET,
                    state.guideline_resource_index,
                    SCENE_RESOURCE_GUIDELINE_SET,
                ),
            ] {
                if state.flags & flag != 0
                    && (dependency >= index || input.resource(dependency)?.kind != kind)
                {
                    return Err(error(E::Record, r.payload_offset));
                }
            }
        }
        if r.kind == SCENE_RESOURCE_TILE_COMPOSITE
            && (r.payload_size != wire::TileComposite::WIRE_SIZE as u32
                || r.auxiliary_size != 0
                || !values::tile_composite(&input.read(r.payload_offset)?))
        {
            return Err(error(E::Value, offset));
        }
        if r.kind == SCENE_RESOURCE_GUIDELINE_SET {
            guidelines(input, &r, offset)?;
        }
        if r.kind == SCENE_RESOURCE_GLYPH_RUN && r.flags & COLOR_GLYPH_BITMAPS != 0 {
            color_glyph::validate_color_glyph_resource(input, &r)?;
        }
        if r.kind == SCENE_RESOURCE_LAYER_MASK {
            let mask = layer_mask::validate_layer_mask_resource(input, &r)?;
            for (start, size) in mask.nested_streams {
                if admit(input.span(start, size)?, depth).is_err() {
                    return Err(error(E::Value, r.auxiliary_offset));
                }
            }
        }
        if r.kind == SCENE_RESOURCE_WPF_SHADER_EFFECT {
            let program = shader_effect::validate_shader_effect_resource(input, &r).map_err(
                |mut failure| {
                    failure.offset = offset;
                    failure
                },
            )?;
            for dependency in [program.sampler, program.input] {
                if dependency == NO_INDEX {
                    continue;
                }
                if dependency >= index {
                    return Err(error(E::Record, offset));
                }
                let source = input.resource(dependency)?;
                if source.kind != SCENE_RESOURCE_IMAGE || source.flags & IMAGE_PICTURE == 0 {
                    return Err(error(E::Record, offset));
                }
                if program.input != NO_INDEX {
                    let Some((picture, presentation)) = values::read_picture_image(
                        input.span(source.payload_offset, source.payload_size)?,
                    ) else {
                        return Err(error(E::Value, offset));
                    };
                    if dependency == program.input
                        && [picture.width, picture.height] != program.capture_size
                        || presentation.dpi_scale_x != 1.0
                        || presentation.dpi_scale_y != 1.0
                        || presentation.viewport_x != 0
                        || presentation.viewport_y != 0
                    {
                        return Err(error(E::Value, offset));
                    }
                }
            }
        }
        if r.kind == SCENE_RESOURCE_EFFECT_CHAIN {
            if r.payload_size != wire::EffectChain::WIRE_SIZE as u32 || r.auxiliary_size == 0 {
                return Err(error(E::Record, offset));
            }
            let chain: wire::EffectChain = input.read(r.payload_offset)?;
            if chain.struct_size != wire::EffectChain::WIRE_SIZE as u32
                || chain.effect_count == 0
                || chain.effect_count > MAX_GROUP_EFFECTS
                || chain.revision == 0
                || chain.reserved != 0
                || u64::from(r.auxiliary_size)
                    != u64::from(chain.effect_count) * wire::GroupEffect::WIRE_SIZE as u64
            {
                return Err(error(E::Value, r.payload_offset));
            }
            for i in 0..chain.effect_count {
                let at = r.auxiliary_offset + i * wire::GroupEffect::WIRE_SIZE as u32;
                if !values::effect(&input.read(at)?) {
                    return Err(error(E::Value, at));
                }
            }
        }
        if r.kind == SCENE_RESOURCE_BRUSH_TABLE {
            brush::validate_brush_table(input, &r)?;
            brushes += u64::from(r.payload_size) / wire::Brush::WIRE_SIZE as u64;
            stops += u64::from(r.auxiliary_size) / wire::GradientStop::WIRE_SIZE as u64;
            if brushes > u64::from(SCENE_MAX_BRUSHES) || stops > u64::from(SCENE_MAX_GRADIENT_STOPS)
            {
                return Err(oom(E::Range, offset));
            }
        }
        if r.kind == SCENE_RESOURCE_TEXT_STYLE_TABLE {
            text::validate_text_style_table(input, &r)?;
            styles += u64::from(r.payload_size) / wire::TextStyle::WIRE_SIZE as u64;
            if styles > u64::from(SCENE_MAX_TEXT_STYLES) {
                return Err(oom(E::Range, offset));
            }
        }
        if r.kind == SCENE_RESOURCE_HIT_TEST_INDEX {
            hit::validate_hit_test_resource(input, &r)?;
        }
        if r.kind == SCENE_RESOURCE_LINE_3D_BATCH {
            if r.payload_size == 0
                || r.payload_size % wire::Line3d::WIRE_SIZE as u32 != 0
                || r.auxiliary_size != 0
            {
                return Err(error(E::Record, offset));
            }
            for i in 0..r.payload_size / wire::Line3d::WIRE_SIZE as u32 {
                let at = r.payload_offset + i * wire::Line3d::WIRE_SIZE as u32;
                if !values::line_3d(&input.read(at)?) {
                    return Err(error(E::Value, at));
                }
            }
        }
        if r.kind == SCENE_RESOURCE_MESH_3D_BATCH {
            mesh_3d(input, &r, offset)?;
        }
        previous_id = r.resource_id;
        payload_bytes += u64::from(r.payload_size) + u64::from(r.auxiliary_size);
    }
    Ok(payload_bytes)
}
fn valid_state(s: &wire::State) -> bool {
    let known = SCENE_STATE_CLIP_RECT
        | SCENE_STATE_MASK
        | SCENE_STATE_GUIDELINE_SET
        | SCENE_STATE_CLIP_PIXEL_CENTERS;
    let t = s.transform;
    let c = s.clip_rect;
    s.struct_size == wire::State::WIRE_SIZE as u32
        && s.flags & !known == 0
        && (s.flags & SCENE_STATE_CLIP_PIXEL_CENTERS == 0 || s.flags & SCENE_STATE_CLIP_RECT != 0)
        && s.reserved == 0
        && [
            t.m11, t.m12, t.m21, t.m22, t.m31, t.m32, s.opacity, c.x, c.y, c.width, c.height,
        ]
        .iter()
        .all(|v| v.is_finite())
        && (0.0..=1.0).contains(&s.opacity)
        && c.width >= 0.0
        && c.height >= 0.0
        && (s.flags & SCENE_STATE_CLIP_RECT != 0 || [c.x, c.y, c.width, c.height] == [0.0; 4])
        && (s.flags & SCENE_STATE_MASK != 0 || s.mask_resource_index == 0)
        && (s.flags & SCENE_STATE_GUIDELINE_SET != 0 || s.guideline_resource_index == 0)
}
fn guidelines(input: &Input<'_>, r: &wire::Resource, offset: u32) -> TransportResult<()> {
    if r.payload_size < wire::GuidelineSet::WIRE_SIZE as u32 || r.auxiliary_size != 0 {
        return Err(error(E::Record, offset));
    }
    let g: wire::GuidelineSet = input.read(r.payload_offset)?;
    let count = u64::from(g.guideline_x_count) + u64::from(g.guideline_y_count);
    let explicit = g.flags & SCENE_GUIDELINE_EXPLICIT_OFFSETS != 0;
    let multiple = g.guideline_x_count > 1 || g.guideline_y_count > 1;
    let multi = g.flags & (SCENE_GUIDELINE_COMPOSITE_ONLY | SCENE_GUIDELINE_PER_POINT);
    if g.struct_size != wire::GuidelineSet::WIRE_SIZE as u32
        || g.flags
            & !(SCENE_GUIDELINE_COMPOSITE_ONLY
                | SCENE_GUIDELINE_PER_POINT
                | SCENE_GUIDELINE_EXPLICIT_OFFSETS)
            != 0
        || multiple != (multi != 0)
        || multi & multi.wrapping_sub(1) != 0
        || g.guideline_x_count > SCENE_MAX_GUIDELINES_PER_AXIS
        || g.guideline_y_count > SCENE_MAX_GUIDELINES_PER_AXIS
        || wire::GuidelineSet::WIRE_SIZE as u64 + count * 8 * if explicit { 2 } else { 1 }
            != u64::from(r.payload_size)
    {
        return Err(error(E::Value, r.payload_offset));
    }
    let start = r.payload_offset + wire::GuidelineSet::WIRE_SIZE as u32;
    let mut i = 0;
    for length in [g.guideline_x_count, g.guideline_y_count] {
        let mut previous = f64::NEG_INFINITY;
        for _ in 0..length {
            let at = start + i * 8;
            let value: f64 = input.read(at)?;
            if value.is_nan() || explicit && !value.is_finite() || value < previous {
                return Err(error(E::Value, at));
            }
            previous = value;
            i += 1;
        }
    }
    if explicit {
        for i in 0..count as u32 {
            let at = start + (count as u32 + i) * 8;
            let value: f64 = input.read(at)?;
            if !value.is_finite() || value.abs() > 1.0 {
                return Err(error(E::Value, at));
            }
        }
    }
    Ok(())
}
fn mesh_3d(input: &Input<'_>, r: &wire::Resource, offset: u32) -> TransportResult<()> {
    let stride = wire::Mesh3d::WIRE_SIZE as u32;
    if r.payload_size == 0 || r.payload_size % stride != 0 || r.auxiliary_size == 0 {
        return Err(error(E::Record, offset));
    }
    let count = r.payload_size / stride;
    let (mut vertices, mut indices, mut lights) = (0u64, 0u64, 0u64);
    for i in 0..count {
        let m: wire::Mesh3d = input.read(r.payload_offset + i * stride)?;
        vertices = vertices.max(u64::from(m.vertex_offset) + u64::from(m.vertex_count));
        indices = indices.max(u64::from(m.index_offset) + u64::from(m.index_count));
        lights = lights.max(u64::from(m.light_offset) + u64::from(m.light_count));
    }
    let vertex_bytes = vertices * wire::Mesh3dVertex::WIRE_SIZE as u64;
    let index_bytes = indices * 4;
    let light_bytes = lights * wire::Light3d::WIRE_SIZE as u64;
    if vertex_bytes + index_bytes + light_bytes != u64::from(r.auxiliary_size) {
        return Err(error(E::Record, r.auxiliary_offset));
    }
    for i in 0..vertices as u32 {
        let at = r.auxiliary_offset + i * wire::Mesh3dVertex::WIRE_SIZE as u32;
        if !values::mesh_3d_vertex(&input.read(at)?) {
            return Err(error(E::Value, at));
        }
    }
    let indices_at = r.auxiliary_offset + vertex_bytes as u32;
    let lights_at = indices_at + index_bytes as u32;
    for i in 0..lights as u32 {
        let at = lights_at + i * wire::Light3d::WIRE_SIZE as u32;
        if !values::light_3d(&input.read(at)?) {
            return Err(error(E::Value, at));
        }
    }
    for i in 0..count {
        let at = r.payload_offset + i * stride;
        let m: wire::Mesh3d = input.read(at)?;
        if !values::mesh_3d(&m, vertices as usize, indices as usize, lights as usize) {
            return Err(error(E::Value, at));
        }
        if m.topology == MESH_3D_EDGE_LIST {
            for j in (m.vertex_offset..m.vertex_offset + m.vertex_count).step_by(2) {
                let first = r.auxiliary_offset + j * wire::Mesh3dVertex::WIRE_SIZE as u32;
                if !values::mesh_3d_edge_pair(
                    &input.read(first)?,
                    &input.read(first + wire::Mesh3dVertex::WIRE_SIZE as u32)?,
                ) {
                    return Err(error(E::Value, first));
                }
            }
        }
        if m.flags & MESH_3D_MATERIAL_IMAGE != 0 {
            if m.material_image_resource_index >= input.header.resource_count {
                return Err(error(E::Range, at));
            }
            let image = input.resource(m.material_image_resource_index)?;
            if image.kind != SCENE_RESOURCE_IMAGE || image.flags & EXTERNAL_IMAGE == 0 {
                return Err(error(E::Record, at));
            }
        }
        for j in m.index_offset..m.index_offset + m.index_count {
            let index_at = indices_at + j * 4;
            let value: u32 = input.read(index_at)?;
            if value >= m.vertex_count {
                return Err(error(E::Value, index_at));
            }
        }
    }
    Ok(())
}
