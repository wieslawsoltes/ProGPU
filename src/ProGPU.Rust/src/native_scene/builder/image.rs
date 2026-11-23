//! Owned native image resources and ordered draw/patch batching.
use super::*;
use crate::native_scene::semantic::{image_payload, values};
mod copy;
pub use copy::NativeSceneFullImageCopy;

/// Optional suffix records remain distinct from source flags. Mismatched flags
/// and records are rejected; no defaults are inferred for cubic or effect input.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeSceneImageOptions<'a> {
    pub sampling: Option<&'a wire::ImageSamplingOptions>,
    pub color_matrix: Option<&'a wire::ImageColorMatrix>,
    pub effect: Option<&'a wire::ImageEffect>,
}
impl NativeSceneBuilder {
    pub fn add_rgba8_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
    ) -> BuildResult<u32> {
        self.add_upload_image(width, height, row_bytes, pixels, false, false)
    }
    pub fn add_bgra8_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
    ) -> BuildResult<u32> {
        self.add_upload_image(width, height, row_bytes, pixels, true, false)
    }
    pub fn add_r8_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
    ) -> BuildResult<u32> {
        self.add_upload_image(width, height, row_bytes, pixels, false, true)
    }
    fn add_upload_image(
        &mut self,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
        bgra: bool,
        r8: bool,
    ) -> BuildResult<u32> {
        let row = u64::from(width) * if r8 { 1 } else { 4 };
        let required = if height == 0 {
            0
        } else {
            (u64::from(row_bytes) * u64::from(height - 1)).wrapping_add(row)
        };
        if width == 0
            || height == 0
            || width > 16384
            || height > 16384
            || u64::from(row_bytes) < row
            || required != pixels.len() as u64
            || required > u64::from(MAX_STREAM_BYTES)
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_IMAGE);
        resource.record.flags |= if bgra {
            SCENE_IMAGE_BGRA8
        } else if r8 {
            SCENE_IMAGE_R8
        } else {
            0
        };
        resource.payload = copy_bytes(pixels)?;
        resource.rgba8_image = !bgra && !r8;
        resource.bgra8_image = bgra;
        resource.r8_image = r8;
        resource.image_width = width;
        resource.image_height = height;
        resource.image_row_bytes = row_bytes;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    /// Record an actual nested source stream. Complete nested semantic admission
    /// is deferred to ingestion, matching the original producer contract.
    pub fn add_picture_image(
        &mut self,
        picture: wire::PictureImage,
        presentation: Option<wire::Presentation>,
        nested_scene: &[u8],
    ) -> BuildResult<u32> {
        if !values::picture_image(&picture, presentation.as_ref())
            || nested_scene.len() < wire::Header::WIRE_SIZE
            || nested_scene.len() > MAX_STREAM_BYTES as usize
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        let mut resource = self.new_resource(SCENE_RESOURCE_IMAGE);
        resource.record.flags |= SCENE_IMAGE_PICTURE;
        resource.payload = encode(&[picture])?;
        if let Some(presentation) = presentation {
            append_encoded(&mut resource.payload, &[presentation])?;
        }
        resource.auxiliary = copy_bytes(nested_scene)?;
        resource.picture_image = true;
        resource.image_width = picture.width;
        resource.image_height = picture.height;
        resource.image_row_bytes = picture.width * 4;
        reserve_append(&mut self.resources, 1)?;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    /// Retain only external resource identity and declared dimensions. Actual GPU
    /// ownership must be supplied by the native scene resource resolver.
    pub fn add_external_image(&mut self, width: u32, height: u32) -> BuildResult<u32> {
        if width == 0
            || height == 0
            || width > 16384
            || height > 16384
            || self.resources.len() >= MAX_RESOURCES as usize
        {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.resources, 1)?;
        let mut resource = self.new_resource(SCENE_RESOURCE_IMAGE);
        resource.record.flags |= SCENE_EXTERNAL_IMAGE;
        resource.image_width = width;
        resource.image_height = height;
        resource.image_row_bytes = width * 4;
        let index = self.resources.len() as u32;
        self.resources.push(resource);
        Ok(index)
    }
    pub fn update_rgba8_image(
        &mut self,
        index: u32,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
        generation: u64,
    ) -> BuildResult<()> {
        self.update_32bit_image(index, width, height, row_bytes, pixels, generation, false)
    }
    pub fn update_bgra8_image(
        &mut self,
        index: u32,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
        generation: u64,
    ) -> BuildResult<()> {
        self.update_32bit_image(index, width, height, row_bytes, pixels, generation, true)
    }
    fn update_32bit_image(
        &mut self,
        index: u32,
        width: u32,
        height: u32,
        row_bytes: u32,
        pixels: &[u8],
        generation: u64,
        bgra: bool,
    ) -> BuildResult<()> {
        let resource = self
            .resources
            .get_mut(index as usize)
            .ok_or(B::InvalidArgument)?;
        if (if bgra {
            !resource.bgra8_image
        } else {
            !resource.rgba8_image
        }) || width != resource.image_width
            || height != resource.image_height
            || row_bytes != resource.image_row_bytes
            || pixels.len() != resource.payload.len()
            || generation <= resource.record.generation
        {
            return Err(B::InvalidArgument);
        }
        let next = copy_bytes(pixels)?;
        resource.payload = next;
        resource.record.generation = generation;
        Ok(())
    }
    pub fn draw_image(
        &mut self,
        index: u32,
        source: wire::ImageDraw,
        bounds: wire::ImageRect,
        state: u32,
        options: NativeSceneImageOptions<'_>,
    ) -> BuildResult<()> {
        if source.flags & SCENE_IMAGE_PATCH_BATCH != 0 {
            return Err(B::InvalidArgument);
        }
        let (image, _) = self.admit_image(index, source, bounds, state, options)?;
        if self.try_merge_image_draw(index, &image, bounds, state, options.sampling)? {
            return Ok(());
        }
        let mut command = self.new_command(SCENE_COMMAND_DRAW_IMAGE, bounds, state, index);
        command.payload = image_payload_bytes(image, options)?;
        reserve_append(&mut self.commands, 1)?;
        self.commands.push(command);
        Ok(())
    }
    pub fn draw_image_patches(
        &mut self,
        index: u32,
        mut source: wire::ImageDraw,
        patches: &[wire::ImagePatch],
        bounds: wire::ImageRect,
        state: u32,
        options: NativeSceneImageOptions<'_>,
    ) -> BuildResult<()> {
        if patches.is_empty() || patches.len() > SCENE_MAX_IMAGE_PATCHES as usize {
            return Err(B::InvalidArgument);
        }
        source.flags |= SCENE_IMAGE_PATCH_BATCH;
        let (image, validation_bytes) = self.admit_image(index, source, bounds, state, options)?;
        let mut command = self.new_command(SCENE_COMMAND_DRAW_IMAGE, bounds, state, index);
        command.payload = image_payload_bytes(image, options)?;
        append_encoded(
            &mut command.payload,
            &[wire::ImagePatchBatch {
                struct_size: 16,
                flags: 0,
                patch_count: patches.len() as u32,
                reserved: 0,
            }],
        )?;
        append_encoded(&mut command.payload, patches)?;
        command.record.payload_size =
            u32::try_from(command.payload.len()).map_err(|_| B::CapacityExceeded)?;
        let bpp = if self.resources[index as usize].r8_image {
            1
        } else {
            4
        };
        let parsed =
            image_payload::read_image_payload(&command.payload, &image, validation_bytes, bpp)
                .ok_or(B::InvalidArgument)?;
        if parsed.patch_count as usize != patches.len() {
            return Err(B::InvalidArgument);
        }
        reserve_append(&mut self.commands, 1)?;
        self.commands.push(command);
        Ok(())
    }
    fn admit_image(
        &self,
        index: u32,
        mut image: wire::ImageDraw,
        bounds: wire::ImageRect,
        state: u32,
        options: NativeSceneImageOptions<'_>,
    ) -> BuildResult<(wire::ImageDraw, u64)> {
        let resource = self
            .resources
            .get(index as usize)
            .ok_or(B::InvalidArgument)?;
        if !self.valid_state_index(state, false)
            || !finite_rect(bounds)
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::InvalidArgument);
        }
        image.struct_size = wire::ImageDraw::WIRE_SIZE as u32;
        let external = resource.record.flags & SCENE_EXTERNAL_IMAGE != 0;
        let validation_bytes = if external || resource.picture_image {
            (u64::from(image.row_bytes) * u64::from(image.image_height.wrapping_sub(1)))
                .wrapping_add(u64::from(image.image_width) * 4)
        } else {
            resource.payload.len() as u64
        };
        if !owned_image(resource) && !external
            || resource.picture_image && image.flags & SCENE_IMAGE_SOURCE_PREMULTIPLIED == 0
            || image.image_width != resource.image_width
            || image.image_height != resource.image_height
            || image.row_bytes != resource.image_row_bytes
            || !values::image(
                &image,
                validation_bytes,
                if resource.r8_image { 1 } else { 4 },
            )
            || (image.sampling == IMAGE_SAMPLING_CUBIC) != options.sampling.is_some()
            || (image.flags & SCENE_IMAGE_COLOR_MATRIX != 0) != options.color_matrix.is_some()
            || (image.flags & SCENE_IMAGE_EFFECT != 0) != options.effect.is_some()
            || options
                .sampling
                .is_some_and(|s| !values::image_sampling_options(s))
            || options
                .color_matrix
                .is_some_and(|m| !values::image_color_matrix(m))
            || options.effect.is_some_and(|e| !values::image_effect(e))
        {
            return Err(B::InvalidArgument);
        }
        Ok((image, validation_bytes))
    }
    fn try_merge_image_draw(
        &mut self,
        index: u32,
        image: &wire::ImageDraw,
        bounds: wire::ImageRect,
        state: u32,
        sampling: Option<&wire::ImageSamplingOptions>,
    ) -> BuildResult<bool> {
        if self.commands.is_empty()
            || self
                .hit_test_owners
                .last()
                .is_some_and(|owner| owner.first_command >= self.commands.len())
        {
            return Ok(false);
        }
        let previous = self.commands.last_mut().ok_or(B::InvalidState)?;
        if previous.record.kind != SCENE_COMMAND_DRAW_IMAGE
            || previous.record.resource_index != index
            || previous.record.state_index != state
        {
            return Ok(false);
        }
        let Some(mut prior) = wire::ImageDraw::read(&previous.payload) else {
            return Ok(false);
        };
        if !can_share_patch_batch(&prior, image) {
            return Ok(false);
        }
        let cubic = image.sampling == IMAGE_SAMPLING_CUBIC;
        let prefix = wire::ImageDraw::WIRE_SIZE;
        let previous_sampling = if cubic {
            wire::ImageSamplingOptions::read(&previous.payload[prefix..])
        } else {
            None
        };
        if cubic && previous_sampling.is_none()
            || !same_sampling(previous_sampling.as_ref(), sampling)
        {
            return Ok(false);
        }
        let batch_offset = prefix
            + if cubic {
                wire::ImageSamplingOptions::WIRE_SIZE
            } else {
                0
            };
        let current = texture_patch(image);
        if prior.flags & SCENE_IMAGE_PATCH_BATCH != 0 {
            let Some(mut batch) = previous
                .payload
                .get(batch_offset..)
                .and_then(wire::ImagePatchBatch::read)
            else {
                return Ok(false);
            };
            if batch.patch_count == 0 || batch.patch_count >= SCENE_MAX_IMAGE_PATCHES {
                return Ok(false);
            }
            let expected = batch_offset
                + wire::ImagePatchBatch::WIRE_SIZE
                + batch.patch_count as usize * wire::ImagePatch::WIRE_SIZE;
            if expected != previous.payload.len() {
                return Ok(false);
            }
            append_encoded(&mut previous.payload, &[current])?;
            batch.patch_count += 1;
            write_records(&[batch], &mut previous.payload[batch_offset..])?;
        } else {
            let first = texture_patch(&prior);
            prior.flags |= SCENE_IMAGE_PATCH_BATCH;
            let mut payload = encode(&[prior])?;
            if let Some(s) = previous_sampling {
                append_encoded(&mut payload, &[s])?;
            }
            append_encoded(
                &mut payload,
                &[wire::ImagePatchBatch {
                    struct_size: 16,
                    flags: 0,
                    patch_count: 2,
                    reserved: 0,
                }],
            )?;
            append_encoded(&mut payload, &[first, current])?;
            previous.payload = payload;
        }
        previous.record.payload_size = previous.payload.len() as u32;
        let old = wire::ImageRect {
            x: previous.record.bounds_x,
            y: previous.record.bounds_y,
            width: previous.record.bounds_width,
            height: previous.record.bounds_height,
        };
        let merged = union_bounds(old, bounds);
        previous.record.bounds_x = merged.x;
        previous.record.bounds_y = merged.y;
        previous.record.bounds_width = merged.width;
        previous.record.bounds_height = merged.height;
        Ok(true)
    }
}
fn image_payload_bytes(
    image: wire::ImageDraw,
    options: NativeSceneImageOptions<'_>,
) -> BuildResult<Vec<u8>> {
    let mut bytes = encode(&[image])?;
    if let Some(s) = options.sampling {
        append_encoded(&mut bytes, &[*s])?;
    }
    if let Some(m) = options.color_matrix {
        append_encoded(&mut bytes, &[*m])?;
    }
    if let Some(e) = options.effect {
        append_encoded(&mut bytes, &[*e])?;
    }
    Ok(bytes)
}
fn owned_image(r: &ResourceEntry) -> bool {
    r.rgba8_image || r.bgra8_image || r.r8_image || r.picture_image
}
fn same_transform(a: wire::Affine2d, b: wire::Affine2d) -> bool {
    [a.m11, a.m12, a.m21, a.m22, a.m31, a.m32].map(f32::to_bits)
        == [b.m11, b.m12, b.m21, b.m22, b.m31, b.m32].map(f32::to_bits)
}
fn identity(t: wire::Affine2d) -> bool {
    t.m11 == 1.0 && t.m12 == 0.0 && t.m21 == 0.0 && t.m22 == 1.0 && t.m31 == 0.0 && t.m32 == 0.0
}
fn same_sampling(
    a: Option<&wire::ImageSamplingOptions>,
    b: Option<&wire::ImageSamplingOptions>,
) -> bool {
    match (a, b) {
        (None, None) => true,
        (Some(a), Some(b)) => {
            a.struct_size == b.struct_size
                && a.flags == b.flags
                && a.cubic_b.to_bits() == b.cubic_b.to_bits()
                && a.cubic_c.to_bits() == b.cubic_c.to_bits()
        }
        _ => false,
    }
}
fn can_share_patch_batch(a: &wire::ImageDraw, b: &wire::ImageDraw) -> bool {
    let excluded = SCENE_IMAGE_COLOR_MATRIX | SCENE_IMAGE_EFFECT;
    a.flags & excluded == 0
        && b.flags & (excluded | SCENE_IMAGE_PATCH_BATCH) == 0
        && a.image_width == b.image_width
        && a.image_height == b.image_height
        && a.row_bytes == b.row_bytes
        && a.sampling == b.sampling
        && a.max_anisotropy == b.max_anisotropy
        && a.flags & !SCENE_IMAGE_PATCH_BATCH == b.flags
        && a.opacity.to_bits() == b.opacity.to_bits()
        && same_transform(a.transform, b.transform)
}
fn texture_patch(image: &wire::ImageDraw) -> wire::ImagePatch {
    wire::ImagePatch {
        struct_size: 88,
        kind: SCENE_IMAGE_PATCH_TEXTURE,
        source_rect: image.source_rect,
        destination_rect: image.destination_rect,
        transform: NativeSceneBuilder::identity_transform(),
        ..Default::default()
    }
}
fn union_bounds(a: wire::ImageRect, b: wire::ImageRect) -> wire::ImageRect {
    // std::min/max retain the first operand for equal values, including signed zero.
    let min = |a, b| if b < a { b } else { a };
    let max = |a, b| if a < b { b } else { a };
    let x = min(a.x, b.x);
    let y = min(a.y, b.y);
    wire::ImageRect {
        x,
        y,
        width: max(a.x + a.width, b.x + b.width) - x,
        height: max(a.y + a.height, b.y + b.height) - y,
    }
}
