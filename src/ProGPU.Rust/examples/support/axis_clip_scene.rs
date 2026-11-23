//! Authored AA execution gallery; complete retained source recording is separate.
use progpu::{
    composition::{AxisClipFrame, AxisClipPlan},
    geometry::{AnalyticPrimitive, Shape},
    image::ImageDraw,
    renderer::{AxisClipMetrics, Frame, PreparedAxisClip, Renderer, ReplayMetrics},
    scene::{LayerBlend, LayerDraw, Scene},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

#[derive(Clone, Copy, Default)]
pub struct GalleryMetrics {
    pub groups: u32,
    pub texture_bytes: u64,
    pub uniform_bytes: u64,
    pub geometry_bytes: u64,
    pub resolve_draws: u32,
    pub background_copy_bytes: u64,
    pub source_draws: u32,
    pub source_uniform_bytes: u64,
    pub loaded_copy_bytes: u64,
}
impl GalleryMetrics {
    fn add(&mut self, m: AxisClipMetrics, replay: ReplayMetrics) {
        self.groups += 1;
        self.texture_bytes +=
            m.source_texture_bytes + m.coverage_texture_bytes + m.output_texture_bytes;
        self.uniform_bytes += m.uniform_bytes;
        self.geometry_bytes += m.geometry_bytes;
        self.resolve_draws += m.resolve_draws;
        self.background_copy_bytes += m.background_copy_bytes;
        self.source_draws += replay.draws;
        self.source_uniform_bytes += replay.uniform_upload_bytes;
        self.loaded_copy_bytes += replay.texture_copy_bytes;
    }
}
fn source(clip: &PreparedAxisClip, advanced: bool) -> Result<Scene> {
    let [_, _, w, h] = clip.plan().extent();
    let dpi = clip.plan().frame().semantic_dpi;
    let [w, h] = [w as f32 / dpi, h as f32 / dpi];
    let mut scene = Scene::default();
    if clip.plan().initializes_background() {
        scene.clear_target([1.0, 0.2, 0.6, 0.0])?;
    }
    scene.draw_analytic(AnalyticPrimitive::rectangle(
        [0.0, 0.0, w, h],
        [0.95, 0.2, 0.45, 0.5],
    ))?;
    let mut ellipse =
        AnalyticPrimitive::rectangle([w * 0.2, h * 0.1, w * 0.6, h * 0.8], [0.15, 1.0, 0.4, 0.7]);
    ellipse.shape = Shape::Ellipse;
    if advanced {
        let mut child = Scene::default();
        child.draw_analytic(ellipse)?;
        let mut layer = LayerDraw::new(child);
        layer.blend = LayerBlend::Overlay;
        scene.draw_layer(layer)?;
    } else {
        scene.draw_analytic(ellipse)?;
    }
    Ok(scene)
}
pub async fn axis_clip_scene(
    renderer: &mut Renderer,
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    dpi: f32,
) -> Result<(Scene, GalleryMetrics)> {
    let frame = Frame {
        width: (960.0 * dpi).ceil() as u32,
        height: (640.0 * dpi).ceil() as u32,
        dpi_scale: dpi,
    };
    let mut background = Scene::default();
    for index in 0..12 {
        let x = 16.0 + (index % 3) as f32 * 312.0;
        let y = 12.0 + (index / 3) as f32 * 156.0;
        background.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 296.0, 144.0],
            [0.08, 0.4, 0.85, 0.9],
        ))?;
    }
    let background = renderer.prepare_scene(&background, frame).await?;
    let parent = renderer.capture(&background, None).await?.into_image();
    let mut metrics = GalleryMetrics::default();
    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
        label: Some("ProGPU authored ordered AA gallery"),
    });
    for index in 0..12 {
        let x = 22.25 + (index % 3) as f32 * 312.0;
        let y = 20.5 + (index / 3) as f32 * 156.0;
        let initialize = index % 2 == 1;
        let mapping = AxisClipFrame {
            root_size: [frame.width, frame.height],
            parent: [0, 0, frame.width, frame.height],
            dpi: if index >= 10 {
                [dpi * 1.025, dpi * 0.95]
            } else {
                [dpi; 2]
            },
            semantic_dpi: dpi,
            viewport: if index >= 10 {
                [
                    3,
                    5,
                    frame.width.saturating_sub(8),
                    frame.height.saturating_sub(10),
                ]
            } else {
                [0, 0, frame.width, frame.height]
            },
        };
        let plan = AxisClipPlan::new([x, y, 280.5, 128.25], mapping, initialize)?;
        let outer = renderer.prepare_axis_clip(&parent, &plan).await?;
        if plan.is_empty() {
            continue;
        }
        let [_, _, w, h] = plan.extent();
        let content = renderer
            .prepare_scene(
                &source(&outer, index >= 8)?,
                Frame {
                    width: w,
                    height: h,
                    dpi_scale: dpi,
                },
            )
            .await?;
        let inner = if index >= 4 {
            let nested = plan.child([x + 17.125, y + 9.25, 240.75, 100.5], initialize)?;
            Some(
                renderer
                    .prepare_axis_clip(outer.source().ok_or("missing AA source")?, &nested)
                    .await?,
            )
        } else {
            None
        };
        let inner_content = if let Some(inner) = &inner {
            let [_, _, w, h] = inner.plan().extent();
            if inner.plan().is_empty() {
                None
            } else {
                Some(
                    renderer
                        .prepare_scene(
                            &source(inner, false)?,
                            Frame {
                                width: w,
                                height: h,
                                dpi_scale: dpi,
                            },
                        )
                        .await?,
                )
            }
        } else {
            None
        };
        let mut pass = outer.begin(&mut encoder).ok_or("missing AA pass")?;
        let (encoder, target) = pass.parts();
        let replay = renderer.encode_loaded(encoder, &content, target)?;
        if let (Some(inner), Some(content)) = (&inner, &inner_content) {
            let mut nested = inner.begin(encoder).ok_or("missing nested AA pass")?;
            let (encoder, target) = nested.parts();
            let replay = renderer.encode_loaded(encoder, content, target)?;
            nested.finish();
            metrics.add(inner.metrics(), replay);
        }
        pass.finish();
        metrics.add(outer.metrics(), replay);
    }
    queue.submit([encoder.finish()]);
    let mut scene = Scene::default();
    scene.draw_image(ImageDraw::new(parent, [0.0, 0.0, 960.0, 640.0]))?;
    Ok((scene, metrics))
}
