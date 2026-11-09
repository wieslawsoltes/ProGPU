//! Shared bounded nested-layer gallery; every source uses root coordinates.
use progpu::{
    effects::{Effect, EffectChain},
    font::ManagedFont,
    geometry::{AnalyticPrimitive, Shape},
    mask::AnalyticMask,
    scene::{LayerBlend, LayerDraw, Scene},
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

pub fn layer_scene(font: Option<ManagedFont>) -> Result<Scene> {
    let modes = [
        LayerBlend::SrcOver,
        LayerBlend::Src,
        LayerBlend::Dst,
        LayerBlend::SrcIn,
        LayerBlend::DstIn,
        LayerBlend::SrcOut,
        LayerBlend::DstOut,
        LayerBlend::SrcAtop,
        LayerBlend::DstAtop,
        LayerBlend::Xor,
        LayerBlend::DstOver,
        LayerBlend::Plus,
        LayerBlend::Clear,
        LayerBlend::Modulate,
    ];
    let mut scene = Scene::default();
    for (index, blend) in modes.into_iter().enumerate() {
        let x = 16.25 + (index % 4) as f32 * 232.0;
        let y = 16.5 + (index / 4) as f32 * 150.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 220.0, 136.0],
            [0.2, 0.23, 0.28, 1.0],
        ))?;
        let mut group = Scene::default();
        let mut background =
            AnalyticPrimitive::rectangle([x + 18.0, y + 18.0, 122.0, 94.0], [0.05, 0.65, 1.0, 0.8]);
        background.shape = Shape::Ellipse;
        group.draw_analytic(background)?;
        let mut foreground = Scene::default();
        let mut primitive =
            AnalyticPrimitive::rectangle([x + 80.0, y + 32.0, 114.0, 90.0], [1.0, 0.2, 0.5, 0.7]);
        primitive.shape = Shape::Ellipse;
        foreground.draw_analytic(primitive)?;
        let mut child = LayerDraw::new(foreground);
        child.bounds = Some([x + 60.0, y + 12.0, 144.0, 114.0]);
        child.blend = blend;
        child.opacity = 0.85;
        child.linear_sampling = index % 2 == 0;
        group.draw_layer(child)?;
        // Restore painter order after the nested composite.
        group.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 24.0, y + 116.0, 164.0, 3.0],
            [0.3, 1.0, 0.6, 0.9],
        ))?;
        let mut outer = LayerDraw::new(group);
        outer.bounds = Some([x + 8.0, y + 8.0, 204.0, 120.0]);
        outer.opacity = if index % 2 == 0 { 1.0 } else { 0.65 };
        let mut mask = AnalyticMask::rectangle([x + 10.0, y + 10.0, 200.0, 116.0]);
        mask.radii_x = [18.0; 4];
        mask.radii_y = [14.0; 4];
        scene.push_mask(mask)?;
        scene.push_clip_rect([x + 6.5, y + 6.5, 204.0, 120.0])?;
        scene.draw_layer(outer)?;
        scene.pop_scope()?;
        scene.pop_scope()?;
    }
    // Offset bounded mask/text source, then shadow, then final group opacity.
    let mut content = Scene::default();
    let mut mask = AnalyticMask::rectangle([486.25, 482.5, 170.0, 100.0]);
    mask.radii_x = [18.0; 4];
    mask.radii_y = [22.0; 4];
    content.push_mask(mask)?;
    content.draw_analytic(AnalyticPrimitive::rectangle(
        [480.0, 478.0, 190.0, 110.0],
        [0.4, 0.7, 1.0, 0.9],
    ))?;
    if let Some(font) = font {
        let layout = ManagedTextLayout::new(
            "Nested Rust".encode_utf16().collect::<Vec<_>>(),
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
        let mut text = TextDraw::layout(Arc::new(layout));
        text.position = [495.0, 520.0];
        text.brush = Some(Arc::new(progpu::vector::Brush::new(
            progpu::vector::BrushKind::Solid([1.0; 4]),
        )));
        content.draw_text(text, TextPaintContext::default())?;
    }
    content.pop_scope()?;
    let mut layer = LayerDraw::new(content);
    layer.bounds = Some([476.0, 472.0, 212.0, 134.0]);
    layer.opacity = 0.75;
    layer.effects = Some(EffectChain::new(vec![Effect::DropShadow {
        sigma: [2.5, 4.0],
        offset: [6.5, 5.0],
        color: [0.8, 0.1, 0.8, 0.9],
    }])?);
    scene.draw_layer(layer)?;
    // Empty bounds own source state but allocate no target or effect resources.
    let mut empty = LayerDraw::new(scene.clone());
    empty.bounds = Some([0.0, 0.0, 0.0, 10.0]);
    scene.draw_layer(empty)?;
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [720.0, 514.0, 180.0, 28.0],
        [0.2, 0.95, 0.65, 1.0],
    ))?;
    Ok(scene)
}
