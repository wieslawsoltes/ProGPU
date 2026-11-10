//! Original image execution/builder suffix parser. Deliberately separate from
//! scene-stream admission, which does not eagerly admit these execution payloads.
use super::*;
use wire::WireRecord;

#[derive(Clone, Copy, Debug)]
pub(crate) struct ImageOptions<'a> {
    pub cubic_b: f32,
    pub cubic_c: f32,
    pub color_matrix: Option<wire::ImageColorMatrix>,
    pub luminance_to_alpha: bool,
    pub effect: Option<wire::ImageEffect>,
    pub patch_bytes: &'a [u8],
    pub patch_count: u32,
}
impl ImageOptions<'_> {
    pub fn patch(&self, index: u32) -> Option<wire::ImagePatch> {
        if index >= self.patch_count {
            return None;
        }
        wire::ImagePatch::read(&self.patch_bytes[index as usize * wire::ImagePatch::WIRE_SIZE..])
    }
}
fn valid_rect(r: wire::ImageRect, positive: bool) -> bool {
    [r.x, r.y, r.width, r.height]
        .into_iter()
        .all(f32::is_finite)
        && (!positive || r.width > 0.0 && r.height > 0.0)
}
fn valid_patch(p: &wire::ImagePatch, image: &wire::ImageDraw) -> bool {
    let t = p.transform;
    p.struct_size == wire::ImagePatch::WIRE_SIZE as u32
        && p.flags == 0
        && p.kind <= SCENE_IMAGE_PATCH_ATLAS_COLOR
        && p.color_blend_mode <= 28
        && valid_rect(p.source_rect, false)
        && valid_rect(p.destination_rect, true)
        && [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]
            .into_iter()
            .all(f32::is_finite)
        && p.color.into_iter().all(f32::is_finite)
        && (p.kind == SCENE_IMAGE_PATCH_FIXED_COLOR
            || p.source_rect.x >= 0.0
                && p.source_rect.y >= 0.0
                && p.source_rect.width > 0.0
                && p.source_rect.height > 0.0
                && p.source_rect.x + p.source_rect.width <= image.image_width as f32
                && p.source_rect.y + p.source_rect.height <= image.image_height as f32)
}
/// Parse a complete payload slice at its actual beginning, retaining borrowed
/// patch bytes only after every original record and exact suffix length is admitted.
pub(crate) fn read_image_payload<'a>(
    bytes: &'a [u8],
    image: &wire::ImageDraw,
    pixel_bytes: u64,
    bytes_per_pixel: u32,
) -> Option<ImageOptions<'a>> {
    if image.struct_size != wire::ImageDraw::WIRE_SIZE as u32
        || !values::image(image, pixel_bytes, bytes_per_pixel)
    {
        return None;
    }
    let mut options = ImageOptions {
        cubic_b: 0.0,
        cubic_c: 0.5,
        color_matrix: None,
        luminance_to_alpha: false,
        effect: None,
        patch_bytes: &[],
        patch_count: 0,
    };
    let mut cursor = wire::ImageDraw::WIRE_SIZE;
    fn read<T: WireRecord>(bytes: &[u8], cursor: &mut usize) -> Option<T> {
        let item = T::read(bytes.get(*cursor..)?)?;
        *cursor += T::WIRE_SIZE;
        Some(item)
    }
    if image.sampling == IMAGE_SAMPLING_CUBIC {
        let s: wire::ImageSamplingOptions = read(bytes, &mut cursor)?;
        if !values::image_sampling_options(&s) {
            return None;
        }
        options.cubic_b = s.cubic_b;
        options.cubic_c = s.cubic_c;
    }
    if image.flags & SCENE_IMAGE_COLOR_MATRIX != 0 {
        let matrix: wire::ImageColorMatrix = read(bytes, &mut cursor)?;
        if !values::image_color_matrix(&matrix) {
            return None;
        }
        options.luminance_to_alpha =
            matrix.flags & SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA != 0;
        options.color_matrix = Some(matrix);
    }
    if image.flags & SCENE_IMAGE_EFFECT != 0 {
        let effect: wire::ImageEffect = read(bytes, &mut cursor)?;
        if !values::image_effect(&effect) {
            return None;
        }
        options.effect = Some(effect);
    }
    if image.flags & SCENE_IMAGE_PATCH_BATCH != 0 {
        let batch: wire::ImagePatchBatch = read(bytes, &mut cursor)?;
        if batch.struct_size != wire::ImagePatchBatch::WIRE_SIZE as u32
            || batch.flags != 0
            || batch.reserved != 0
            || batch.patch_count == 0
            || batch.patch_count > SCENE_MAX_IMAGE_PATCHES
            || batch.patch_count as usize
                > bytes.len().checked_sub(cursor)? / wire::ImagePatch::WIRE_SIZE
        {
            return None;
        }
        let end = cursor + batch.patch_count as usize * wire::ImagePatch::WIRE_SIZE;
        options.patch_bytes = bytes.get(cursor..end)?;
        options.patch_count = batch.patch_count;
        for index in 0..batch.patch_count {
            if !valid_patch(&options.patch(index)?, image) {
                return None;
            }
        }
        cursor = end;
    }
    (cursor == bytes.len()).then_some(options)
}
/// Canonical Texture shader vertex metadata, preserving signed cubic opacity.
pub(crate) fn image_vertex_color(image: &wire::ImageDraw, has_effect: bool) -> [f32; 4] {
    let premult = has_effect || image.flags & SCENE_IMAGE_SOURCE_PREMULTIPLIED != 0;
    [
        if premult { image.opacity } else { 1.0 },
        if premult { 1.0 } else { 0.0 },
        if image.flags & SCENE_IMAGE_SOURCE_ALPHA_IGNORE != 0 {
            -1.0
        } else if premult {
            image.opacity
        } else {
            1.0
        },
        if image.sampling == IMAGE_SAMPLING_CUBIC {
            -image.opacity
        } else {
            image.opacity
        },
    ]
}
#[derive(Clone, Copy, Debug)]
pub(crate) struct ImagePatchAttributes {
    pub color: [f32; 4],
    pub kind: f32,
    pub color_blend_mode: f32,
    pub opacity: f32,
}
pub(crate) fn image_patch_attributes(
    image: &wire::ImageDraw,
    patch: &wire::ImagePatch,
    has_effect: bool,
) -> ImagePatchAttributes {
    let premult = image.flags & SCENE_IMAGE_SOURCE_PREMULTIPLIED != 0;
    if patch.kind == SCENE_IMAGE_PATCH_FIXED_COLOR {
        let alpha = patch.color[3] * image.opacity;
        return ImagePatchAttributes {
            color: [
                if premult {
                    patch.color[0] * alpha
                } else {
                    patch.color[0]
                },
                if premult {
                    patch.color[1] * alpha
                } else {
                    patch.color[1]
                },
                if premult {
                    patch.color[2] * alpha
                } else {
                    patch.color[2]
                },
                alpha,
            ],
            kind: if premult { 2.0 } else { 1.0 },
            color_blend_mode: 0.0,
            opacity: 1.0,
        };
    }
    if patch.kind == SCENE_IMAGE_PATCH_ATLAS_COLOR {
        let alpha = patch.color[3];
        return ImagePatchAttributes {
            color: [
                patch.color[0] * alpha,
                patch.color[1] * alpha,
                patch.color[2] * alpha,
                alpha,
            ],
            kind: if premult { 4.0 } else { 3.0 },
            color_blend_mode: patch.color_blend_mode as f32,
            opacity: if image.sampling == IMAGE_SAMPLING_CUBIC {
                -image.opacity
            } else {
                image.opacity
            },
        };
    }
    ImagePatchAttributes {
        color: image_vertex_color(image, has_effect),
        kind: 0.0,
        color_blend_mode: 0.0,
        opacity: 1.0,
    }
}
