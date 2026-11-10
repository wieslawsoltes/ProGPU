//! Authored production gallery: actual ordered host backdrops, advanced image
//! draws and the original distinct managed vector blend policy. No pixel oracle.
use progpu::{
    backdrop::{
        BackdropMaterialBrush, BackdropMaterialDraw, BackdropMaterialKind,
        BackdropMaterialParameters, BackdropMaterialSource,
    },
    canvas::Canvas,
    geometry::{Affine, AnalyticPrimitive, Shape},
    image::{Image, ImageAlpha, ImageDraw, ImageFormat, ImageSampling},
    mask::AnalyticMask,
    paint::Paint,
    renderer::{Frame, Renderer},
    scene::{LayerBlend, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn backdrop_blend_scene(renderer: &mut Renderer, dpi: f32) -> Result<Scene> {
    let mut source = Scene::default();
    source.draw_analytic(AnalyticPrimitive::rectangle(
        [0.0, 0.0, 64.0, 64.0],
        [0.08, 0.22, 0.55, 0.65],
    ))?;
    let mut ellipse =
        AnalyticPrimitive::rectangle([10.0, 9.0, 42.0, 47.0], [0.92, 0.28, 0.13, 0.8]);
    ellipse.shape = Shape::Ellipse;
    source.draw_analytic(ellipse)?;
    let prepared = renderer
        .prepare_scene(
            &source,
            Frame {
                width: (64.0 * dpi).ceil() as u32,
                height: (64.0 * dpi).ceil() as u32,
                dpi_scale: dpi,
            },
        )
        .await?;
    let captured = renderer.capture(&prepared, None).await?.into_image();
    let image = Image::new(
        [2, 2],
        8,
        ImageFormat::Rgba8,
        ImageAlpha::Straight,
        vec![
            230, 50, 65, 180, 40, 220, 145, 170, 45, 120, 245, 210, 245, 205, 30, 110,
        ],
    )?;
    let premultiplied = Image::new(
        [2, 2],
        8,
        ImageFormat::Rgba8,
        ImageAlpha::Premultiplied,
        vec![
            160, 35, 45, 180, 26, 145, 96, 170, 37, 99, 202, 210, 106, 88, 13, 110,
        ],
    )?;
    let mut scene = Scene::default();
    for i in 0..24 {
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [i as f32 * 40.0, 0.0, 21.0, 130.0],
            [
                0.12 + (i % 4) as f32 * 0.18,
                0.2,
                0.68 - (i % 3) as f32 * 0.15,
                0.8,
            ],
        ))?;
    }
    for i in 0..6 {
        let x = 8.0 + i as f32 * 158.0;
        let mut brush = if i == 1 {
            BackdropMaterialBrush::mica(false)
        } else {
            BackdropMaterialBrush::default()
        };
        brush.kind = match i {
            2 => BackdropMaterialKind::Blur,
            3 => BackdropMaterialKind::Tint,
            4 => BackdropMaterialKind::Fallback,
            _ => brush.kind,
        };
        brush.tint_color = [0.72, 0.84, 0.98, 0.32];
        brush.material_opacity = 0.86;
        brush.use_fallback = i == 4;
        if i == 5 {
            brush.source = BackdropMaterialSource::Texture;
        }
        let mut parameters = BackdropMaterialParameters::from_brush(
            &brush,
            [x, 12.0, 146.0, 102.0],
            [16.0, 6.0, 22.0, 10.0],
            [14.0, 12.0, 20.0, 8.0],
        );
        if i == 5 {
            parameters.source_texture = Some(captured.clone());
        }
        scene.draw_backdrop_material(BackdropMaterialDraw::new(parameters))?;
        // These marks are recorded after each capture, preserving painter order.
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 12.0, 90.0, 12.0 + i as f32 * 8.0, 4.0],
            [0.08, 0.15, 0.28, 0.8],
        ))?;
    }
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
        let x = 8.0 + (i % 5) as f32 * 190.0;
        let y = 142.0 + (i / 5) as f32 * 122.0;
        let panel = [x, y, 178.0, 110.0];
        scene.draw_analytic(AnalyticPrimitive::rectangle(panel, [0.12, 0.35, 0.62, 0.7]))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 89.0, 110.0],
            [0.9, 0.4, 0.12, 0.5],
        ))?;
        scene.push_clip_rect(panel)?;
        if i % 3 == 0 {
            let mut mask = AnalyticMask::rectangle([x + 3.0, y + 4.0, 170.0, 102.0]);
            mask.radii_x = [12.0; 4];
            mask.radii_y = [12.0; 4];
            scene.push_mask(mask)?;
        }
        scene.push_blend(mode)?;
        let mut draw = ImageDraw::new(
            if i % 2 == 0 {
                image.clone()
            } else {
                premultiplied.clone()
            },
            [x + 8.0, y + 14.0, 74.0, 82.0],
        );
        draw.transform = Affine([1.0, 0.06, -0.08, 1.0, (y + 14.0) * 0.08, -(x + 8.0) * 0.06]);
        draw.sampling = ImageSampling::Linear;
        draw.opacity = 0.84;
        scene.draw_image(draw)?;
        // Right half intentionally exposes the original family-specific vector
        // factors/fallback rather than pretending it is the image resolve route.
        let mut vector =
            AnalyticPrimitive::rectangle([x + 99.0, y + 16.0, 68.0, 78.0], [0.9, 0.16, 0.42, 0.72]);
        vector.shape = Shape::Ellipse;
        scene.draw_analytic(vector)?;
        scene.pop_blend()?;
        if i % 3 == 0 {
            scene.pop_scope()?;
        }
        scene.pop_scope()?;
    }
    // Picture transport retains child image quads and vector commands at their
    // original command positions under the receiving canvas blend stack.
    let mut child = Canvas::new([960.0, 650.0])?;
    child.draw_image(
        image,
        None,
        [20.0, 530.0, 410.0, 82.0],
        ImageSampling::Linear,
        None,
    )?;
    let mut paint = Paint::default();
    paint.color = [215, 60, 180, 175];
    child.draw_rect([450.0, 530.0, 930.0, 612.0], &paint)?;
    let mut canvas = Canvas::new([960.0, 650.0])?;
    paint.set_blend_mode(LayerBlend::SoftLight);
    canvas.draw_picture(child.finish()?, Some(&paint))?;
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [8.0, 518.0, 944.0, 108.0],
        [0.08, 0.5, 0.32, 0.8],
    ))?;
    scene.append(&canvas.finish()?.to_scene()?)?;
    Ok(scene)
}
