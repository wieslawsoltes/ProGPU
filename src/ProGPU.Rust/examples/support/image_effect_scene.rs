//! Shared original fused image-effect, RGB/NV12 blur and retained composition gallery.
use progpu::{
    geometry::AnalyticPrimitive,
    image::{Image, ImageAlpha, ImageSource},
    image_effect::{BlurInput, ImageBlurPolicy, ImageEffectDraw, ImageEffectSource},
    mask::AnalyticMask,
    scene::{LayerBlend, LayerDraw, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub fn image_effect_scene(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    size: [u32; 2],
) -> Result<Scene> {
    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64 {
        for x in 0..64 {
            pixels.extend_from_slice(&[
                (x * 4) as u8,
                (y * 4) as u8,
                if (x / 8 + y / 8) % 2 == 0 { 240 } else { 30 },
                if x < 32 { 255 } else { 160 },
            ]);
        }
    }
    let source = Image::rgba8([64, 64], ImageAlpha::Straight, pixels)?;
    let yuv = planar(device, queue)?;
    let mask = Image::new(
        size,
        size[0],
        progpu::image::ImageFormat::R8,
        ImageAlpha::Straight,
        (0..size[1])
            .flat_map(|_| (0..size[0]).map(|x| ((x % 240) * 255 / 239) as u8))
            .collect::<Vec<_>>(),
    )?;
    let mut scene = Scene::default();
    for index in 0..20 {
        let x = 12.25 + (index % 4) as f32 * 236.0;
        let y = 10.5 + (index / 4) as f32 * 124.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 116.0],
            [0.14, 0.18, 0.28, 1.0],
        ))?;
        let input = if index >= 16 {
            ImageEffectSource::from(yuv.clone())
        } else {
            ImageEffectSource::from(source.clone())
        };
        let mut draw = ImageEffectDraw::new(input, [x + 8.0, y + 5.0, 208.0, 106.0]);
        match index {
            0 => (),
            1 => draw.effect.effects0[0] = 0.25,
            2 => draw.effect.effects0[1] = 1.8,
            3 => draw.effect.effects0[2] = 0.0,
            4 => draw.effect.effects0[3] = 0.8,
            5 => draw.effect.effects1[0] = 1.0,
            6 => draw.effect.effects1[1] = 1.0,
            7 => draw.effect.flags0[3] = 1.0,
            8 => {
                draw.effect.flags0[2] = 1.0;
                draw.effect.color_matrix_red = [0.0, 1.0, 0.0, 0.0];
                draw.effect.color_matrix_green = [0.0, 0.0, 1.0, 0.0];
                draw.effect.color_matrix_blue = [1.0, 0.0, 0.0, 0.0];
                draw.effect.color_matrix_alpha = [0.0, 0.0, 0.0, 0.65];
            }
            9 => draw.effect.effects1[2] = 1.25,
            10 => {
                draw.effect.effects1[2] = 6.0;
                draw.blur_storage = ImageBlurPolicy::Managed;
            }
            11 => {
                draw.mask = Some(ImageSource::from(mask.clone()));
                draw.effect.effects1[1] = 0.5;
            }
            12 | 13 => {
                draw.effect.spherical0 = [1.0, 1.2, 208.0 / 106.0, 0.0];
                draw.effect.spherical_uv_rect = [0.0, 0.0, 1.0, 1.0];
                if index == 13 {
                    draw.effect.spherical_rotation0 = [0.0, 0.0, 1.0, 0.0];
                    draw.effect.spherical_rotation2 = [-1.0, 0.0, 0.0, 0.0];
                }
            }
            14 | 15 => {
                draw.effect.effects1[2] = 3.5;
                draw.effect.effects0[2] = 1.5;
                draw.effect.texture0[3] = 1.0;
            }
            16 => (),
            17 => draw.effect.effects1[2] = 2.25,
            18 => {
                draw.effect.effects1[2] = 4.0;
                draw.blur_storage = ImageBlurPolicy::Managed;
            }
            19 => {
                draw.effect.spherical0 = [1.0, 1.4, 208.0 / 106.0, 0.0];
                draw.effect.spherical_uv_rect = [0.0, 0.0, 1.0, 1.0];
                draw.effect.effects1[0] = 0.5;
            }
            _ => unreachable!(),
        }
        let mut content = Scene::default();
        if matches!(index, 9..=11) {
            let mut mask = AnalyticMask::rectangle([x + 16.0, y + 12.0, 192.0, 92.0]);
            mask.radii_x = [20.0; 4];
            mask.radii_y = [14.0; 4];
            content.push_mask(mask)?;
            content.push_mask(AnalyticMask::rectangle([x + 24.0, y + 8.0, 184.0, 100.0]))?;
        }
        content.push_clip_rect([x + 9.5, y + 6.5, 206.0, 104.0])?;
        content.draw_image_effect(draw)?;
        content.pop_scope()?;
        if matches!(index, 9..=11) {
            content.pop_scope()?;
            content.pop_scope()?;
        }
        if index == 14 {
            let mut layer = LayerDraw::new(content);
            layer.bounds = Some([x + 4.0, y + 2.0, 216.0, 112.0]);
            layer.opacity = 0.7;
            layer.blend = LayerBlend::Overlay;
            scene.draw_layer(layer)?;
        } else if index == 15 {
            scene.draw_axis_clip([x + 12.25, y + 10.5, 198.5, 96.25], content)?;
        } else {
            scene.append(&content)?;
        }
    }
    Ok(scene)
}
fn planar(device: &wgpu::Device, queue: &wgpu::Queue) -> Result<BlurInput> {
    let upload = |width, height, format, bpp, data: &[u8]| {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU authored NV12 gallery plane"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        queue.write_texture(
            texture.as_image_copy(),
            data,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(width * bpp),
                rows_per_image: Some(height),
            },
            texture.size(),
        );
        texture
    };
    let luma = (0..64)
        .flat_map(|y| (0..64).map(move |x| ((x + y) * 2) as u8))
        .collect::<Vec<_>>();
    let chroma = (0..32 * 32).flat_map(|_| [96u8, 160]).collect::<Vec<_>>();
    let luma = upload(64, 64, wgpu::TextureFormat::R8Unorm, 1, &luma);
    let chroma = upload(32, 32, wgpu::TextureFormat::Rg8Unorm, 2, &chroma);
    Ok(BlurInput::planar(
        device,
        &luma,
        &chroma,
        progpu::image_effect::YuvConversion {
            range: [0.0, 1.0, 0.5, 1.0],
            red: [1.0, 0.0, 1.5748, 0.0],
            green: [1.0, -0.187324, -0.468124, 0.0],
            blue: [1.0, 1.8556, 0.0, 0.0],
        },
    )?)
}
