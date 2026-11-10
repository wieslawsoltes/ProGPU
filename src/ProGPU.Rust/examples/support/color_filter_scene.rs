//! Authored original color-filter graph and direct native matrix gallery.
use progpu::{
    geometry::AnalyticPrimitive,
    image::{
        ColorFilter, ColorFilteredImageDraw, HighContrast, Image, ImageAlpha, ImageColorDraw,
        ImageColorMatrix, ImageDraw, ImageSampling, InvertStyle,
    },
    mask::AnalyticMask,
    renderer::Renderer,
    scene::{LayerBlend, LayerDraw, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn color_filter_scene(
    _renderer: &mut Renderer,
    _device: &wgpu::Device,
    _queue: &wgpu::Queue,
    _dpi: f32,
    _size: [u32; 2],
) -> Result<Scene> {
    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    let mut counts = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64u32 {
        for x in 0..64u32 {
            pixels.extend_from_slice(&[
                (x * 4) as u8,
                (y * 4) as u8,
                if (x / 8 + y / 8) % 2 == 0 { 235 } else { 30 },
                if x < 32 { 255 } else { (y * 4) as u8 },
            ]);
            counts.extend_from_slice(&[0, 0, 0, (x / 8) as u8]);
        }
    }
    let source = Image::rgba8([64, 64], ImageAlpha::Straight, pixels)?;
    let counts = Image::rgba8([64, 64], ImageAlpha::Premultiplied, counts)?;
    let identity = ImageColorMatrix::default();
    let rotate = ImageColorMatrix {
        red: [0.0, 1.0, 0.0, 0.0],
        green: [0.0, 0.0, 1.0, 0.0],
        blue: [1.0, 0.0, 0.0, 0.0],
        ..identity
    };
    let matrix = ColorFilter::matrix(rotate.managed_values())?;
    let hsla = ColorFilter::hsla_matrix(
        ImageColorMatrix {
            offset: [0.18, 0.0, 0.0, 0.0],
            ..identity
        }
        .managed_values(),
    )?;
    let contrast = ColorFilter::high_contrast(HighContrast {
        grayscale: false,
        invert: InvertStyle::Lightness,
        contrast: 0.35,
    })?;
    let alpha: [u8; 256] = std::array::from_fn(|i| i as u8);
    let red: [u8; 256] = std::array::from_fn(|i| 255 - i as u8);
    let poster: [u8; 256] = std::array::from_fn(|i| ((i / 64) * 85) as u8);
    let table = ColorFilter::tables(&alpha, &red, &poster, &alpha)?;
    let blend = ColorFilter::blend([230, 55, 105, 180], LayerBlend::Overlay)
        .ok_or(progpu::Error::InvalidInput)?;
    let composed = ColorFilter::compose(
        contrast.clone(),
        ColorFilter::compose(matrix.clone(), table.clone())?,
    )?;
    let lerp = ColorFilter::lerp(
        0.35,
        ColorFilter::compose(hsla.clone(), table.clone())?,
        composed.clone(),
    )?;
    let overdraw = ColorFilter::overdraw([
        [25, 90, 220, 255],
        [25, 210, 230, 255],
        [30, 200, 60, 255],
        [220, 210, 20, 255],
        [240, 100, 20, 255],
        [240, 30, 80, 255],
    ]);
    let filters = [
        matrix.clone(),
        table.clone(),
        hsla.clone(),
        contrast.clone(),
        blend.clone(),
        ColorFilter::luma(),
        ColorFilter::srgb_to_linear(),
        ColorFilter::linear_to_srgb(),
        composed,
        lerp,
        ColorFilter::compose(blend, hsla)?,
        overdraw,
    ];
    let mut scene = Scene::default();
    for index in 0..16 {
        let x = 12.25 + (index % 4) as f32 * 236.0;
        let y = 10.5 + (index / 4) as f32 * 144.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 134.0],
            [0.14, 0.18, 0.28, 1.0],
        ))?;
        let mut content = Scene::default();
        let mut draw = ImageDraw::new(
            if index == 15 {
                counts.clone()
            } else {
                source.clone()
            },
            [x + 8.0, y + 7.0, 208.0, 120.0],
        );
        if index == 12 {
            draw.sampling = ImageSampling::LinearMipmap;
            draw.opacity = 0.8;
        }
        let masked = matches!(index, 2 | 3 | 6 | 9 | 12);
        if masked {
            let mut mask = AnalyticMask::rectangle([x + 14.0, y + 13.0, 196.0, 108.0]);
            mask.radii_x = [20.0; 4];
            mask.radii_y = [14.0; 4];
            content.push_mask(mask)?;
            content.push_mask(AnalyticMask::rectangle([x + 18.5, y + 9.25, 190.0, 118.0]))?;
        }
        content.push_clip_rect([x + 9.5, y + 8.5, 206.0, 118.0])?;
        if index < 4 {
            let matrix = match index {
                0 => rotate,
                1 => ImageColorMatrix {
                    alpha: [0.0, 0.0, 0.0, 0.6],
                    offset: [0.1, 0.0, 0.0, 0.0],
                    ..identity
                },
                2 => ImageColorMatrix {
                    luminance_to_alpha: true,
                    ..identity
                },
                _ => ImageColorMatrix {
                    luminance_to_alpha: true,
                    ..rotate
                },
            };
            content.draw_image_color(ImageColorDraw { draw, matrix })?;
        } else {
            content.draw_color_filtered_image(ColorFilteredImageDraw::new(
                draw,
                filters[index - 4].clone(),
            ))?;
        }
        content.pop_scope()?;
        if masked {
            content.pop_scope()?;
            content.pop_scope()?;
        }
        // Later analytic geometry witnesses source painter ordering independently.
        content.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 176.0, y + 108.0, 34.0, 13.0],
            [0.8, 0.9, 1.0, 0.55],
        ))?;
        if index == 13 {
            let mut layer = LayerDraw::new(content);
            layer.bounds = Some([x + 4.0, y + 3.0, 216.0, 128.0]);
            layer.opacity = 0.72;
            layer.blend = LayerBlend::Overlay;
            scene.draw_layer(layer)?;
        } else if index == 14 {
            scene.draw_axis_clip([x + 12.25, y + 11.5, 198.5, 112.25], content)?;
        } else {
            scene.append(&content)?;
        }
    }
    Ok(scene)
}
