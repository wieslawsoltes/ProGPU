use super::*;
pub(super) fn preflight(
    frame: &NativeCommandFrame,
    external: &[NativeExternalImageInfo],
    budgeted: &mut [bool],
    cost: &mut NativeDrawCommandCost,
    metrics: &mut NativeDrawPlanMetrics,
) -> Result<(), Error> {
    let command = frame.command();
    let resource = frame
        .scene
        .envelope()
        .resource(command.record.resource_index)
        .ok_or(Error::InvalidInput)?;
    let color = resource.record.flags & SCENE_COLOR_GLYPH_BITMAPS != 0;
    let (glyph_bytes, glyph_count) = native_glyph_payload(command).ok_or(Error::InvalidInput)?;
    let outline_size = if color {
        wire::ColorGlyphBitmap::WIRE_SIZE
    } else {
        wire::GlyphOutline::WIRE_SIZE
    };
    if resource.payload.is_empty()
        || resource.payload.len() % outline_size != 0
        || !color
            && (resource.auxiliary.is_empty()
                || resource.auxiliary.len() % wire::PathSegment::WIRE_SIZE != 0)
    {
        return Err(Error::InvalidInput);
    }
    let outline_count = resource.payload.len() / outline_size;
    let segment_count = if color {
        0
    } else {
        resource.auxiliary.len() / wire::PathSegment::WIRE_SIZE
    };
    if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN {
        let t = frame.state.transform;
        if t.m11 != 1.0 || t.m12 != 0.0 || t.m21 != 0.0 || t.m22 != 1.0 {
            return Err(Error::UnsupportedExecution);
        }
        if color {
            return Err(Error::InvalidInput);
        }
        text::validate_painted_glyph_draw(&input(&frame.scene), &command.record)
            .map_err(|_| Error::InvalidInput)?;
        let draw: wire::PaintedGlyphDraw = read(command.payload, 0)?;
        let paint = wire::GlyphPaint::read(&command.payload[wire::PaintedGlyphDraw::WIRE_SIZE..])
            .ok_or(Error::InvalidInput)?;
        if paint.kind == SCENE_GLYPH_PAINT_TEXTURE {
            let image = frame
                .scene
                .envelope()
                .resource(draw.paint_resource_index)
                .ok_or(Error::InvalidInput)?;
            let slot = engine_resources::external_slot(
                external,
                image.record.resource_id,
                image.record.generation,
                NativeImageRole::Primary,
            )
            .ok_or(Error::InvalidInput)?;
            if external[slot].size.into_iter().any(|v| v == 0 || v > 16384) {
                return Err(Error::InvalidInput);
            }
        }
        metrics.glyph_paint_count += 1;
    }
    let first = !budgeted[command.record.resource_index as usize];
    if outline_count > 1 << 20 || segment_count > 1 << 24 || glyph_count > 1 << 24 {
        return Err(Error::InvalidInput);
    }
    cost.vertex_bytes = u64::from(glyph_count) * 96
        + if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN {
            wire::GlyphPaint::WIRE_SIZE as u64
        } else {
            0
        };
    if color && first {
        cost.texture_bytes = resource.auxiliary.len() as u64;
    }
    if first {
        for i in 0..segment_count {
            let s: wire::PathSegment = read(resource.auxiliary, i)?;
            if !glyph_scan::segment(&s) {
                return Err(Error::InvalidInput);
            }
            metrics.has_binary_glyphs |= s.pad0 != 0;
            metrics.has_vector_glyphs |= s.pad0 == 0;
        }
        if !color {
            for i in 0..outline_count {
                let outline: wire::GlyphOutline = read(resource.payload, i)?;
                let bytes = values::glyph_outline(&outline, segment_count as u64)
                    .ok_or(Error::InvalidInput)?;
                if !glyph_scan::outline_with(&outline, outline.segment_count as usize, |index| {
                    let offset =
                        (outline.segment_offset as usize + index) * wire::PathSegment::WIRE_SIZE;
                    wire::PathSegment::read(&resource.auxiliary[offset..])
                        .expect("complete admitted source segment range")
                }) {
                    return Err(Error::InvalidInput);
                }
                if bytes > MAX_COVERAGE_BYTES - cost.coverage_bytes {
                    return Err(Error::Capacity);
                }
                cost.coverage_bytes += bytes;
            }
        }
    }
    for i in 0..glyph_count as usize {
        let mut glyph: wire::PositionedGlyph = read(glyph_bytes, i)?;
        if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN
            || command.record.flags & SCENE_GLYPH_STYLED != 0
        {
            apply_positioned_glyph_transform(&mut glyph, &frame.state);
        } else {
            apply_positioned_glyph_state(&mut glyph, &frame.state);
        }
        if !values::positioned_glyph(&glyph, outline_count as u64) {
            return Err(Error::InvalidInput);
        }
    }
    if first {
        add(&mut metrics.glyph_coverage_bytes, cost.coverage_bytes)?;
        budgeted[command.record.resource_index as usize] = true;
        if color {
            add(&mut metrics.color_glyph_bitmap_count, outline_count as u64)?;
            add(
                &mut metrics.color_glyph_pixel_bytes,
                resource.auxiliary.len() as u64,
            )?;
        } else {
            add(&mut metrics.glyph_outline_count, outline_count as u64)?;
            add(&mut metrics.glyph_segment_count, segment_count as u64)?;
        }
    }
    metrics.glyph_draw_count += 1;
    add(&mut metrics.glyph_count, u64::from(glyph_count))?;
    metrics.has_styled_glyphs |= command.record.flags & SCENE_GLYPH_STYLED != 0;
    Ok(())
}
