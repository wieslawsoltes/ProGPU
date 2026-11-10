//! Shared authored visual-tree sample. It uses actual original renderer entrypoints.
use progpu::{
    Error,
    composition::visual::{
        CompositionBrushValue, CompositionEffectValue as Effect, Visual, VisualBlurKernel,
        VisualCompositeState, VisualCompositor, VisualProperties,
    },
    geometry::{Affine, AnalyticPrimitive},
    scene::{LayerBlend, PathDraw, PathSampling, Scene},
    vector::{RoundRect, SweepDirection},
};
use std::sync::Arc;

/// Twelve panels retain one live tree across resize/re-preparation. No source UI
/// callback or geometry recording is performed while encoding a completed frame.
pub fn visual_scene() -> Result<Visual, Error> {
    let compositor = VisualCompositor::new();
    let root = compositor.create_container_visual();
    root.set_properties(VisualProperties {
        size: [960.0, 640.0],
        ..Default::default()
    })?;
    let content = Arc::new(content()?);
    for index in 0..12 {
        let x = 12.0 + (index % 4) as f32 * 236.0;
        let y = 12.0 + (index / 4) as f32 * 186.0;
        let panel = compositor.create_container_visual();
        panel.set_properties(VisualProperties {
            offset: [x, y],
            size: [224.0, 174.0],
            ..Default::default()
        })?;
        let mut background = Scene::default();
        background.draw_analytic(AnalyticPrimitive::rectangle(
            [0.0, 0.0, 224.0, 174.0],
            [0.12, 0.16, 0.24, 1.0],
        ))?;
        panel.add_child(
            compositor
                .create_drawing_visual(Arc::new(background))
                .into(),
        )?;
        let node = compositor.create_drawing_visual(content.clone());
        let mut properties = VisualProperties {
            offset: [20.0, 24.0],
            size: [180.0, 120.0],
            local_render_bounds: Some([0.0, 0.0, 180.0, 120.0]),
            ..Default::default()
        };
        match index {
            1 => properties.opacity = 0.55,
            2 => properties.rotation = 0.15,
            3 => properties.composite.cache_as_layer = true,
            4 => {
                properties.composite.requires_layer_cache = true;
                properties.composite.layer_cache_render_scale = Some(1.5);
            }
            5 => properties.composite.clip_bounds = Some([12.0, 8.0, 142.0, 94.0]),
            6 => {
                properties.composite.geometry_clip = Some(Arc::new(
                    RoundRect::uniform([4.0, 4.0, 176.0, 116.0], [25.0, 18.0])
                        .to_geometry(SweepDirection::Clockwise, 6)?,
                ))
            }
            _ => {}
        }
        node.set_properties(properties)?;
        match index {
            7 => node.set_effect(Some(compositor.create_effect(Effect::Blur {
                radius: 4.0,
                kernel: VisualBlurKernel::Gaussian,
            })))?,
            8 => node.set_effect(Some(compositor.create_effect(Effect::Blur {
                radius: 4.0,
                kernel: VisualBlurKernel::Box,
            })))?,
            9 => node.set_effect(Some(compositor.create_effect(Effect::DropShadow {
                radii: [5.0, 3.0],
                offset: [6.0, 5.0],
                color: [0.0, 0.0, 0.0, 0.8],
                draw_source: true,
            })))?,
            10 => node.set_effect(Some(
                compositor.create_effect(Effect::Blend(LayerBlend::Screen)),
            ))?,
            11 => {
                node.set_opacity_mask(Some(
                    compositor.create_brush(CompositionBrushValue::Color([255, 255, 255, 160]))?,
                ))?;
                node.set_composite_state(VisualCompositeState {
                    cache_as_layer: true,
                    ..Default::default()
                })?;
            }
            _ => {}
        }
        panel.add_child(node.into())?;
        let mut marker = Scene::default();
        marker.draw_analytic(AnalyticPrimitive::rectangle(
            [8.0, 156.0, 12.0 + index as f32 * 14.0, 4.0],
            [0.25, 0.8, 0.95, 1.0],
        ))?;
        panel.add_topmost_child(compositor.create_drawing_visual(Arc::new(marker)).into())?;
        root.add_child(panel.into())?;
    }
    Ok(root.into())
}
fn content() -> Result<Scene, Error> {
    let mut scene = Scene::default();
    let shape = RoundRect::from_radii(
        [0.0, 0.0, 180.0, 120.0],
        [[26.0, 18.0], [8.0, 8.0], [32.0, 24.0], [12.0, 18.0]],
    )
    .to_geometry(SweepDirection::Clockwise, 6)?;
    if let Some(path) = shape.compile_raster_path()? {
        scene.draw_path(PathDraw {
            path,
            transform: Affine::IDENTITY,
            color: [0.1, 0.65, 0.8, 1.0],
            sampling: PathSampling::new(4)?,
        })?;
    }
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [22.0, 18.0, 78.0, 70.0],
        [0.95, 0.4, 0.12, 0.85],
    ))?;
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [76.0, 45.0, 82.0, 55.0],
        [0.8, 0.2, 0.7, 0.8],
    ))?;
    Ok(scene)
}
