//! Original draw-family selection shared by render passes and retained bundles.
use super::*;
use crate::native_scene::draw::{NativeAnalyticDraw, NativeGlyphDraw, NativePathDraw};
use crate::native_scene::engine_resources::PreparedNativeImagePage;
use std::ops::Range;

/// A complete draw owns its selected physical target/material frame and source
/// page. Preparation checks all ranges before commands are emitted. Target pass,
/// scissor, viewport, depth and source order remain the enclosing executor's job.
pub struct PreparedNativeDraw {
    pipeline: wgpu::RenderPipeline,
    groups: [Option<wgpu::BindGroup>; 4],
    vertices: wgpu::Buffer,
    vertex_bytes: u64,
    indices: Option<(wgpu::Buffer, u64, Range<u32>, i32)>,
    vertex_range: Range<u32>,
    instances: Range<u32>,
    command_index: u32,
    frame: Arc<NativeFrameBindings>,
    mask: Option<Arc<NativeMaskBinding>>,
    source: DrawSource,
}
enum DrawSource {
    Image(Arc<PreparedNativeImagePage>),
    Vector(Arc<NativeVectorRenderPage>),
    Path(Arc<NativePathRenderPage>),
    Glyph(Arc<NativeGlyphFrameBindings>),
}
impl PreparedNativeDraw {
    pub fn command_index(&self) -> u32 {
        self.command_index
    }
    pub fn frame(&self) -> &Arc<NativeFrameBindings> {
        &self.frame
    }
    pub fn mask(&self) -> Option<&Arc<NativeMaskBinding>> {
        self.mask.as_ref()
    }
    pub fn source_scene(&self) -> &Arc<crate::native_scene::NativeScene> {
        match &self.source {
            DrawSource::Image(page) => page.page().owner(),
            DrawSource::Vector(page) => page.source.scene(),
            DrawSource::Path(page) => page.source.page().source().scene(),
            DrawSource::Glyph(binding) => binding.page.source.page().source().scene(),
        }
    }
    /// Call after the queue submission that consumes this draw. Actual producer
    /// owners remain alive independently of cache eviction or caller release.
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
    pub async fn image(
        pipelines: &mut NativePipelineCache,
        page: Arc<PreparedNativeImagePage>,
        draw_index: usize,
        frame: Arc<NativeFrameBindings>,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Self, Error> {
        let device = pipelines.device();
        if page.device() != device
            || frame.material().device() != device
            || mask.as_ref().is_some_and(|m| m.device() != device)
            || !Arc::ptr_eq(page.page().owner(), frame.material().brushes().scene())
        {
            return Err(Error::ResourceDomain);
        }
        let draw = page.draws().get(draw_index).ok_or(Error::InvalidInput)?;
        let record = &draw.descriptor;
        let first = record.first_vertex;
        let count = record.vertex_count;
        let end = first.checked_add(count).ok_or(Error::Capacity)?;
        let vertex_bytes = page.page().metrics().vertex_bytes;
        if vertex_bytes == 0
            || count != 4 && (count == 0 || count % 6 != 0)
            || u64::from(end) * 56 > vertex_bytes
            || vertex_bytes > page.vertices.size()
            || record.indexed != (count == 4)
        {
            return Err(Error::InvalidInput);
        }
        let mut groups: [Option<wgpu::BindGroup>; 4] = Default::default();
        groups[0] = Some(frame.frame.clone());
        let mode = mask.as_ref().map_or(NativeMaskMode::None, |m| m.mode());
        // An explicit source effect mask takes precedence over the enclosing
        // mask chain exactly as the original native image draw selection does.
        let has_effect_mask = draw.mask.is_some();
        let chained = !has_effect_mask && mode == NativeMaskMode::Chain;
        let key = if record.effect.is_some() {
            groups[1] = Some(
                draw.effect_uniform_binding
                    .clone()
                    .ok_or(Error::InvalidInput)?,
            );
            groups[2] = Some(
                draw.effect_texture_binding
                    .clone()
                    .ok_or(Error::InvalidInput)?,
            );
            groups[3] = Some(if has_effect_mask || mask.is_none() {
                draw.effect_mask_binding
                    .clone()
                    .ok_or(Error::InvalidInput)?
            } else {
                mask.as_ref().ok_or(Error::InvalidInput)?.binding.clone()
            });
            NativeRenderPipeline::ImageEffect {
                mask_chain: chained,
            }
        } else {
            groups[1] = Some(draw.texture_binding.clone().ok_or(Error::InvalidInput)?);
            if let Some(mask) = &mask {
                groups[2] = Some(mask.binding.clone());
            }
            if record.color_matrix.is_some() {
                groups[if mask.is_some() { 3 } else { 2 }] = Some(
                    draw.color_matrix_binding
                        .clone()
                        .ok_or(Error::InvalidInput)?,
                );
                NativeRenderPipeline::ColorMatrix(mode)
            } else {
                NativeRenderPipeline::Image(mode)
            }
        };
        let indices = if count == 4 {
            let buffer = page.quad_indices.clone().ok_or(Error::InvalidInput)?;
            if buffer.size() < 24 {
                return Err(Error::InvalidInput);
            }
            Some((buffer, 24, 0..6, first as i32))
        } else {
            None
        };
        let command_index = record.command_index;
        let (pipeline, _) = pipelines.prepare(key).await?;
        Ok(Self {
            pipeline,
            groups,
            vertices: page.vertices.clone(),
            vertex_bytes,
            indices,
            vertex_range: first..end,
            instances: 0..1,
            command_index,
            frame,
            mask,
            source: DrawSource::Image(page),
        })
    }
    pub async fn vector(
        pipelines: &mut NativePipelineCache,
        page: Arc<NativeVectorRenderPage>,
        draw_index: usize,
        frame: Arc<NativeFrameBindings>,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Option<Self>, Error> {
        let selected = *page
            .source
            .draws()
            .get(draw_index)
            .ok_or(Error::InvalidInput)?;
        let draw = selected.draw;
        Self::vector_range(pipelines, page, selected.command_index, draw, frame, mask).await
    }
    /// The ordered compiler has already proved contiguous native records and
    /// equal target/scissor/mask scopes. Full backing bounds remain checked here.
    pub(crate) async fn vector_range(
        pipelines: &mut NativePipelineCache,
        page: Arc<NativeVectorRenderPage>,
        command_index: u32,
        draw: NativeAnalyticDraw,
        frame: Arc<NativeFrameBindings>,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Option<Self>, Error> {
        if draw.vertex_count == 0 && draw.index_count == 0 {
            return Ok(None);
        }
        admit_frame(pipelines, &frame, &mask, page.source.scene(), &page.device)?;
        let vertex_bytes = page.metrics().vertex_upload_bytes;
        let index_bytes = page.metrics().index_upload_bytes;
        if draw.vertex_count == 0
            || draw.index_count == 0
            || draw.vertex_offset_bytes >= vertex_bytes
            || draw.index_offset_bytes >= index_bytes
            || u64::from(draw.vertex_count) > (vertex_bytes - draw.vertex_offset_bytes) / 56
            || u64::from(draw.index_count) > (index_bytes - draw.index_offset_bytes) / 4
        {
            return Err(Error::InvalidInput);
        }
        let first = u32::try_from(draw.index_offset_bytes / 4).map_err(|_| Error::Capacity)?;
        let end = first.checked_add(draw.index_count).ok_or(Error::Capacity)?;
        let mode = mask.as_ref().map_or(NativeMaskMode::None, |m| m.mode());
        let (pipeline, _) = pipelines
            .prepare(NativeRenderPipeline::Vector(mode))
            .await?;
        Ok(Some(Self {
            pipeline,
            groups: [
                Some(frame.vector.clone()),
                Some(page.atlas.clone()),
                mask.as_ref().map(|m| m.binding.clone()),
                None,
            ],
            vertices: page.vertices.clone(),
            vertex_bytes,
            indices: Some((page.indices.clone(), index_bytes, first..end, 0)),
            vertex_range: 0..0,
            instances: 0..1,
            command_index,
            frame,
            mask,
            source: DrawSource::Vector(page),
        }))
    }
    pub async fn path(
        pipelines: &mut NativePipelineCache,
        page: Arc<NativePathRenderPage>,
        draw_index: usize,
        frame: Arc<NativeFrameBindings>,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Self, Error> {
        let selected = *page
            .source
            .page()
            .source()
            .draws()
            .get(draw_index)
            .ok_or(Error::InvalidInput)?;
        Self::path_range(
            pipelines,
            page,
            selected.command_index,
            selected.draw,
            frame,
            mask,
        )
        .await
    }
    pub(crate) async fn path_range(
        pipelines: &mut NativePipelineCache,
        page: Arc<NativePathRenderPage>,
        command_index: u32,
        draw: NativePathDraw,
        frame: Arc<NativeFrameBindings>,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Self, Error> {
        let source = page.source.page().source();
        admit_frame(
            pipelines,
            &frame,
            &mask,
            source.scene(),
            page.source.device(),
        )?;
        let raster = page.source.page();
        let vertex_bytes = raster.vertices().len() as u64 * 56;
        let index_bytes = raster.indices().len() as u64 * 4;
        let end = draw
            .first_index
            .checked_add(draw.index_count)
            .ok_or(Error::Capacity)?;
        if draw.index_count == 0
            || end as usize > raster.indices().len()
            || vertex_bytes == 0
            || vertex_bytes > page.source.vertices().size()
            || index_bytes > page.source.indices().size()
        {
            return Err(Error::InvalidInput);
        }
        let mode = mask.as_ref().map_or(NativeMaskMode::None, |m| m.mode());
        let (pipeline, _) = pipelines
            .prepare(NativeRenderPipeline::Vector(mode))
            .await?;
        Ok(Self {
            pipeline,
            groups: [
                Some(frame.vector.clone()),
                Some(page.atlas.clone()),
                mask.as_ref().map(|m| m.binding.clone()),
                None,
            ],
            vertices: page.source.vertices().clone(),
            vertex_bytes,
            indices: Some((
                page.source.indices().clone(),
                index_bytes,
                draw.first_index..end,
                0,
            )),
            vertex_range: 0..0,
            instances: 0..1,
            command_index,
            frame,
            mask,
            source: DrawSource::Path(page),
        })
    }
    pub async fn glyph(
        pipelines: &mut NativePipelineCache,
        bindings: Arc<NativeGlyphFrameBindings>,
        draw_index: usize,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Self, Error> {
        let selected = *bindings
            .page
            .source
            .page()
            .source()
            .draws()
            .get(draw_index)
            .ok_or(Error::InvalidInput)?;
        Self::glyph_range(
            pipelines,
            bindings,
            selected.command_index,
            selected.draw,
            mask,
        )
        .await
    }
    pub(crate) async fn glyph_range(
        pipelines: &mut NativePipelineCache,
        bindings: Arc<NativeGlyphFrameBindings>,
        command_index: u32,
        draw: NativeGlyphDraw,
        mask: Option<Arc<NativeMaskBinding>>,
    ) -> Result<Self, Error> {
        let page = &bindings.page;
        let frame = &bindings.frame;
        let source = page.source.page().source();
        admit_frame(
            pipelines,
            frame,
            &mask,
            source.scene(),
            page.source.device(),
        )?;
        let instances = page.source.page().instances();
        let end = draw
            .first_instance
            .checked_add(draw.instance_count)
            .ok_or(Error::Capacity)?;
        let vertex_bytes = instances.len() as u64 * 96;
        if draw.instance_count == 0
            || end as usize > instances.len()
            || vertex_bytes > page.source.instances().size()
        {
            return Err(Error::InvalidInput);
        }
        let mode = mask.as_ref().map_or(NativeMaskMode::None, |m| m.mode());
        let mut groups = [
            Some(frame.text.clone()),
            Some(page.atlas.clone()),
            mask.as_ref().map(|m| m.binding.clone()),
            None,
        ];
        let mut vertices = 6;
        let key = if draw.paint_index == SCENE_NO_INDEX {
            NativeRenderPipeline::Text(mode)
        } else {
            let paint = source
                .paints()
                .get(draw.paint_index as usize)
                .ok_or(Error::InvalidInput)?;
            let paint_bindings = page.paints.as_ref().ok_or(Error::InvalidInput)?;
            groups[0] = Some(bindings.paint.clone().ok_or(Error::InvalidInput)?);
            groups[2] = Some(
                mask.as_ref()
                    .map_or_else(|| page.empty_mask.clone(), |m| m.binding.clone()),
            );
            groups[3] = Some(
                paint_bindings
                    .bindings()
                    .get(draw.paint_index as usize)
                    .ok_or(Error::InvalidInput)?
                    .clone(),
            );
            if paint.kind == SCENE_GLYPH_PAINT_TEXTURE
                && paint.flags & SCENE_GLYPH_PAINT_BOUNDED != 0
            {
                vertices = 12;
            }
            let premultiplied = pipelines.target_format() == wgpu::TextureFormat::R8Unorm
                || paint.kind == SCENE_GLYPH_PAINT_TEXTURE
                    && paint.flags & SCENE_GLYPH_PAINT_PREMULTIPLIED != 0;
            NativeRenderPipeline::GlyphPaint {
                mask: mode,
                premultiplied,
            }
        };
        let (pipeline, _) = pipelines.prepare(key).await?;
        Ok(Self {
            pipeline,
            groups,
            vertices: page.source.instances().clone(),
            vertex_bytes,
            indices: None,
            vertex_range: 0..vertices,
            instances: draw.first_instance..end,
            command_index,
            frame: frame.clone(),
            mask,
            source: DrawSource::Glyph(bindings),
        })
    }
}

fn admit_frame(
    pipelines: &NativePipelineCache,
    frame: &NativeFrameBindings,
    mask: &Option<Arc<NativeMaskBinding>>,
    scene: &Arc<crate::native_scene::NativeScene>,
    source_device: &wgpu::Device,
) -> Result<(), Error> {
    let device = pipelines.device();
    if source_device != device
        || frame.material().device() != device
        || mask.as_ref().is_some_and(|m| m.device() != device)
        || !Arc::ptr_eq(scene, frame.material().brushes().scene())
    {
        return Err(Error::ResourceDomain);
    }
    Ok(())
}

// Both routes emit precisely the same pipeline, groups, full packed buffers and
// original first-index/base-vertex/instance fields. No draw preparation or source
// callback happens during replay.
macro_rules! encode_draw {
    ($method:ident, $encoder:ty) => {
        pub fn $method<'a>(&'a self, encoder: &mut $encoder) {
            encoder.set_pipeline(&self.pipeline);
            for (index, group) in self.groups.iter().enumerate() {
                if let Some(group) = group {
                    encoder.set_bind_group(index as u32, group, &[]);
                }
            }
            encoder.set_vertex_buffer(0, self.vertices.slice(..self.vertex_bytes));
            if let Some((buffer, size, range, base)) = &self.indices {
                encoder.set_index_buffer(buffer.slice(..*size), wgpu::IndexFormat::Uint32);
                encoder.draw_indexed(range.clone(), *base, self.instances.clone());
            } else {
                encoder.draw(self.vertex_range.clone(), self.instances.clone());
            }
        }
    };
}
impl PreparedNativeDraw {
    encode_draw!(encode, wgpu::RenderPass<'a>);
    encode_draw!(encode_bundle, wgpu::RenderBundleEncoder<'a>);
}
