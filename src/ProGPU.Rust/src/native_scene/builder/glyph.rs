//! Original owned text-style, outline/color atlas and positioned-glyph producers.
use super::*;
use crate::native_scene::semantic::glyph_scan as scan;
use crate::native_scene::semantic::{rgb_glyph, values};

impl NativeSceneBuilder {
    pub fn add_text_style(&mut self, source: &wire::TextStyle) -> BuildResult<u32> {
        let style = wire::TextStyle {
            reserved1: 0,
            reserved2: 0,
            ..*source
        };
        if !values::text_style(&style) {
            return Err(B::InvalidArgument);
        }
        // Native memcmp identity preserves signed zero and all accepted bits.
        let mut key = [0u8; wire::TextStyle::WIRE_SIZE];
        style.write_into(&mut key).map_err(|_| B::InvalidState)?;
        for (index, stored) in self.text_styles.iter().enumerate() {
            let mut bytes = [0u8; wire::TextStyle::WIRE_SIZE];
            stored.write_into(&mut bytes).map_err(|_| B::InvalidState)?;
            if key == bytes {
                return Ok(index as u32);
            }
        }
        if self.text_styles.len() >= SCENE_MAX_TEXT_STYLES as usize {
            return Err(B::CapacityExceeded);
        }
        reserve_append(&mut self.text_styles, 1)?;
        if self.text_style_resource_index == NO_INDEX {
            if self.resources.len() >= MAX_RESOURCES as usize {
                return Err(B::CapacityExceeded);
            }
            reserve_append(&mut self.resources, 1)?;
            let mut resource = self.new_resource(SCENE_RESOURCE_TEXT_STYLE_TABLE);
            resource.text_style_table = true;
            self.text_style_resource_index = self.resources.len() as u32;
            self.resources.push(resource);
        }
        let index = self.text_styles.len() as u32;
        self.text_styles.push(style);
        Ok(index)
    }
    pub fn add_glyph_outlines(
        &mut self,
        outlines: &[wire::GlyphOutline],
        segments: &[wire::PathSegment],
    ) -> BuildResult<u32> {
        if outlines.is_empty()
            || segments.is_empty()
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut covered = 0u64;
        for outline in outlines {
            if outline.segment_offset > covered
                || values::glyph_outline(outline, segments.len() as u64).is_none()
            {
                return Err(B::InvalidArgument);
            }
            let end = outline.segment_offset + outline.segment_count;
            if !scan::outline(
                outline,
                &segments[outline.segment_offset as usize..end as usize],
            ) {
                return Err(B::InvalidArgument);
            }
            covered = covered.max(end);
        }
        if covered != segments.len() as u64 || !segments.iter().all(scan::segment) {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_GLYPH_RUN);
        resource.payload = encode(outlines)?;
        resource.auxiliary = encode(segments)?;
        resource.glyph_outline_count =
            u32::try_from(outlines.len()).map_err(|_| B::CapacityExceeded)?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    pub fn add_color_glyph_bitmaps(
        &mut self,
        bitmaps: &[wire::ColorGlyphBitmap],
        rgba_pixels: &[u8],
    ) -> BuildResult<u32> {
        if bitmaps.is_empty()
            || rgba_pixels.is_empty()
            || self.resources.len() >= MAX_RESOURCES as usize
            || bitmaps.len() > 1 << 20
            || !bitmaps
                .iter()
                .all(|b| values::color_glyph_bitmap(b, rgba_pixels.len()))
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_GLYPH_RUN);
        resource.record.flags |= SCENE_COLOR_GLYPH_BITMAPS;
        resource.payload = encode(bitmaps)?;
        resource.auxiliary = copy_bytes(rgba_pixels)?;
        resource.glyph_outline_count = bitmaps.len() as u32;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    pub fn draw_rgb_glyph_run(
        &mut self,
        glyph_resource_index: u32,
        descriptor: &wire::RgbGlyphDraw,
        glyphs: &[wire::RgbGlyphTile],
        bounds: wire::ImageRect,
        state_resource_index: u32,
    ) -> BuildResult<()> {
        if !rgb_glyph::descriptor(descriptor)
            || glyphs.len() != descriptor.glyph_count as usize
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::InvalidArgument);
        }
        let resource = self
            .resources
            .get(glyph_resource_index as usize)
            .ok_or(B::InvalidArgument)?;
        if resource.record.kind != SCENE_RESOURCE_GLYPH_RUN
            || resource.record.flags & SCENE_COLOR_GLYPH_BITMAPS != 0
            || resource.glyph_outline_count == 0
            || resource.glyph_outline_count > 65536
            || resource.auxiliary.len() / wire::PathSegment::WIRE_SIZE > 1048576
        {
            return Err(B::InvalidArgument);
        }
        let mut pixels = 0u64;
        for glyph in glyphs {
            pixels = pixels.wrapping_add(u64::from(glyph.width) * u64::from(glyph.height));
            if !rgb_glyph::tile(glyph, resource.glyph_outline_count) || pixels > 4096 * 4096 {
                return Err(B::InvalidArgument);
            }
        }
        reserve_append(&mut self.commands, 1)?;
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_RGB_GLYPH_RUN,
            bounds,
            state_resource_index,
            glyph_resource_index,
        );
        command.payload = encode(&[*descriptor])?;
        append_encoded(&mut command.payload, glyphs)?;
        self.commands.push(command);
        Ok(())
    }
    pub fn draw_glyph_run(
        &mut self,
        glyph_resource_index: u32,
        glyphs: &[wire::PositionedGlyph],
        bounds: wire::ImageRect,
        state_resource_index: u32,
        text_style_index: u32,
        local_ink_bounds: Option<wire::ImageRect>,
    ) -> BuildResult<()> {
        if glyphs.is_empty()
            || !finite_rect(bounds)
            || !self.valid_state_index(state_resource_index, false)
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::InvalidArgument);
        }
        let resource = self
            .resources
            .get(glyph_resource_index as usize)
            .ok_or(B::InvalidArgument)?;
        let record_ink = local_ink_bounds.filter(|_| {
            self.hit_test_owners
                .last()
                .is_some_and(|o| o.owner.is_some())
        });
        if record_ink.is_some_and(|r| !finite_rect(r)) {
            return Err(B::InvalidArgument);
        }
        let styled = text_style_index != NO_INDEX;
        if resource.record.kind != SCENE_RESOURCE_GLYPH_RUN
            || resource.glyph_outline_count == 0
            || (styled
                && (self.text_style_resource_index == NO_INDEX
                    || text_style_index as usize >= self.text_styles.len()))
            || !glyphs
                .iter()
                .all(|g| values::positioned_glyph(g, u64::from(resource.glyph_outline_count)))
        {
            return Err(B::InvalidArgument);
        }
        let payload = glyphs
            .len()
            .checked_mul(wire::PositionedGlyph::WIRE_SIZE)
            .and_then(|n| {
                n.checked_add(if styled {
                    wire::GlyphDraw::WIRE_SIZE
                } else {
                    0
                })
            })
            .filter(|&n| n <= u32::MAX as usize)
            .ok_or(B::CapacityExceeded)?;
        reserve_append(&mut self.commands, 1)?;
        if record_ink.is_some() && self.glyph_hit_bounds.len() == self.glyph_hit_bounds.capacity() {
            let count = self
                .glyph_hit_bounds
                .len()
                .checked_mul(2)
                .ok_or(B::InvalidState)?
                .max(16);
            reserve_total(&mut self.glyph_hit_bounds, count)?;
        }
        let mut command = self.new_command(
            SCENE_COMMAND_DRAW_GLYPH_RUN,
            bounds,
            state_resource_index,
            glyph_resource_index,
        );
        command.payload = zeroed(payload)?;
        let start = if styled {
            command.record.flags |= SCENE_GLYPH_STYLED;
            let descriptor = wire::GlyphDraw {
                struct_size: wire::GlyphDraw::WIRE_SIZE as u32,
                style_resource_index: self.text_style_resource_index,
                style_index: text_style_index,
                glyph_count: glyphs.len() as u32,
                reserved0: 0,
                reserved1: 0,
            };
            write_records(&[descriptor], &mut command.payload)?;
            wire::GlyphDraw::WIRE_SIZE
        } else {
            0
        };
        write_records(glyphs, &mut command.payload[start..])?;
        if let Some(local_bounds) = record_ink {
            self.glyph_hit_bounds.push(GlyphHitBoundsEntry {
                command_index: self.commands.len(),
                local_bounds,
            });
        }
        self.commands.push(command);
        Ok(())
    }
}
