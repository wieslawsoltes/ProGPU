//! The original final isolated-root replacement, with an actual retained
//! destination attachment rather than an invented external texture view.
use super::gpu::{admit_quad, begin_pass, group_key};
use super::*;
use crate::native_scene::{
    engine_gpu::{NativePipelineCache, NativeRenderAttachment},
    engine_resources::layers::{
        NativeLayerTargetId, PreparedNativeLayerComposite, PreparedNativeLayerTargets,
    },
};
#[derive(Clone)]
pub struct PreparedNativeRootCopy {
    destination: NativeRenderAttachment,
    targets: PreparedNativeLayerTargets,
    composite: Arc<PreparedNativeLayerComposite>,
    first_vertex: u32,
    pipeline: wgpu::RenderPipeline,
    pipeline_created: bool,
}
impl PreparedNativeRootCopy {
    pub async fn prepare(
        pipelines: &mut NativePipelineCache,
        targets: &PreparedNativeLayerTargets,
        destination: NativeRenderAttachment,
        composite: Arc<PreparedNativeLayerComposite>,
        first_vertex: u32,
    ) -> Result<Self, Error> {
        let root = targets
            .target(NativeLayerTargetId::Root)
            .and_then(|t| t.color())
            .ok_or(Error::ResourceDomain)?;
        let frame = targets.composite_frame().ok_or(Error::ResourceDomain)?;
        if destination.device() != pipelines.device()
            || root.device() != pipelines.device()
            || composite.device() != pipelines.device()
            || destination.texture().format() != pipelines.target_format()
            || root.texture().format() != pipelines.target_format()
            || destination.size() != root.size()
            || destination.texture() == root.texture()
            || frame.material().device() != pipelines.device()
        {
            return Err(Error::ResourceDomain);
        }
        admit_quad(&composite, first_vertex)?;
        let (pipeline, hit) = pipelines.prepare(group_key(false, false, false)).await?;
        Ok(Self {
            destination,
            targets: targets.clone(),
            composite,
            first_vertex,
            pipeline,
            pipeline_created: !hit,
        })
    }
    pub fn destination(&self) -> &NativeRenderAttachment {
        &self.destination
    }
    pub fn pipeline_created(&self) -> bool {
        self.pipeline_created
    }
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) -> Result<(), Error> {
        let root = self
            .targets
            .target(NativeLayerTargetId::Root)
            .and_then(|t| t.color())
            .ok_or(Error::ResourceDomain)?;
        let frame = self
            .targets
            .composite_frame()
            .ok_or(Error::ResourceDomain)?;
        let mut pass = begin_pass(
            encoder,
            self.destination.view(),
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
            "Native isolated root final replacement",
        );
        let [w, h] = root.size();
        pass.set_scissor_rect(0, 0, w, h);
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &frame.frame, &[]);
        pass.set_bind_group(1, root.linear_binding(), &[]);
        self.composite.encode_quad(&mut pass, self.first_vertex)
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
