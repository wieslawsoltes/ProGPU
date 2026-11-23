//! Original managed single-color evaluation for brush colors and gradient stops.
//! Pixel buffers remain GPU work; independent RGBA arithmetic uses intrinsic lanes.
use super::*;
use crate::scene::LayerBlend;
use wide::f32x4 as V;
pub(super) fn to_byte(value: f32) -> u8 {
    (value.clamp(0.0, 1.0) * 255.0)
        .round_ties_even()
        .clamp(0.0, 255.0) as u8
}
pub(super) fn srgb_to_linear(v: f32) -> f32 {
    if v <= 0.04045 {
        v / 12.92
    } else {
        ((v + 0.055) / 1.055).powf(2.4)
    }
}
pub(super) fn linear_to_srgb(v: f32) -> f32 {
    if v <= 0.0031308 {
        v * 12.92
    } else {
        1.055 * v.powf(1.0 / 2.4) - 0.055
    }
}
fn premultiply(color: [u8; 4]) -> V {
    let alpha = f32::from(color[3]) / 255.0;
    V::from([
        f32::from(color[0]) / 255.0 * alpha,
        f32::from(color[1]) / 255.0 * alpha,
        f32::from(color[2]) / 255.0 * alpha,
        alpha,
    ])
}
fn from_premultiplied(color: V) -> [u8; 4] {
    let color = color.to_array();
    let alpha = color[3].clamp(0.0, 1.0);
    if alpha <= 0.0 {
        return [0; 4];
    }
    [color[0] / alpha, color[1] / alpha, color[2] / alpha, alpha].map(to_byte)
}
fn matrix(source: [f32; 4], m: ImageColorMatrix) -> [f32; 4] {
    let rows = [m.red, m.green, m.blue, m.alpha];
    let column = |i| V::from(rows.map(|row| row[i]));
    // Four output channels are independent; additions retain source order.
    (V::splat(source[0]) * column(0)
        + V::splat(source[1]) * column(1)
        + V::splat(source[2]) * column(2)
        + V::splat(source[3]) * column(3)
        + V::from(m.offset))
    .to_array()
}
pub(super) fn apply(filter: &ColorFilter, source: [u8; 4]) -> [u8; 4] {
    match filter.kind() {
        // Original runtime instances have no matrix/table/luma or retained
        // nonlinear policy, so SKColorFilter.Apply reaches default Clear.
        ColorFilterKind::Runtime(_) => [0; 4],
        ColorFilterKind::Compose { outer, inner } => apply(outer, apply(inner, source)),
        ColorFilterKind::Lerp {
            weight,
            filter0,
            filter1,
        } => {
            let a = premultiply(apply(filter0, source));
            let b = premultiply(apply(filter1, source));
            from_premultiplied(a * V::splat(1.0 - weight) + b * V::splat(*weight))
        }
        ColorFilterKind::Matrix(m) => matrix(source.map(|v| f32::from(v) / 255.0), *m).map(to_byte),
        ColorFilterKind::Luma => [
            0,
            0,
            0,
            to_byte(
                (f32::from(source[0]) / 255.0 * 0.2126
                    + f32::from(source[1]) / 255.0 * 0.7152
                    + f32::from(source[2]) / 255.0 * 0.0722)
                    * (f32::from(source[3]) / 255.0),
            ),
        ],
        ColorFilterKind::Table(t) => {
            let [a, r, g, b] = t.views();
            [
                r[usize::from(source[0])],
                g[usize::from(source[1])],
                b[usize::from(source[2])],
                a[usize::from(source[3])],
            ]
        }
        ColorFilterKind::Blend { color, mode } => blend(*color, source, *mode),
        ColorFilterKind::Overdraw(colors) => {
            if source[3] == 0 {
                [0; 4]
            } else {
                colors[usize::from(source[3] - 1).min(5)]
            }
        }
        ColorFilterKind::Hsla(m) => {
            let straight = source.map(|v| f32::from(v) / 255.0);
            let hsl = rgb_to_hsl([straight[0], straight[1], straight[2]]);
            let transformed = matrix([hsl[0], hsl[1], hsl[2], straight[3]], *m);
            let rgb = hsl_to_rgb([transformed[0], transformed[1], transformed[2]]);
            [rgb[0], rgb[1], rgb[2], transformed[3]].map(to_byte)
        }
        ColorFilterKind::HighContrast(c) => {
            let mut rgb =
                [source[0], source[1], source[2]].map(|v| srgb_to_linear(f32::from(v) / 255.0));
            if c.grayscale {
                rgb = [rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722; 3];
            }
            match c.invert {
                InvertStyle::None => {}
                InvertStyle::Brightness => rgb = rgb.map(|v| 1.0 - v),
                InvertStyle::Lightness => {
                    let mut hsl = rgb_to_hsl(rgb);
                    hsl[2] = 1.0 - hsl[2];
                    rgb = hsl_to_rgb(hsl);
                }
            }
            let contrast = c.contrast.clamp(-1.0 + f32::EPSILON, 1.0 - f32::EPSILON);
            let scale = (1.0 + contrast) / (1.0 - contrast);
            rgb = rgb.map(|v| linear_to_srgb((0.5 + (v - 0.5) * scale).clamp(0.0, 1.0)));
            [to_byte(rgb[0]), to_byte(rgb[1]), to_byte(rgb[2]), source[3]]
        }
    }
}
fn rgb_to_hsl(c: [f32; 3]) -> [f32; 3] {
    let max = c[0].max(c[1].max(c[2]));
    let min = c[0].min(c[1].min(c[2]));
    let delta = max - min;
    let light = (max + min) * 0.5;
    if delta <= 0.000001 {
        return [0.0, 0.0, light];
    }
    let saturation = delta / (1.0 - (2.0 * light - 1.0).abs()).max(0.000001);
    let hue = if max == c[0] {
        (c[1] - c[2]) / delta + if c[1] < c[2] { 6.0 } else { 0.0 }
    } else if max == c[1] {
        (c[2] - c[0]) / delta + 2.0
    } else {
        (c[0] - c[1]) / delta + 4.0
    };
    [hue / 6.0, saturation, light]
}
fn hsl_to_rgb(hsl: [f32; 3]) -> [f32; 3] {
    if hsl[1].abs() <= 0.000001 {
        return [hsl[2]; 3];
    }
    let q = if hsl[2] < 0.5 {
        hsl[2] * (1.0 + hsl[1])
    } else {
        hsl[2] + hsl[1] - hsl[2] * hsl[1]
    };
    let p = 2.0 * hsl[2] - q;
    [hsl[0] + 1.0 / 3.0, hsl[0], hsl[0] - 1.0 / 3.0].map(|hue| {
        let hue = hue - hue.floor();
        if hue < 1.0 / 6.0 {
            p + (q - p) * 6.0 * hue
        } else if hue < 0.5 {
            q
        } else if hue < 2.0 / 3.0 {
            p + (q - p) * (2.0 / 3.0 - hue) * 6.0
        } else {
            p
        }
    })
}
fn luminosity(c: [f32; 3]) -> f32 {
    0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2]
}
fn saturation(c: [f32; 3]) -> f32 {
    c[0].max(c[1].max(c[2])) - c[0].min(c[1].min(c[2]))
}
fn set_saturation(c: [f32; 3], value: f32) -> [f32; 3] {
    let minimum = c[0].min(c[1].min(c[2]));
    let maximum = c[0].max(c[1].max(c[2]));
    if maximum <= minimum {
        [0.0; 3]
    } else {
        c.map(|v| (v - minimum) * (value / (maximum - minimum)))
    }
}
fn set_luminosity(c: [f32; 3], value: f32) -> [f32; 3] {
    let delta = value - luminosity(c);
    clip_color(c.map(|v| v + delta))
}
fn clip_color(mut c: [f32; 3]) -> [f32; 3] {
    let lum = luminosity(c);
    let min = c[0].min(c[1].min(c[2]));
    if min < 0.0 {
        c = c.map(|v| lum + (v - lum) * (lum / (lum - min)));
    }
    let max = c[0].max(c[1].max(c[2]));
    if max > 1.0 {
        c = c.map(|v| lum + (v - lum) * ((1.0 - lum) / (max - lum)));
    }
    c.map(|v| v.clamp(0.0, 1.0))
}
fn component(mode: LayerBlend, s: f32, d: f32) -> f32 {
    match mode {
        LayerBlend::Multiply => s * d,
        LayerBlend::Screen => s + d - s * d,
        LayerBlend::Overlay => {
            if d <= 0.5 {
                2.0 * s * d
            } else {
                1.0 - 2.0 * (1.0 - s) * (1.0 - d)
            }
        }
        LayerBlend::Darken => s.min(d),
        LayerBlend::Lighten => s.max(d),
        LayerBlend::ColorDodge => {
            if d <= 0.0 {
                0.0
            } else if s >= 1.0 {
                1.0
            } else {
                (d / (1.0 - s)).min(1.0)
            }
        }
        LayerBlend::ColorBurn => {
            if d >= 1.0 {
                1.0
            } else if s <= 0.0 {
                0.0
            } else {
                1.0 - ((1.0 - d) / s).min(1.0)
            }
        }
        LayerBlend::HardLight => {
            if s <= 0.5 {
                2.0 * s * d
            } else {
                1.0 - 2.0 * (1.0 - s) * (1.0 - d)
            }
        }
        LayerBlend::SoftLight => {
            if s <= 0.5 {
                d - (1.0 - 2.0 * s) * d * (1.0 - d)
            } else {
                let softened = if d <= 0.25 {
                    ((16.0 * d - 12.0) * d + 4.0) * d
                } else {
                    d.sqrt()
                };
                d + (2.0 * s - 1.0) * (softened - d)
            }
        }
        LayerBlend::Difference => (d - s).abs(),
        LayerBlend::Exclusion => s + d - 2.0 * s * d,
        _ => 0.0,
    }
}
fn blend(source: [u8; 4], destination: [u8; 4], mode: LayerBlend) -> [u8; 4] {
    let s = premultiply(source);
    let d = premultiply(destination);
    let sa = s.to_array()[3];
    let da = d.to_array()[3];
    let result = match mode {
        LayerBlend::Clear => V::ZERO,
        LayerBlend::Src => s,
        LayerBlend::Dst => d,
        LayerBlend::SrcOver => s + d * V::splat(1.0 - sa),
        LayerBlend::DstOver => d + s * V::splat(1.0 - da),
        LayerBlend::SrcIn => s * V::splat(da),
        LayerBlend::DstIn => d * V::splat(sa),
        LayerBlend::SrcOut => s * V::splat(1.0 - da),
        LayerBlend::DstOut => d * V::splat(1.0 - sa),
        LayerBlend::SrcAtop => s * V::splat(da) + d * V::splat(1.0 - sa),
        LayerBlend::DstAtop => d * V::splat(sa) + s * V::splat(1.0 - da),
        LayerBlend::Xor => s * V::splat(1.0 - da) + d * V::splat(1.0 - sa),
        LayerBlend::Plus => (s + d).min(V::ONE),
        LayerBlend::Modulate => s * d,
        _ => {
            let ss = s.to_array();
            let dd = d.to_array();
            let straight_s =
                std::array::from_fn::<_, 3, _>(|i| if sa > 0.0 { ss[i] / sa } else { 0.0 });
            let straight_d =
                std::array::from_fn::<_, 3, _>(|i| if da > 0.0 { dd[i] / da } else { 0.0 });
            let rgb = match mode {
                LayerBlend::Hue => set_luminosity(
                    set_saturation(straight_s, saturation(straight_d)),
                    luminosity(straight_d),
                ),
                LayerBlend::Saturation => set_luminosity(
                    set_saturation(straight_d, saturation(straight_s)),
                    luminosity(straight_d),
                ),
                LayerBlend::Color => set_luminosity(straight_s, luminosity(straight_d)),
                LayerBlend::Luminosity => set_luminosity(straight_d, luminosity(straight_s)),
                _ => std::array::from_fn(|i| component(mode, straight_s[i], straight_d[i])),
            };
            let rgb = std::array::from_fn::<_, 3, _>(|i| {
                ss[i] * (1.0 - da) + dd[i] * (1.0 - sa) + sa * da * rgb[i]
            });
            V::from([rgb[0], rgb[1], rgb[2], sa + da - sa * da])
        }
    };
    from_premultiplied(result)
}
