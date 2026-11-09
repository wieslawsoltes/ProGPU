//! Shared authored material gallery. This is an integration sample, not an
//! original-source pixel oracle or completed native/browser qualification.
use progpu::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    path::{FillRule, Path, Segment},
    scene::{MaterialPaint, PathDraw, PathSampling, Scene},
    vector::{
        Brush, BrushKind, GradientStop, HatchLineFamily, HatchPattern, HatchPatternSet,
        LinearGradient, Matrix4, PathGradient, PathGradientBlendStop, PerlinNoise, RadialGradient,
        SweepGradient, TilePattern, TwoPointConicalGradient,
    },
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub fn material_scene() -> Result<Scene> {
    let colors = [
        [0.12, 0.72, 0.95, 1.0],
        [0.85, 0.28, 0.62, 0.9],
        [1.0, 0.78, 0.22, 1.0],
    ];
    let stops: Vec<_> = colors
        .iter()
        .enumerate()
        .map(|(i, color)| GradientStop {
            color: *color,
            offset: i as f32 * 0.5,
        })
        .collect();
    let path_gradient = PathGradient::new(
        &[[0.0, 0.0], [200.0, 0.0], [200.0, 132.0], [0.0, 132.0]],
        &[colors[0], colors[1], colors[2], colors[0]],
        [100.0, 66.0],
        [1.0; 4],
        &[
            PathGradientBlendStop {
                factor: 0.0,
                offset: 0.0,
            },
            PathGradientBlendStop {
                factor: 1.0,
                offset: 1.0,
            },
        ],
        &[],
    )?;
    let hatch_set = HatchPatternSet::new(
        &[
            HatchLineFamily {
                base: [0.0, 0.0],
                direction: [1.0, 0.0],
                tangent_shift: 3.0,
                spacing: 14.0,
                dash_offset: 0,
                dash_count: 2,
                dash_period: 10.0,
            },
            HatchLineFamily {
                base: [2.0, 2.0],
                direction: [0.0, 1.0],
                tangent_shift: 0.0,
                spacing: 20.0,
                dash_offset: 2,
                dash_count: 2,
                dash_period: 6.0,
            },
        ],
        &[6.0, -4.0, 0.0, -6.0],
        1.5,
        colors[0],
    )?;
    let kinds = vec![
        BrushKind::Solid(colors[0]),
        BrushKind::Linear(LinearGradient::new(
            [0.0, 0.0],
            [200.0, 132.0],
            stops.clone(),
        )),
        BrushKind::Radial(RadialGradient::new(
            [100.0, 66.0],
            [75.0, 45.0],
            [100.0, 66.0],
            stops.clone(),
        )),
        BrushKind::TwoPointConical(TwoPointConicalGradient::new(
            [75.0, 55.0],
            3.0,
            [105.0, 66.0],
            100.0,
            stops.clone(),
        )),
        BrushKind::Sweep(SweepGradient::new([100.0, 66.0], stops.clone())),
        BrushKind::PerlinNoise(PerlinNoise::new(
            false,
            [0.025, 0.04],
            4,
            17.0,
            [200.0, 132.0],
        )),
        BrushKind::Hatch(HatchPattern::new(0.55, 12.0, 2.0, colors[2])),
        BrushKind::CrossHatch(HatchPattern::new(0.55, 16.0, 1.25, colors[1])),
        BrushKind::Tile(TilePattern::new(
            0xaa55_aa55_aa55_aa55,
            colors[0],
            colors[1],
            [0.0, 0.0],
        )),
        BrushKind::HatchSet(hatch_set),
        BrushKind::Path(path_gradient),
    ];
    let mut scene = Scene::default();
    for (index, mut kind) in kinds.into_iter().enumerate() {
        let x = 28.0 + (index % 4) as f32 * 228.0;
        let y = 28.0 + (index / 4) as f32 * 170.0;
        let transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, -x, -y]));
        match &mut kind {
            BrushKind::Solid(_) => {}
            BrushKind::Linear(g) => g.options.transform = transform,
            BrushKind::Radial(g) => g.options.transform = transform,
            BrushKind::Path(g) => g.options.transform = transform,
            BrushKind::TwoPointConical(g) => g.options.transform = transform,
            BrushKind::Sweep(g) => g.options.transform = transform,
            BrushKind::PerlinNoise(g) => g.transform = transform,
            BrushKind::Hatch(g) | BrushKind::CrossHatch(g) => g.transform = transform,
            BrushKind::HatchSet(g) => g.transform = transform,
            BrushKind::Tile(tile) => {
                *tile =
                    TilePattern::new(tile.pattern(), tile.foreground(), tile.background(), [x, y])
            }
        }
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x - 2.0, y - 2.0, 204.0, 136.0],
            [0.1, 0.13, 0.18, 1.0],
        ))?;
        let mut primitive = AnalyticPrimitive::rectangle([x, y, 200.0, 132.0], [0.0; 4]);
        primitive.shape = [Shape::RoundedRectangle, Shape::Rectangle, Shape::Ellipse][index % 3];
        primitive.corner_radius = 18.0;
        scene.draw_material_analytic(
            primitive,
            MaterialPaint::new(Arc::new(Brush::new(kind))),
            [x + 100.0, y + 66.0],
        )?;
    }
    let path = Path::new(
        vec![
            Segment::cubic([[0.0, 56.0], [24.0, -18.0], [156.0, -18.0], [180.0, 56.0]]),
            Segment::line([180.0, 56.0], [0.0, 56.0]),
        ],
        [0.0, -18.0, 180.0, 56.0],
        FillRule::NonZero,
    )?;
    let paint = Arc::new(Brush::new(BrushKind::Linear(LinearGradient::new(
        [0.0, 0.0],
        [180.0, 56.0],
        stops,
    ))));
    for index in 0..4 {
        scene.draw_material_path(
            PathDraw {
                path: path.clone(),
                transform: Affine([1.0, 0.0, 0.0, 1.0, 28.0 + index as f32 * 228.0, 552.0]),
                color: [0.0; 4],
                sampling: PathSampling::default(),
            },
            MaterialPaint {
                brush: paint.clone(),
                semantic_opacity: 1.0 - index as f32 * 0.18,
            },
        )?;
    }
    Ok(scene)
}
