//! Exact original surface upload/readback conversion, independent of Pixmap's
//! general conversion rules. SIMD lanes are original independent RGBA components.
use super::*;
fn admit(info: &ImageInfo) -> Result<(), ImageResourceError> {
    if info.color_type != PixelColorType::Rgb565 && info.bytes_per_pixel() < 4 {
        return Err(super::super::RasterError::UnsupportedFormat.into());
    }
    Ok(())
}
fn premultiply(mut color: [u8; 4], execution: RasterExecution) -> [u8; 4] {
    let alpha = u32::from(color[3]);
    if execution == RasterExecution::CpuSimd {
        let v = wide::f64x4::from(color.map(f64::from));
        let out = ((v * wide::f64x4::splat(f64::from(alpha)) + wide::f64x4::splat(127.0))
            / wide::f64x4::splat(255.0))
        .floor()
        .to_array();
        for i in 0..3 {
            color[i] = out[i] as u8;
        }
    } else {
        for c in &mut color[..3] {
            *c = ((u32::from(*c) * alpha + 127) / 255) as u8;
        }
    }
    color
}
fn unpremultiply(mut color: [u8; 4], execution: RasterExecution) -> [u8; 4] {
    let alpha = u32::from(color[3]);
    if alpha == 0 {
        return [0; 4];
    }
    if execution == RasterExecution::CpuSimd {
        let v = wide::f64x4::from(color.map(f64::from));
        let out = ((v * wide::f64x4::splat(255.0) + wide::f64x4::splat(f64::from(alpha / 2)))
            / wide::f64x4::splat(f64::from(alpha)))
        .floor()
        .to_array();
        for i in 0..3 {
            color[i] = out[i].min(255.0) as u8;
        }
    } else {
        for c in &mut color[..3] {
            *c = ((u32::from(*c) * 255 + alpha / 2) / alpha).min(255) as u8;
        }
    }
    color
}
pub(super) fn upload_pixels(
    info: &ImageInfo,
    stride: i32,
    source: &[u8],
    execution: RasterExecution,
) -> Result<Vec<u8>, ImageResourceError> {
    admit(info)?;
    Pixmap::new(info.clone(), stride, source)?;
    let mut out = super::super::raster::zeros(texture::surface_byte_count([
        info.width as u32,
        info.height as u32,
    ])?)?;
    for y in 0..info.height as usize {
        for x in 0..info.width as usize {
            let mut color = if info.color_type == PixelColorType::Rgb565 {
                let i = y * stride as usize + x * 2;
                let p = u16::from_le_bytes([source[i], source[i + 1]]);
                if execution == RasterExecution::CpuSimd {
                    let values = wide::f64x4::from([
                        f64::from((p >> 11) & 31) * 255.0,
                        f64::from((p >> 5) & 63) * 255.0,
                        f64::from(p & 31) * 255.0,
                        255.0,
                    ]);
                    (values / wide::f64x4::from([31.0, 63.0, 31.0, 1.0]))
                        .floor()
                        .to_array()
                        .map(|v| v as u8)
                } else {
                    [
                        (((p >> 11) & 31) * 255 / 31) as u8,
                        (((p >> 5) & 63) * 255 / 63) as u8,
                        ((p & 31) * 255 / 31) as u8,
                        255,
                    ]
                }
            } else {
                let i = y * stride as usize + x * 4;
                let mut c: [u8; 4] = source[i..i + 4].try_into().unwrap();
                if info.color_type == PixelColorType::Bgra8888 {
                    c.swap(0, 2);
                }
                c
            };
            match info.alpha_type {
                PixelAlphaType::Opaque => color[3] = 255,
                PixelAlphaType::Straight => color = premultiply(color, execution),
                _ => {}
            }
            let i = (y * info.width as usize + x) * 4;
            out[i..i + 4].copy_from_slice(&color);
        }
    }
    Ok(out)
}
pub(super) fn copy_readback(
    info: &ImageInfo,
    stride: i32,
    destination: &mut [u8],
    readback: &texture::TextureReadback,
    regions: Option<&[[f32; 4]]>,
    execution: RasterExecution,
) -> Result<(), ImageResourceError> {
    admit(info)?;
    PixmapMut::new(info.clone(), stride, destination)?;
    let source = readback.pixels()?;
    let source_alpha = readback.info().alpha_type;
    let full = [[0.0, 0.0, info.width as f32, info.height as f32]];
    for region in regions.unwrap_or(&full) {
        let left = (region[0].floor() as i32).clamp(0, info.width);
        let top = (region[1].floor() as i32).clamp(0, info.height);
        let right = (region[2].ceil() as i32).clamp(left, info.width);
        let bottom = (region[3].ceil() as i32).clamp(top, info.height);
        for y in top as usize..bottom as usize {
            for x in left as usize..right as usize {
                let i = y * readback.row_bytes() as usize + x * 4;
                let mut color: [u8; 4] = source[i..i + 4].try_into().unwrap();
                // This is the source surface routine's original RGBA interpretation;
                // native-order generic ReadPixels remains a separate Pixmap path.
                if source_alpha == PixelAlphaType::Premultiplied
                    && matches!(
                        info.alpha_type,
                        PixelAlphaType::Straight | PixelAlphaType::Opaque
                    )
                {
                    color = unpremultiply(color, execution);
                } else if source_alpha == PixelAlphaType::Straight
                    && info.alpha_type == PixelAlphaType::Premultiplied
                {
                    color = premultiply(color, execution);
                }
                if info.alpha_type == PixelAlphaType::Opaque {
                    color[3] = 255;
                }
                if info.color_type == PixelColorType::Rgb565 {
                    let channels = if execution == RasterExecution::CpuSimd {
                        ((wide::f64x4::from(color.map(f64::from))
                            * wide::f64x4::from([31.0, 63.0, 31.0, 0.0])
                            + wide::f64x4::splat(127.0))
                            / wide::f64x4::splat(255.0))
                        .floor()
                        .to_array()
                        .map(|v| v as u16)
                    } else {
                        [
                            (u16::from(color[0]) * 31 + 127) / 255,
                            (u16::from(color[1]) * 63 + 127) / 255,
                            (u16::from(color[2]) * 31 + 127) / 255,
                            0,
                        ]
                    };
                    let p = (channels[0] << 11) | (channels[1] << 5) | channels[2];
                    let i = y * stride as usize + x * 2;
                    destination[i..i + 2].copy_from_slice(&p.to_le_bytes());
                } else {
                    if info.color_type == PixelColorType::Bgra8888 {
                        color.swap(0, 2);
                    }
                    let i = y * stride as usize + x * 4;
                    destination[i..i + 4].copy_from_slice(&color);
                }
            }
        }
    }
    Ok(())
}
