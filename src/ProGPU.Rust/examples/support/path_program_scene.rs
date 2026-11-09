//! Original native program inputs through direct painting and inline/staged clips.
use progpu::{
    geometry::{Affine, AnalyticPrimitive},
    mask::{ClipPath, ClipPlan, MaskChannel, MaskComposer, TextureMask},
    path::{
        ExecutionPreference, FillRule, Path, PathInstruction as I, Segment, SignedWindingPolicy,
    },
    scene::{PathDraw, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

pub async fn path_program_scene(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
) -> Result<Scene> {
    let rectangle = |x: f32, y: f32, w: f32, h: f32, reverse: bool| -> Result<Path> {
        let mut points = vec![[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
        if reverse {
            points.reverse();
        }
        Ok(Path::new(
            (0..4)
                .map(|i| Segment::line(points[i], points[(i + 1) % 4]))
                .collect::<Vec<_>>(),
            [x, y, x + w, y + h],
            FillRule::NonZero,
        )?)
    };
    let a = rectangle(8.0, 10.0, 70.0, 64.0, false)?;
    let b = rectangle(30.0, 20.0, 70.0, 64.0, false)?;
    let reversed = rectangle(30.0, 20.0, 70.0, 64.0, true)?;
    let curve = Path::new(
        vec![
            Segment::line([10.0, 10.0], [98.0, 10.0]),
            Segment::quadratic([[98.0, 10.0], [122.0, 48.0], [98.0, 80.0]]),
            Segment::cubic([[98.0, 80.0], [65.0, 60.0], [40.0, 100.0], [10.0, 80.0]]),
            Segment::line([10.0, 80.0], [10.0, 10.0]),
        ],
        [10.0, 10.0, 122.0, 100.0],
        FillRule::NonZero,
    )?;
    let programs = vec![
        vec![I::Winding(a.clone()), I::Winding(a.clone()), I::Add],
        vec![I::Winding(a.clone()), I::Winding(reversed), I::Add],
        vec![I::Winding(a.clone()), I::Negate],
        vec![I::Winding(a.clone()), I::Leaf(b.clone()), I::Add],
        vec![I::Winding(curve), I::Winding(b.clone()), I::Negate, I::Add],
        vec![I::Leaf(a.clone()), I::Leaf(b.clone()), I::Xor],
        vec![I::Leaf(a.clone()), I::Leaf(b), I::Intersect],
        vec![I::Winding(a), I::Empty, I::Difference],
    ]
    .into_iter()
    .map(|nodes| Path::from_program(&nodes, [0.0, 0.0, 128.0, 104.0], FillRule::NonZero))
    .collect::<std::result::Result<Vec<_>, _>>()?;
    let size = [(128.0 * dpi).ceil() as u32, (104.0 * dpi).ceil() as u32];
    let mut inline = MaskComposer::new(device, queue, ExecutionPreference::Automatic)?;
    let mut staged = MaskComposer::new(device, queue, ExecutionPreference::Automatic)?;
    staged.set_signed_winding_policy(SignedWindingPolicy::Staged);
    let mut scene = Scene::default();
    for (index, path) in programs.iter().enumerate() {
        for mode in 0..3 {
            let column = (index % 2) * 3 + mode;
            let x = 10.25 + column as f32 * 154.0;
            let y = 10.5 + (index / 2) as f32 * 150.0;
            let bounds = [x, y, 128.0, 104.0];
            scene.draw_analytic(AnalyticPrimitive::rectangle(bounds, [0.1, 0.12, 0.17, 1.0]))?;
            let color = [0.18 + index as f32 * 0.07, 0.75, 0.92, 0.9];
            scene.push_clip_rect(bounds)?;
            if mode == 0 {
                scene.draw_path(PathDraw {
                    path: path.clone(),
                    transform: Affine([1.0, 0.0, 0.0, 1.0, x, y]),
                    color,
                    sampling: Default::default(),
                })?;
            } else {
                let composer = if mode == 1 { &mut inline } else { &mut staged };
                let plan = ClipPlan::new(&[ClipPath::new(path.clone())], size, dpi)?;
                let mask = composer.prepare_clip(&plan).await?;
                let mut encoder = device.create_command_encoder(&Default::default());
                composer.encode(&mut encoder, &mask)?;
                queue.submit([encoder.finish()]);
                scene.push_mask(TextureMask::new(
                    mask.into_image(),
                    bounds,
                    MaskChannel::Red,
                ))?;
                scene.draw_analytic(AnalyticPrimitive::rectangle(bounds, color))?;
                scene.pop_scope()?;
            }
            scene.pop_scope()?;
        }
    }
    Ok(scene)
}
