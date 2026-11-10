//! Original transparent RGBA8 render captures, replayed on the consumer encoder.
use super::*;
use crate::{
    composition::SourceRenderFrame,
    image_filter::FilterSampling,
    vector::{Affine, Matrix4},
};
pub(super) struct SceneCapture {
    renderer: Box<Renderer>,
    scene: Box<PreparedScene>,
    source: Arc<Scene>,
    output: GpuImage,
    view: wgpu::TextureView,
    source_capture: bool,
}
impl SceneCapture {
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        self.scene.retain_submitted_images(queue);
    }
    pub(super) fn output(&self) -> &GpuImage {
        &self.output
    }
    pub(super) fn current(&self) -> bool {
        self.renderer.is_scene_current(&self.scene)
    }
    pub(super) fn encode(&self, encoder: &mut wgpu::CommandEncoder) -> Result<(), Error> {
        let _source = &self.source;
        self.renderer
            .encode(encoder, &self.scene, &self.view, wgpu::Color::TRANSPARENT)
    }
    pub(super) fn add_metrics(&self, metrics: &mut ImageFilterMetrics) -> Result<(), Error> {
        let upload = self.scene.metrics();
        let layers = self.scene.layer_metrics();
        let replay = self.scene.replay_metrics();
        // Nested prepared metrics are already aggregated by their owning scene
        // and layer tree. Report source draws separately from this capture pass.
        metrics.merge(ImageFilterMetrics {
            render_passes: 1,
            replay_draws: u64::from(replay.draws) + layers.source_draws + layers.effect_passes,
            texture_bytes: u64::from(self.output.size()[0]) * u64::from(self.output.size()[1]) * 4
                + upload.image_texture_bytes
                + upload.mask_texture_bytes
                + layers.capture_bytes
                + layers.effect_texture_bytes,
            uniform_bytes: upload.uniform_bytes
                + layers.uniform_bytes
                + layers.effect_uniform_bytes,
            geometry_bytes: upload.geometry_bytes + layers.geometry_bytes,
            source_captures: u32::from(self.source_capture),
            source_draws: u64::from(replay.draws) + layers.source_draws,
            source_upload_bytes: upload.image_upload_bytes
                + upload.mask_upload_bytes
                + upload.glyph_upload_bytes
                + layers.image_mask_upload_bytes
                + layers.glyph_upload_bytes,
            source_replay_uniform_bytes: replay.uniform_upload_bytes
                + layers.source_replay_uniform_bytes,
            ..Default::default()
        })?;
        for mut child in [
            self.scene.image_filter_metrics(),
            layers.source_image_filters,
        ] {
            child.geometry_bytes = 0;
            child.uniform_bytes = 0;
            child.source_upload_bytes = 0;
            child.source_draws = 0;
            child.replay_draws = 0;
            child.source_replay_uniform_bytes = 0;
            metrics.merge(child)?;
        }
        for mut child in [
            self.scene.color_filter_metrics(),
            layers.source_color_filters,
        ] {
            child.geometry_bytes = 0;
            child.uniform_bytes = 0;
            let previous_replay = metrics.replay_draws;
            metrics.capture(child)?;
            metrics.replay_draws = previous_replay;
        }
        Ok(())
    }
}
pub(super) async fn prepare(
    renderer: &Renderer,
    source: Arc<Scene>,
    size: [u32; 2],
    frame: Option<SourceRenderFrame>,
    sampling: ImageSamplingPath,
    source_capture: bool,
) -> Result<SceneCapture, Error> {
    let mut child = Box::new(
        Renderer::new(
            &renderer.device,
            &renderer.queue,
            wgpu::TextureFormat::Rgba8Unorm,
        )
        .await?,
    );
    child.image_sampling = Some(sampling);
    child.image_preference = match sampling {
        ImageSamplingPath::NativeSampler => crate::image::ImageSamplingPreference::NativeSampler,
        ImageSamplingPath::ExplicitShader => crate::image::ImageSamplingPreference::ExplicitShader,
    };
    let frame = frame.unwrap_or(SourceRenderFrame::image_filter(size, Matrix4::IDENTITY)?);
    let prepared = Box::pin(child.prepare_source_scene(&source, frame)).await?;
    let (output, view) = cf::output(&renderer.device, size, 1)?;
    Ok(SceneCapture {
        renderer: child,
        scene: Box::new(prepared),
        source,
        output,
        view,
        source_capture,
    })
}
pub(super) fn rect(rect: FilterRect) -> [f32; 4] {
    [rect.0[0], rect.0[1], rect.width(), rect.height()]
}
pub(super) fn full(size: [u32; 2]) -> [f32; 4] {
    [0.0, 0.0, size[0] as f32, size[1] as f32]
}
pub(super) fn xy(matrix: Matrix4) -> Affine {
    let m = matrix.0;
    Affine([m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]])
}
pub(super) fn sample(draw: &mut ImageDraw, sampling: FilterSampling) {
    draw.sampling = sampling.mode;
    draw.cubic = sampling.cubic;
    if sampling.anisotropy > 0 {
        draw.sampling = crate::image::ImageSampling::LinearMipmap;
        draw.max_anisotropy = sampling.anisotropy.clamp(1, 16) as u8;
    }
}
pub(super) async fn operation(
    renderer: &Renderer,
    operation: &Operation,
    inputs: &[GpuImage],
    size: [u32; 2],
    sampling: ImageSamplingPath,
) -> Result<SceneCapture, Error> {
    let mut scene = Scene::default();
    let mut frame = None;
    let first = || inputs.first().ok_or(Error::ResourceDomain);
    let mut source_capture = false;
    match operation {
        Operation::Transparent => {}
        Operation::Composite { offsets } => {
            if offsets.len() != inputs.len() {
                return Err(Error::ResourceDomain);
            }
            for (input, offset) in inputs.iter().zip(offsets.iter()) {
                scene.draw_image(ImageDraw::new(
                    input.clone(),
                    [
                        offset[0],
                        offset[1],
                        input.size()[0] as f32,
                        input.size()[1] as f32,
                    ],
                ))?;
            }
        }
        Operation::MatrixTransform { matrix, sampling } => {
            let input = first()?;
            let mut draw = ImageDraw::new(input.clone(), full(input.size()));
            draw.transform = xy(*matrix);
            sample(&mut draw, *sampling);
            scene.draw_image(draw)?;
        }
        Operation::Crop { rect, transform } => {
            scene.push_image_filter_clip(rect.0, transform.effective())?;
            let input = first()?;
            scene.draw_image(ImageDraw::new(input.clone(), full(input.size())))?;
            scene.pop_scope()?;
        }
        Operation::Tile {
            source,
            destination,
            range,
        } => {
            scene.push_image_filter_clip(destination.0, Matrix4::IDENTITY)?;
            let input = first()?;
            for y in range[1]..range[3] {
                for x in range[0]..range[2] {
                    let mut draw = ImageDraw::new(
                        input.clone(),
                        [
                            source.0[0] + x as f32 * source.width(),
                            source.0[1] + y as f32 * source.height(),
                            source.width(),
                            source.height(),
                        ],
                    );
                    draw.source = rect(*source);
                    draw.extended_source = true;
                    scene.draw_image(draw)?;
                }
            }
            scene.pop_scope()?;
        }
        Operation::ImageSource(data) => {
            let mut draw = ImageDraw::new(data.source.image.clone(), rect(data.destination));
            draw.source = rect(data.source_rect);
            draw.extended_source = true;
            sample(&mut draw, data.sampling);
            scene.draw_image(draw)?;
            source_capture = true;
        }
        Operation::PictureSource { source, transform } => {
            scene.push_image_filter_clip(source.target.0, Matrix4::IDENTITY)?;
            scene.append(&source.scene)?;
            scene.pop_scope()?;
            frame = Some(SourceRenderFrame::image_filter(
                size,
                transform.effective(),
            )?);
            source_capture = true;
        }
        _ => return Err(Error::UnsupportedExecution),
    }
    prepare(
        renderer,
        Arc::new(scene),
        size,
        frame,
        sampling,
        source_capture,
    )
    .await
}
