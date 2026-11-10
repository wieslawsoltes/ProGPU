//! Original actual-target owned shader preparation, publication and retirement.
use super::*;
use crate::{
    composition::SourceRenderFrame,
    scene::{ManagedShaderEffectDraw, OwnedShaderEffectDraw},
    shader_effect::{
        ManagedCaptureFrame, ManagedShaderQuad, OwnedShaderEffectParameters,
        OwnedShaderEffectRecording, OwnedShaderEffectSource, SampleLattice, source_pixels_per_unit,
    },
};
use std::{cell::RefCell, collections::HashSet, rc::Rc};

/// Exact actual parent and independently derived implicit-input capture frame.
/// Output transform and source semantic DPI never replace the projection scale.
#[derive(Clone, Copy, Debug)]
pub struct OwnedShaderTargetFrame {
    /// Original parent projection after actual bounded-target remapping.
    pub projection: [f32; 16],
    /// Actual physical viewport X/Y/width/height.
    pub viewport: [f32; 4],
    /// Actual physical parent origin and extent.
    pub target: SampleLattice,
    /// Independent semantic text/snapping DPI.
    pub dpi_scale: f32,
    /// Original complete source/padding/UV/raster descriptor.
    pub capture_frame: ManagedCaptureFrame,
    /// Original source-cache text suppression inherited from this target.
    pub suppress_cached_cleartype: bool,
    /// Actual nested RenderOffscreen depth, preserving canonical mask admission.
    pub offscreen_depth: u16,
}
impl OwnedShaderTargetFrame {
    /// Exact immutable target identity, including float bits and original frames.
    pub fn same_target(self, other: Self) -> bool {
        self.projection.map(f32::to_bits) == other.projection.map(f32::to_bits)
            && self.viewport.map(f32::to_bits) == other.viewport.map(f32::to_bits)
            && self.target == other.target
            && self.dpi_scale.to_bits() == other.dpi_scale.to_bits()
            && self.capture_frame.same_capture(&other.capture_frame)
            && self.suppress_cached_cleartype == other.suppress_cached_cleartype
            && self.offscreen_depth == other.offscreen_depth
    }
}

/// Exclusive source-generation preparation context. A recipe may realize its
/// immutable recorded samplers using this renderer and the actual target. It
/// must not inspect mutable UI or encode the consumer draw during preparation.
pub struct ShaderEffectPreparationContext<'a> {
    /// Actual creating renderer, available only for this preparation borrow.
    pub renderer: &'a mut Renderer,
    /// Independently retained original source recipe identity.
    pub source: OwnedShaderEffectSource,
    /// Actual current target and derived implicit-input capture.
    pub target: OwnedShaderTargetFrame,
}
impl ShaderEffectPreparationContext<'_> {
    /// Actual live device owner, never a global or previously captured context.
    pub fn device(&self) -> &wgpu::Device {
        &self.renderer.device
    }
}

#[derive(Default)]
pub(super) struct OwnedShaderState {
    targets: Vec<TargetEntry>,
    preparing: Rc<RefCell<HashSet<usize>>>,
    frame: u64,
    depth: u16,
}
impl OwnedShaderState {
    pub(super) fn frame_number(&self) -> u64 {
        self.frame
    }
}
struct TargetEntry {
    target: Arc<OwnedShaderTarget>,
    last_used: u64,
}
pub(super) struct OwnedShaderTarget {
    recording: OwnedShaderEffectRecording,
    context: OwnedShaderTargetFrame,
    parameters: OwnedShaderEffectParameters,
    capture: CapturedSourceScene,
    owner: Arc<()>,
    device: wgpu::Device,
}
impl OwnedShaderTarget {
    fn draw(&self, source: &OwnedShaderEffectDraw) -> Result<ManagedShaderEffectDraw, Error> {
        let frame = self.context.capture_frame;
        let mut quad = ManagedShaderQuad::new(frame.padded_bounds);
        quad.uv = frame.texture_uv_bounds;
        quad.output_edges = Some(frame.output_edges);
        quad.transform = source.affine_transform()?;
        quad.opacity = source.opacity;
        let result = ManagedShaderEffectDraw {
            parameters: self
                .parameters
                .snapshot()
                .with_captured_input(self.capture.image().clone()),
            quad,
            target: self.context.target,
            blend: source.blend,
            mask: None,
        };
        result.validate()?;
        Ok(result)
    }
}

/// A completed prepared tree retains exact source/parameter/capture owners even
/// after a later completed frame evicts their mutable renderer cache entries.
#[derive(Default)]
pub(super) struct PreparedOwnedShaders {
    targets: Vec<Arc<OwnedShaderTarget>>,
    metrics: OwnedShaderMetrics,
}
/// Actual source-generation work for this scene's direct owned shader commands.
/// Captured children keep their own complete preparation/replay counters.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct OwnedShaderMetrics {
    /// Ordered owned commands resolved in this preparation.
    pub commands: u32,
    /// Exact target generations reused from a prior successful preparation.
    pub cache_hits: u32,
    /// New immutable source textures captured for these commands.
    pub captures: u32,
    /// New source allocation bytes, excluding nested source intermediates.
    pub capture_bytes: u64,
}
impl OwnedShaderMetrics {
    pub(super) fn merge(&mut self, other: Self) -> Result<(), Error> {
        self.commands = self
            .commands
            .checked_add(other.commands)
            .ok_or(Error::Capacity)?;
        self.cache_hits = self
            .cache_hits
            .checked_add(other.cache_hits)
            .ok_or(Error::Capacity)?;
        self.captures = self
            .captures
            .checked_add(other.captures)
            .ok_or(Error::Capacity)?;
        self.capture_bytes = self
            .capture_bytes
            .checked_add(other.capture_bytes)
            .ok_or(Error::Capacity)?;
        Ok(())
    }
}
impl PreparedScene {
    /// Actual direct owned shader preparation work; replay does not recapture.
    pub fn owned_shader_metrics(&self) -> OwnedShaderMetrics {
        self.owned_shaders.metrics
    }
}

// Complete-frame eviction is independent of nested source preparation. Failed
// and cancelled candidates truncate only newly published variants; earlier
// prepared scenes keep their Arc-owned source generations alive.
pub(super) struct OwnedShaderFrame<'a> {
    pub renderer: &'a mut Renderer,
    checkpoint: usize,
    cached_checkpoint: usize,
    visual_checkpoint: Option<visual::VisualCacheCheckpoint>,
    owner: Arc<()>,
    complete: bool,
}
impl<'a> OwnedShaderFrame<'a> {
    pub fn begin(renderer: &'a mut Renderer) -> Result<Self, Error> {
        let state = &mut renderer.owned_shaders;
        let outer = state.depth == 0;
        if outer {
            renderer.mesh3d_plans.clear();
            // render/capture marks only successfully submitted complete frames.
            // Cache maintenance runs at the next exclusive preparation boundary;
            // no successful preparation alone retires unused source variants.
            let completed = renderer
                .owned_shader_completed
                .swap(0, std::sync::atomic::Ordering::Relaxed);
            if completed != 0 {
                state.targets.retain(|t| t.last_used == completed);
                renderer.cached_pictures.sweep(completed);
            }
            state.frame = state.frame.checked_add(1).ok_or(Error::Capacity)?;
        }
        state.depth = state.depth.checked_add(1).ok_or(Error::Capacity)?;
        Ok(Self {
            checkpoint: state.targets.len(),
            cached_checkpoint: renderer.cached_pictures.targets.len(),
            visual_checkpoint: Some(renderer.visuals.checkpoint()),
            owner: renderer.owner.clone(),
            renderer,
            complete: false,
        })
    }
    pub fn commit(mut self) -> Result<(), Error> {
        if !Arc::ptr_eq(&self.owner, &self.renderer.owner) {
            return Err(Error::ResourceDomain);
        }
        self.complete = true;
        Ok(())
    }
}
impl Drop for OwnedShaderFrame<'_> {
    fn drop(&mut self) {
        if !Arc::ptr_eq(&self.owner, &self.renderer.owner) {
            return;
        }
        let state = &mut self.renderer.owned_shaders;
        if !self.complete {
            state.targets.truncate(self.checkpoint);
            if let Some(checkpoint) = self.visual_checkpoint.take() {
                self.renderer.visuals.restore(checkpoint);
            }
            self.renderer
                .cached_pictures
                .targets
                .truncate(self.cached_checkpoint);
        }
        state.depth = state.depth.saturating_sub(1);
        if state.depth == 0 {
            self.renderer.mesh3d_plans.clear();
        }
    }
}
/// Detached source-cycle lease shared by ordered picture and visual recipes.
/// It owns the exact registry generation; cancellation removes only its entry.
pub(super) struct OwnedShaderSourceGuard {
    registry: Rc<RefCell<HashSet<usize>>>,
    source: usize,
    _owner: OwnedShaderEffectSource,
}
impl Drop for OwnedShaderSourceGuard {
    fn drop(&mut self) {
        self.registry.borrow_mut().remove(&self.source);
    }
}
impl Renderer {
    pub(super) fn begin_owned_shader_source(
        &self,
        source: &OwnedShaderEffectSource,
    ) -> Result<OwnedShaderSourceGuard, Error> {
        let registry = self.owned_shaders.preparing.clone();
        let mut entries = registry.borrow_mut();
        if entries.len() >= 256
            || self.owned_shaders.targets.len() >= 16384
            || !entries.insert(source.identity())
        {
            return Err(Error::Capacity);
        }
        drop(entries);
        Ok(OwnedShaderSourceGuard {
            registry,
            source: source.identity(),
            _owner: source.clone(),
        })
    }
}
struct SourcePreparation<'a> {
    renderer: &'a mut Renderer,
    _source: OwnedShaderSourceGuard,
    checkpoint: usize,
    visual_checkpoint: Option<visual::VisualCacheCheckpoint>,
    owner: Arc<()>,
    complete: bool,
}
impl Drop for SourcePreparation<'_> {
    fn drop(&mut self) {
        if !Arc::ptr_eq(&self.owner, &self.renderer.owner) {
            return;
        }
        let state = &mut self.renderer.owned_shaders;
        if !self.complete {
            state.targets.truncate(self.checkpoint);
            if let Some(checkpoint) = self.visual_checkpoint.take() {
                self.renderer.visuals.restore(checkpoint);
            }
        }
    }
}
impl Renderer {
    /// Submit a caller encoder after encoding this complete prepared scene. This
    /// marks successful frame submission for deferred owned-target cache sweeping.
    /// Use this with `encode` when additional same-device commands share the frame;
    /// ordinary `render` and `capture` already perform this ownership step.
    pub fn submit_encoded_scene(
        &self,
        scene: &PreparedScene,
        encoder: wgpu::CommandEncoder,
    ) -> Result<wgpu::SubmissionIndex, Error> {
        if !self.is_scene_current(scene) {
            return Err(Error::ResourceDomain);
        }
        let submission = self.queue.submit([encoder.finish()]);
        scene.retain_submitted_images(&self.queue);
        self.submitted_owned_shader_frame(scene);
        Ok(submission)
    }
    /// Actual cached source/target variants. Prepared scene leases are independent
    /// of this count and can outlive the next completed-frame sweep.
    pub fn owned_shader_cached_targets(&self) -> usize {
        self.owned_shaders.targets.len()
    }
    pub(super) fn submitted_owned_shader_frame(&self, scene: &PreparedScene) {
        if self.owned_shaders.depth == 0 && scene.owned_shader_frame != 0 {
            self.owned_shader_completed.fetch_max(
                scene.owned_shader_frame,
                std::sync::atomic::Ordering::Relaxed,
            );
        }
    }
    fn owned_shader_target(
        &self,
        source: &OwnedShaderEffectSource,
        frame: RenderFrame,
    ) -> Result<OwnedShaderTargetFrame, Error> {
        let projection = frame
            .uniforms(self.device.limits().max_texture_dimension_2d)?
            .projection;
        let viewport = [0.0, 0.0, frame.width as f32, frame.height as f32];
        let scale = source_pixels_per_unit(projection, viewport)?;
        let capture_frame = ManagedCaptureFrame::source_physical(
            source.source_capture(),
            source.source_translation(),
            scale,
            frame.dpi_scale,
        )?;
        let target = SampleLattice {
            origin: [
                i32::try_from(frame.origin[0]).map_err(|_| Error::Capacity)?,
                i32::try_from(frame.origin[1]).map_err(|_| Error::Capacity)?,
            ],
            size: [frame.width, frame.height],
        };
        Ok(OwnedShaderTargetFrame {
            offscreen_depth: frame.mask_depth,
            projection,
            viewport,
            target,
            dpi_scale: frame.dpi_scale,
            capture_frame,
            suppress_cached_cleartype: frame.source.is_some_and(|s| s.suppress_cached_cleartype()),
        })
    }
    pub(super) fn preflight_owned_shaders(
        &self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        let mut count = 0usize;
        let mut bytes = 0u64;
        for (_, draw) in scene.owned_shader_effects() {
            draw.validate()?;
            draw.recording
                .scene()
                .preflight_source_capture_devices(&self.device)?;
            let context = self.owned_shader_target(draw.recording.source(), frame)?;
            let [w, h] = context.capture_frame.pixel_size;
            let maximum = self.device.limits().max_texture_dimension_2d;
            if w > maximum || h > maximum {
                return Err(Error::InvalidInput);
            }
            bytes = bytes
                .checked_add(target_texture_bytes(self.format, [w, h])?)
                .ok_or(Error::Capacity)?;
            count += 1;
            if bytes > 512 * 1024 * 1024 || count > 16384 {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    async fn prepare_owned_shader(
        &mut self,
        recording: &OwnedShaderEffectRecording,
        frame: RenderFrame,
    ) -> Result<(Arc<OwnedShaderTarget>, bool), Error> {
        let context = self.owned_shader_target(recording.source(), frame)?;
        let epoch = self.owned_shaders.frame;
        for entry in &mut self.owned_shaders.targets {
            if entry.target.recording.identity() == recording.identity()
                && entry.target.context.same_target(context)
                && Arc::ptr_eq(&entry.target.owner, &self.owner)
                && entry.target.device == self.device
            {
                entry.last_used = epoch;
                return Ok((entry.target.clone(), true));
            }
        }
        let source_guard = self.begin_owned_shader_source(recording.source())?;
        let actual_device = self.device.clone();
        let actual_owner = self.owner.clone();
        let mut guard = SourcePreparation {
            _source: source_guard,
            checkpoint: self.owned_shaders.targets.len(),
            visual_checkpoint: Some(self.visuals.checkpoint()),
            owner: actual_owner.clone(),
            renderer: self,
            complete: false,
        };
        let parameters = recording
            .source()
            .preparation()
            .prepare(ShaderEffectPreparationContext {
                renderer: &mut *guard.renderer,
                source: recording.source().clone(),
                target: context,
            })
            .await?;
        parameters.validate_device(&actual_device)?;
        if guard.renderer.device != actual_device
            || !Arc::ptr_eq(&guard.renderer.owner, &actual_owner)
            || !guard
                .renderer
                .owned_shader_target(recording.source(), frame)?
                .same_target(context)
        {
            return Err(Error::ResourceDomain);
        }
        let capture_frame =
            SourceRenderFrame::effect(context.capture_frame, context.suppress_cached_cleartype)?;
        let capture = Box::pin(
            guard
                .renderer
                .capture_source_scene(recording.scene().clone(), capture_frame),
        )
        .await?;
        if guard.renderer.device != actual_device
            || !Arc::ptr_eq(&guard.renderer.owner, &actual_owner)
            || !guard
                .renderer
                .owned_shader_target(recording.source(), frame)?
                .same_target(context)
        {
            return Err(Error::ResourceDomain);
        }
        let candidate = Arc::new(OwnedShaderTarget {
            recording: recording.clone(),
            context,
            parameters,
            capture,
            owner: actual_owner,
            device: actual_device,
        });
        guard
            .renderer
            .owned_shaders
            .targets
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        guard.renderer.owned_shaders.targets.push(TargetEntry {
            target: candidate.clone(),
            last_used: epoch,
        });
        guard.complete = true;
        Ok((candidate, false))
    }
    pub(super) async fn prepare_owned_shader_scene(
        &mut self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(Scene, PreparedOwnedShaders), Error> {
        let mut prepared = PreparedOwnedShaders::default();
        let mut replacements = Vec::new();
        for (_, draw) in scene.owned_shader_effects() {
            prepared
                .targets
                .try_reserve(1)
                .map_err(|_| Error::Capacity)?;
            replacements.try_reserve(1).map_err(|_| Error::Capacity)?;
            let (target, hit) = Box::pin(self.prepare_owned_shader(&draw.recording, frame)).await?;
            replacements.push(target.draw(draw)?);
            prepared.metrics.commands = prepared
                .metrics
                .commands
                .checked_add(1)
                .ok_or(Error::Capacity)?;
            if hit {
                prepared.metrics.cache_hits = prepared
                    .metrics
                    .cache_hits
                    .checked_add(1)
                    .ok_or(Error::Capacity)?;
            } else {
                prepared.metrics.captures = prepared
                    .metrics
                    .captures
                    .checked_add(1)
                    .ok_or(Error::Capacity)?;
                prepared.metrics.capture_bytes = prepared
                    .metrics
                    .capture_bytes
                    .checked_add(target.capture.capture().capture_bytes())
                    .ok_or(Error::Capacity)?;
            }
            prepared.targets.push(target);
        }
        Ok((scene.replace_owned_shader_effects(replacements)?, prepared))
    }
}
