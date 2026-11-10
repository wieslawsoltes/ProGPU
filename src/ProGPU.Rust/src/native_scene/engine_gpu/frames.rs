//! Original root versus materialized-target frame identity, shared by every
//! bundle that consumes the same actual frame/material/paint generation.
use super::*;
use crate::native_scene::{
    engine_resources::layers::{NativeLayerTargetId, PreparedNativeLayerTargets},
    execution::{
        NativeReplayDraw, NativeReplayOperation, NativeReplayPlan, budget::LAYER_SLOT_COUNT,
    },
};
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSceneFrameMetrics {
    /// Layer slot uploads are reported by their actual target pool, separately.
    pub root_uniform_upload_bytes: u64,
    pub root_binding_allocations: u32,
    pub glyph_paint_binding_allocations: u32,
}
pub struct NativeSceneFrames {
    source: Arc<NativeReplayPlan>,
    targets: Arc<PreparedNativeLayerTargets>,
    frames: Vec<Option<Arc<NativeFrameBindings>>>,
    glyphs: Vec<Option<Arc<NativeGlyphFrameBindings>>>,
    root_certified: bool,
    metrics: NativeSceneFrameMetrics,
}
impl NativeSceneFrames {
    pub(crate) async fn prepare(
        pipelines: &NativePipelineCache,
        source: Arc<NativeReplayPlan>,
        pages: &PreparedNativeScenePages,
        targets: Arc<PreparedNativeLayerTargets>,
        previous: Option<&Self>,
    ) -> Result<Arc<Self>, Error> {
        if !Arc::ptr_eq(source.source(), pages.source().source())
            || !Arc::ptr_eq(source.targets(), targets.plan())
            || pipelines.device() != targets.device()
            || pipelines.target_format() != targets.format()
            || pipelines.device() != pages.material().device()
        {
            return Err(Error::ResourceDomain);
        }
        let frame = source.source().frame();
        let mut uniforms = NativeGpuUniforms::new([frame.width, frame.height], frame.dpi_scale)?;
        let root_certified = !source.source().budget().destination_sampling
            && uniforms.certify_root_glyph_coverage(
                [frame.width, frame.height],
                [0.0, 0.0, frame.width as f32, frame.height as f32],
            );
        let mut metrics = NativeSceneFrameMetrics::default();
        let root = if let Some(previous) = previous.and_then(|p| p.frames[0].as_ref()).filter(|p| {
            p.material().device() == pipelines.device()
                && p.uniforms().to_bytes() == uniforms.to_bytes()
        }) {
            if Arc::ptr_eq(previous.material(), pages.material()) {
                previous.clone()
            } else {
                let bindings =
                    NativeFrameBindings::rebind(pipelines, pages.material().clone(), previous)
                        .await?;
                metrics.root_binding_allocations = bindings.binding_allocations();
                Arc::new(bindings)
            }
        } else {
            let bindings =
                NativeFrameBindings::prepare(pipelines, pages.material().clone(), uniforms).await?;
            metrics.root_uniform_upload_bytes = bindings.upload_bytes();
            metrics.root_binding_allocations = bindings.binding_allocations();
            Arc::new(bindings)
        };
        let mut frames = slots()?;
        frames[0] = Some(root);
        for request in targets.plan().requests() {
            if request.target == NativeLayerTargetId::Root {
                continue;
            }
            let selected = targets
                .target(request.target)
                .ok_or(Error::ResourceDomain)?;
            let binding = selected.frame().ok_or(Error::ResourceDomain)?;
            let expected = NativeGpuUniforms::new(request.size, frame.dpi_scale)?;
            if !Arc::ptr_eq(binding.material(), pages.material())
                || binding.uniforms().to_bytes() != expected.to_bytes()
            {
                return Err(Error::ResourceDomain);
            }
            frames[index(request.target)?] = Some(binding.clone());
        }
        let mut needs_glyphs = [false; LAYER_SLOT_COUNT as usize + 1];
        for operation in source.operations() {
            if let NativeReplayOperation::Bundle(bundle) = operation {
                if bundle
                    .draws
                    .iter()
                    .any(|d| matches!(d, NativeReplayDraw::Glyph { .. }))
                {
                    needs_glyphs[index(bundle.target)?] = true;
                }
            }
        }
        let mut glyphs = slots()?;
        for (i, needed) in needs_glyphs.into_iter().enumerate() {
            if !needed {
                continue;
            }
            let frame = frames[i].as_ref().ok_or(Error::ResourceDomain)?;
            let page = pages.glyphs().ok_or(Error::ResourceDomain)?;
            if let Some(old) = previous
                .and_then(|p| p.glyphs[i].as_ref())
                .filter(|old| Arc::ptr_eq(old.page(), page) && Arc::ptr_eq(old.frame(), frame))
            {
                glyphs[i] = Some(old.clone());
                continue;
            }
            if let Some(binding) = previous
                .and_then(|p| p.glyphs[i].as_ref())
                .and_then(|old| old.rebind(page.clone(), frame.clone()))
            {
                glyphs[i] = Some(Arc::new(binding));
                continue;
            }
            let binding =
                NativeGlyphFrameBindings::prepare(pipelines, page.clone(), frame.clone()).await?;
            metrics.glyph_paint_binding_allocations +=
                u32::from(!page.page().page().source().paints().is_empty());
            glyphs[i] = Some(Arc::new(binding));
        }
        Ok(Arc::new(Self {
            source,
            targets,
            frames,
            glyphs,
            root_certified,
            metrics,
        }))
    }
    pub fn source(&self) -> &Arc<NativeReplayPlan> {
        &self.source
    }
    pub fn targets(&self) -> &Arc<PreparedNativeLayerTargets> {
        &self.targets
    }
    pub fn root_glyph_coverage_certified(&self) -> bool {
        self.root_certified
    }
    pub fn metrics(&self) -> NativeSceneFrameMetrics {
        self.metrics
    }
    pub fn frame(&self, target: NativeLayerTargetId) -> Result<&Arc<NativeFrameBindings>, Error> {
        self.frames
            .get(index(target)?)
            .and_then(Option::as_ref)
            .ok_or(Error::ResourceDomain)
    }
    pub fn glyph_frame(
        &self,
        target: NativeLayerTargetId,
    ) -> Result<&Arc<NativeGlyphFrameBindings>, Error> {
        self.glyphs
            .get(index(target)?)
            .and_then(Option::as_ref)
            .ok_or(Error::ResourceDomain)
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn slots<T>() -> Result<Vec<Option<Arc<T>>>, Error> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(LAYER_SLOT_COUNT as usize + 1)
        .map_err(|_| Error::Capacity)?;
    values.resize_with(LAYER_SLOT_COUNT as usize + 1, || None);
    Ok(values)
}
fn index(target: NativeLayerTargetId) -> Result<usize, Error> {
    match target {
        NativeLayerTargetId::Root => Ok(0),
        NativeLayerTargetId::Slot(slot) if slot < LAYER_SLOT_COUNT => Ok(slot as usize + 1),
        _ => Err(Error::InvalidInput),
    }
}
