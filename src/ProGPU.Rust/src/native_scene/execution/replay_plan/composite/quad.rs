//! Original geometry_base.hpp occupied-page record writer. The actual texture
//! generation is owned by the outer replay plan, not fabricated as an ImageSource.
use super::*;
use bytemuck::Zeroable;
pub(super) fn tile(
    output: [f32; 4],
    transform: wire::Affine2d,
    occupied: [u32; 2],
    texture: [u32; 2],
    address: [u32; 2],
    sampling: u32,
    opacity: f32,
) -> Result<[VectorVertex; 4], Error> {
    if occupied.contains(&0)
        || (0..2).any(|i| occupied[i] > texture[i] || occupied[i] > 1 << 24)
        || address.into_iter().any(|a| a > IMAGE_ADDRESS_MIRROR_REPEAT)
        || (sampling > IMAGE_SAMPLING_LINEAR && sampling != IMAGE_SAMPLING_FANT)
        || !opacity.is_finite()
        || !(0.0..=1.0).contains(&opacity)
        || ![
            transform.m11,
            transform.m12,
            transform.m21,
            transform.m22,
            transform.m31,
            transform.m32,
        ]
        .into_iter()
        .all(f32::is_finite)
        || !output.into_iter().all(f32::is_finite)
        || output[2] <= 0.0
        || output[3] <= 0.0
    {
        return Err(Error::InvalidInput);
    }
    let mut result = [VectorVertex::zeroed(); 4];
    for (v, corner) in result
        .iter_mut()
        .zip([[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]])
    {
        v.position = [
            output[0] + corner[0] * output[2],
            output[1] + corner[1] * output[3],
        ];
        v.texture_coordinate = [
            v.position[0] * transform.m11 + v.position[1] * transform.m21 + transform.m31,
            v.position[0] * transform.m12 + v.position[1] * transform.m22 + transform.m32,
        ];
        if !v
            .position
            .into_iter()
            .chain(v.texture_coordinate)
            .all(f32::is_finite)
        {
            return Err(Error::InvalidInput);
        }
        v.color = [occupied[0] as f32, occupied[1] as f32, 0.0, opacity];
        v.brush_index = -2.0;
        v.shape_size = [
            if sampling == IMAGE_SAMPLING_NEAREST {
                -128.0
            } else if sampling == IMAGE_SAMPLING_FANT {
                -32.0
            } else {
                -64.0
            },
            0.0,
        ];
        v.corner_radius = address[0] as f32;
        v.stroke_thickness = address[1] as f32;
    }
    Ok(result)
}
pub(super) fn captured(
    destination: &mut [VectorVertex; 4],
    occupied: [u32; 2],
    texture: [u32; 2],
    sampling: u32,
    opacity: f32,
) -> Result<(), Error> {
    let mut quad = tile(
        [0.0, 0.0, 1.0, 1.0],
        identity_transform(),
        occupied,
        texture,
        [IMAGE_ADDRESS_CLAMP; 2],
        sampling,
        opacity,
    )?;
    for (dst, src) in quad.iter_mut().zip(destination.iter()) {
        if !src.position.into_iter().all(f32::is_finite) {
            return Err(Error::InvalidInput);
        }
        dst.position = src.position;
    }
    *destination = quad;
    Ok(())
}
