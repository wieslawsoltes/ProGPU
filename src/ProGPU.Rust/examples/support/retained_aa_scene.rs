//! Retained source AA, binary clips, Clear, ordinary owners and live text.
use progpu::{
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

pub fn retained_aa_scene(font: Option<ManagedFont>) -> Result<Scene> {
    let mut scene = Scene::default();
    for index in 0..12 {
        let x = 12.0 + (index % 3) as f32 * 312.0;
        let y = 10.0 + (index / 3) as f32 * 154.0;
        let r = |[l, t, w, h]: [f32; 4]| [x + l, y + t, w, h];
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([0.0, 0.0, 296.0, 142.0]),
            [0.12, 0.3, 0.7, 1.0],
        ))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([6.0, 6.0, 284.0, 130.0]),
            [0.85, 0.5, 0.15, 0.7],
        ))?;
        let mut source = Scene::default();
        source.draw_analytic(AnalyticPrimitive::rectangle(
            r([4.0, 4.0, 288.0, 132.0]),
            [0.1, 0.8, 0.65, 0.6],
        ))?;
        let mut inner = Scene::default();
        if index % 2 == 1 {
            // Only this exact consecutive AA chain loads the live background.
            inner.push_clip_rect(r([44.5, 28.5, 204.0, 76.0]))?;
            inner.clear_target([0.9, 0.2, 0.7, if index % 4 == 1 { 0.0 } else { 0.4 }])?;
            inner.pop_scope()?;
        }
        inner.draw_analytic(AnalyticPrimitive::rectangle(
            r([34.0, 52.0, 228.0, 38.0]),
            [0.9, 0.1, 0.5, 0.75],
        ))?;
        if let Some(font) = &font {
            add_text(
                &mut inner,
                font.clone(),
                "Retained AA",
                [x + 50.25, y + 60.5],
                18.0 + index as f32,
            )?;
        }
        if index >= 4 {
            // Clear in an ordinary owner cannot initialize its outer AA parent.
            let mut isolated = Scene::default();
            isolated.clear_target([0.3, 0.1, 0.8, 0.25])?;
            isolated.draw_analytic(AnalyticPrimitive::rectangle(
                r([56.0, 42.0, 172.0, 64.0]),
                [0.8, 0.9, 0.1, 0.75],
            ))?;
            let mut layer = LayerDraw::new(isolated);
            layer.bounds = Some(r([52.25, 38.5, 180.5, 72.25]));
            layer.opacity = 0.65;
            if index >= 8 {
                layer.blend = LayerBlend::Overlay;
            }
            inner.draw_layer(layer)?;
        }
        source.draw_axis_clip(r([24.75, 18.5, 242.5, 98.25]), inner)?;
        source.draw_analytic(AnalyticPrimitive::rectangle(
            r([24.0, 116.0, 248.0, 3.0]),
            [0.95, 0.95, 0.1, 1.0],
        ))?;
        // A retained closed child remains in the depth inventory but owns no
        // target when its actual extent is disjoint from the inherited clip.
        let mut hidden = Scene::default();
        hidden.clear_target([1.0, 0.0, 0.0, 1.0])?;
        source.push_clip_rect(r([280.0, 120.0, 8.0, 8.0]))?;
        source.draw_axis_clip(r([10.0, 10.0, 12.0, 12.0]), hidden)?;
        source.pop_scope()?;
        let mut group = Scene::default();
        group.draw_axis_clip(r([12.25, 10.5, 272.5, 120.25]), source)?;
        scene.push_clip_rect(r([
            8.5,
            8.5,
            if index % 3 == 2 { 230.0 } else { 280.0 },
            126.0,
        ]))?;
        if index >= 6 {
            // Offset ordinary target, independent opacity/mask, then AA source.
            let mut owner = LayerDraw::new(group);
            owner.bounds = Some(r([4.25, 4.5, 288.5, 134.25]));
            owner.opacity = 0.7;
            if index >= 10 {
                owner.blend = LayerBlend::Difference;
            }
            let mut mask = AnalyticMask::rectangle(r([5.0, 5.0, 286.0, 132.0]));
            mask.radii_x = [14.0; 4];
            mask.radii_y = [10.0; 4];
            scene.push_mask(mask)?;
            scene.draw_layer(owner)?;
            scene.pop_scope()?;
        } else {
            scene.append(&group)?;
        }
        scene.pop_scope()?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            r([4.0, 136.0, 288.0, 3.0]),
            [0.1, 1.0, 0.6, 1.0],
        ))?;
    }
    // Resolved after the deferred children; their actual atlas keys stay pinned
    // until all source scenes and the final texture bindings have been prepared.
    if let Some(font) = font {
        add_text(
            &mut scene,
            font,
            "Rust 0123456789 ABCxyz",
            [20.25, 618.5],
            15.0,
        )?;
    }
    Ok(scene)
}

fn add_text(
    scene: &mut Scene,
    font: ManagedFont,
    value: &str,
    position: [f32; 2],
    size: f32,
) -> Result<()> {
    let layout = ManagedTextLayout::new(
        value.encode_utf16().collect::<Vec<_>>(),
        font,
        size,
        Default::default(),
        ManagedPreparationOptions::new(
            ManagedNormalizationProfile::Unicode17_0,
            ManagedMarkProfile::Unicode16_0,
            ManagedGraphemeProfile::Unicode16_0,
        ),
        &[] as &[ManagedFont],
    )?;
    let mut text = TextDraw::layout(Arc::new(layout));
    text.position = position;
    text.brush = Some(Arc::new(progpu::vector::Brush::new(
        progpu::vector::BrushKind::Solid([1.0; 4]),
    )));
    scene.draw_text(text, TextPaintContext::default())?;
    Ok(())
}
