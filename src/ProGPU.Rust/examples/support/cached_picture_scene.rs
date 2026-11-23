//! Authored retained sources reused across original placement and opacity scopes.
use progpu::{
    Error,
    composition::cached_picture::{CachedPicture, CachedPictureSnapshot},
    geometry::{Affine, AnalyticPrimitive},
    scene::{CachedPictureDraw, LayerDraw, Scene},
    vector::Matrix4,
};
use std::sync::Arc;
/// Twelve final consumers share three original immutable source/cache policies.
/// Actual-target lowering performs capture; recording never initializes WebGPU.
pub fn cached_picture_scene() -> Result<Scene, Error> {
    let mut source = Scene::default();
    source.draw_analytic(AnalyticPrimitive::rectangle(
        [8.5, 12.25, 144.0, 92.0],
        [0.09, 0.2, 0.45, 0.9],
    ))?;
    for i in 0..6 {
        source.draw_analytic(AnalyticPrimitive::rectangle(
            [14.0 + i as f32 * 23.0, 19.0, 14.0, 78.0],
            [0.9, 0.18 + i as f32 * 0.13, 0.12, 0.85],
        ))?;
    }
    let source = Arc::new(source);
    let mut pictures = Vec::new();
    for scale in [0.75, 1.0, 1.5] {
        let mut snapshot = CachedPictureSnapshot::new(source.clone(), [8.5, 12.25, 144.0, 92.0])?;
        snapshot.render_scale = scale;
        snapshot.enable_cleartype = false;
        pictures.push(CachedPicture::new(snapshot)?);
    }
    let mut result = Scene::default();
    for i in 0..12 {
        let x = 12.0 + (i % 4) as f32 * 236.0;
        let y = 12.0 + (i / 4) as f32 * 186.0;
        result.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 174.0],
            [0.12, 0.16, 0.24, 1.0],
        ))?;
        let mut draw = CachedPictureDraw::new(pictures[i % 3].clone());
        draw.transform = Matrix4::from_affine(Affine([
            1.25,
            if i >= 4 { 0.1 } else { 0.0 },
            -0.04,
            1.2,
            x + 10.0,
            y + 14.0,
        ]));
        draw.opacity = if i >= 8 { 0.65 } else { 1.0 };
        if i >= 8 {
            let mut child = Scene::default();
            child.draw_cached_picture(draw)?;
            let mut layer = LayerDraw::new(child);
            layer.bounds = Some([x + 2.25, y + 2.5, 219.0, 167.0]);
            result.draw_layer(layer)?;
        } else {
            result.push_clip_rect([x + 2.0, y + 2.0, 220.0, 170.0])?;
            result.draw_cached_picture(draw)?;
            result.pop_scope()?;
        }
    }
    Ok(result)
}
