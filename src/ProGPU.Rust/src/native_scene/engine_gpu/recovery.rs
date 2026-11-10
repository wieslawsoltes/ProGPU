//! Original explicit device-loss notification and retained CPU recreation.
//! This module is a child of `renderer`: no GPU or external-image owner is part
//! of the cloned state, and a lost renderer remains terminal.
use super::*;
use crate::native_scene::{
    NativeSceneAdmissionError, NativeSceneContentHashes, NativeSceneValidationError,
    engine_materials::{NativeBrushPage, NativeTextStylePage},
    identity::{raw_fnv1a64, scene_bytes_equal},
};
use std::thread::{self, ThreadId};

/// Original native lifecycle diagnostics, independently of the generic renderer
/// error. GPU entry points retain their existing `Error` return type; this API
/// exposes the precise native owner-thread/loss/update status.
#[derive(Debug, thiserror::Error)]
pub enum NativeSceneRecoveryError {
    #[error("native device recovery must run on the renderer owner thread")]
    WrongThread,
    #[error("the native WebGPU device was lost; recreate the renderer on a replacement device")]
    DeviceLost,
    #[error("only a lost native renderer can be recreated")]
    DeviceNotLost,
    #[error("the replacement renderer must preserve the original target format")]
    TargetFormat,
    #[error("no native CPU scene has been retained")]
    MissingScene,
    #[error(transparent)]
    Admission(#[from] NativeSceneAdmissionError),
    #[error(
        "one native scene generation must be immutable and resource generations must not regress"
    )]
    Generation { metrics: wire::Metrics },
    #[error(transparent)]
    Renderer(#[from] Error),
}
impl NativeSceneRecoveryError {
    pub fn status(&self) -> u32 {
        match self {
            Self::WrongThread => STATUS_WRONG_THREAD,
            Self::DeviceLost => STATUS_DEVICE_LOST,
            Self::Admission(error) => error.status(),
            Self::Renderer(Error::Capacity) => STATUS_OUT_OF_MEMORY,
            Self::Renderer(Error::UnsupportedExecution) => STATUS_UNSUPPORTED,
            Self::Renderer(Error::Gpu(_)) => STATUS_INTERNAL_ERROR,
            _ => STATUS_INVALID_ARGUMENT,
        }
    }
    /// Admission diagnostics are produced before a generation rejection, just
    /// as in the native update entry point. Lifecycle failures have no metrics.
    pub fn scene_metrics(&self) -> Option<wire::Metrics> {
        match self {
            Self::Admission(error) => Some(error.metrics()),
            Self::Generation { metrics } => Some(*metrics),
            _ => None,
        }
    }
    pub fn write_scene_metrics(&self, destination: &mut wire::Metrics) {
        if let Some(metrics) = self.scene_metrics() {
            publish(destination, metrics);
        }
    }
    pub(super) fn into_render_error(self) -> Error {
        match self {
            Self::Renderer(error) => error,
            Self::WrongThread | Self::DeviceLost => Error::ResourceDomain,
            _ => Error::InvalidInput,
        }
    }
}

/// Complete CPU scene metadata copied by the original recreation function.
/// Brush/style caches can refer to an older source after a CPU-only update;
/// their independent family hashes decide whether the next compile reuses them.
#[derive(Clone, Debug)]
pub struct NativeRetainedCpuState {
    scene: Arc<NativeScene>,
    raw_hash: u64,
    hashes: NativeSceneContentHashes,
    materials: Option<Arc<NativeBrushPage>>,
    styles: Option<Arc<NativeTextStylePage>>,
}
impl NativeRetainedCpuState {
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn raw_hash(&self) -> u64 {
        self.raw_hash
    }
    pub fn content_hashes(&self) -> NativeSceneContentHashes {
        self.hashes
    }
    pub fn metrics(&self) -> wire::Metrics {
        self.scene.metrics()
    }
    pub fn header(&self) -> wire::Header {
        self.scene.envelope().header()
    }
    pub fn brushes(&self) -> Option<&Arc<NativeBrushPage>> {
        self.materials.as_ref()
    }
    pub fn text_styles(&self) -> Option<&Arc<NativeTextStylePage>> {
        self.styles.as_ref()
    }
}

pub(super) struct NativeDeviceRecovery {
    owner: ThreadId,
    lost: bool,
    generation: u64,
    retained: Option<NativeRetainedCpuState>,
}
impl NativeDeviceRecovery {
    pub fn new() -> Self {
        Self {
            owner: thread::current().id(),
            lost: false,
            generation: 0,
            retained: None,
        }
    }
    fn require_owner(&self) -> Result<(), NativeSceneRecoveryError> {
        if thread::current().id() != self.owner {
            Err(NativeSceneRecoveryError::WrongThread)
        } else {
            Ok(())
        }
    }
    fn require_live(&self) -> Result<(), NativeSceneRecoveryError> {
        self.require_owner()?;
        if self.lost {
            Err(NativeSceneRecoveryError::DeviceLost)
        } else {
            Ok(())
        }
    }
    pub fn require_gpu(&self) -> Result<(), Error> {
        self.require_live()
            .map_err(NativeSceneRecoveryError::into_render_error)
    }
    fn mark_lost(&mut self) -> Result<(), NativeSceneRecoveryError> {
        self.require_owner()?;
        if !self.lost {
            self.lost = true;
            self.generation = self.generation.wrapping_add(1);
        }
        Ok(())
    }
    pub fn brushes(&self) -> Option<&Arc<NativeBrushPage>> {
        self.retained.as_ref().and_then(|r| r.materials.as_ref())
    }
    pub fn styles(&self) -> Option<&Arc<NativeTextStylePage>> {
        self.retained.as_ref().and_then(|r| r.styles.as_ref())
    }
    fn install(
        &mut self,
        scene: Arc<NativeScene>,
        scope: u64,
        independent_capture: bool,
    ) -> Result<(wire::Metrics, bool), NativeSceneRecoveryError> {
        self.require_owner()?;
        if let Some(previous) = &self.retained {
            if scene_bytes_equal(previous.scene.envelope().bytes(), scene.envelope().bytes()) {
                let mut metrics = previous.scene.metrics();
                metrics.flags |= SCENE_METRICS_SNAPSHOT_REUSED;
                return Ok((metrics, false));
            }
            let incoming = scene.envelope().header();
            let old = previous.scene.envelope().header();
            if !independent_capture && incoming.scene_id == old.scene_id {
                let offset = if incoming.generation <= old.generation {
                    Some(0)
                } else {
                    scene
                        .admit_resource_generations_after(&previous.scene)
                        .err()
                        .map(|e| e.offset)
                };
                if let Some(offset) = offset {
                    let mut metrics = scene.metrics();
                    metrics.validation_error = NativeSceneValidationError::Generation as u32;
                    metrics.error_offset = offset;
                    return Err(NativeSceneRecoveryError::Generation { metrics });
                }
            }
        }
        let next = NativeRetainedCpuState {
            raw_hash: raw_fnv1a64(scene.envelope().bytes()),
            hashes: scene.content_hashes(scope),
            materials: self.brushes().cloned(),
            styles: self.styles().cloned(),
            scene,
        };
        let metrics = next.metrics();
        self.retained = Some(next);
        Ok((metrics, true))
    }
    pub fn retain_plan(
        &mut self,
        plan: &NativeSceneResourcePlan,
        independent_capture: bool,
    ) -> Result<(), Error> {
        self.install(
            plan.scene().clone(),
            plan.options().resource_scope,
            independent_capture,
        )
        .map_err(NativeSceneRecoveryError::into_render_error)?;
        let retained = self.retained.as_mut().ok_or(Error::ResourceDomain)?;
        retained.materials = Some(plan.materials().clone());
        retained.styles = Some(plan.text_styles().clone());
        Ok(())
    }
}

impl NativeSceneRenderer {
    /// Explicit notification, matching the native entry point. It neither polls
    /// the device nor removes retained GPU objects; terminal destruction and
    /// already-submitted owners retain their normal lifetime responsibilities.
    pub fn mark_device_lost(&mut self) -> Result<(), NativeSceneRecoveryError> {
        self.core.mark_device_lost()?;
        self.pictures.mark_device_lost()?;
        Ok(())
    }
    pub fn is_device_lost(&self) -> bool {
        self.core.recovery.lost
    }
    pub fn device_loss_generation(&self) -> u64 {
        self.core.recovery.generation
    }
    pub fn require_live_device(&self) -> Result<(), NativeSceneRecoveryError> {
        self.core.recovery.require_live()
    }
    pub fn retained_cpu_state(&self) -> Option<&NativeRetainedCpuState> {
        self.core.recovery.retained.as_ref()
    }
    /// Source-only updates remain legal after loss. Exact byte equality is
    /// checked before parsing; a failed candidate leaves the old state intact.
    pub fn update_scene(
        &mut self,
        bytes: Arc<[u8]>,
    ) -> Result<wire::Metrics, NativeSceneRecoveryError> {
        self.core.recovery.require_owner()?;
        if let Some(previous) = &self.core.recovery.retained {
            if scene_bytes_equal(previous.scene.envelope().bytes(), &bytes) {
                let mut metrics = previous.metrics();
                metrics.flags |= SCENE_METRICS_SNAPSHOT_REUSED;
                return Ok(metrics);
            }
        }
        self.update_admitted_scene(Arc::new(NativeScene::new(bytes)?))
    }
    pub fn update_admitted_scene(
        &mut self,
        scene: Arc<NativeScene>,
    ) -> Result<wire::Metrics, NativeSceneRecoveryError> {
        let scope = self.core.cache.as_ref().map_or(0, |c| c.resource_scope());
        let (metrics, changed) = self.core.recovery.install(scene, scope, false)?;
        if changed && !self.core.recovery.lost {
            self.core.reuse = None;
        }
        Ok(metrics)
    }
    /// A replacement always starts with new pipeline/page/atlas/layer/picture
    /// caches and an empty external-image table. Supplied options use the same
    /// original constructor admission; the source engine stays lost and owned.
    pub fn recreate(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativeSceneRendererOptions,
    ) -> Result<Self, NativeSceneRecoveryError> {
        self.core.recovery.require_owner()?;
        if !self.core.recovery.lost {
            return Err(NativeSceneRecoveryError::DeviceNotLost);
        }
        // Original recreation creates the candidate first and drops it if the
        // retained-CPU clone rejects a different target format.
        let mut replacement = Self::new(device, queue, format, options)?;
        if self.core.format != replacement.core.format {
            return Err(NativeSceneRecoveryError::TargetFormat);
        }
        replacement.core.recovery.retained = self.core.recovery.retained.clone();
        replacement.core.recovery.generation = self.core.recovery.generation;
        Ok(replacement)
    }
    /// Recompile the retained source against the actual replacement limits and
    /// newly supplied external images. No old frame/attachment proof is reused.
    pub async fn render_retained(
        &mut self,
        frame: NativeSceneFrame,
        resource_scope: u64,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
    ) -> Result<NativeSceneRenderMetrics, NativeSceneRecoveryError> {
        self.require_live_device()?;
        let scene = self
            .retained_cpu_state()
            .ok_or(NativeSceneRecoveryError::MissingScene)?
            .scene()
            .clone();
        Ok(self
            .render(scene, frame, resource_scope, attachment, external)
            .await?)
    }
}
impl NativeSceneGpuContext {
    pub(super) fn retain_scene_for_render(
        &mut self,
        scene: Arc<NativeScene>,
        scope: u64,
    ) -> Result<Arc<NativeScene>, Error> {
        self.recovery.require_gpu()?;
        let (_, changed) = self
            .recovery
            .install(scene, scope, false)
            .map_err(NativeSceneRecoveryError::into_render_error)?;
        if changed {
            self.reuse = None;
        }
        Ok(self
            .recovery
            .retained
            .as_ref()
            .ok_or(Error::ResourceDomain)?
            .scene
            .clone())
    }
    pub(super) fn mark_device_lost(&mut self) -> Result<(), NativeSceneRecoveryError> {
        self.recovery.mark_lost()?;
        if let Some(cache) = &mut self.cache {
            cache.mark_device_lost();
        }
        Ok(())
    }
}
fn publish(destination: &mut wire::Metrics, mut metrics: wire::Metrics) {
    if destination.struct_size >= wire::Metrics::WIRE_SIZE as u32 {
        metrics.struct_size = destination.struct_size;
        *destination = metrics;
    }
}
