//! Shared authored storage-Clear gallery, distinct from source qualification.
use progpu::{
    effects::{Effect, EffectChain},
    font::ManagedFont,
    geometry::AnalyticPrimitive,
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

pub fn clear_scene(font: Option<ManagedFont>) -> Result<Scene> {
    let mut scene = Scene::default();
    for index in 0..12 {
        let x = 24.0 + (index % 3) as f32 * 306.0;
        let y = 16.0 + (index / 3) as f32 * 154.0;
        let r = |[rx, ry, w, h]: [f32; 4]| [x + rx, y + ry, w, h];
        scene.push_clip_rect(r([0.0, 0.0, 288.0, 144.0]))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([0.0, 0.0, 288.0, 144.0]),
            [0.06, 0.35, 0.8, 1.0],
        ))?;
        match index {
            0..=2 => {
                scene.push_clip_rect(r([18.0, 18.0, 252.0, 108.0]))?;
                let alpha = [1.0, 0.0, 0.4][index];
                scene.clear_target([1.0, 0.25, 0.1, alpha])?;
                scene.pop_scope()?;
            }
            3..=6 => {
                // Fractional and exact/adjacent half-edge binary admission.
                let left = x + 18.5;
                let left = match index {
                    3 => x + 18.25,
                    5 => left.next_up(),
                    6 => left.next_down(),
                    _ => left,
                };
                scene.push_clip_rect([left, y + 18.5, 252.0, 108.0])?;
                scene.clear_target([0.9, 0.15, 0.6, 0.5])?;
                scene.pop_scope()?;
            }
            7 => {
                scene.push_clip_rect(r([18.0, 18.0, 252.0, 108.0]))?;
                scene.push_clip_rect(r([76.0, 42.0, 136.0, 60.0]))?;
                scene.clear_target([0.1, 0.8, 0.3, 1.0])?;
                scene.push_clip_rect(r([0.0, 0.0, 0.0, 144.0]))?;
                scene.clear_target([1.0, 0.0, 0.0, 1.0])?;
                scene.pop_scope()?;
                scene.pop_scope()?;
                scene.pop_scope()?;
            }
            8 | 10 | 11 => {
                // The child owns its storage. Parent opacity/mask applies only
                // at the final composite, independently of source Clear.
                let mut source = Scene::default();
                source.draw_analytic(AnalyticPrimitive::rectangle(
                    r([12.0, 12.0, 264.0, 120.0]),
                    [0.9, 0.5, 0.1, 1.0],
                ))?;
                source.push_clip_rect(r([30.0, 30.0, 228.0, 84.0]))?;
                source.clear_target([1.0, 0.2, 0.5, 0.0])?;
                source.pop_scope()?;
                source.draw_analytic(AnalyticPrimitive::rectangle(
                    r([42.0, 54.0, 204.0, 36.0]),
                    [0.9, 0.1, 0.6, 0.7],
                ))?;
                if index == 11
                    && let Some(font) = font.clone()
                {
                    let layout = ManagedTextLayout::new(
                        "Clear + text".encode_utf16().collect::<Vec<_>>(),
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
                    text.position = [x + 60.0, y + 56.0];
                    text.brush = Some(Arc::new(progpu::vector::Brush::new(
                        progpu::vector::BrushKind::Solid([1.0; 4]),
                    )));
                    source.draw_text(text, TextPaintContext::default())?;
                }
                let mut layer = LayerDraw::new(source);
                layer.bounds = Some(r([6.0, 6.0, 276.0, 132.0]));
                layer.opacity = 0.65;
                if index == 10 {
                    layer.effects = Some(EffectChain::new(vec![Effect::DropShadow {
                        sigma: [2.0, 3.0],
                        offset: [4.0, 3.0],
                        color: [0.8, 0.1, 0.8, 0.8],
                    }])?);
                }
                let mut mask = AnalyticMask::rectangle(r([12.0, 12.0, 264.0, 120.0]));
                mask.radii_x = [20.0; 4];
                mask.radii_y = [16.0; 4];
                scene.push_mask(mask)?;
                scene.draw_layer(layer)?;
                scene.pop_scope()?;
            }
            9 => {
                // Clears before, between and after destination-reading passes
                // must address their actual current root storage.
                for (step, blend) in [LayerBlend::Overlay, LayerBlend::Difference]
                    .into_iter()
                    .enumerate()
                {
                    scene.push_clip_rect(r([18.0 + step as f32 * 72.0, 18.0, 72.0, 108.0]))?;
                    scene.clear_target([0.3, 0.7, 0.1, 0.6])?;
                    scene.pop_scope()?;
                    let mut source = Scene::default();
                    source.draw_analytic(AnalyticPrimitive::rectangle(
                        r([36.0, 36.0 + step as f32 * 36.0, 216.0, 36.0]),
                        [1.0, 0.1, 0.5, 0.7],
                    ))?;
                    let mut layer = LayerDraw::new(source);
                    layer.bounds = Some(r([12.0, 12.0, 264.0, 120.0]));
                    layer.blend = blend;
                    scene.draw_layer(layer)?;
                }
                scene.push_clip_rect(r([198.0, 18.0, 72.0, 108.0]))?;
                scene.clear_target([0.5, 0.8, 0.2, 0.25])?;
                scene.pop_scope()?;
            }
            _ => unreachable!(),
        }
        scene.pop_scope()?;
        // Following draws prove that Clear does not retain its private scissor.
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([4.0, 136.0, 280.0, 3.0]),
            [0.2, 1.0, 0.6, 1.0],
        ))?;
    }
    Ok(scene)
}
