//! Original ordinary pop-layer texture and fixed-blend selection.
use super::frames::NativeSceneFrames;
use super::*;
use crate::native_scene::{
    engine_resources::{
        PreparedNativeMask,
        layers::{NativeLayerTargetId, NativeLayerTexture, PreparedNativeLayerComposite},
    },
    execution::{NativeReplayLayerPop, NativeReplayOperation, NativeReplayPlan},
};
pub struct PreparedNativeLayerDraw {
    source: Arc<NativeReplayPlan>,
    operation_index: usize,
    frame: Arc<NativeFrameBindings>,
    vertices: Arc<PreparedNativeLayerComposite>,
    texture: NativeLayerTexture,
    texture_binding: wgpu::BindGroup,
    mask: Option<Arc<PreparedNativeMask>>,
    pipeline: wgpu::RenderPipeline,
}
impl PreparedNativeLayerDraw {
    pub(crate) async fn prepare(
        pipelines: &mut NativePipelineCache,
        source: Arc<NativeReplayPlan>,
        operation_index: usize,
        frames: &NativeSceneFrames,
        vertices: Arc<PreparedNativeLayerComposite>,
        mask: Option<Arc<PreparedNativeMask>>,
    ) -> Result<Option<Arc<Self>>, Error> {
        let NativeReplayOperation::PopLayer(pop) = source
            .operations()
            .get(operation_index)
            .ok_or(Error::InvalidInput)?
        else {
            return Err(Error::InvalidInput);
        };
        let draw = &pop.composite;
        if !draw.drawable {
            return Ok(None);
        }
        if draw.advanced.is_some()
            || pop.effect.as_ref().is_some_and(|e| e.final_sample())
            || !Arc::ptr_eq(&source, frames.source())
            || vertices.device() != pipelines.device()
            || vertices.vertices().len() != source.vertices().len()
        {
            return Err(Error::ResourceDomain);
        }
        let offset = u64::from(draw.first_vertex) * 56;
        if draw.first_vertex % 4 != 0
            || draw.first_vertex as usize + 4 > vertices.vertices().len()
            || offset + 224 > vertices.capacity()
            || !draw.scissor.drawable
            || draw.scissor.width == 0
            || draw.scissor.height == 0
        {
            return Err(Error::InvalidInput);
        }
        match (&draw.mask, &mask) {
            (None, None) => {}
            (Some(expected), Some(actual))
                if Arc::ptr_eq(expected, actual.request())
                    && actual.binding().device() == pipelines.device() => {}
            _ => return Err(Error::ResourceDomain),
        }
        let slot = frames
            .targets()
            .target(pop.source)
            .ok_or(Error::ResourceDomain)?;
        let texture = if let Some(effect) = &pop.effect {
            slot.effects()
                .and_then(|e| e.textures().get(effect.final_texture() as usize))
        } else {
            slot.color()
        }
        .ok_or(Error::ResourceDomain)?
        .clone();
        let texture_binding = if pop.effect.is_none() && draw.nearest {
            texture.nearest_binding().ok_or(Error::ResourceDomain)?
        } else {
            texture.linear_binding()
        }
        .clone();
        let frame = if pop.target == NativeLayerTargetId::Root {
            frames
                .targets()
                .composite_frame()
                .ok_or(Error::ResourceDomain)?
        } else {
            frames.frame(pop.target)?
        }
        .clone();
        if frame.material().device() != pipelines.device()
            || texture.device() != pipelines.device()
            || pipelines.target_format() != source.source().options().format.texture_format()
        {
            return Err(Error::ResourceDomain);
        }
        let (pipeline, _) = pipelines
            .prepare(NativeRenderPipeline::Group(NativeGroupPipeline {
                blend_mode: draw.blend_mode,
                masked: mask.is_some(),
                coverage_only: false,
                channels: if pop.target_ignores_alpha {
                    NativeWriteChannels::Rgb
                } else {
                    NativeWriteChannels::All
                },
                linear_byte_opacity: draw.linear_byte_opacity,
            }))
            .await?;
        Ok(Some(Arc::new(Self {
            source,
            operation_index,
            frame,
            vertices,
            texture,
            texture_binding,
            mask,
            pipeline,
        })))
    }
    pub fn source(&self) -> &NativeReplayLayerPop {
        match &self.source.operations()[self.operation_index] {
            NativeReplayOperation::PopLayer(pop) => pop,
            _ => unreachable!("prepared pop-layer operation"),
        }
    }
    pub fn texture(&self) -> &NativeLayerTexture {
        &self.texture
    }
    pub fn mask(&self) -> Option<&Arc<PreparedNativeMask>> {
        self.mask.as_ref()
    }
    pub fn encode(&self, pass: &mut wgpu::RenderPass<'_>) {
        let draw = &self.source().composite;
        let clip = draw.scissor;
        pass.set_scissor_rect(clip.x, clip.y, clip.width, clip.height);
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &self.frame.frame, &[]);
        pass.set_bind_group(1, &self.texture_binding, &[]);
        if let Some(mask) = &self.mask {
            pass.set_bind_group(2, mask.binding().bind_group(), &[]);
        }
        let offset = u64::from(draw.first_vertex) * 56;
        pass.set_vertex_buffer(0, self.vertices.buffer().slice(offset..offset + 224));
        pass.set_index_buffer(
            self.vertices.indices().slice(..24),
            wgpu::IndexFormat::Uint32,
        );
        pass.draw_indexed(0..6, 0, 0..1);
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
