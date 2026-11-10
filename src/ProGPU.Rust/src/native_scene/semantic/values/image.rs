//! Original image/picture suffix admission and independent sampler policy.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct ImageSamplerOptions {
    pub mag_linear: bool,
    pub min_linear: bool,
    pub mip_linear: bool,
    pub max_anisotropy: u16,
}
pub(crate) fn image_sampler_options(
    sampling: u32,
    max_anisotropy: u32,
) -> Option<ImageSamplerOptions> {
    let anisotropy = if max_anisotropy == 0 {
        1
    } else {
        max_anisotropy
    };
    if anisotropy > 16 || (sampling != IMAGE_SAMPLING_LINEAR_MIPMAP && anisotropy != 1) {
        return None;
    }
    let mut value = ImageSamplerOptions {
        mag_linear: false,
        min_linear: false,
        mip_linear: false,
        max_anisotropy: anisotropy as u16,
    };
    match sampling {
        IMAGE_SAMPLING_NEAREST => {}
        IMAGE_SAMPLING_LINEAR
        | IMAGE_SAMPLING_CUBIC
        | IMAGE_SAMPLING_FANT
        | IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST => {
            value.mag_linear = true;
            value.min_linear = true;
        }
        IMAGE_SAMPLING_LINEAR_MIPMAP => {
            value.mag_linear = true;
            value.min_linear = true;
            value.mip_linear = true;
        }
        IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_LINEAR => {
            value.mag_linear = true;
            value.mip_linear = true;
        }
        IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_NEAREST => value.mag_linear = true,
        IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_LINEAR => {
            value.min_linear = true;
            value.mip_linear = true;
        }
        IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_NEAREST => value.min_linear = true,
        IMAGE_SAMPLING_MAG_NEAREST_MIN_NEAREST_MIP_LINEAR => value.mip_linear = true,
        _ => return None,
    }
    Some(value)
}
pub(crate) fn picture_image(
    picture: &wire::PictureImage,
    presentation: Option<&wire::Presentation>,
) -> bool {
    let has = picture.flags & SCENE_PICTURE_IMAGE_PRESENTATION != 0;
    if picture.flags & !SCENE_PICTURE_IMAGE_PRESENTATION != 0 || has != presentation.is_some() {
        return false;
    }
    if let Some(p) = presentation {
        if p.struct_size != wire::Presentation::WIRE_SIZE as u32
            || p.viewport_x != 0
            || p.viewport_y != 0
            || p.viewport_width != picture.width
            || p.viewport_height != picture.height
            || p.reserved != 0
            || !p.dpi_scale_x.is_finite()
            || p.dpi_scale_x <= 0.0
            || !p.dpi_scale_y.is_finite()
            || p.dpi_scale_y <= 0.0
            || !(picture.width as f32 / p.dpi_scale_x).is_finite()
            || !(picture.height as f32 / p.dpi_scale_y).is_finite()
        {
            return false;
        }
    }
    picture.struct_size == wire::PictureImage::WIRE_SIZE as u32
        && picture.width != 0
        && picture.height != 0
        && picture.width <= 16384
        && picture.height <= 16384
        && picture.dpi_scale.is_finite()
        && picture.dpi_scale > 0.0
        && finite(picture.clear_color)
        && [
            picture.clear_color.r,
            picture.clear_color.g,
            picture.clear_color.b,
            picture.clear_color.a,
        ]
        .into_iter()
        .all(|v| (0.0..=1.0).contains(&v))
        && picture.reserved == [0; 3]
        && u64::from(picture.width) * u64::from(picture.height) * 4
            <= u64::from(SCENE_MAX_LAYER_BYTES)
}
pub(crate) fn read_picture_image(
    payload: &[u8],
) -> Option<(wire::PictureImage, wire::Presentation)> {
    let picture = wire::PictureImage::read(payload)?;
    let has = picture.flags & SCENE_PICTURE_IMAGE_PRESENTATION != 0;
    if payload.len()
        != wire::PictureImage::WIRE_SIZE
            + if has {
                wire::Presentation::WIRE_SIZE
            } else {
                0
            }
    {
        return None;
    }
    let axes = if has {
        wire::Presentation::read(&payload[wire::PictureImage::WIRE_SIZE..])?
    } else {
        wire::Presentation {
            struct_size: wire::Presentation::WIRE_SIZE as u32,
            viewport_x: 0,
            viewport_y: 0,
            viewport_width: picture.width,
            viewport_height: picture.height,
            dpi_scale_x: picture.dpi_scale,
            dpi_scale_y: picture.dpi_scale,
            reserved: 0,
        }
    };
    picture_image(&picture, has.then_some(&axes)).then_some((picture, axes))
}
pub(crate) fn image(image: &wire::ImageDraw, pixel_bytes: u64, bytes_per_pixel: u32) -> bool {
    if bytes_per_pixel != 1 && bytes_per_pixel != 4 {
        return false;
    }
    let valid_rect = |r: wire::ImageRect| {
        [r.x, r.y, r.width, r.height]
            .into_iter()
            .all(f32::is_finite)
            && r.width > 0.0
            && r.height > 0.0
    };
    let row = u64::from(image.image_width) * u64::from(bytes_per_pixel);
    let required = if image.image_height == 0 {
        0
    } else {
        (u64::from(image.row_bytes) * u64::from(image.image_height - 1)).wrapping_add(row)
    };
    let known = SCENE_IMAGE_COLOR_MATRIX
        | SCENE_IMAGE_EFFECT
        | SCENE_IMAGE_SNAP_TO_PIXELS
        | SCENE_IMAGE_SOURCE_PREMULTIPLIED
        | SCENE_IMAGE_PATCH_BATCH
        | SCENE_IMAGE_ADDRESS_U_MASK
        | SCENE_IMAGE_ADDRESS_V_MASK
        | SCENE_IMAGE_EXTENDED_SOURCE_RECT
        | SCENE_IMAGE_SOURCE_ALPHA_IGNORE;
    let u = (image.flags & SCENE_IMAGE_ADDRESS_U_MASK) >> SCENE_IMAGE_ADDRESS_U_SHIFT;
    let v = (image.flags & SCENE_IMAGE_ADDRESS_V_MASK) >> SCENE_IMAGE_ADDRESS_V_SHIFT;
    let extended = image.flags & SCENE_IMAGE_EXTENDED_SOURCE_RECT != 0;
    let bounded = image.source_rect.x >= 0.0
        && image.source_rect.y >= 0.0
        && image.source_rect.x + image.source_rect.width <= image.image_width as f32
        && image.source_rect.y + image.source_rect.height <= image.image_height as f32;
    image.struct_size >= wire::ImageDraw::WIRE_SIZE as u32
        && image.flags & !known == 0
        && image.flags & known != known
        && u <= IMAGE_ADDRESS_MIRROR_REPEAT
        && v <= IMAGE_ADDRESS_MIRROR_REPEAT
        && ((u == 0 && v == 0) || extended)
        && (!extended || image.flags & SCENE_IMAGE_PATCH_BATCH == 0)
        && !(image.flags & SCENE_IMAGE_SOURCE_ALPHA_IGNORE != 0
            && image.flags & SCENE_IMAGE_PATCH_BATCH != 0)
        && image.image_width != 0
        && image.image_height != 0
        && image.image_width <= 16384
        && image.image_height <= 16384
        && u64::from(image.row_bytes) >= row
        && required <= pixel_bytes
        && image_sampler_options(image.sampling, image.max_anisotropy).is_some()
        && !(image.flags & SCENE_IMAGE_EFFECT != 0 && image.sampling == IMAGE_SAMPLING_CUBIC)
        && valid_rect(image.source_rect)
        && valid_rect(image.destination_rect)
        && (extended || bounded)
        && finite(image.transform)
        && image.opacity.is_finite()
        && image.opacity >= 0.0
        && image.opacity <= 1.0
}
pub(crate) fn image_sampling_options(options: &wire::ImageSamplingOptions) -> bool {
    options.struct_size == wire::ImageSamplingOptions::WIRE_SIZE as u32
        && options.flags == 0
        && options.cubic_b.is_finite()
        && options.cubic_c.is_finite()
        && options.cubic_b.abs() <= 16.0
        && options.cubic_c.abs() <= 16.0
}
pub(crate) fn image_color_matrix(matrix: &wire::ImageColorMatrix) -> bool {
    matrix.struct_size == wire::ImageColorMatrix::WIRE_SIZE as u32
        && matrix.flags & !SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA == 0
        && matrix.reserved == [0; 2]
        && [
            matrix.red,
            matrix.green,
            matrix.blue,
            matrix.alpha,
            matrix.offset,
        ]
        .into_iter()
        .flatten()
        .all(|v| v.is_finite() && v.abs() <= 1024.0)
}
pub(crate) fn image_effect(effect: &wire::ImageEffect) -> bool {
    effect.struct_size == wire::ImageEffect::WIRE_SIZE as u32
        && effect.flags & !SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR == 0
        && (effect.flags & SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR == 0 || effect.flags0[0] == 1.0)
        && effect.reserved0 == 0
        && effect.reserved1 == 0
        && [
            effect.color_matrix_red,
            effect.color_matrix_green,
            effect.color_matrix_blue,
            effect.color_matrix_alpha,
            effect.color_matrix_offset,
            effect.effects0,
            effect.effects1,
            effect.texture0,
            effect.flags0,
            effect.yuv_range,
            effect.yuv_red,
            effect.yuv_green,
            effect.yuv_blue,
            effect.spherical0,
            effect.spherical_uv_rect,
            effect.spherical_rotation0,
            effect.spherical_rotation1,
            effect.spherical_rotation2,
        ]
        .into_iter()
        .flatten()
        .all(f32::is_finite)
        && effect.effects1[2] >= 0.0
        && effect.effects1[2] <= 32.0
        && binary_flag(effect.effects1[3])
        && effect.flags0.into_iter().all(binary_flag)
        && effect.texture0[0] > 0.0
        && effect.texture0[1] > 0.0
        && effect.texture0[2] == 0.0
        && effect.texture0[3] == 0.0
        && binary_flag(effect.spherical0[0])
}
