//! Shared authored mask gallery; source pixel qualification remains independent.
use progpu::{
    font::ManagedFont,
    geometry::{Affine, AnalyticPrimitive},
    image::{Image, ImageAlpha, ImageDraw, ImageFormat},
    mask::{AnalyticMask, CoverageMask, MaskSampling},
    scene::Scene,
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub fn mask_scene(font: Option<ManagedFont>) -> Result<Scene> {
    let mut scene = Scene::default();
    // Tiny original R8 source with explicit row padding; no conversion/readback.
    let mut bytes = vec![0u8; 16 * 20];
    for y in 0..16 {
        for x in 0..16 {
            bytes[y * 20 + x] = ((x + y) * 255 / 30) as u8;
        }
    }
    let coverage = Image::new([16, 16], 20, ImageFormat::R8, ImageAlpha::Straight, bytes)?;
    let image = super::image_sample::checker_image()?;
    if let Some(font) = font {
        scene.push_mask(CoverageMask {
            image: coverage,
            bounds: [0.0, 0.0, 940.0, 600.0],
            transform: Affine::IDENTITY,
            sampling: MaskSampling::Linear,
            opacity: 1.0,
        })?;
        let mut rounded = AnalyticMask::rectangle([16.0, 16.0, 900.0, 560.0]);
        rounded.radii_x = [24.0; 4];
        rounded.radii_y = [32.0; 4];
        scene.push_mask(rounded)?;
        scene.push_clip_rect([24.25, 24.5, 880.0, 540.0])?;
        scene.draw_image(ImageDraw::new(image, [0.0, 0.0, 940.0, 600.0]))?;
        scene.append(&super::text_sample::text_scene(font)?)?;
        scene.pop_scope()?;
        scene.pop_scope()?;
        scene.pop_scope()?;
        return Ok(scene);
    }
    for index in 0..6 {
        let x = 20.0 + (index % 3) as f32 * 306.0;
        let y = 20.0 + (index / 3) as f32 * 290.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 286.0, 270.0],
            [0.10, 0.13, 0.19, 1.0],
        ))?;
        scene.push_clip_rect([x + 8.25, y + 8.5, 270.0, 250.0])?;
        let mut rounded = AnalyticMask::rectangle([x + 14.0, y + 12.0, 258.0, 240.0]);
        rounded.radii_x = [70.0, 18.0, 95.0, 30.0];
        rounded.radii_y = [24.0, 90.0, 60.0, 14.0];
        if index >= 3 {
            scene.push_mask(CoverageMask {
                image: coverage.clone(),
                bounds: [x, y, 286.0, 270.0],
                transform: Affine::IDENTITY,
                sampling: if index == 3 {
                    MaskSampling::Nearest
                } else {
                    MaskSampling::Linear
                },
                opacity: 0.85,
            })?;
        }
        if index == 1 {
            rounded.transform = Affine([1.0, 0.08, -0.12, 1.0, 36.0, -24.0]);
        }
        scene.push_mask(rounded)?;
        if index == 2 || index == 5 {
            scene.push_mask(AnalyticMask::axis_clip_area([
                x + 40.25,
                y + 32.75,
                190.5,
                192.5,
            ]))?;
            let mut inner = AnalyticMask::rectangle([x + 20.0, y + 28.0, 236.0, 222.0]);
            inner.opacity = 0.7;
            inner.radii_x = [55.0; 4];
            inner.radii_y = [55.0; 4];
            scene.push_mask(inner)?;
        }
        scene.draw_image(ImageDraw::new(image.clone(), [x, y, 286.0, 270.0]))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 2.0, y + 110.0, 282.0, 50.0],
            [0.1, 0.7, 1.0, 0.6],
        ))?;
        if index == 2 || index == 5 {
            scene.pop_scope()?;
            scene.pop_scope()?;
        }
        scene.pop_scope()?;
        if index >= 3 {
            scene.pop_scope()?;
        }
        // An empty clip suppresses this command, then pop restores the parent.
        scene.push_clip_rect([x, y, 0.0, 0.0])?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 286.0, 270.0],
            [1.0, 0.0, 0.0, 1.0],
        ))?;
        scene.pop_scope()?;
        scene.pop_scope()?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y + 262.0, 286.0, 3.0],
            [0.3, 0.9, 0.6, 1.0],
        ))?;
    }
    Ok(scene)
}
