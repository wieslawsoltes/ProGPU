//! Shared authored v10 archive recording/replay gallery, without a pixel oracle.
use progpu::{
    font::ManagedFont,
    geometry::Affine,
    picture_archive::{
        ArchiveBrush, ArchiveCommand, ArchiveCommandKind as Kind, ArchivePath, ArchivePen,
        ArchivePicture, ArchiveRecorder, ArchiveReplayOptions, PictureArchive,
    },
    scene::Scene,
    text::paint::TextPaintContext,
    vector::{
        Brush, BrushKind, Matrix4, PathGeometry, PenOptions, VertexColorBlend, VertexMesh,
        VertexMode,
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub fn archive_scene(font: Option<ManagedFont>, dpi: f32) -> Result<Scene> {
    let mut recording = ArchiveRecorder::new();
    let colors = [
        [0.13, 0.65, 0.85, 1.0],
        [0.95, 0.38, 0.22, 1.0],
        [0.38, 0.78, 0.49, 1.0],
    ];
    for panel in 0..12 {
        let x = 12.0 + (panel % 4) as f32 * 228.0;
        let y = 12.0 + (panel / 4) as f32 * 170.0;
        let transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, x, y]));
        let brush = Arc::new(ArchiveBrush::from(Brush::new(BrushKind::Solid(
            colors[panel % 3],
        ))));
        recording.record(ArchiveCommand {
            kind: Kind::DrawRect,
            rect: [0.0, 0.0, 216.0, 156.0],
            brush: Some(Arc::new(ArchiveBrush::from(Brush::new(BrushKind::Solid(
                [0.07, 0.09, 0.14, 1.0],
            ))))),
            transform,
            ..Default::default()
        })?;
        let pen = Arc::new(ArchivePen {
            brush: brush.clone(),
            options: PenOptions {
                thickness: 6.0,
                ..Default::default()
            },
            dashes: None,
        });
        let mut command = ArchiveCommand {
            brush: Some(brush),
            transform,
            path_sample_grid: 4,
            path_coverage_gamma: 1.0,
            is_pen_thickness_local: true,
            ..Default::default()
        };
        match panel {
            0 => {
                command.kind = Kind::DrawRect;
                command.rect = [24.0, 23.0, 165.0, 108.0];
            }
            1 | 2 => {
                command.kind = if panel == 1 {
                    Kind::DrawEllipse
                } else {
                    Kind::DrawCircle
                };
                command.position2 = [108.0, 78.0];
                command.radius_x = if panel == 1 { 83.0 } else { 54.0 };
                command.radius_y = 48.0;
            }
            3 => {
                command.kind = Kind::DrawRoundedRect;
                command.rect = [24.0, 23.0, 165.0, 108.0];
                command.corner_radius = 22.0;
                command.radius_x = 22.0;
                command.radius_y = 22.0;
            }
            4 | 5 => {
                command.kind = if panel == 4 {
                    Kind::FillTriangle
                } else {
                    Kind::FillQuad
                };
                command.position = [25.0, 118.0];
                command.position2 = [62.0, 22.0];
                command.position3 = [187.0, 46.0];
                command.position4 = [166.0, 127.0];
            }
            6 => {
                command.kind = Kind::DrawPath;
                command.path = Some(Arc::new(ArchivePath::from_geometry(&PathGeometry::parse(
                    "M 23 105 C 30 5 150 5 189 105 Q 105 55 23 105 Z",
                )?)?));
            }
            7 => {
                command.kind = Kind::DrawCubicBezier;
                command.brush = None;
                command.pen = Some(pen);
                command.position = [22.0, 119.0];
                command.position2 = [68.0, -10.0];
                command.position3 = [142.0, 168.0];
                command.position4 = [193.0, 28.0];
            }
            8 => {
                command.kind = Kind::DrawPointBatch;
                command.radius_x = 4.0;
                command.int_param = 1;
                let points: Vec<_> = (0..35)
                    .map(|i| [30.0 + (i % 7) as f32 * 26.0, 28.0 + (i / 7) as f32 * 25.0])
                    .collect();
                let range = recording.append_points(&points)?;
                command.point_buffer_offset = range.offset;
                command.point_buffer_count = range.count;
            }
            9 => {
                command.kind = Kind::DrawDotGrid;
                command.rect = [20.0, 22.0, 176.0, 112.0];
                command.radius_x = 15.0;
                command.radius_y = 15.0;
                command.position2 = [2.5, 0.0];
            }
            10 => {
                let nested = ArchivePicture {
                    commands: vec![ArchiveCommand {
                        kind: Kind::FillTriangle,
                        brush: command.brush.clone(),
                        position: [20.0, 130.0],
                        position2: [108.0, 15.0],
                        position3: [196.0, 130.0],
                        ..Default::default()
                    }]
                    .into(),
                    ..Default::default()
                };
                recording.record(ArchiveCommand {
                    kind: Kind::PushClip,
                    rect: [45.0, 32.0, 128.0, 91.0],
                    transform,
                    ..Default::default()
                })?;
                command.kind = Kind::DrawPicture;
                command.picture = Some(Arc::new(nested));
            }
            _ => {
                command.kind = Kind::DrawVertexMesh;
                command.vertex_mesh = Some(Arc::new(VertexMesh::from_owned(
                    VertexMode::Triangles,
                    vec![[24.0, 123.0], [105.0, 22.0], [192.0, 123.0]],
                    Vec::new(),
                    vec![
                        [0.94, 0.24, 0.18, 1.0],
                        [0.16, 0.82, 0.4, 1.0],
                        [0.2, 0.42, 0.98, 1.0],
                    ],
                    Vec::new(),
                )?));
                command.vertex_color_blend_mode = VertexColorBlend::Dst;
            }
        }
        recording.record(command)?;
        if panel == 10 {
            recording.record(ArchiveCommand {
                kind: Kind::PopClip,
                ..Default::default()
            })?;
        }
    }
    if let Some(font) = font {
        let indices: Vec<_> = "Owned Rust picture archive"
            .chars()
            .map(|c| font.glyph_index(c as u32))
            .collect::<std::result::Result<_, _>>()?;
        let positions: Vec<_> = (0..indices.len())
            .map(|i| [24.0 + i as f32 * 24.0, 568.0])
            .collect();
        recording.record(ArchiveCommand {
            kind: Kind::DrawGlyphRun,
            font: Some(font),
            font_size: 27.0,
            glyph_indices: Some(indices.into()),
            glyph_positions: Some(positions.into()),
            brush: Some(Arc::new(ArchiveBrush::from(Brush::new(BrushKind::Solid(
                [0.9, 0.94, 1.0, 1.0],
            ))))),
            prefer_glyph_atlas: true,
            ..Default::default()
        })?;
    }
    let source = PictureArchive::new(recording.finish(), [0.0, 0.0, 936.0, 590.0]);
    let encoded = source.serialize()?;
    let decoded = PictureArchive::deserialize(&encoded)?;
    Ok(decoded.picture.to_scene_with(ArchiveReplayOptions {
        text_context: TextPaintContext {
            dpi_scale: dpi,
            ..Default::default()
        },
        ..Default::default()
    })?)
}
