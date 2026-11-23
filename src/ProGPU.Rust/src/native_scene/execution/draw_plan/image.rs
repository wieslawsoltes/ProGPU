use super::*;
pub(super) fn preflight(
    frame: &NativeCommandFrame,
    external: &[NativeExternalImageInfo],
    cost: &mut NativeDrawCommandCost,
    metrics: &mut NativeDrawPlanMetrics,
) -> Result<(), Error> {
    let command = frame.command();
    let resource = frame
        .scene
        .envelope()
        .resource(command.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    let mut image: wire::ImageDraw = read(command.payload, 0)?;
    apply_image_state(&mut image, &frame.state);
    let external_image = resource.record.flags & SCENE_EXTERNAL_IMAGE != 0;
    let picture_image = resource.record.flags & SCENE_IMAGE_PICTURE != 0;
    let picture = if picture_image {
        Some(
            values::read_picture_image(resource.payload)
                .ok_or(Error::InvalidInput)?
                .0,
        )
    } else {
        None
    };
    let bytes = if external_image || picture_image {
        (u64::from(image.row_bytes) * u64::from(image.image_height.wrapping_sub(1)))
            .wrapping_add(u64::from(image.image_width) * 4)
    } else {
        resource.payload.len() as u64
    };
    if !picture_image && !resource.auxiliary.is_empty() {
        return Err(Error::InvalidInput);
    }
    if let Some(p) = picture {
        if p.width != image.image_width
            || p.height != image.image_height
            || image.row_bytes != p.width * 4
            || image.flags & SCENE_IMAGE_SOURCE_PREMULTIPLIED == 0
        {
            return Err(Error::InvalidInput);
        }
    }
    let bpp = if resource.record.flags & SCENE_IMAGE_R8 != 0 {
        1
    } else {
        4
    };
    let options = image_payload::read_image_payload(command.payload, &image, bytes, bpp)
        .ok_or(Error::InvalidInput)?;
    let binding = |role| {
        if external_image {
            engine_resources::external_slot(
                external,
                resource.record.resource_id,
                resource.record.generation,
                role,
            )
        } else {
            None
        }
    };
    if external_image
        && binding(NativeImageRole::Primary)
            .is_none_or(|i| external[i].size != [image.image_width, image.image_height])
    {
        return Err(Error::InvalidInput);
    }
    if let Some(effect) = options.effect {
        if effect.flags0[0] > 0.5 && binding(NativeImageRole::Chroma).is_none()
            || effect.flags0[1] > 0.5 && binding(NativeImageRole::Mask).is_none()
        {
            return Err(Error::InvalidInput);
        }
    }
    metrics.has_image_color_matrices |= options.color_matrix.is_some();
    let vertex_count = if options.patch_count == 0 {
        4
    } else {
        u64::from(options.patch_count) * 6
    };
    cost.vertex_bytes = vertex_count * 56;
    cost.index_bytes = if options.patch_count == 0 { 24 } else { 0 };
    cost.texture_bytes = if external_image {
        0
    } else if let Some(p) = picture {
        u64::from(p.width) * u64::from(p.height) * 4
    } else {
        resource.payload.len() as u64
    };
    metrics.image_draw_count += 1;
    add(&mut metrics.image_vertex_count, vertex_count)?;
    Ok(())
}
