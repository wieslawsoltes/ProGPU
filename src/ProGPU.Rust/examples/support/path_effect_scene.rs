//! Authored analytic measurement and all nine original path-effect families.
use progpu::{
    Error,
    geometry::{Affine, AnalyticPrimitive},
    scene::{PathDraw, PathSampling, Scene},
    vector::{
        Brush, BrushKind, EffectMatrix, EffectPaintStyle, FillRule, Matrix4, MeasureCommand as C,
        MeasuredPath, PathEffect, PathEffectKind as K, PathEffectSource, PathStampStyle, Pen,
        StrokeDraw, TrimMode,
    },
};
use std::sync::Arc;
/// Twelve panels include original stamp placement variants and trim complements.
pub fn path_effect_scene() -> Result<Scene, Error> {
    let source = PathEffectSource::new(
        MeasuredPath::new(vec![
            C::Move([8.0, 20.0]),
            C::Line([65.0, 5.0]),
            C::Line([155.0, 24.0]),
            C::Line([170.0, 100.0]),
            C::Line([80.0, 115.0]),
            C::Line([10.0, 92.0]),
            C::Close,
        ]),
        FillRule::NonZero,
        false,
    );
    let stamp = PathEffectSource::new(
        MeasuredPath::new(vec![
            C::Move([-3.0, -3.0]),
            C::Line([4.0, 0.0]),
            C::Line([-3.0, 3.0]),
            C::Close,
        ]),
        FillRule::NonZero,
        false,
    );
    let dash = PathEffect::dash(&[14.0, 7.0], 3.0)?;
    let corner = PathEffect::new(K::Corner(16.0))?;
    let effects = vec![
        dash.clone(),
        corner.clone(),
        PathEffect::new(K::Discrete {
            segment_length: 9.0,
            deviation: 3.0,
            seed: 17,
        })?,
        PathEffect::new(K::Trim {
            start: 0.15,
            stop: 0.8,
            mode: TrimMode::Normal,
        })?,
        PathEffect::new(K::Compose {
            outer: dash.clone(),
            inner: corner.clone(),
        })?,
        PathEffect::new(K::Sum {
            first: dash,
            second: PathEffect::new(K::Trim {
                start: 0.85,
                stop: 0.95,
                mode: TrimMode::Normal,
            })?,
        })?,
        PathEffect::new(K::Path1D {
            stamp: stamp.clone(),
            advance: 18.0,
            phase: 2.0,
            style: PathStampStyle::Translate,
        })?,
        PathEffect::new(K::Path1D {
            stamp: stamp.clone(),
            advance: 18.0,
            phase: 2.0,
            style: PathStampStyle::Rotate,
        })?,
        PathEffect::new(K::Path1D {
            stamp: stamp.clone(),
            advance: 18.0,
            phase: 2.0,
            style: PathStampStyle::Morph,
        })?,
        PathEffect::new(K::Line2D {
            width: 2.0,
            matrix: EffectMatrix([12.0, 3.0, 0.0, 0.0, 12.0, 0.0, 0.0, 0.0, 1.0]),
        })?,
        PathEffect::new(K::Path2D {
            stamp,
            matrix: EffectMatrix([17.0, 0.0, 0.0, 0.0, 17.0, 0.0, 0.0, 0.0, 1.0]),
        })?,
        PathEffect::new(K::Trim {
            start: 0.2,
            stop: 0.7,
            mode: TrimMode::Inverted,
        })?,
    ];
    let mut scene = Scene::default();
    for (index, effect) in effects.into_iter().enumerate() {
        let x = 12.0 + (index % 4) as f32 * 236.0;
        let y = 12.0 + (index / 4) as f32 * 186.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 224.0, 174.0],
            [0.12, 0.16, 0.24, 1.0],
        ))?;
        let result = effect.apply(&source, 2.0)?;
        if !result.applied {
            return Err(Error::InvalidInput);
        }
        let geometry = result.output.geometry()?;
        let color = [0.1 + index as f32 * 0.05, 0.8, 0.95, 1.0];
        let transform = Affine([1.0, 0.0, 0.0, 1.0, x + 20.0, y + 24.0]);
        if result.paint.style == Some(EffectPaintStyle::Fill) {
            if let Some(path) = geometry.compile_raster_path()? {
                scene.draw_path(PathDraw {
                    path,
                    transform,
                    color,
                    sampling: PathSampling::new(4)?,
                })?;
            }
        } else {
            let mut pen = Pen::new(Arc::new(Brush::new(BrushKind::Solid(color))));
            pen.options.thickness = result.paint.stroke_width.unwrap_or(2.0);
            let mut draw = StrokeDraw::new(Arc::new(geometry), Arc::new(pen));
            draw.frame.transform = Matrix4::from_affine(transform);
            scene.draw_stroke(draw)?;
        }
    }
    Ok(scene)
}
