//! Authored retained mesh/patch and per-draw blend gallery, shared by both hosts.
use progpu::{
    Error,
    geometry::{Affine, AnalyticPrimitive},
    scene::{LayerBlend, Scene},
    vector::{
        Brush, BrushKind, CoonsPatch, Matrix4, VertexColorBlend as C, VertexMesh, VertexMeshDraw,
        VertexMode,
    },
};
use std::sync::Arc;
/// First 29 panels: original vertex-color modes. Next three: strip, fan, Coons.
/// Last sixteen: fourteen fixed destination modes, Coons and invalid-index skipping.
pub fn mesh_scene() -> Result<Scene, Error> {
    let color_modes = [
        C::Clear,
        C::Src,
        C::Dst,
        C::SrcOver,
        C::DstOver,
        C::SrcIn,
        C::DstIn,
        C::SrcOut,
        C::DstOut,
        C::SrcATop,
        C::DstATop,
        C::Xor,
        C::Plus,
        C::Modulate,
        C::Screen,
        C::Overlay,
        C::Darken,
        C::Lighten,
        C::ColorDodge,
        C::ColorBurn,
        C::HardLight,
        C::SoftLight,
        C::Difference,
        C::Exclusion,
        C::Multiply,
        C::Hue,
        C::Saturation,
        C::Color,
        C::Luminosity,
    ];
    let destination = [
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
    let positions = [[8.0, 10.0], [91.0, 12.0], [83.0, 70.0], [14.0, 68.0]];
    let colors = [
        [240, 40, 80, 230],
        [20, 230, 80, 150],
        [30, 70, 250, 180],
        [245, 210, 20, 220],
    ];
    let base = VertexMesh::copy_rgba8(
        VertexMode::Triangles,
        &positions,
        None,
        Some(&colors),
        Some(&[0, 1, 2, 0, 2, 3]),
    )?;
    let brush = Arc::new(Brush::new(BrushKind::Solid([0.2, 0.65, 0.9, 0.65])));
    let mut scene = Scene::default();
    for index in 0..48 {
        let x = 8.0 + (index % 8) as f32 * 118.0;
        let y = 8.0 + (index / 8) as f32 * 96.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 110.0, 88.0],
            [0.16, 0.2, 0.28, 1.0],
        ))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 40.0, y + 12.0, 56.0, 62.0],
            [0.8, 0.3, 0.18, 0.6],
        ))?;
        let transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, x, y]));
        let mesh = match index {
            29 => VertexMesh::copy_rgba8(
                VertexMode::TriangleStrip,
                &positions,
                None,
                Some(&colors),
                Some(&[0, 1, 3, 2]),
            )?,
            30 => VertexMesh::copy_rgba8(
                VertexMode::TriangleFan,
                &positions,
                None,
                Some(&colors),
                None,
            )?,
            31 | 46 => {
                let mut patch = CoonsPatch::new([
                    [8.0, 10.0],
                    [40.0, -5.0],
                    [70.0, 32.0],
                    [100.0, 10.0],
                    [85.0, 28.0],
                    [110.0, 50.0],
                    [100.0, 75.0],
                    [80.0, 56.0],
                    [35.0, 98.0],
                    [8.0, 75.0],
                    [28.0, 52.0],
                    [-8.0, 30.0],
                ]);
                patch.colors = Some(colors);
                patch.mesh(transform)?
            }
            47 => VertexMesh::copy_rgba8(
                VertexMode::Triangles,
                &positions,
                None,
                Some(&colors),
                Some(&[0, 1, 2, 0, 99, 3, 0, 2, 3]),
            )?,
            _ => base.clone(),
        };
        let mut draw = VertexMeshDraw::new(mesh, brush.clone());
        draw.transform = transform;
        draw.color_blend = color_modes.get(index).copied().unwrap_or(C::Modulate);
        let mode = index
            .checked_sub(32)
            .and_then(|i| destination.get(i))
            .copied();
        if let Some(mode) = mode {
            scene.push_blend(mode)?;
        }
        scene.draw_vertex_mesh(draw)?;
        if mode.is_some() {
            scene.pop_blend()?;
        }
    }
    Ok(scene)
}
