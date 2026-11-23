//! Original whole-image copy recognition and clip suspension transactions.
use super::*;

/// Complete copied metadata; no pointer into mutable builder storage escapes.
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneFullImageCopy {
    pub resource_index: u32,
    pub resource_flags: u32,
    pub image: wire::ImageDraw,
    pub color_matrix: wire::ImageColorMatrix,
    pub picture: wire::PictureImage,
}
impl Default for NativeSceneFullImageCopy {
    fn default() -> Self {
        Self {
            resource_index: NO_INDEX,
            resource_flags: 0,
            image: Default::default(),
            color_matrix: Default::default(),
            picture: Default::default(),
        }
    }
}
fn same_rect(a: wire::ImageRect, b: wire::ImageRect) -> bool {
    a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height
}
impl NativeSceneBuilder {
    /// Original legacy reader refuses to discard explicit picture presentation.
    pub fn full_image_copy(
        &self,
        bounds: wire::ImageRect,
        pixel_width: u32,
        pixel_height: u32,
    ) -> Option<NativeSceneFullImageCopy> {
        let (copy, _) =
            self.full_image_copy_with_presentation(bounds, pixel_width, pixel_height)?;
        (copy.picture.flags & SCENE_PICTURE_IMAGE_PRESENTATION == 0).then_some(copy)
    }
    pub fn full_image_copy_with_presentation(
        &self,
        bounds: wire::ImageRect,
        pixel_width: u32,
        pixel_height: u32,
    ) -> Option<(NativeSceneFullImageCopy, wire::Presentation)> {
        if self.stack_depth != 0
            || self.commands.len() != 3
            || !finite_rect(bounds)
            || bounds.width <= 0.0
            || bounds.height <= 0.0
            || pixel_width == 0
            || pixel_height == 0
        {
            return None;
        }
        let push = &self.commands[0];
        let draw = &self.commands[1];
        let pop = &self.commands[2];
        if push.record.kind != SCENE_COMMAND_PUSH_LAYER
            || draw.record.kind != SCENE_COMMAND_DRAW_IMAGE
            || pop.record.kind != SCENE_COMMAND_POP_LAYER
            || push.record.state_index != NO_INDEX
            || draw.record.state_index != NO_INDEX
            || pop.record.state_index != NO_INDEX
            || push.payload.len() != wire::Layer::WIRE_SIZE
            || !pop.payload.is_empty()
            || draw.payload.len() < wire::ImageDraw::WIRE_SIZE
        {
            return None;
        }
        let layer = wire::Layer::read(&push.payload)?;
        if layer.flags != SCENE_LAYER_BOUNDS
            || layer.blend_mode != BLEND_SRC
            || layer.opacity != 1.0
            || !same_rect(layer.bounds, bounds)
            || layer.mask_resource_index != NO_INDEX
            || layer.effect_resource_index != NO_INDEX
            || layer.content_revision != 0
            || layer.composite_revision != 0
            || layer.reserved0 != 0
            || layer.reserved1 != 0
        {
            return None;
        }
        let image = wire::ImageDraw::read(&draw.payload)?;
        let resource = self.resources.get(draw.record.resource_index as usize)?;
        let matrix = image.flags & SCENE_IMAGE_COLOR_MATRIX != 0;
        if !owned_image(resource)
            || image.image_width != resource.image_width
            || image.image_height != resource.image_height
            || image.row_bytes != resource.image_row_bytes
            || image.opacity != 1.0
            || image.sampling != IMAGE_SAMPLING_NEAREST
            || image.max_anisotropy != 1
            || image.flags
                & !(SCENE_IMAGE_SOURCE_PREMULTIPLIED
                    | SCENE_IMAGE_SOURCE_ALPHA_IGNORE
                    | SCENE_IMAGE_COLOR_MATRIX)
                != 0
            || !identity(image.transform)
            || !same_rect(image.destination_rect, bounds)
            || image.source_rect.width != pixel_width as f32
            || image.source_rect.height != pixel_height as f32
            || !image.source_rect.x.is_finite()
            || !image.source_rect.y.is_finite()
            || image.source_rect.x < 0.0
            || image.source_rect.y < 0.0
            || image.source_rect.x.floor() != image.source_rect.x
            || image.source_rect.y.floor() != image.source_rect.y
            || image.source_rect.x + image.source_rect.width > image.image_width as f32
            || image.source_rect.y + image.source_rect.height > image.image_height as f32
            || !same_rect(
                wire::ImageRect {
                    x: draw.record.bounds_x,
                    y: draw.record.bounds_y,
                    width: draw.record.bounds_width,
                    height: draw.record.bounds_height,
                },
                bounds,
            )
            || draw.payload.len()
                != wire::ImageDraw::WIRE_SIZE
                    + if matrix {
                        wire::ImageColorMatrix::WIRE_SIZE
                    } else {
                        0
                    }
        {
            return None;
        }
        let mut copy = NativeSceneFullImageCopy {
            resource_index: draw.record.resource_index,
            resource_flags: resource.record.flags,
            image,
            ..Default::default()
        };
        if matrix {
            copy.color_matrix =
                wire::ImageColorMatrix::read(&draw.payload[wire::ImageDraw::WIRE_SIZE..])?;
        }
        let mut presentation = wire::Presentation::default();
        if resource.picture_image {
            (copy.picture, presentation) = values::read_picture_image(&resource.payload)?;
        }
        Some((copy, presentation))
    }
    pub fn copy_image_resource_from(
        &mut self,
        source: &Self,
        source_index: u32,
    ) -> BuildResult<u32> {
        if std::ptr::eq(self, source) || self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::InvalidArgument);
        }
        let source = source
            .resources
            .get(source_index as usize)
            .filter(|r| owned_image(r))
            .ok_or(B::InvalidArgument)?;
        let resource = ResourceEntry {
            record: wire::Resource {
                resource_id: self.resources.len() as u64 + 1,
                generation: self.generation,
                ..source.record
            },
            payload: copy_bytes(&source.payload)?,
            auxiliary: copy_bytes(&source.auxiliary)?,
            brush_table: source.brush_table,
            text_style_table: source.text_style_table,
            rgba8_image: source.rgba8_image,
            bgra8_image: source.bgra8_image,
            r8_image: source.r8_image,
            picture_image: source.picture_image,
            source_geometry_clip: source.source_geometry_clip,
            image_width: source.image_width,
            image_height: source.image_height,
            image_row_bytes: source.image_row_bytes,
            glyph_outline_count: source.glyph_outline_count,
        };
        reserve_append(&mut self.resources, 1)?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    pub fn copy_image_from_memory(
        &mut self,
        image: wire::ImageDraw,
        storage_flags: u32,
        pixels: &[u8],
        matrix: Option<&wire::ImageColorMatrix>,
    ) -> BuildResult<()> {
        if !matches!(storage_flags, 0 | SCENE_IMAGE_BGRA8 | SCENE_IMAGE_R8) {
            return Err(B::InvalidArgument);
        }
        let resources = self.resources.len();
        let result = (|| {
            let index = self.add_upload_image(
                image.image_width,
                image.image_height,
                image.row_bytes,
                pixels,
                storage_flags == SCENE_IMAGE_BGRA8,
                storage_flags == SCENE_IMAGE_R8,
            )?;
            self.append_image_copy_commands(index, image, matrix)
        })();
        if result.is_err() {
            self.resources.truncate(resources);
        }
        result
    }
    /// Consume a source recorder and move its actual selected storage generation.
    pub fn copy_image_from_builder(
        &mut self,
        mut source: Self,
        source_index: u32,
        image: wire::ImageDraw,
        matrix: Option<&wire::ImageColorMatrix>,
    ) -> BuildResult<()> {
        let resource = source
            .resources
            .get(source_index as usize)
            .filter(|r| owned_image(r))
            .ok_or(B::InvalidArgument)?;
        if !owned_image(resource) || self.resources.len() >= MAX_RESOURCES as usize {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = std::mem::take(&mut source.resources[source_index as usize]);
        let index = self.resources.len() as u32;
        resource.record.resource_id = u64::from(index) + 1;
        resource.record.generation = self.generation;
        self.resources.push(resource);
        let result = self.append_image_copy_commands(index, image, matrix);
        if result.is_err() {
            self.resources.pop();
        }
        result
    }
    pub fn copy_image_from_memory_outside_clips(
        &mut self,
        image: wire::ImageDraw,
        storage_flags: u32,
        pixels: &[u8],
        matrix: Option<&wire::ImageColorMatrix>,
    ) -> BuildResult<()> {
        self.copy_image_outside_clips(|this| {
            this.copy_image_from_memory(image, storage_flags, pixels, matrix)
        })
    }
    pub fn copy_image_from_builder_outside_clips(
        &mut self,
        source: Self,
        source_index: u32,
        image: wire::ImageDraw,
        matrix: Option<&wire::ImageColorMatrix>,
    ) -> BuildResult<()> {
        self.copy_image_outside_clips(move |this| {
            this.copy_image_from_builder(source, source_index, image, matrix)
        })
    }
    fn copy_image_outside_clips(
        &mut self,
        copy: impl FnOnce(&mut Self) -> BuildResult<()>,
    ) -> BuildResult<()> {
        let depth = self.stack_depth;
        if depth == 0 {
            return copy(self);
        }
        if !self.hit_test_owners.is_empty() {
            return Err(B::InvalidState);
        }
        for i in 0..depth as usize {
            let index = self.stack_state_indices[i];
            if self.stack_kinds[i] != 1
                || self.hit_rectangle_stack[i] != 0
                || self.input_only_stack[i] != 0
                || self.render_only_stack[i] != 0
                || index as usize >= self.resources.len()
                || !self.valid_state_index(index, false)
            {
                return Err(B::InvalidState);
            }
            let state = wire::State::read(&self.resources[index as usize].payload)
                .ok_or(B::InvalidState)?;
            if state.flags & !SCENE_STATE_CLIP_PIXEL_CENTERS != SCENE_STATE_CLIP_RECT
                || state.opacity != 1.0
                || !identity(state.transform)
            {
                return Err(B::InvalidState);
            }
        }
        let commands = self.commands.len();
        let resources = self.resources.len();
        let maximum = self.maximum_stack_depth;
        let states = self.stack_state_indices;
        let kinds = self.stack_kinds;
        let result = (|| {
            for _ in 0..depth {
                self.restore()?;
            }
            copy(self)?;
            for &state in &states[..depth as usize] {
                self.save(state, NativeSceneSaveOptions::default())?;
            }
            Ok(())
        })();
        if result.is_err() {
            self.commands.truncate(commands);
            self.resources.truncate(resources);
            self.stack_depth = depth;
            self.materialized_layer_depth = 0;
            self.maximum_stack_depth = maximum;
            self.stack_state_indices = states;
            self.stack_kinds = kinds;
        }
        result
    }
    fn append_image_copy_commands(
        &mut self,
        index: u32,
        image: wire::ImageDraw,
        matrix: Option<&wire::ImageColorMatrix>,
    ) -> BuildResult<()> {
        if self.stack_depth != 0
            || !identity(image.transform)
            || image.opacity != 1.0
            || image.sampling != IMAGE_SAMPLING_NEAREST
        {
            return Err(B::InvalidArgument);
        }
        let commands = self.commands.len();
        let maximum = self.maximum_stack_depth;
        let layer = wire::Layer {
            struct_size: wire::Layer::WIRE_SIZE as u32,
            flags: SCENE_LAYER_BOUNDS,
            bounds: image.destination_rect,
            opacity: 1.0,
            blend_mode: BLEND_SRC,
            mask_resource_index: NO_INDEX,
            effect_resource_index: NO_INDEX,
            ..Default::default()
        };
        let result = (|| {
            self.push_layer(layer)?;
            self.draw_image(
                index,
                image,
                image.destination_rect,
                NO_INDEX,
                NativeSceneImageOptions {
                    color_matrix: matrix,
                    ..Default::default()
                },
            )?;
            self.pop_layer()
        })();
        if result.is_err() {
            self.commands.truncate(commands);
            self.stack_depth = 0;
            self.materialized_layer_depth = 0;
            self.maximum_stack_depth = maximum;
            self.stack_kinds[0] = 0;
        }
        result
    }
}
