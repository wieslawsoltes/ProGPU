//! Original nested native picture ownership, capture identity and child engines.
//! The actual ordered renderer is supplied through a typed executor; no raster
//! substitute or unrendered texture is returned by the capture runtime.
use super::*;
use crate::native_scene::execution::{NativeSceneFrame, NativeSceneResourcePlan};
#[cfg(feature = "gpu")]
mod adapters;
pub(crate) mod identity;
mod metrics;
pub use metrics::NativePictureOutputMetrics;
#[cfg(feature = "gpu")]
mod runtime;
#[cfg(feature = "gpu")]
pub use runtime::{
    NativePictureCapture, NativePictureExecutor, NativePictureExecutorFactory, NativePictureRuntime,
};

#[derive(Clone, Copy, Debug)]
pub struct NativePictureRuntimeOptions {
    pub engine_flags: u32,
    pub antialias_padding_pixels: f32,
}
/// Actual child work excludes the capture attachment supplied by this runtime.
/// Descriptor budget bytes and physical output allocation are reported separately.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativePictureWorkMetrics {
    pub frame: wire::FrameMetrics,
    pub texture_bytes: u64,
    pub texture_allocations: u64,
    pub render_passes: u32,
    pub compute_passes: u32,
    pub live_blur_passes: u64,
    pub path_rasters: u32,
    pub nested_captures: u32,
    pub cache_hits: u32,
    pub seed_copy_bytes: u64,
}
/// Fully admitted immutable child candidate. `scene` can be the original
/// append-only command suffix; `source_scene` always retains the full capture.
pub struct NativePictureExecutionRequest {
    source_scene: Arc<NativeScene>,
    scene: Arc<NativeScene>,
    plan: Arc<NativeSceneResourcePlan>,
    frame: NativeSceneFrame,
    resource_scope: u64,
    first_command: u32,
    exact_snapshot: bool,
    external_changed: bool,
}
impl NativePictureExecutionRequest {
    pub fn source_scene(&self) -> &Arc<NativeScene> {
        &self.source_scene
    }
    pub fn scene(&self) -> &Arc<NativeScene> {
        &self.scene
    }
    pub fn plan(&self) -> &Arc<NativeSceneResourcePlan> {
        &self.plan
    }
    pub fn frame(&self) -> NativeSceneFrame {
        self.frame
    }
    pub fn resource_scope(&self) -> u64 {
        self.resource_scope
    }
    pub fn first_command(&self) -> u32 {
        self.first_command
    }
    pub fn exact_snapshot(&self) -> bool {
        self.exact_snapshot
    }
    pub fn external_changed(&self) -> bool {
        self.external_changed
    }
}
