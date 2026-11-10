//! Shared path/brush/picture and mixed retained-mask integration gallery.
use progpu::{
    geometry::{Affine, AnalyticPrimitive, Shape},
    image::{Image, ImageAlpha, ImageDraw, ImageFormat},
    image_effect::ImageEffectDraw,
    mask::{
        AnalyticMask, ClipPath, ClipPlan, MaskChannel, MaskComposer, MaskCompositionMetrics,
        MaskOperation, TextureMask,
    },
    path::{ExecutionPreference, FillRule, Path, Segment},
    renderer::{Frame, MaskBrushFrame, Renderer},
    scene::{LayerBlend, LayerDraw, MaterialPaint, Scene},
    vector::{Brush, BrushKind, GradientStop, HatchPattern, LinearGradient},
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn materialized_mask_scene(
    renderer: &mut Renderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
) -> Result<(Scene, MaskCompositionMetrics)> {
    let size = [(128.0 * dpi).ceil() as u32, (96.0 * dpi).ceil() as u32];
    let frame = Frame {
        width: size[0],
        height: size[1],
        dpi_scale: dpi,
    };
    let path = Path::new(
        vec![
            Segment::line([8.0, 8.0], [110.0, 8.0]),
            Segment::quadratic([[110.0, 8.0], [138.0, 50.0], [110.0, 88.0]]),
            Segment::cubic([[110.0, 88.0], [86.0, 70.0], [40.0, 110.0], [8.0, 88.0]]),
            Segment::line([8.0, 88.0], [8.0, 8.0]),
        ],
        [8.0, 8.0, 128.0, 96.0],
        FillRule::NonZero,
    )?;
    let hole = Path::new(
        vec![
            Segment::line([40.0, 28.0], [84.0, 28.0]),
            Segment::line([84.0, 28.0], [84.0, 68.0]),
            Segment::line([84.0, 68.0], [40.0, 68.0]),
            Segment::line([40.0, 68.0], [40.0, 28.0]),
        ],
        [40.0, 28.0, 84.0, 68.0],
        FillRule::NonZero,
    )?;
    let mut composer = MaskComposer::new(device, queue, ExecutionPreference::Automatic)?;
    let mut paths = Vec::new();
    let mut metrics = MaskCompositionMetrics::default();
    for mode in 0..4 {
        let mut node = ClipPath::new(path.clone());
        if mode == 2 {
            node.transform = Affine([1.0, 0.0, 0.0, 1.0, 0.35, 0.625]);
            node.sample_grid = 4;
        }
        if mode == 3 {
            node.transform = Affine([0.85, 0.1, -0.12, 0.85, 12.0, 0.0]);
        }
        let mut nodes = vec![node];
        if mode != 0 {
            let mut remove = ClipPath::new(hole.clone());
            remove.operation = MaskOperation::Difference;
            nodes.push(remove);
        }
        let plan = ClipPlan::new(&nodes, size, dpi)?;
        let prepared = composer.prepare_clip(&plan).await?;
        let mut encoder = device.create_command_encoder(&Default::default());
        add(&mut metrics, composer.encode(&mut encoder, &prepared)?);
        queue.submit([encoder.finish()]);
        paths.push(prepared.into_image());
    }
    let mut brushes = Vec::new();
    for kind in [
        BrushKind::Linear(LinearGradient::new(
            [0.0, 0.0],
            [128.0, 96.0],
            vec![
                GradientStop {
                    offset: 0.0,
                    color: [0.0, 0.0, 0.0, 0.05],
                },
                GradientStop {
                    offset: 0.5,
                    color: [1.0, 0.0, 1.0, 0.6],
                },
                GradientStop {
                    offset: 1.0,
                    color: [1.0; 4],
                },
            ],
        )),
        BrushKind::Hatch(HatchPattern::new(0.5, 12.0, 4.0, [0.2, 0.8, 1.0, 0.85])),
    ] {
        let mut scene = Scene::default();
        let mut shape = AnalyticPrimitive::rectangle([4.0, 4.0, 120.0, 88.0], [1.0; 4]);
        shape.shape = Shape::Ellipse;
        scene.draw_material_analytic(
            shape,
            MaterialPaint::new(Arc::new(Brush::new(kind))),
            [64.0, 48.0],
        )?;
        let compiled = scene.compile(dpi)?;
        let captured = renderer
            .capture_geometry_mask(&compiled, frame, MaskBrushFrame::Target)
            .await?;
        add(&mut metrics, captured.metrics());
        brushes.push(captured.into_image());
    }
    let mut picture = Scene::default();
    picture.draw_analytic(AnalyticPrimitive::rectangle(
        [4.0, 6.0, 80.0, 70.0],
        [1.0, 0.0, 0.0, 0.4],
    ))?;
    let mut ellipse = AnalyticPrimitive::rectangle([30.0, 18.0, 90.0, 70.0], [0.0, 0.5, 1.0, 0.7]);
    ellipse.shape = Shape::Ellipse;
    picture.draw_analytic(ellipse)?;
    let prepared = renderer.prepare_scene(&picture, frame).await?;
    let picture = renderer
        .capture(&prepared, None)
        .await?
        .into_mask([0.0, 0.0, 128.0, 96.0]);
    let content = Image::rgba8(
        [16, 16],
        ImageAlpha::Straight,
        (0..256)
            .flat_map(|i| [32 + (i % 16) as u8 * 12, 50 + (i / 16) as u8 * 10, 220, 255])
            .collect::<Vec<_>>(),
    )?;
    let coverage = Image::new(
        [16, 16],
        16,
        ImageFormat::R8,
        ImageAlpha::Straight,
        (0..256).map(|i| (i % 16) as u8 * 17).collect::<Vec<_>>(),
    )?;
    let mut scene = Scene::default();
    for index in 0..16 {
        let x = 14.25 + (index % 4) as f32 * 236.0;
        let y = 12.5 + (index / 4) as f32 * 154.0;
        let bounds = [x + 10.0, y + 10.0, 208.0, 124.0];
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 228.0, 144.0],
            [0.14, 0.18, 0.26, 1.0],
        ))?;
        let mut child = Scene::default();
        let mut scopes = 0;
        match index {
            0..=3 => {
                child.push_mask(TextureMask::new(
                    paths[index].clone(),
                    bounds,
                    MaskChannel::Red,
                ))?;
                scopes += 1;
            }
            4 => {
                child.push_mask(TextureMask::new(coverage.clone(), bounds, MaskChannel::Red))?;
                let mut mask = picture.clone();
                mask.bounds = bounds;
                child.push_mask(mask)?;
                scopes += 2;
            }
            5 | 6 => {
                child.push_mask(TextureMask::new(
                    brushes[index - 5].clone(),
                    bounds,
                    MaskChannel::Red,
                ))?;
                scopes += 1;
            }
            7 => {
                let mut mask = picture.clone();
                mask.bounds = bounds;
                child.push_mask(mask)?;
                scopes += 1;
            }
            8 | 11 => {
                for i in 0..if index == 11 { 64 } else { 5 } {
                    let inset = (i % 8) as f32 * 0.5;
                    let mut mask = AnalyticMask::rectangle([
                        bounds[0] + inset,
                        bounds[1] + inset,
                        bounds[2] - 2.0 * inset,
                        bounds[3] - 2.0 * inset,
                    ]);
                    mask.radii_x = [16.0; 4];
                    mask.radii_y = [12.0; 4];
                    child.push_mask(mask)?;
                    scopes += 1;
                }
            }
            9 => {
                child.push_mask_difference(AnalyticMask::rectangle([
                    x + 60.0,
                    y + 42.0,
                    100.0,
                    55.0,
                ]))?;
                scopes += 1;
            }
            10 | 13 | 14 => {
                child.push_mask(AnalyticMask::rectangle([x + 25.0, y + 20.0, 180.0, 105.0]))?;
                child.push_mask(TextureMask::new(coverage.clone(), bounds, MaskChannel::Red))?;
                scopes += 2;
            }
            12 => {
                child.push_mask(AnalyticMask::axis_clip_area([
                    x + 20.25,
                    y + 17.75,
                    187.5,
                    115.5,
                ]))?;
                scopes += 1;
            }
            15 => {
                child.push_mask(TextureMask::new(paths[2].clone(), bounds, MaskChannel::Red))?;
                child.push_mask(TextureMask::new(
                    brushes[0].clone(),
                    bounds,
                    MaskChannel::Red,
                ))?;
                let mut mask = picture.clone();
                mask.bounds = bounds;
                child.push_mask(mask)?;
                child.push_mask_difference(AnalyticMask::rectangle([
                    x + 75.0,
                    y + 45.0,
                    45.0,
                    45.0,
                ]))?;
                scopes += 4;
            }
            _ => unreachable!(),
        }
        child.push_clip_rect(bounds)?;
        if index == 12 {
            let mut draw = ImageEffectDraw::new(content.clone(), bounds);
            draw.effect.effects1[0] = 0.8;
            child.draw_image_effect(draw)?;
        } else {
            child.draw_image(ImageDraw::new(content.clone(), bounds))?;
        }
        child.pop_scope()?;
        for _ in 0..scopes {
            child.pop_scope()?;
        }
        if index == 13 {
            let mut layer = LayerDraw::new(child);
            layer.bounds = Some(bounds);
            layer.opacity = 0.85;
            layer.blend = LayerBlend::Overlay;
            scene.draw_layer(layer)?;
        } else if index == 14 {
            scene.draw_axis_clip([x + 18.25, y + 16.5, 196.5, 113.25], child)?;
        } else {
            scene.append(&child)?;
        }
    }
    Ok((scene, metrics))
}
fn add(total: &mut MaskCompositionMetrics, source: MaskCompositionMetrics) {
    total.nodes += source.nodes;
    total.render_passes += source.render_passes;
    total.texture_bytes += source.texture_bytes;
    total.atlas_bytes += source.atlas_bytes;
    total.uniform_bytes += source.uniform_bytes;
    total.geometry_bytes += source.geometry_bytes;
}
