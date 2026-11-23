//! Shared authored static-DXF gallery. No source/pixel equivalence is implied.
use progpu::{
    font::ManagedFont,
    geometry::{Affine, AnalyticPrimitive},
    picture_archive::{
        ArchiveBrush, ArchiveCommand, ArchiveCommandKind as Kind, ArchivePath, ArchivePen,
        ArchiveRecorder, StaticDxfBuffer, StaticDxfCompileOptions, StaticHatchViewport,
    },
    scene::{LayerBlend, Scene, StaticDxfDraw, StaticDxfRoute},
    vector::{
        Brush, BrushKind, Matrix4, PathGeometry, PenOptions, VertexColorBlend, VertexMesh,
        VertexMode,
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
fn brush(color: [f32; 4]) -> Arc<ArchiveBrush> {
    Arc::new(ArchiveBrush::from(Brush::new(BrushKind::Solid(color))))
}
fn mixed_source(dpi: f32, font: Option<ManagedFont>) -> Result<StaticDxfBuffer> {
    let mut source = ArchiveRecorder::new();
    let cyan = brush([0.1, 0.76, 0.92, 0.9]);
    let coral = brush([0.98, 0.38, 0.24, 0.85]);
    let gold = brush([0.98, 0.78, 0.24, 0.9]);
    let pen = Arc::new(ArchivePen {
        brush: cyan.clone(),
        options: PenOptions {
            thickness: 4.0,
            ..Default::default()
        },
        dashes: None,
    });
    source.record(ArchiveCommand {
        kind: Kind::DrawRoundedRect,
        rect: [6.0, 6.0, 244.0, 194.0],
        radius_x: 18.0,
        radius_y: 18.0,
        corner_radius: 18.0,
        brush: Some(brush([0.13, 0.17, 0.25, 1.0])),
        ..Default::default()
    })?;
    source.record(ArchiveCommand {
        kind: Kind::DrawEllipse,
        position2: [62.0, 54.0],
        radius_x: 41.0,
        radius_y: 29.0,
        brush: Some(cyan.clone()),
        ..Default::default()
    })?;
    source.record(ArchiveCommand {
        kind: Kind::DrawPath,
        path: Some(Arc::new(ArchivePath::from_geometry(&PathGeometry::parse(
            "M 122 72 C 112 6 216 10 232 71 Q 172 38 122 72 Z",
        )?)?)),
        brush: Some(coral.clone()),
        path_sample_grid: 4,
        path_coverage_gamma: 1.0,
        ..Default::default()
    })?;
    source.record(ArchiveCommand {
        kind: Kind::DrawHatch,
        path: Some(Arc::new(ArchivePath::from_geometry(&PathGeometry::parse(
            "M 20 103 L 91 90 Q 114 115 88 151 L 29 147 Z",
        )?)?)),
        brush: Some(gold.clone()),
        ..Default::default()
    })?;
    source.record(ArchiveCommand {
        kind: Kind::DrawVertexMesh,
        brush: Some(cyan.clone()),
        vertex_mesh: Some(Arc::new(VertexMesh::from_owned(
            VertexMode::Triangles,
            vec![[128.0, 151.0], [171.0, 91.0], [231.0, 147.0]],
            Vec::new(),
            vec![
                [0.92, 0.24, 0.25, 1.0],
                [0.14, 0.8, 0.5, 1.0],
                [0.22, 0.36, 0.98, 1.0],
            ],
            Vec::new(),
        )?)),
        vertex_color_blend_mode: VertexColorBlend::Dst,
        ..Default::default()
    })?;
    let points: Vec<_> = (0..13).map(|i| [21.0 + i as f32 * 17.5, 173.0]).collect();
    let range = source.append_points(&points)?;
    source.record(ArchiveCommand {
        kind: Kind::DrawPointBatch,
        point_buffer_offset: range.offset,
        point_buffer_count: range.count,
        radius_x: 2.5,
        int_param: 1,
        brush: Some(gold),
        ..Default::default()
    })?;
    source.record(ArchiveCommand {
        kind: Kind::DrawExtension,
        extension_id: 4,
        polyline_points: Some(
            vec![[18.0, 188.0], [65.0, 157.0], [179.0, 218.0], [237.0, 183.0]].into(),
        ),
        spline_knots: Some(vec![0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0].into()),
        spline_degree: 3,
        pen: Some(pen),
        is_pen_thickness_local: true,
        ..Default::default()
    })?;
    if let Some(font) = font {
        let indices = "Retained Rust"
            .chars()
            .map(|c| font.glyph_index(c as u32))
            .collect::<std::result::Result<Vec<_>, _>>()?;
        let positions: Vec<_> = (0..indices.len())
            .map(|i| [18.0 + i as f32 * 17.0, 228.0])
            .collect();
        source.record(ArchiveCommand {
            kind: Kind::DrawGlyphRun,
            font: Some(font),
            font_size: 21.0,
            glyph_indices: Some(indices.into()),
            glyph_positions: Some(positions.into()),
            brush: Some(brush([0.92, 0.95, 1.0, 1.0])),
            ..Default::default()
        })?;
    }
    Ok(StaticDxfBuffer::compile(
        Arc::new(source.finish()),
        StaticDxfCompileOptions {
            dpi_scale: dpi,
            ..Default::default()
        },
    )?)
}

/// Six independently prepared targets share one immutable static source. Panels
/// cover extension/legacy routes, zoom/pan, fixed blends and actual outer clips.
pub fn static_dxf_scene(dpi: f32, font: Option<ManagedFont>) -> Result<Scene> {
    let buffer = mixed_source(dpi, font)?;
    let mut scene = Scene::default();
    for panel in 0..6 {
        let x = 16.0 + (panel % 3) as f32 * 310.0;
        let y = 18.0 + (panel / 3) as f32 * 284.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 294.0, 266.0],
            [0.08, 0.10, 0.16, 1.0],
        ))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 28.0, y + 30.0, 218.0, 180.0],
            [0.25, 0.15, 0.22, 1.0],
        ))?;
        let mut draw = StaticDxfDraw::new(buffer.clone());
        draw.transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, x + 19.0, y + 26.0]));
        if panel == 1 {
            draw.route = StaticDxfRoute::Legacy;
        }
        let zoom = match panel {
            2 => 0.8,
            3 => 1.18,
            _ => 1.0,
        };
        draw.viewport = Some(StaticHatchViewport::new(
            zoom,
            if panel == 3 { [8.0, -4.0] } else { [0.0; 2] },
            [128.0, 104.0],
        )?);
        scene.push_clip_rect(if panel == 5 {
            [x + 55.0, y + 56.0, 172.0, 150.0]
        } else {
            [x + 8.0, y + 8.0, 278.0, 250.0]
        })?;
        if panel == 4 {
            scene.push_blend(LayerBlend::Screen)?;
        }
        scene.draw_static_dxf(draw)?;
        if panel == 4 {
            scene.pop_blend()?;
        }
        scene.pop_scope()?;
    }
    Ok(scene)
}
