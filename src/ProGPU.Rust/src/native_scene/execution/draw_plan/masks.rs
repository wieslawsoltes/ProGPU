use super::*;
pub(super) fn preflight(
    frame: &NativeCommandFrame,
    budgeted: &mut [bool],
    metrics: &mut NativeDrawPlanMetrics,
) -> Result<(), Error> {
    if frame.state.flags & SCENE_STATE_MASK == 0 {
        return Ok(());
    }
    let kind = frame.command().record.kind;
    if matches!(
        kind,
        SCENE_COMMAND_DRAW_LINE_3D_BATCH | SCENE_COMMAND_DRAW_MESH_3D_BATCH
    ) {
        return Err(Error::UnsupportedExecution);
    }
    let index = frame.state.mask_resource_index as usize;
    let resource = frame
        .scene
        .envelope()
        .resource(index as u32)
        .ok_or(Error::InvalidInput)?;
    let mask = resource
        .payload
        .get(4..8)
        .and_then(|b| <[u8; 4]>::try_from(b).ok())
        .map(u32::from_le_bytes)
        .ok_or(Error::InvalidInput)?;
    if !matches!(
        mask,
        SCENE_LAYER_MASK_ROUNDED_RECTANGLE
            | SCENE_LAYER_MASK_COVERAGE_BITMAP
            | SCENE_LAYER_MASK_ANALYTIC_CHAIN
            | SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
            | SCENE_LAYER_MASK_BRUSH
            | SCENE_LAYER_MASK_GEOMETRY
            | SCENE_LAYER_MASK_PICTURE
            | SCENE_LAYER_MASK_COMPOSITE
    ) {
        return Err(Error::UnsupportedExecution);
    }
    if matches!(
        mask,
        SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
            | SCENE_LAYER_MASK_BRUSH
            | SCENE_LAYER_MASK_GEOMETRY
            | SCENE_LAYER_MASK_PICTURE
            | SCENE_LAYER_MASK_COMPOSITE
    ) && !budgeted[index]
    {
        let multiplier = if mask == SCENE_LAYER_MASK_COMPOSITE {
            let c: wire::LayerCompositeMask = read(resource.payload, 0)?;
            u64::from(c.component_count) + 2 + u64::from(c.picture_mask_count) * 4
        } else if mask == SCENE_LAYER_MASK_PICTURE {
            5
        } else {
            1
        };
        let bytes = u64::from(frame.target.width)
            .checked_mul(u64::from(frame.target.height))
            .and_then(|pixels| pixels.checked_mul(multiplier))
            .ok_or(Error::Capacity)?;
        if bytes > u64::from(SCENE_MAX_LAYER_BYTES) - metrics.generated_mask_bytes {
            return Err(Error::Capacity);
        }
        metrics.generated_mask_bytes += bytes;
        budgeted[index] = true;
    }
    metrics.has_state_masks = true;
    let chain = mask == SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    let glyph = matches!(
        kind,
        SCENE_COMMAND_DRAW_GLYPH_RUN | SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN
    );
    if chain {
        if glyph {
            metrics.has_text_mask_chains = true;
        } else if kind == SCENE_COMMAND_DRAW_IMAGE {
            metrics.has_image_mask_chains = true;
        } else if kind != SCENE_COMMAND_DRAW_RGB_GLYPH_RUN {
            metrics.has_vector_mask_chains = true;
        }
    }
    metrics.has_masked_glyphs |= !chain && glyph;
    metrics.has_masked_images |= !chain && kind == SCENE_COMMAND_DRAW_IMAGE;
    Ok(())
}
