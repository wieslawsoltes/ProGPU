//! Shared native/browser authored scene; production geometry stays in the crate.
use progpu::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    path::{FillRule, Path, Segment},
    scene::{PathDraw, PathSampling, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
pub fn sample_scene() -> Result<Scene> {
    let mut scene = Scene::default();
    for row in 0..6 {
        for column in 0..12 {
            let mut primitive = AnalyticPrimitive::rectangle(
                [
                    24.0 + column as f32 * 76.0,
                    24.0 + row as f32 * 74.0,
                    56.0,
                    50.0,
                ],
                [
                    0.15 + column as f32 * 0.055,
                    0.35 + row as f32 * 0.065,
                    0.9,
                    0.85,
                ],
            );
            primitive.shape =
                [Shape::Rectangle, Shape::Ellipse, Shape::RoundedRectangle][column % 3];
            primitive.corner_radius = 12.0;
            primitive.stroke_thickness = if row % 3 == 1 { 2.0 } else { 0.0 };
            primitive.aliased = row % 3 == 2;
            scene.draw_analytic(primitive)?;
        }
    }
    let quadratic = Segment::quadratic([[0.0, 40.0], [28.0, -40.0], [56.0, 40.0]]);
    let cubic = Segment::cubic([[0.0, 40.0], [0.0, -14.0], [56.0, -14.0], [56.0, 40.0]]);
    let rational = Segment {
        kind: 4,
        parameters: [0.5f32.to_bits(), 0, 0],
        ..quadratic
    };
    let paths: Vec<_> = [quadratic, cubic, rational]
        .into_iter()
        .map(|curve| {
            Path::new(
                vec![curve, Segment::line([56.0, 40.0], [0.0, 40.0])],
                [0.0, -40.0, 56.0, 40.0],
                FillRule::NonZero,
            )
        })
        .collect::<std::result::Result<_, _>>()?;
    for row in 6..8 {
        for column in 0..12 {
            // Repeated immutable paths share rasters at equal scale and phase.
            // Interleaved translucent analytic paint tests command ordering.
            scene.draw_path(PathDraw {
                path: paths[column % paths.len()].clone(),
                transform: Affine([
                    1.0,
                    0.0,
                    0.0,
                    1.0,
                    24.0 + column as f32 * 76.0,
                    24.0 + row as f32 * 74.0,
                ]),
                color: [0.95, 0.35 + column as f32 * 0.035, 0.25, 0.9],
                sampling: PathSampling::default(),
            })?;
            scene.draw_analytic(AnalyticPrimitive::rectangle(
                [
                    36.0 + column as f32 * 76.0,
                    44.0 + row as f32 * 74.0,
                    32.0,
                    10.0,
                ],
                [0.1, 0.6, 1.0, 0.35],
            ))?;
        }
    }
    Ok(scene)
}
