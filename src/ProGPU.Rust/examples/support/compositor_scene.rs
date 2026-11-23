//! Shared original ROP3 and uniform-backdrop compositor gallery.
use progpu::{
    composition::{RasterOperation, RasterPattern},
    geometry::{Affine, AnalyticPrimitive, Shape},
    image::{Image, ImageAlpha, ImageDraw, ImageFormat, ImageSampling},
    mask::AnalyticMask,
    path::ExecutionPreference,
    renderer::{Frame, Renderer, UniformBackdropEngine},
    scene::{LayerBlend, LayerDraw, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn compositor_scene(
    renderer: &mut Renderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
) -> Result<Scene> {
    let mut pixels = Vec::new();
    for y in 0..32 {
        for x in 0..32 {
            pixels.extend_from_slice(&[x * 8, y * 8, 255 - x * 4, if x < 8 { 0 } else { 192 }]);
        }
    }
    let image = Image::new(
        [32, 32],
        128,
        ImageFormat::Rgba8,
        ImageAlpha::Straight,
        pixels,
    )?;
    let tile = Image::new(
        [3, 2],
        12,
        ImageFormat::Rgba8,
        ImageAlpha::Straight,
        vec![
            255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 255, 255, 255, 0, 255, 255,
            255, 255, 0, 255,
        ],
    )?;
    let mut scene = Scene::default();
    let codes = [
        0x00, 0xff, 0xcc, 0xaa, 0xf0, 0x66, 0x5a, 0x88, 0xee, 0x33, 0x55, 0x0f, 0xc0, 0xbb, 0xfb,
        0x44, 0x22, 0x11, 0x77, 0xdd, 0x99, 0x69, 0x96, 0xe2, 0xb8,
    ];
    for (index, code) in codes.into_iter().enumerate() {
        let x = 8.0 + (index % 8) as f32 * 118.0;
        let y = 8.0 + (index / 8) as f32 * 122.0;
        let bounds = [x, y, 110.0, 112.0];
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            bounds,
            [0.15, 0.25, 0.46, 0.75],
        ))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 55.0, 112.0],
            [0.75, 0.3, 0.1, 0.6],
        ))?;
        let pattern = match index % 4 {
            0 => RasterPattern::Solid([0.3, 0.8, 0.4, 1.0]),
            1 | 2 => RasterPattern::Tile {
                bits: 0x55aa55aa55aa55aa,
                foreground: [1.0, 0.3, 0.7, 1.0],
                background: [0.2, 0.8, 0.3, if index % 4 == 2 { 0.0 } else { 1.0 }],
                origin: [-3.25, 1.5],
            },
            _ => RasterPattern::Texture {
                image: tile.clone().into(),
                origin: [-2.5, 3.25],
            },
        };
        let mut draw = ImageDraw::new(image.clone(), [x + 7.25, y + 8.5, 95.0, 94.0]);
        draw.sampling = ImageSampling::Nearest;
        if index % 5 == 0 {
            draw.transform = Affine([1.0, 0.0, 0.08, 1.0, -y * 0.08, 0.0]);
        }
        let mut child = Scene::default();
        child.push_clip_rect(bounds)?;
        if index % 5 == 1 {
            let mut mask = AnalyticMask::rectangle([x + 12.0, y + 13.0, 84.0, 86.0]);
            mask.radii_x = [18.0; 4];
            mask.radii_y = [18.0; 4];
            child.push_mask(mask)?;
        }
        child.draw_raster_operation(draw, RasterOperation { code, pattern })?;
        if index % 5 == 1 {
            child.pop_scope()?;
        }
        child.pop_scope()?;
        if index == 24 {
            let mut layer = LayerDraw::new(child);
            layer.bounds = Some(bounds);
            layer.opacity = 0.8;
            scene.draw_layer(layer)?;
        } else {
            scene.append(&child)?;
        }
    }
    let frame = Frame {
        width: (96.0 * dpi).ceil() as u32,
        height: (96.0 * dpi).ceil() as u32,
        dpi_scale: dpi,
    };
    let mut source = Scene::default();
    source.draw_analytic(AnalyticPrimitive::rectangle(
        [8.0, 8.0, 56.0, 70.0],
        [0.95, 0.2, 0.15, 0.65],
    ))?;
    let mut ellipse = AnalyticPrimitive::rectangle([30.0, 25.0, 60.0, 64.0], [0.1, 0.7, 0.95, 0.7]);
    ellipse.shape = Shape::Ellipse;
    source.draw_analytic(ellipse)?;
    let prepared = renderer.prepare_scene(&source, frame).await?;
    let captured = renderer.capture(&prepared, None).await?.into_image();
    let mut engine = UniformBackdropEngine::new(
        device,
        captured.texture().format(),
        ExecutionPreference::Automatic,
    )?;
    let modes = [
        LayerBlend::Multiply,
        LayerBlend::Screen,
        LayerBlend::Darken,
        LayerBlend::Lighten,
        LayerBlend::Exclusion,
        LayerBlend::Overlay,
        LayerBlend::ColorDodge,
        LayerBlend::ColorBurn,
        LayerBlend::HardLight,
        LayerBlend::SoftLight,
        LayerBlend::Difference,
        LayerBlend::Hue,
        LayerBlend::Saturation,
        LayerBlend::Color,
        LayerBlend::Luminosity,
    ];
    for (i, mode) in modes.into_iter().enumerate() {
        let index = i + 25;
        let x = 8.0 + (index % 8) as f32 * 118.0;
        let y = 8.0 + (index / 8) as f32 * 122.0;
        let output = engine
            .prepare(&captured, [0.15, 0.35, 0.25, 0.6], mode, None)
            .await?;
        let mut encoder = device.create_command_encoder(&Default::default());
        engine.encode(&mut encoder, &output)?;
        queue.submit([encoder.finish()]);
        scene.draw_image(ImageDraw::new(output.into_image(), [x, y, 110.0, 112.0]))?;
    }
    Ok(scene)
}
