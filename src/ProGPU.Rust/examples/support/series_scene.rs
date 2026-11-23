//! Shared authored series gallery. No source or pixel equivalence is implied.
use progpu::{
    Error,
    geometry::{Affine, AnalyticPrimitive},
    picture_archive::{ArchiveCommand, ArchiveCommandKind},
    renderer::Renderer,
    scene::Scene,
    series::{SeriesData, SeriesDraw, SeriesKind, SeriesLayout, SeriesShader},
    vector::{Brush, BrushKind, Matrix4},
};
use std::sync::Arc;

/// Twelve panels: both original shader families, explicit/packed scatter radii,
/// NaN line breaks, mirrored data, repeated CPU generations and static GPU owners.
pub fn series_scene(renderer: &mut Renderer) -> Result<Scene, Error> {
    let xy: Arc<[f32]> = (0..33)
        .flat_map(|i| {
            let x = i as f32 / 32.0;
            [x, (x * 12.0).sin() * 0.35 + 0.5]
        })
        .collect();
    let xyr: Arc<[f32]> = xy
        .chunks_exact(2)
        .enumerate()
        .flat_map(|(i, p)| [p[0], p[1], 2.0 + (i % 5) as f32])
        .collect();
    let mut broken = xy.to_vec();
    broken[28] = f32::NAN;
    let broken: Arc<[f32]> = broken.into();
    let mut scene = Scene::default();
    scene.draw_custom_grid(Arc::new(ArchiveCommand {
        kind: ArchiveCommandKind::DrawExtension,
        extension_id: 7,
        ..Default::default()
    }))?;
    for panel in 0..12 {
        let x = 12.0 + (panel % 4) as f32 * 234.0;
        let y = 12.0 + (panel / 4) as f32 * 194.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 220.0, 180.0],
            [0.08, 0.10, 0.16, 1.0],
        ))?;
        let kind = if panel % 4 < 2 {
            SeriesKind::Line
        } else {
            SeriesKind::Scatter
        };
        let (data, layout) = if panel % 4 == 3 {
            (xyr.clone(), SeriesLayout::XyRadius)
        } else if panel == 4 || panel == 5 {
            (broken.clone(), SeriesLayout::Xy)
        } else {
            (xy.clone(), SeriesLayout::Xy)
        };
        let mut draw = SeriesDraw::new(kind, SeriesData::new(data, 33, layout)?);
        draw.shader = if panel % 2 == 0 {
            SeriesShader::BuiltIn
        } else {
            SeriesShader::Extension
        };
        draw.transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, x + 18.0, y + 20.0]));
        draw.scale = [184.0, 132.0];
        if panel == 6 || panel == 7 {
            draw.scale[0] = -184.0;
            draw.translate[0] = 184.0;
        }
        draw.radius_or_width = if kind == SeriesKind::Line { 3.0 } else { 4.0 };
        draw.brush = Some(Arc::new(Brush::new(BrushKind::Solid(if panel % 2 == 0 {
            [0.15, 0.74, 0.92, 0.85]
        } else {
            [0.98, 0.49, 0.25, 0.85]
        }))));
        if panel >= 8 {
            draw.source = renderer.realize_series(&draw)?.into();
        }
        scene.push_clip_rect([x + 8.0, y + 8.0, 204.0, 164.0])?;
        scene.draw_series(draw)?;
        scene.pop_scope()?;
    }
    Ok(scene)
}
