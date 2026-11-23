//! Original ordered native replay. Preparation owns every resource before the
//! first target pass; encoding does not invoke a source producer or publish cache keys.
//!
//! Ported from `progpu_native_semantic_render_execution.cpp` 6010–6638;
//! source SHA-256 `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae`.
//! Implementation only: builds, pixel checks and performance qualification remain deferred.
//! Cross-generation page/bundle cache integration is a separate pending slice.
//! The outer renderer's native R8 shader/mask-engine construction remains gated;
//! this loop does not substitute RGBA storage for that original child-engine route.
use super::*;
use crate::native_scene::{
    engine_resources::{
        NativeRgbGlyphMetrics, NativeRgbGlyphPage, NativeRgbGlyphSubmission, PreparedNativeMask,
        PreparedNativeRgbGlyphPage,
        advanced_blend::{PreparedNativeAdvancedBlends, PreparedNativeRootCopy},
        effects::PreparedNativeEffects,
        layers::{
            NativeLayerOutputUpdates, NativeLayerTargetId, NativeLayerTargetPool,
            PreparedNativeLayerComposite,
        },
        shader_effects::PreparedNativeShaderEffect,
    },
    execution::{NativeReplayEffect, NativeReplayOperation, NativeReplayPlan},
};
mod encode;
mod inventory;
mod skip;
pub(crate) use skip::native_replay_skipped_operations;

/// Resources at exact operation indices. Empty entries are required for other
/// operation families; no index may silently select an unrelated source record.
/// RGB occurrences are supplied independently as fresh one-shot frame work.
pub(crate) struct NativePreparedReplayResources {
    pub source: Arc<NativeReplayPlan>,
    pub pages: Arc<PreparedNativeScenePages>,
    pub frames: Arc<NativeSceneFrames>,
    pub composite: Option<Arc<PreparedNativeLayerComposite>>,
    pub effects: Option<Arc<PreparedNativeEffects>>,
    pub advanced: Option<Arc<PreparedNativeAdvancedBlends>>,
    pub masks: Vec<Arc<PreparedNativeMask>>,
    /// Immutable mask programs run only before the generation's first submitted
    /// use. Warm reuse retains their actual completed texture owners.
    pub prepare_masks: bool,
    pub bundles: Vec<Option<Arc<PreparedNativeReplayBundle>>>,
    pub layer_draws: Vec<Option<Arc<PreparedNativeLayerDraw>>>,
    pub backdrops: Vec<Option<Arc<PreparedNativeBackdrop>>>,
    pub shaders: Vec<Option<Arc<PreparedNativeShaderEffect>>>,
    pub rgb_pages: Vec<Option<Arc<NativeRgbGlyphPage>>>,
    pub effect_indices: Vec<Option<usize>>,
    pub advanced_indices: Vec<Option<usize>>,
    pub root_copy: Option<Arc<PreparedNativeRootCopy>>,
}
/// Actual work emitted by this replay, independent of immutable preparation
/// uploads. Source draw accounting deliberately excludes legacy effect passes
/// and retains the original backdrop counter; encoded_draws reports all draws.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeReplayExecutionMetrics {
    pub draw_calls: u64,
    pub encoded_draws: u64,
    pub render_passes: u64,
    /// Actually encoded built-in effects and RGB compute coverage; excludes the
    /// caller's preceding ordinary coverage/live-image work and mask preparation.
    pub compute_passes: u64,
    pub layer_content_passes: u64,
    pub effect_operations: u64,
    pub effect_passes: u64,
    pub effect_cache_hits: u64,
    pub advanced_passes: u64,
    pub root_copies: u64,
    pub target_clears: u64,
    pub clear_uniform_upload_bytes: u64,
    pub texture_copy_bytes: u64,
    pub rgb: NativeRgbGlyphMetrics,
    pub submissions: u64,
}
/// Unsubmitted actual commands and working cache keys. Dropping this result
/// retires the candidate without publishing any output or submitting GPU work.
pub(crate) struct NativeEncodedReplay {
    resources: Arc<NativePreparedReplayResources>,
    root: NativeRenderAttachment,
    preceding: Vec<wgpu::CommandBuffer>,
    command: wgpu::CommandBuffer,
    updates: NativeLayerOutputUpdates,
    rgb: Vec<NativeRgbGlyphSubmission>,
    metrics: NativeReplayExecutionMetrics,
}
impl NativeEncodedReplay {
    pub fn metrics(&self) -> NativeReplayExecutionMetrics {
        self.metrics
    }
    /// The original preceding coverage buffers submit individually in order.
    /// Output keys become visible only after the actual main submission. Even
    /// a stale-pool rejection retains every submitted source until completion.
    pub fn submit(
        self,
        queue: &wgpu::Queue,
        pool: &mut NativeLayerTargetPool,
    ) -> Result<NativeReplayExecutionMetrics, Error> {
        let Self {
            resources,
            root,
            preceding,
            command,
            updates,
            rgb,
            mut metrics,
        } = self;
        for command in preceding {
            queue.submit([command]);
            metrics.submissions += 1;
        }
        queue.submit([command]);
        metrics.submissions += 1;
        resources.retain_submitted(queue);
        root.retain_submitted(queue);
        for packet in rgb {
            packet.retain_submitted(queue);
        }
        pool.commit_submitted(resources.frames.targets(), updates, queue)?;
        Ok(metrics)
    }
}
impl NativePreparedReplayResources {
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        self.pages.retain_submitted(queue);
        self.frames.retain_submitted(queue);
        if let Some(vertices) = &self.composite {
            vertices.retain_submitted(queue);
        }
        if let Some(effects) = &self.effects {
            effects.retain_submitted(queue);
        }
        if let Some(advanced) = &self.advanced {
            advanced.retain_submitted(queue);
        }
        for mask in &self.masks {
            mask.retain_submitted(queue);
        }
        for shader in self.shaders.iter().flatten() {
            shader.retain_submitted(queue);
        }
        if let Some(copy) = &self.root_copy {
            copy.retain_submitted(queue);
        }
        // Includes bundles, material frames, background sources and ordinary
        // composite texture leases, even if the engine replaces this inventory.
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
