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
        LayerBlend::Multiply,
        LayerBlend::Screen,
        LayerBlend::Darken,
        LayerBlend::Lighten,
        LayerBlend::Exclusion,
        LayerBlend::Plus,
        LayerBlend::Clear,
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
        LayerBlend::Modulate,
    ];
    let mut scene = Scene::default();
    for (index, blend) in modes.into_iter().enumerate() {
        let x = 12.25 + (index % 6) as f32 * 156.0;
        let y = 12.5 + (index / 6) as f32 * 122.0;
        let r = |[rx, ry, w, h]: [f32; 4]| [x + rx * 0.66, y + ry * 0.82, w * 0.66, h * 0.82];
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([0.0, 0.0, 220.0, 136.0]),
            [0.2, 0.23, 0.28, 1.0],
        ))?;
        let mut group = Scene::default();
        let mut background =
            AnalyticPrimitive::rectangle(r([18.0, 18.0, 122.0, 94.0]), [0.05, 0.65, 1.0, 0.8]);
        background.shape = Shape::Ellipse;
        group.draw_analytic(background)?;
        let mut foreground = Scene::default();
        let mut primitive =
            AnalyticPrimitive::rectangle(r([80.0, 32.0, 114.0, 90.0]), [1.0, 0.2, 0.5, 0.7]);
        primitive.shape = Shape::Ellipse;
        foreground.draw_analytic(primitive)?;
        let mut child = LayerDraw::new(foreground);
        child.bounds = Some(r([60.0, 12.0, 144.0, 114.0]));
        child.blend = blend;
        child.opacity = 0.85;
        child.linear_sampling = index % 2 == 0;
        group.draw_layer(child)?;
        group.draw_analytic(AnalyticPrimitive::rectangle(
            r([24.0, 116.0, 164.0, 3.0]),
            [0.3, 1.0, 0.6, 0.9],
        ))?;
        let mut outer = LayerDraw::new(group);
        outer.bounds = Some(r([8.0, 8.0, 204.0, 120.0]));
        outer.opacity = if index % 2 == 0 { 1.0 } else { 0.65 };
        // Exercise both bounded child destinations and actual root destination
        // reads, with direct mask/scissor scopes applied during source resolve.
        if !blend.is_fixed() && index % 2 == 0 {
            outer.blend = blend;
        }
        let mut mask = AnalyticMask::rectangle(r([10.0, 10.0, 200.0, 116.0]));
        mask.radii_x = [12.0; 4];
        mask.radii_y = [10.0; 4];
        scene.push_mask(mask)?;
        scene.push_clip_rect(r([6.5, 6.5, 204.0, 120.0]))?;
        scene.draw_layer(outer)?;
        scene.pop_scope()?;
        scene.pop_scope()?;
    }
    // Offset bounded mask/text source, then shadow, then final group opacity.
    let mut content = Scene::default();
    let mut mask = AnalyticMask::rectangle([800.25, 510.5, 132.0, 94.0]);
    mask.radii_x = [18.0; 4];
    mask.radii_y = [22.0; 4];
    content.push_mask(mask)?;
    content.draw_analytic(AnalyticPrimitive::rectangle(
        [794.0, 506.0, 146.0, 106.0],
        [0.4, 0.7, 1.0, 0.9],
    ))?;
    if let Some(font) = font {
        let layout = ManagedTextLayout::new(
            "Nested Rust".encode_utf16().collect::<Vec<_>>(),
            font,
            16.0,
            Default::default(),
            ManagedPreparationOptions::new(
                ManagedNormalizationProfile::Unicode17_0,
                ManagedMarkProfile::Unicode16_0,
                ManagedGraphemeProfile::Unicode16_0,
            ),
            &[] as &[ManagedFont],
        )?;
        let mut text = TextDraw::layout(Arc::new(layout));
        text.position = [806.0, 542.0];
        text.brush = Some(Arc::new(progpu::vector::Brush::new(
            progpu::vector::BrushKind::Solid([1.0; 4]),
        )));
        content.draw_text(text, TextPaintContext::default())?;
    }
    content.pop_scope()?;
    let mut layer = LayerDraw::new(content);
    layer.bounds = Some([786.0, 498.0, 164.0, 128.0]);
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
    // A nonempty clip disjoint from an advanced layer suppresses its operation
    // without losing the subsequent root state or fabricating empty geometry.
    let mut hidden = Scene::default();
    hidden.draw_analytic(AnalyticPrimitive::rectangle(
        [2.0, 2.0, 18.0, 18.0],
        [1.0, 0.0, 0.0, 1.0],
    ))?;
    let mut hidden = LayerDraw::new(hidden);
    hidden.bounds = Some([2.0, 2.0, 20.0, 20.0]);
    hidden.blend = LayerBlend::Overlay;
    scene.push_clip_rect([900.0, 0.0, 10.0, 10.0])?;
    scene.draw_layer(hidden)?;
    scene.pop_scope()?;
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [792.0, 630.0, 150.0, 3.0],
        [0.2, 0.95, 0.65, 1.0],
    ))?;
    Ok(scene)
}
