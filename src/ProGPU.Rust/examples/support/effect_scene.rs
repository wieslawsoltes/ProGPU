//! Authored shared capture/effect integration gallery, with explicit source padding.
use progpu::{
    effects::{Effect, EffectChain},
    font::ManagedFont,
    geometry::{AnalyticPrimitive, Shape},
    image::ImageDraw,
    mask::AnalyticMask,
    renderer::{Frame, Renderer},
    scene::Scene,
    text::{
        layout::ManagedTextLayout,
        paint::{TextDraw, TextPaintContext},
        preparation::ManagedPreparationOptions,
        unicode::{
            ManagedMarkProfile, grapheme::ManagedGraphemeProfile,
            normalization::ManagedNormalizationProfile,
        },
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

#[derive(Clone, Copy, Default)]
pub struct GalleryMetrics {
    pub captures: u32,
    pub capture_bytes: u64,
    pub effect_bytes: u64,
    pub effect_uniform_bytes: u64,
    pub effect_passes: u32,
}

pub async fn effect_scene(
    renderer: &mut Renderer,
    dpi: f32,
    font: Option<ManagedFont>,
) -> Result<(Scene, GalleryMetrics)> {
    let mut source = Scene::default();
    let mut rounded = AnalyticMask::rectangle([28.0, 28.0, 200.0, 148.0]);
    rounded.radii_x = [28.0; 4];
    rounded.radii_y = [22.0; 4];
    source.push_mask(rounded)?;
    source.draw_analytic(AnalyticPrimitive::rectangle(
        [24.0, 24.0, 160.0, 140.0],
        [0.12, 0.68, 1.0, 0.85],
    ))?;
    let mut ellipse =
        AnalyticPrimitive::rectangle([100.0, 56.0, 132.0, 126.0], [0.95, 0.2, 0.45, 0.75]);
    ellipse.shape = Shape::Ellipse;
    source.draw_analytic(ellipse)?;
    if let Some(font) = font {
        let layout = ManagedTextLayout::new(
            "ProGPU Rust".encode_utf16().collect::<Vec<_>>(),
            font,
            22.0,
            Default::default(),
            ManagedPreparationOptions::new(
                ManagedNormalizationProfile::Unicode17_0,
                ManagedMarkProfile::Unicode16_0,
                ManagedGraphemeProfile::Unicode16_0,
            ),
            &[] as &[ManagedFont],
        )?;
        let mut draw = TextDraw::layout(Arc::new(layout));
        draw.brush = Some(Arc::new(progpu::vector::Brush::new(
            progpu::vector::BrushKind::Solid([1.0; 4]),
        )));
        draw.position = [40.0, 60.0];
        source.draw_text(draw, TextPaintContext::default())?;
    }
    source.pop_scope()?;
    let frame = Frame {
        width: (264.0 * dpi).ceil() as u32,
        height: (208.0 * dpi).ceil() as u32,
        dpi_scale: dpi,
    };
    let prepared = renderer.prepare_scene(&source, frame).await?;
    let gaussian = Effect::GaussianBlur { sigma: [2.2, 4.0] };
    let box_blur = Effect::BoxBlur { radius: [2.8, 5.2] };
    let sharp_shadow = Effect::DropShadow {
        sigma: [0.0; 2],
        offset: [9.5, 6.25],
        color: [0.1, 0.0, 0.4, 0.9],
    };
    let shadow = Effect::DropShadow {
        sigma: [3.0, 4.0],
        offset: [8.0, 10.5],
        color: [0.2, 0.0, 0.6, 0.7],
    };
    let cases = [
        None,
        Some(EffectChain::new(vec![gaussian])?),
        Some(EffectChain::new(vec![box_blur])?),
        Some(EffectChain::new(vec![sharp_shadow])?),
        Some(EffectChain::new(vec![shadow])?),
        Some(EffectChain::new(vec![
            gaussian,
            sharp_shadow,
            box_blur,
            shadow,
            gaussian,
            box_blur,
            sharp_shadow,
            gaussian,
        ])?),
    ];
    let mut scene = Scene::default();
    let mut metrics = GalleryMetrics::default();
    for (index, chain) in cases.iter().enumerate() {
        let capture = renderer.capture(&prepared, chain.as_ref()).await?;
        metrics.captures += 1;
        metrics.capture_bytes += capture.capture_bytes();
        if let Some(effect) = capture.effect_metrics() {
            metrics.effect_bytes += effect.texture_bytes;
            metrics.effect_uniform_bytes += effect.uniform_bytes;
            metrics.effect_passes += effect.compute_passes;
        }
        let x = 24.0 + (index % 3) as f32 * 306.0;
        let y = 24.0 + (index / 3) as f32 * 284.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 288.0, 252.0],
            [0.22, 0.25, 0.30, 1.0],
        ))?;
        // Group opacity is applied only to the materialized/evaluated output.
        let mut image = ImageDraw::new(
            capture.into_image(),
            [
                x + 12.0,
                y + 12.0,
                frame.width as f32 / dpi,
                frame.height as f32 / dpi,
            ],
        );
        image.opacity = if index == 5 { 0.65 } else { 1.0 };
        scene.push_clip_rect([x, y, 288.0, 252.0])?;
        scene.draw_image(image)?;
        scene.pop_scope()?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 12.0, y + 232.0, 264.0, 4.0],
            [0.35, 0.85, 0.65, 1.0],
        ))?;
    }
    Ok((scene, metrics))
}
