//! Original byte-domain conversion, preserving each producer's alpha policy.
use super::{
    ImageInfo, PixelAlphaType as A, PixelColorType as C, RasterError as E, RasterExecution as X,
};
use wide::{f64x4 as F, u32x4 as U};
fn divide(values: [u32; 4], divisors: [u32; 4], execution: X) -> [u32; 4] {
    if execution == X::CpuSimd {
        // All inputs here fit exactly in f64 and the result is <=65535. The
        // integer quotient is therefore preserved by original-order floor.
        (F::from(values.map(f64::from)) / F::from(divisors.map(f64::from)))
            .floor()
            .to_array()
            .map(|v| v as u32)
    } else {
        std::array::from_fn(|i| values[i] / divisors[i])
    }
}
pub(super) fn premultiply(rgb: [u8; 3], alpha: u8, execution: X) -> [u8; 3] {
    if execution == X::CpuSimd {
        let n = U::from([u32::from(rgb[0]), u32::from(rgb[1]), u32::from(rgb[2]), 0])
            * U::splat(u32::from(alpha))
            + U::splat(127);
        let q = ((n + U::splat(1) + (n >> 8)) >> 8).to_array();
        [q[0] as u8, q[1] as u8, q[2] as u8]
    } else {
        rgb.map(|v| ((u32::from(v) * u32::from(alpha) + 127) / 255) as u8)
    }
}
pub(super) fn unpremultiply(rgb: [u8; 3], alpha: u8, execution: X) -> [u8; 3] {
    if alpha == 0 {
        return [0; 3];
    }
    let values = [rgb[0], rgb[1], rgb[2], 0].map(|v| u32::from(v) * 255 + u32::from(alpha) / 2);
    let q = divide(values, [u32::from(alpha); 4], execution);
    [
        q[0].min(255) as u8,
        q[1].min(255) as u8,
        q[2].min(255) as u8,
    ]
}
pub(super) fn unpack565(bytes: &[u8], execution: X) -> [u8; 4] {
    let p = u32::from(u16::from_le_bytes([bytes[0], bytes[1]]));
    let q = divide(
        [
            ((p >> 11) & 31) * 255,
            ((p >> 5) & 63) * 255,
            (p & 31) * 255,
            255,
        ],
        [31, 63, 31, 1],
        execution,
    );
    q.map(|v| v as u8)
}
pub(super) fn pack565(rgb: [u8; 3], execution: X) -> [u8; 2] {
    let values = if execution == X::CpuSimd {
        (U::from([u32::from(rgb[0]), u32::from(rgb[1]), u32::from(rgb[2]), 0])
            * U::from([31, 63, 31, 0])
            + U::splat(127))
        .to_array()
    } else {
        [
            u32::from(rgb[0]) * 31 + 127,
            u32::from(rgb[1]) * 63 + 127,
            u32::from(rgb[2]) * 31 + 127,
            0,
        ]
    };
    let q = divide(values, [255; 4], execution);
    ((q[0] << 11 | q[1] << 5 | q[2]) as u16).to_le_bytes()
}
pub(super) fn read(pixel: &[u8], info: &ImageInfo, execution: X) -> Result<[u8; 4], E> {
    if !info.color_type.supports_color() {
        return Err(E::UnsupportedFormat);
    }
    let p = pixel
        .get(..info.bytes_per_pixel() as usize)
        .ok_or(E::InsufficientPixels)?;
    let mut c = match info.color_type {
        C::Rgba8888 | C::Srgba8888 => [p[0], p[1], p[2], p[3]],
        C::Bgra8888 => [p[2], p[1], p[0], p[3]],
        C::Rgb888x => [p[0], p[1], p[2], 255],
        C::Rgb565 => unpack565(p, execution),
        C::Argb4444 => {
            let v = u16::from_le_bytes([p[0], p[1]]);
            [
                (v >> 8 & 15) as u8 * 17,
                (v >> 4 & 15) as u8 * 17,
                (v & 15) as u8 * 17,
                (v >> 12) as u8 * 17,
            ]
        }
        C::Alpha8 => [0, 0, 0, p[0]],
        C::Gray8 => [p[0], p[0], p[0], 255],
        C::R8Unorm => [p[0], 0, 0, 255],
        C::Rg88 => [p[0], p[1], 0, 255],
        _ => return Err(E::UnsupportedFormat),
    };
    if info.alpha_type == A::Opaque {
        c[3] = 255;
    } else if info.alpha_type == A::Premultiplied && c[3] > 0 && c[3] < 255 {
        let rgb = unpremultiply([c[0], c[1], c[2]], c[3], execution);
        c[..3].copy_from_slice(&rgb);
    }
    Ok(c)
}
pub(super) fn write(
    pixel: &mut [u8],
    info: &ImageInfo,
    color: [u8; 4],
    execution: X,
) -> Result<(), E> {
    if !info.color_type.supports_color() {
        return Err(E::UnsupportedFormat);
    }
    let p = pixel
        .get_mut(..info.bytes_per_pixel() as usize)
        .ok_or(E::InsufficientPixels)?;
    let alpha = if info.alpha_type == A::Opaque {
        255
    } else {
        color[3]
    };
    let original = [color[0], color[1], color[2]];
    let rgb = if info.alpha_type == A::Premultiplied {
        premultiply(original, alpha, execution)
    } else {
        original
    };
    match info.color_type {
        C::Rgba8888 | C::Srgba8888 => p.copy_from_slice(&[rgb[0], rgb[1], rgb[2], alpha]),
        C::Bgra8888 => p.copy_from_slice(&[rgb[2], rgb[1], rgb[0], alpha]),
        C::Rgb888x => p.copy_from_slice(&[color[0], color[1], color[2], 255]),
        C::Rgb565 => p.copy_from_slice(&pack565(original, execution)),
        C::Argb4444 => p.copy_from_slice(
            &((u16::from(alpha >> 4) << 12)
                | (u16::from(rgb[0] >> 4) << 8)
                | (u16::from(rgb[1] >> 4) << 4)
                | u16::from(rgb[2] >> 4))
            .to_le_bytes(),
        ),
        C::Alpha8 => p[0] = alpha,
        C::Gray8 => {
            p[0] = ((u32::from(color[0]) * 54
                + u32::from(color[1]) * 183
                + u32::from(color[2]) * 19
                + 128)
                >> 8) as u8
        }
        C::R8Unorm => p[0] = color[0],
        C::Rg88 => p.copy_from_slice(&color[..2]),
        _ => return Err(E::UnsupportedFormat),
    }
    Ok(())
}
pub(super) fn legacy_read(pixel: &[u8], info: &ImageInfo, execution: X) -> Result<[u8; 4], E> {
    if info.color_type == C::Rgb565 {
        return Ok(unpack565(
            pixel.get(..2).ok_or(E::InsufficientPixels)?,
            execution,
        ));
    }
    if info.bytes_per_pixel() < 4 {
        return Err(E::UnsupportedFormat);
    }
    let p = pixel.get(..4).ok_or(E::InsufficientPixels)?;
    let mut c = if info.color_type == C::Bgra8888 {
        [p[2], p[1], p[0], p[3]]
    } else {
        [p[0], p[1], p[2], p[3]]
    };
    // SKBitmap.GetPixel does not force opaque metadata into stored alpha.
    if info.alpha_type == A::Premultiplied && c[3] > 0 && c[3] < 255 {
        let rgb = unpremultiply([c[0], c[1], c[2]], c[3], execution);
        c[..3].copy_from_slice(&rgb);
    }
    Ok(c)
}
pub(super) fn legacy_write(
    pixel: &mut [u8],
    info: &ImageInfo,
    color: [u8; 4],
    execution: X,
    erase: bool,
) -> Result<(), E> {
    let alpha = if !erase && info.alpha_type == A::Opaque {
        255
    } else {
        color[3]
    };
    let rgb = if info.alpha_type == A::Premultiplied {
        premultiply([color[0], color[1], color[2]], alpha, execution)
    } else {
        [color[0], color[1], color[2]]
    };
    if info.color_type == C::Rgb565 {
        pixel
            .get_mut(..2)
            .ok_or(E::InsufficientPixels)?
            .copy_from_slice(&pack565(rgb, execution));
        return Ok(());
    }
    if info.bytes_per_pixel() < 4 {
        return Err(E::UnsupportedFormat);
    }
    let p = pixel.get_mut(..4).ok_or(E::InsufficientPixels)?;
    p.copy_from_slice(&if info.color_type == C::Bgra8888 {
        [rgb[2], rgb[1], rgb[0], alpha]
    } else {
        [rgb[0], rgb[1], rgb[2], alpha]
    });
    Ok(())
}
pub(super) fn float_byte(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0)
        .round_ties_even()
        .clamp(0.0, 255.0) as u8
}
