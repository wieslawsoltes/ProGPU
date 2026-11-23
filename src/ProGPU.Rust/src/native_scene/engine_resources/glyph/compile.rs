//! Native page preflight and original resource-byte sharing, before publication.
use super::*;
use std::collections::HashMap;
#[derive(Clone, Copy, Default)]
struct Layout {
    compiled: bool,
    color: bool,
    outline: usize,
    segment: usize,
    bitmap: usize,
    pixel: usize,
}
impl NativeGlyphPage {
    pub fn compile(
        scene: Arc<NativeScene>,
        frames: &[NativeCommandFrame],
        materials: &NativeBrushPage,
        styles: &NativeTextStylePage,
        external: &[NativeExternalImageInfo],
        engine_flags: u32,
    ) -> Result<Self, Error> {
        if !Arc::ptr_eq(&scene, materials.scene())
            || !Arc::ptr_eq(&scene, styles.scene())
            || !external_info_valid(external)
        {
            return Err(Error::InvalidInput);
        }
        let (
            mut outlines,
            mut segments,
            mut glyphs,
            mut style_indices,
            mut paint_indices,
            mut rendering_modes,
        ) = (
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        );
        let (
            mut paints,
            mut paint_resources,
            mut color_bitmaps,
            mut color_pixels,
            mut color_bitmap_indices,
            mut draws,
        ) = (
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        );
        let mut metrics = NativeGlyphPageMetrics::default();
        let mut layouts = allocate(scene.envelope().header().resource_count as usize)?;
        layouts.resize(
            scene.envelope().header().resource_count as usize,
            Layout::default(),
        );
        let mut shared: HashMap<(u32, &[u8], &[u8]), u32> = HashMap::new();
        shared
            .try_reserve(frames.len().min(layouts.len()))
            .map_err(|_| Error::Capacity)?;
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
                SCENE_COMMAND_DRAW_GLYPH_RUN | SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN
            ) {
                return Err(Error::InvalidInput);
            }
            let resource = scene
                .envelope()
                .resource(command.record.resource_index)
                .ok_or(Error::InvalidInput)?;
            if resource.record.kind != SCENE_RESOURCE_GLYPH_RUN {
                return Err(Error::InvalidInput);
            }
            let color = resource.record.flags & SCENE_COLOR_GLYPH_BITMAPS != 0;
            let (glyph_bytes, glyph_count) =
                native_glyph_payload(command).ok_or(Error::InvalidInput)?;
            let outline_size = if color {
                wire::ColorGlyphBitmap::WIRE_SIZE
            } else {
                wire::GlyphOutline::WIRE_SIZE
            };
            if resource.payload.is_empty()
                || resource.payload.len() % outline_size != 0
                || (!color
                    && (resource.auxiliary.is_empty()
                        || resource.auxiliary.len() % wire::PathSegment::WIRE_SIZE != 0))
            {
                return Err(Error::InvalidInput);
            }
            let outline_count = resource.payload.len() / outline_size;
            let segment_count = if color {
                0
            } else {
                resource.auxiliary.len() / wire::PathSegment::WIRE_SIZE
            };
            if outline_count > 1 << 20 || segment_count > 1 << 24 || glyph_count > 1 << 24 {
                return Err(Error::InvalidInput);
            }
            let slot = command.record.resource_index as usize;
            let first = !layouts[slot].compiled;
            if first {
                if color {
                    add(&mut metrics.color_bitmap_count, outline_count as u64)?;
                    add(
                        &mut metrics.color_pixel_bytes,
                        resource.auxiliary.len() as u64,
                    )?;
                    for i in 0..outline_count {
                        if !values::color_glyph_bitmap(
                            &read(resource.payload, i)?,
                            resource.auxiliary.len(),
                        ) {
                            return Err(Error::InvalidInput);
                        }
                    }
                } else {
                    add(&mut metrics.source_outline_count, outline_count as u64)?;
                    add(&mut metrics.source_segment_count, segment_count as u64)?;
                    let raw_segments = read_all::<wire::PathSegment>(resource.auxiliary)?;
                    if !raw_segments.iter().all(glyph_scan::segment) {
                        return Err(Error::InvalidInput);
                    }
                    for i in 0..outline_count {
                        let outline: wire::GlyphOutline = read(resource.payload, i)?;
                        let bytes = values::glyph_outline(&outline, segment_count as u64)
                            .ok_or(Error::InvalidInput)?;
                        let selected = &raw_segments[outline.segment_offset as usize
                            ..(outline.segment_offset + outline.segment_count) as usize];
                        if !glyph_scan::outline(&outline, selected) {
                            return Err(Error::InvalidInput);
                        }
                        if selected[0].pad0 != 0
                            && engine_flags
                                & (ENGINE_GLYPH_RASTER_SHADER_FALLBACK
                                    | ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK
                                    | ENGINE_GLYPH_SCALAR_CPU_FALLBACK)
                                != 0
                        {
                            return Err(Error::UnsupportedExecution);
                        }
                        add(&mut metrics.coverage_bytes, bytes)?;
                        if metrics.coverage_bytes > 256 * 1024 * 1024 {
                            return Err(Error::Capacity);
                        }
                    }
                }
            }
            let mut layout = layouts[slot];
            if !layout.compiled && !color {
                let identity = (resource.record.flags, resource.payload, resource.auxiliary);
                if let Some(&existing) = shared.get(&identity) {
                    layout = layouts[existing as usize];
                    add(&mut metrics.shared_outline_count, outline_count as u64)?;
                    add(&mut metrics.shared_segment_count, segment_count as u64)?;
                } else {
                    shared.try_reserve(1).map_err(|_| Error::Capacity)?;
                    shared.insert(identity, command.record.resource_index);
                }
            }
            if !layout.compiled {
                layout = Layout {
                    compiled: false,
                    color,
                    outline: outlines.len(),
                    segment: segments.len(),
                    bitmap: color_bitmaps.len(),
                    pixel: color_pixels.len(),
                };
            } else if layout.color != color {
                return Err(Error::InvalidInput);
            }
            let mut style_index = SCENE_NO_INDEX;
            let mut paint_index = SCENE_NO_INDEX;
            let mut rendering_mode = SCENE_TEXT_GRAYSCALE;
            if command.record.kind == SCENE_COMMAND_DRAW_PAINTED_GLYPH_RUN {
                if color
                    || frame.state.transform.m11 != 1.0
                    || frame.state.transform.m12 != 0.0
                    || frame.state.transform.m21 != 0.0
                    || frame.state.transform.m22 != 1.0
                {
                    return Err(Error::UnsupportedExecution);
                }
                let descriptor: wire::PaintedGlyphDraw = read(command.payload, 0)?;
                let mut paint = wire::GlyphPaint::read(
                    command
                        .payload
                        .get(wire::PaintedGlyphDraw::WIRE_SIZE..)
                        .ok_or(Error::InvalidInput)?,
                )
                .ok_or(Error::InvalidInput)?;
                if !text::paint(&paint) {
                    return Err(Error::InvalidInput);
                }
                rendering_mode = descriptor.rendering_mode;
                paint_index = count_u32(paints.len())?;
                let source = scene
                    .envelope()
                    .resource(descriptor.paint_resource_index)
                    .ok_or(Error::InvalidInput)?;
                if paint.kind == SCENE_GLYPH_PAINT_MATERIAL {
                    paint.brush_index = materials
                        .draw_brush_index(frame.command_index, 0)
                        .ok_or(Error::InvalidInput)?;
                } else {
                    let binding = external_slot(
                        external,
                        source.record.resource_id,
                        source.record.generation,
                        NativeImageRole::Primary,
                    )
                    .ok_or(Error::InvalidInput)?;
                    if external[binding]
                        .size
                        .into_iter()
                        .any(|v| v == 0 || v > 16384)
                    {
                        return Err(Error::InvalidInput);
                    }
                    paint.source_offset_opacity[2] *= frame.state.opacity;
                    let mode = (paint.flags & SCENE_GLYPH_PAINT_SAMPLING_MASK) >> 8;
                    let sampling = if mode == 0 {
                        IMAGE_SAMPLING_LINEAR
                    } else if mode == 1 {
                        IMAGE_SAMPLING_NEAREST
                    } else {
                        mode
                    };
                    if mode != 2 && paint.sampling[0] == 0.0 {
                        paint.sampling[0] = sampling_coefficient(engine_flags, sampling);
                    }
                    for quad in [&mut paint.texture_quad01, &mut paint.texture_quad23] {
                        quad[0] += frame.state.transform.m31;
                        quad[1] += frame.state.transform.m32;
                        quad[2] += frame.state.transform.m31;
                        quad[3] += frame.state.transform.m32;
                    }
                }
                paint.source_offset_opacity[0] += frame.state.transform.m31;
                paint.source_offset_opacity[1] += frame.state.transform.m32;
                if !text::paint(&paint) {
                    return Err(Error::InvalidInput);
                }
                push(&mut paints, paint)?;
                push(
                    &mut paint_resources,
                    NativeGlyphPaintResource {
                        resource_id: source.record.resource_id,
                        generation: source.record.generation,
                    },
                )?;
                add(&mut metrics.paint_bytes, wire::GlyphPaint::WIRE_SIZE as u64)?;
            }
            if command.record.flags & SCENE_GLYPH_STYLED != 0 {
                style_index = styles
                    .command_style_index(frame.command_index)
                    .filter(|i| *i != SCENE_NO_INDEX)
                    .ok_or(Error::InvalidInput)?;
            }
            if !layout.compiled {
                if color {
                    extend(&mut color_pixels, resource.auxiliary)?;
                    for i in 0..outline_count {
                        let mut bitmap: wire::ColorGlyphBitmap = read(resource.payload, i)?;
                        bitmap.pixel_offset = bitmap
                            .pixel_offset
                            .checked_add(layout.pixel as u64)
                            .ok_or(Error::Capacity)?;
                        push(&mut color_bitmaps, bitmap)?;
                    }
                } else {
                    for i in 0..segment_count {
                        push(&mut segments, read(resource.auxiliary, i)?)?;
                    }
                    for i in 0..outline_count {
                        let mut outline: wire::GlyphOutline = read(resource.payload, i)?;
                        outline.segment_offset = outline
                            .segment_offset
                            .checked_add(layout.segment as u64)
                            .ok_or(Error::Capacity)?;
                        push(&mut outlines, outline)?;
                    }
                }
            }
            layout.compiled = true;
            layouts[slot] = layout;
            let first_instance = count_u32(glyphs.len())?;
            for i in 0..glyph_count as usize {
                let mut glyph: wire::PositionedGlyph = read(glyph_bytes, i)?;
                if style_index == SCENE_NO_INDEX && paint_index == SCENE_NO_INDEX {
                    execution::apply_positioned_glyph_state(&mut glyph, &frame.state);
                } else {
                    execution::apply_positioned_glyph_transform(&mut glyph, &frame.state);
                }
                if !values::positioned_glyph(&glyph, outline_count as u64) {
                    return Err(Error::InvalidInput);
                }
                if color {
                    push(
                        &mut color_bitmap_indices,
                        count_u32(layout.bitmap + glyph.outline_index as usize)?,
                    )?;
                    glyph.outline_index = 0;
                } else {
                    push(&mut color_bitmap_indices, SCENE_NO_INDEX)?;
                    glyph.outline_index = glyph
                        .outline_index
                        .checked_add(count_u32(layout.outline)?)
                        .ok_or(Error::Capacity)?;
                }
                push(&mut glyphs, glyph)?;
                push(&mut style_indices, style_index)?;
                push(&mut paint_indices, paint_index)?;
                push(&mut rendering_modes, rendering_mode)?;
            }
            push(
                &mut draws,
                NativeGlyphDrawRange {
                    command_index: frame.command_index,
                    draw: crate::native_scene::draw::NativeGlyphDraw {
                        first_instance,
                        instance_count: glyph_count,
                        paint_index,
                    },
                },
            )?;
            add(&mut metrics.draw_count, 1)?;
            add(&mut metrics.glyph_count, u64::from(glyph_count))?;
            add(&mut metrics.instance_bytes, u64::from(glyph_count) * 96)?;
        }
        let mut retained = allocate(frames.len())?;
        retained.extend_from_slice(frames);
        let mut external_images = allocate(external.len())?;
        external_images.extend_from_slice(external);
        if metrics
            .instance_bytes
            .checked_add(metrics.paint_bytes)
            .is_none_or(|v| v > 256 * 1024 * 1024)
            || metrics.color_pixel_bytes > 256 * 1024 * 1024
        {
            return Err(Error::Capacity);
        }
        Ok(Self {
            scene,
            frames: retained.into(),
            styles: styles.clone(),
            materials: materials.clone(),
            outlines: outlines.into(),
            segments: segments.into(),
            glyphs: glyphs.into(),
            style_indices: style_indices.into(),
            paint_indices: paint_indices.into(),
            rendering_modes: rendering_modes.into(),
            paints: paints.into(),
            paint_resources: paint_resources.into(),
            external_images: external_images.into(),
            color_bitmaps: color_bitmaps.into(),
            color_pixels: color_pixels.into(),
            color_bitmap_indices: color_bitmap_indices.into(),
            draws: draws.into(),
            metrics,
            engine_flags,
        })
    }
}
fn sampling_coefficient(flags: u32, sampling: u32) -> f32 {
    if flags & ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING != 0 {
        match sampling {
            IMAGE_SAMPLING_FANT => -256.0,
            IMAGE_SAMPLING_NEAREST => -128.0,
            IMAGE_SAMPLING_LINEAR => -64.0,
            _ => 0.0,
        }
    } else if sampling == IMAGE_SAMPLING_FANT {
        -32.0
    } else {
        0.0
    }
}
