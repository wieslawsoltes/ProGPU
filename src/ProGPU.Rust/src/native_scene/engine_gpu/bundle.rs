//! Original source-ordered clip spans, with immutable GPU/source owners.
use super::frames::NativeSceneFrames;
use super::*;
use crate::native_scene::{
    engine_resources::PreparedNativeMask,
    execution::{NativeReplayBundle, NativeReplayDraw, NativeReplayOperation, NativeReplayPlan},
};
enum Draw {
    Ordinary(PreparedNativeDraw),
    ThreeD(u32),
}
/// A bundle keeps the complete original operation and source generation alive.
/// Coverage preparation and actual target pass/depth load remain separate work.
pub struct PreparedNativeReplayBundle {
    source: Arc<NativeReplayPlan>,
    operation_index: usize,
    pages: Arc<PreparedNativeScenePages>,
    frame: Arc<NativeFrameBindings>,
    glyph_frame: Option<Arc<NativeGlyphFrameBindings>>,
    mask: Option<Arc<PreparedNativeMask>>,
    draws: Vec<Draw>,
    bundle: wgpu::RenderBundle,
}
impl PreparedNativeReplayBundle {
    /// The actual target's frame binding is supplied by the ordered executor.
    /// Every merged range has already passed original CPU adjacency admission;
    /// the draw builders independently bound it against its immutable GPU page.
    pub(crate) async fn prepare(
        pipelines: &mut NativePipelineCache,
        source: Arc<NativeReplayPlan>,
        operation_index: usize,
        pages: Arc<PreparedNativeScenePages>,
        frames: &NativeSceneFrames,
        mask: Option<Arc<PreparedNativeMask>>,
    ) -> Result<Arc<Self>, Error> {
        let NativeReplayOperation::Bundle(span) = source
            .operations()
            .get(operation_index)
            .ok_or(Error::InvalidInput)?
        else {
            return Err(Error::InvalidInput);
        };
        let frame = frames.frame(span.target)?.clone();
        if !Arc::ptr_eq(&source, frames.source())
            || !Arc::ptr_eq(source.source(), pages.source().source())
            || !Arc::ptr_eq(frame.material(), pages.material())
            || frame.material().device() != pipelines.device()
            || source.source().options().format.texture_format() != pipelines.target_format()
            || !span.scissor.drawable
            || span.scissor.width == 0
            || span.scissor.height == 0
        {
            return Err(Error::ResourceDomain);
        }
        match (&span.mask, &mask) {
            (None, None) => {}
            (Some(expected), Some(actual))
                if Arc::ptr_eq(expected, actual.request())
                    && actual.binding().device() == pipelines.device() => {}
            _ => return Err(Error::ResourceDomain),
        }
        let mask_binding = mask.as_ref().map(|m| m.binding().clone());
        let has_glyphs = span
            .draws
            .iter()
            .any(|d| matches!(d, NativeReplayDraw::Glyph { .. }));
        let glyph_frame = if has_glyphs {
            Some(frames.glyph_frame(span.target)?.clone())
        } else {
            None
        };
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(span.draws.len())
            .map_err(|_| Error::Capacity)?;
        for draw in span.draws.iter() {
            let prepared = match *draw {
                NativeReplayDraw::Vector {
                    command_index,
                    draw,
                } => {
                    let value = PreparedNativeDraw::vector_range(
                        pipelines,
                        pages.vector().ok_or(Error::ResourceDomain)?.clone(),
                        command_index,
                        draw,
                        frame.clone(),
                        mask_binding.clone(),
                    )
                    .await?
                    .ok_or(Error::InvalidInput)?;
                    Draw::Ordinary(value)
                }
                NativeReplayDraw::Path {
                    command_index,
                    draw,
                } => Draw::Ordinary(
                    PreparedNativeDraw::path_range(
                        pipelines,
                        pages.paths().ok_or(Error::ResourceDomain)?.clone(),
                        command_index,
                        draw,
                        frame.clone(),
                        mask_binding.clone(),
                    )
                    .await?,
                ),
                NativeReplayDraw::Glyph {
                    command_index,
                    draw,
                } => Draw::Ordinary(
                    PreparedNativeDraw::glyph_range(
                        pipelines,
                        glyph_frame.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                        command_index,
                        draw,
                        mask_binding.clone(),
                    )
                    .await?,
                ),
                NativeReplayDraw::Image(index) => Draw::Ordinary(
                    PreparedNativeDraw::image(
                        pipelines,
                        pages.images().ok_or(Error::ResourceDomain)?.clone(),
                        index as usize,
                        frame.clone(),
                        mask_binding.clone(),
                    )
                    .await?,
                ),
                NativeReplayDraw::ThreeD(index) => {
                    let page = pages.three_d().ok_or(Error::ResourceDomain)?;
                    if !span.uses_depth
                        || mask.is_some()
                        || page.device() != pipelines.device()
                        || page.target_format() != pipelines.target_format()
                        || index as usize >= page.page().draws().len()
                    {
                        return Err(Error::ResourceDomain);
                    }
                    Draw::ThreeD(index)
                }
            };
            if span.uses_depth && !matches!(prepared, Draw::ThreeD(_)) {
                return Err(Error::InvalidInput);
            }
            draws.push(prepared);
        }
        if draws.is_empty() {
            return Err(Error::InvalidInput);
        }
        let device = pipelines.device();
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let formats = [Some(pipelines.target_format())];
        let mut encoder =
            device.create_render_bundle_encoder(&wgpu::RenderBundleEncoderDescriptor {
                label: Some(if span.uses_depth {
                    "Native retained 3D clip span"
                } else {
                    "Native retained 2D clip span"
                }),
                color_formats: &formats,
                depth_stencil: span.uses_depth.then_some(wgpu::RenderBundleDepthStencil {
                    format: wgpu::TextureFormat::Depth24Plus,
                    depth_read_only: false,
                    stencil_read_only: false,
                }),
                sample_count: 1,
                multiview_mask: None,
            });
        for draw in &draws {
            match draw {
                Draw::Ordinary(draw) => draw.encode_bundle(&mut encoder),
                Draw::ThreeD(index) => pages
                    .three_d()
                    .ok_or(Error::ResourceDomain)?
                    .encode_bundle_draw(&mut encoder, *index as usize)?,
            }
        }
        let bundle = encoder.finish(&wgpu::RenderBundleDescriptor {
            label: Some("Native immutable ordered clip span"),
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Arc::new(Self {
            source,
            operation_index,
            pages,
            frame,
            glyph_frame,
            mask,
            draws,
            bundle,
        }))
    }
    pub fn source(&self) -> &NativeReplayBundle {
        match &self.source.operations()[self.operation_index] {
            NativeReplayOperation::Bundle(span) => span,
            _ => unreachable!("prepared bundle operation"),
        }
    }
    pub fn pages(&self) -> &Arc<PreparedNativeScenePages> {
        &self.pages
    }
    pub fn frame(&self) -> &Arc<NativeFrameBindings> {
        &self.frame
    }
    pub fn glyph_frame(&self) -> Option<&Arc<NativeGlyphFrameBindings>> {
        self.glyph_frame.as_ref()
    }
    pub fn mask(&self) -> Option<&Arc<PreparedNativeMask>> {
        self.mask.as_ref()
    }
    pub fn command_ranges(&self) -> usize {
        self.draws.len()
    }
    /// Actual GPU draws in this immutable bundle. A native mesh command may
    /// contain several records and an additional occluded-edge draw per record.
    pub fn encoded_draw_count(&self) -> u64 {
        self.draws
            .iter()
            .map(|draw| match draw {
                Draw::Ordinary(_) => 1,
                Draw::ThreeD(index) => {
                    let page = self
                        .pages
                        .three_d()
                        .expect("prepared original 3D page")
                        .page();
                    let draw = &page.draws()[*index as usize];
                    if draw.frame.command().record.kind == SCENE_COMMAND_DRAW_LINE_3D_BATCH {
                        1
                    } else {
                        page.mesh_draws()[draw.first_record as usize
                            ..(draw.first_record + draw.record_count) as usize]
                            .iter()
                            .map(|mesh| {
                                1 + u64::from(
                                    mesh.topology == MESH_3D_EDGE_LIST
                                        && mesh.flags & MESH_3D_EDGE_DISPLAY_OCCLUDED != 0,
                                )
                            })
                            .sum()
                    }
                }
            })
            .sum()
    }
    /// Caller has selected the exact target/depth attachment and viewport. Source
    /// mask/coverage work must precede this consumer; no callback or upload occurs.
    pub fn encode(&self, pass: &mut wgpu::RenderPass<'_>) {
        let clip = self.source().scissor;
        pass.set_scissor_rect(clip.x, clip.y, clip.width, clip.height);
        pass.execute_bundles(std::iter::once(&self.bundle));
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
