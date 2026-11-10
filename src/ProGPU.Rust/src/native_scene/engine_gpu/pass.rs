//! Original pass continuation and actual attachment history. The encoder stays
//! inaccessible while a pass is open; cancellation ends the pass before dropping
//! its encoder and never submits or publishes a cache generation.
use super::*;
use crate::native_scene::engine_resources::layers::{
    NativeLayerPassState, NativeLayerTargetId, NativeTargetPass, PreparedNativeLayerTargets,
};
use crate::native_scene::execution::budget::LAYER_SLOT_COUNT;

pub(crate) struct NativeReplayPassEncoder {
    // Declaration order is intentional: an abandoned pass retires before encoder.
    pass: Option<wgpu::RenderPass<'static>>,
    encoder: Option<wgpu::CommandEncoder>,
    root: NativeRenderAttachment,
    targets: Arc<PreparedNativeLayerTargets>,
    attachments: Vec<Option<NativeRenderAttachment>>,
    history: NativeLayerPassState,
    active: Option<(NativeLayerTargetId, bool)>,
    passes: u64,
}
impl NativeReplayPassEncoder {
    pub fn new(
        encoder: wgpu::CommandEncoder,
        root: NativeRenderAttachment,
        targets: Arc<PreparedNativeLayerTargets>,
        preserve_root: bool,
        root_certified: bool,
        destination_sampling: bool,
    ) -> Result<Self, Error> {
        let frame = targets.plan().frame();
        if root.device() != targets.device()
            || root.format().texture_format() != targets.format()
            || root.size() != [frame.width, frame.height]
        {
            return Err(Error::ResourceDomain);
        }
        let mut attachments = Vec::new();
        attachments
            .try_reserve_exact(LAYER_SLOT_COUNT as usize + 1)
            .map_err(|_| Error::Capacity)?;
        attachments.resize_with(LAYER_SLOT_COUNT as usize + 1, || None);
        attachments[0] = Some(if destination_sampling {
            NativeRenderAttachment::from_layer(
                targets
                    .target(NativeLayerTargetId::Root)
                    .and_then(|t| t.color())
                    .ok_or(Error::ResourceDomain)?,
            )?
        } else {
            root.clone()
        });
        for request in targets.plan().requests() {
            if let NativeLayerTargetId::Slot(slot) = request.target {
                if let Some(color) = targets.target(request.target).and_then(|t| t.color()) {
                    attachments[slot as usize + 1] =
                        Some(NativeRenderAttachment::from_layer(color)?);
                }
            }
        }
        let history = NativeLayerPassState::new(targets.plan(), preserve_root, root_certified);
        Ok(Self {
            pass: None,
            encoder: Some(encoder),
            root,
            targets,
            attachments,
            history,
            active: None,
            passes: 0,
        })
    }
    pub fn root(&self) -> &NativeRenderAttachment {
        &self.root
    }
    pub fn history(&self) -> &NativeLayerPassState {
        &self.history
    }
    pub fn history_mut(&mut self) -> &mut NativeLayerPassState {
        &mut self.history
    }
    pub fn passes(&self) -> u64 {
        self.passes
    }
    pub fn active(&self) -> Option<(NativeLayerTargetId, bool)> {
        self.active
    }
    pub fn attachment(
        &self,
        target: NativeLayerTargetId,
    ) -> Result<&NativeRenderAttachment, Error> {
        let i = match target {
            NativeLayerTargetId::Root => 0,
            NativeLayerTargetId::Slot(slot) if slot < LAYER_SLOT_COUNT => slot as usize + 1,
            _ => return Err(Error::InvalidInput),
        };
        self.attachments[i].as_ref().ok_or(Error::ResourceDomain)
    }
    pub fn finish_pass(&mut self) {
        drop(self.pass.take());
        self.active = None;
    }
    pub fn encoder(&mut self) -> Result<&mut wgpu::CommandEncoder, Error> {
        if self.pass.is_some() {
            return Err(Error::ResourceDomain);
        }
        self.encoder.as_mut().ok_or(Error::ResourceDomain)
    }
    pub fn with_encoder<T>(
        &mut self,
        f: impl FnOnce(&mut wgpu::CommandEncoder, &NativeLayerPassState) -> Result<T, Error>,
    ) -> Result<T, Error> {
        if self.pass.is_some() {
            return Err(Error::ResourceDomain);
        }
        f(
            self.encoder.as_mut().ok_or(Error::ResourceDomain)?,
            &self.history,
        )
    }
    pub fn pass(&mut self) -> Result<&mut wgpu::RenderPass<'static>, Error> {
        self.pass.as_mut().ok_or(Error::ResourceDomain)
    }
    pub fn begin_root(&mut self, uses_depth: bool) -> Result<(), Error> {
        self.finish_pass();
        let plan = self.history.begin_root(uses_depth);
        self.begin(plan)
    }
    pub fn begin_layer(
        &mut self,
        slot: u32,
        ignore_alpha: bool,
        background_initialized: bool,
    ) -> Result<(), Error> {
        self.finish_pass();
        let plan = self
            .history
            .begin_layer(slot, ignore_alpha, background_initialized)?;
        self.begin(plan)
    }
    pub fn resume(&mut self, target: NativeLayerTargetId, uses_depth: bool) -> Result<(), Error> {
        self.finish_pass();
        let plan = self.history.resume(target, uses_depth)?;
        self.begin(plan)
    }
    pub fn ensure(&mut self, target: NativeLayerTargetId, uses_depth: bool) -> Result<(), Error> {
        if self.active == Some((target, uses_depth)) {
            Ok(())
        } else {
            self.resume(target, uses_depth)
        }
    }
    fn begin(&mut self, plan: NativeTargetPass) -> Result<(), Error> {
        if self.pass.is_some() {
            return Err(Error::ResourceDomain);
        }
        let attachment = self.attachment(plan.target)?.clone();
        let depth = if plan.depth.is_some() {
            Some(
                self.targets
                    .target(plan.target)
                    .and_then(|t| t.depth())
                    .ok_or(Error::ResourceDomain)?,
            )
        } else {
            None
        };
        let descriptor = wgpu::RenderPassDescriptor {
            label: Some("Native original ordered target continuation"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: attachment.view(),
                depth_slice: None,
                resolve_target: None,
                ops: plan.color_operations(),
            })],
            depth_stencil_attachment: depth.map(|target| wgpu::RenderPassDepthStencilAttachment {
                view: target.view(),
                depth_ops: plan.depth_operations(),
                stencil_ops: None,
            }),
            ..Default::default()
        };
        // Wgpu's safe API retains its actual encoder lock at runtime. Every
        // mutation/copy path above requires this pass to have ended first.
        let mut pass = self
            .encoder
            .as_mut()
            .ok_or(Error::ResourceDomain)?
            .begin_render_pass(&descriptor)
            .forget_lifetime();
        if let Some([width, height]) = plan.root_viewport {
            pass.set_viewport(0.0, 0.0, width as f32, height as f32, 0.0, 1.0);
        }
        self.active = Some((plan.target, plan.depth.is_some()));
        self.pass = Some(pass);
        self.passes += 1;
        Ok(())
    }
    pub fn finish(mut self) -> Result<wgpu::CommandBuffer, Error> {
        self.finish_pass();
        Ok(self.encoder.take().ok_or(Error::ResourceDomain)?.finish())
    }
}
