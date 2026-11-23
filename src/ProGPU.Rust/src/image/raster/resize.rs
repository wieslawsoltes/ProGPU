//! Original CPU bitmap center sampling, retained premultiplication and rounding.
use super::{
    Bitmap, ImageInfo, PixelAlphaType as A, PixelColorType as C, Pixmap, RasterError as E,
    RasterExecution as X, color,
};
use super::{RasterFilterMode, RasterSampling as FilterSampling};
use wide::f32x4 as V;

pub(super) fn resize(
    source: &Pixmap<'_>,
    info: ImageInfo,
    sampling: FilterSampling,
    execution: X,
) -> Result<Bitmap, E> {
    if source.pixels.is_empty()
        || source.info.is_empty()
        || info.is_empty()
        || !source.info.color_type.supports_resize()
        || !info.color_type.supports_resize()
        || (sampling
            .cubic
            .is_some_and(|v| v.iter().any(|c| !c.is_finite())))
    {
        return Err(E::UnsupportedFormat);
    }
    let mut result = Bitmap::tight(info.clone())?;
    let target_stride = result.row_bytes() as usize;
    let sx = source.info.width as f32 / info.width as f32;
    let sy = source.info.height as f32 / info.height as f32;
    let mut target = result.pixels_mut()?;
    let source_premul = source.info.alpha_type == A::Premultiplied;
    let nearest = sampling.cubic.is_none()
        && sampling.max_anisotropy == 0
        && sampling.filter != RasterFilterMode::Linear;
    if nearest {
        let copy = source.info.color_type == info.color_type
            && source.info.alpha_type == info.alpha_type
            && (source.info.alpha_type != A::Opaque || source.info.color_type == C::Rgb565);
        for y in 0..info.height {
            let source_y =
                ((((y as f32 + 0.5) * sy) - 1.0).ceil() as i32).clamp(0, source.info.height - 1);
            for x in 0..info.width {
                let source_x =
                    ((((x as f32 + 0.5) * sx) - 1.0).ceil() as i32).clamp(0, source.info.width - 1);
                let input = source.pixel_span(source_x, source_y)?;
                let at = y as usize * target_stride + x as usize * info.bytes_per_pixel() as usize;
                let output = &mut target[at..at + info.bytes_per_pixel() as usize];
                if copy {
                    output.copy_from_slice(&input[..output.len()]);
                } else {
                    write(
                        output,
                        &info,
                        read(input, &source.info, execution),
                        source_premul,
                        execution,
                    );
                }
            }
        }
    } else {
        let count = source
            .info
            .width
            .checked_mul(source.info.height)
            .and_then(|v| usize::try_from(v).ok())
            .ok_or(E::Capacity)?;
        let mut input = Vec::new();
        input.try_reserve_exact(count).map_err(|_| E::Capacity)?;
        for y in 0..source.info.height {
            for x in 0..source.info.width {
                input.push(read(source.pixel_span(x, y)?, &source.info, execution));
            }
        }
        for y in 0..info.height {
            let source_y = ((y as f32 + 0.5) * sy) - 0.5;
            for x in 0..info.width {
                let source_x = ((x as f32 + 0.5) * sx) - 0.5;
                let color = sample(
                    &input,
                    source.info.size(),
                    [source_x, source_y],
                    sampling,
                    execution,
                );
                let at = y as usize * target_stride + x as usize * info.bytes_per_pixel() as usize;
                write(&mut target[at..], &info, color, source_premul, execution);
            }
        }
    }
    drop(target);
    Ok(result)
}
fn read(p: &[u8], info: &ImageInfo, execution: X) -> [f32; 4] {
    if info.color_type == C::Rgb565 {
        let v = u16::from_le_bytes([p[0], p[1]]);
        let values = [
            f32::from(v >> 11 & 31),
            f32::from(v >> 5 & 63),
            f32::from(v & 31),
            1.0,
        ];
        return if execution == X::CpuSimd {
            (V::from(values) / V::from([31.0, 63.0, 31.0, 1.0])).to_array()
        } else {
            [values[0] / 31.0, values[1] / 63.0, values[2] / 31.0, 1.0]
        };
    }
    let alpha = if info.alpha_type == A::Opaque || info.color_type == C::Rgb888x {
        255
    } else {
        p[3]
    };
    let values = if info.color_type == C::Bgra8888 {
        [p[2], p[1], p[0], alpha]
    } else {
        [p[0], p[1], p[2], alpha]
    }
    .map(f32::from);
    if execution == X::CpuSimd {
        (V::from(values) / V::splat(255.0)).to_array()
    } else {
        values.map(|v| v / 255.0)
    }
}
fn get(source: &[[f32; 4]], [width, height]: [i32; 2], x: i32, y: i32) -> [f32; 4] {
    source[y.clamp(0, height - 1) as usize * width as usize + x.clamp(0, width - 1) as usize]
}
fn lerp(a: [f32; 4], b: [f32; 4], t: f32, execution: X) -> [f32; 4] {
    if execution == X::CpuSimd {
        (V::from(a) * V::splat(1.0 - t) + V::from(b) * V::splat(t)).to_array()
    } else {
        std::array::from_fn(|i| a[i] * (1.0 - t) + b[i] * t)
    }
}
fn sample(
    source: &[[f32; 4]],
    size: [i32; 2],
    [x, y]: [f32; 2],
    sampling: FilterSampling,
    execution: X,
) -> [f32; 4] {
    let bx = x.floor() as i32;
    let by = y.floor() as i32;
    let fx = x - bx as f32;
    let fy = y - by as f32;
    if let Some(cubic) = sampling.cubic {
        let mut color = [0.0; 4];
        let mut total = 0.0;
        for ty in -1..=2 {
            let wy = weight(fy - ty as f32, cubic);
            for tx in -1..=2 {
                let w = weight(fx - tx as f32, cubic) * wy;
                let pixel = get(source, size, bx + tx, by + ty);
                color = if execution == X::CpuSimd {
                    (V::from(color) + V::from(pixel) * V::splat(w)).to_array()
                } else {
                    std::array::from_fn(|i| color[i] + pixel[i] * w)
                };
                total += w;
            }
        }
        if total.abs() > 0.0001 {
            if execution == X::CpuSimd {
                (V::from(color) / V::splat(total)).to_array()
            } else {
                color.map(|v| v / total)
            }
        } else {
            get(source, size, bx, by)
        }
    } else {
        let top = lerp(
            get(source, size, bx, by),
            get(source, size, bx + 1, by),
            fx,
            execution,
        );
        let bottom = lerp(
            get(source, size, bx, by + 1),
            get(source, size, bx + 1, by + 1),
            fx,
            execution,
        );
        lerp(top, bottom, fy, execution)
    }
}
fn weight(value: f32, [b, c]: [f32; 2]) -> f32 {
    let x = value.abs();
    let x2 = x * x;
    let x3 = x2 * x;
    if b == 0.0 && c == 0.5 {
        let a = -0.5;
        if x <= 1.0 {
            ((a + 2.0) * x3) - ((a + 3.0) * x2) + 1.0
        } else if x < 2.0 {
            (a * x3) - (5.0 * a * x2) + (8.0 * a * x) - (4.0 * a)
        } else {
            0.0
        }
    } else if x <= 1.0 {
        ((12.0 - 9.0 * b - 6.0 * c) * x3 + (-18.0 + 12.0 * b + 6.0 * c) * x2 + (6.0 - 2.0 * b))
            / 6.0
    } else if x < 2.0 {
        ((-b - 6.0 * c) * x3
            + (6.0 * b + 30.0 * c) * x2
            + (-12.0 * b - 48.0 * c) * x
            + (8.0 * b + 24.0 * c))
            / 6.0
    } else {
        0.0
    }
}
fn write(
    destination: &mut [u8],
    info: &ImageInfo,
    color: [f32; 4],
    source_premul: bool,
    execution: X,
) {
    let alpha = color[3].clamp(0.0, 1.0);
    let has_alpha =
        matches!(info.color_type, C::Rgba8888 | C::Bgra8888) && info.alpha_type != A::Opaque;
    let target_premul = has_alpha && info.alpha_type == A::Premultiplied;
    let mut rgb = [color[0], color[1], color[2], 0.0];
    if source_premul && !target_premul {
        rgb = if alpha > 0.0 {
            if execution == X::CpuSimd {
                (V::from(rgb) / V::splat(alpha)).to_array()
            } else {
                rgb.map(|v| v / alpha)
            }
        } else {
            [0.0; 4]
        };
    } else if !source_premul && target_premul {
        rgb = if execution == X::CpuSimd {
            (V::from(rgb) * V::splat(alpha)).to_array()
        } else {
            rgb.map(|v| v * alpha)
        };
    }
    let byte = |v: f32| ((v.clamp(0.0, 1.0) * 255.0 + 0.5).floor()).clamp(0.0, 255.0) as u8;
    let [r, g, b, _] = rgb.map(byte);
    let a = if has_alpha { byte(alpha) } else { 255 };
    if info.color_type == C::Rgb565 {
        destination[..2].copy_from_slice(&color::pack565([r, g, b], execution));
    } else {
        destination[..4].copy_from_slice(&if info.color_type == C::Bgra8888 {
            [b, g, r, a]
        } else {
            [r, g, b, a]
        });
    }
}
