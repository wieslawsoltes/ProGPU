//! Native storage Clear reuses the exact owned target-clear WGSL/pipeline.
use super::*;
use crate::native_scene::{
    engine_resources::layers::NativeLayerPassState,
    execution::{NativeTargetClearOperation, NativeTargetFormat},
};
use crate::renderer::clear::{ClearPipeline, ReplayMetrics, TargetClearRequest};
pub struct NativeTargetClearEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: NativeTargetFormat,
    pipeline: ClearPipeline,
}
impl NativeTargetClearEngine {
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
    ) -> Result<Self, Error> {
        let pipeline = ClearPipeline::new(device, format.texture_format()).await?;
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            format,
            pipeline,
        })
    }
    /// A fresh 16-byte uniform is uploaded per occurrence, including warm replay.
    /// Caller finishes the preceding pass before this operation and resumes the
    /// same actual target with Load afterward. No submission occurs here.
    pub fn encode(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        attachment: &NativeRenderAttachment,
        state: &NativeLayerPassState,
        operation: &NativeTargetClearOperation,
    ) -> Result<ReplayMetrics, Error> {
        if attachment.device() != &self.device
            || attachment.format() != self.format
            || attachment.size() != operation.size()
        {
            return Err(Error::ResourceDomain);
        }
        state.admit_target_clear(
            operation.target(),
            operation.size(),
            operation.frame().ignores_alpha(),
        )?;
        let s = operation.scissor();
        self.pipeline.encode(
            &self.device,
            &self.queue,
            encoder,
            attachment.view(),
            operation.size(),
            TargetClearRequest {
                color: operation.color(),
                scissor: [s.x, s.y, s.width, s.height],
                alpha: operation.target_alpha(),
            },
        )
    }
}
