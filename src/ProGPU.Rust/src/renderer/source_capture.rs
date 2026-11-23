//! Actual source-frame preparation and owned source recordings through capture.
use super::*;
use crate::composition::SourceRenderFrame;

/// A fresh original source capture retaining the exact input recording and frame.
/// It is a source image, not an already evaluated programmable shader result.
/// Retained recordings and prepared consumers can outlive the renderer cache.
pub struct CapturedSourceScene {
    source: Arc<Scene>,
    frame: SourceRenderFrame,
    capture: CapturedScene,
}
impl CapturedSourceScene {
    /// Original independently owned recording, before source placement.
    pub fn source(&self) -> &Arc<Scene> {
        &self.source
    }
    /// Complete original physical/logical/DPI/placement generation.
    pub fn frame(&self) -> SourceRenderFrame {
        self.frame
    }
    /// Actual source texture and source-preparation/replay resource counters.
    pub fn capture(&self) -> &CapturedScene {
        &self.capture
    }
    /// Actual independently allocated premultiplied input texture.
    pub fn image(&self) -> &crate::image::GpuImage {
        self.capture.image()
    }
}
impl PreparedScene {
    /// Exact original source-frame generation, independent of the public physical
    /// allocation Frame. Ordinary window preparations return None.
    pub fn source_frame(&self) -> Option<SourceRenderFrame> {
        self.render_frame.source
    }
}
impl Renderer {
    pub(super) fn source_target(&self, source: SourceRenderFrame) -> Result<RenderFrame, Error> {
        let maximum = self.device.limits().max_texture_dimension_2d;
        if let Some(cache) = source.cache_frame()
            && cache.maximum_dimensions() != [maximum; 2]
        {
            // Original raw-cache capture requires the actual live device caps,
            // including empty realizations. A guessed default cap is not proof.
            return Err(Error::ResourceDomain);
        }
        let [width, height] = source.size();
        if target_texture_bytes(self.format, [width, height])? > 256 * 1024 * 1024 {
            return Err(Error::Capacity);
        }
        let frame = Frame {
            width,
            height,
            dpi_scale: source.dpi_scale(),
        };
        let target = RenderFrame {
            frame,
            root: frame,
            origin: [0; 2],
            source: Some(source),
            camera: None,
            mask_depth: self
                .preparation_mask_depth
                .get()
                .checked_add(1)
                .ok_or(Error::Capacity)?,
        };
        target.uniforms(maximum)?;
        Ok(target)
    }
    /// Compile an original source recording with exact source placement before
    /// geometry, stroke, text and mask planning. Actual projection and semantic
    /// DPI remain independent through the complete prepared target tree.
    pub async fn prepare_source_scene(
        &mut self,
        source: &Scene,
        frame: SourceRenderFrame,
    ) -> Result<PreparedScene, Error> {
        let target = self.source_target(frame)?;
        source.preflight_source_capture_devices(&self.device)?;
        let placed = source.place_source_capture(frame)?;
        self.prepare_target_scene(&placed, target).await
    }
    /// Capture one independently owned source generation. Retain the complete
    /// original recording before preparing even a proven empty/zero-scale cache.
    /// Source preparation does not evaluate the consumer programmable effect.
    pub async fn capture_source_scene(
        &mut self,
        source: impl Into<Arc<Scene>>,
        frame: SourceRenderFrame,
    ) -> Result<CapturedSourceScene, Error> {
        let source = source.into();
        let prepared = self.prepare_source_scene(&source, frame).await?;
        let capture = self.capture(&prepared, None).await?;
        Ok(CapturedSourceScene {
            source,
            frame,
            capture,
        })
    }
}

/// Stable source identity independent of a recording revision or target size.
/// Clone the token for later immutable generations of the same actual source.
#[derive(Clone, Debug)]
pub struct CaptureSourceIdentity(Arc<()>);
impl Default for CaptureSourceIdentity {
    fn default() -> Self {
        Self::new()
    }
}
impl CaptureSourceIdentity {
    /// Allocate a new source identity; this supplies no bounds or device evidence.
    pub fn new() -> Self {
        Self(Arc::new(()))
    }
    /// Whether two generations identify the same retained source owner.
    pub fn same_source(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

/// An independently owned raw-cache shader sampler and exact source generation.
/// Cloning retains the original recording, texture, device and request together.
/// Sampling does not apply consumer brush opacity, transform or effect bounds.
#[derive(Clone)]
pub struct CacheSamplerRaster(Arc<CacheSamplerGeneration>);
struct CacheSamplerGeneration {
    capture: CapturedSourceScene,
    source: CaptureSourceIdentity,
    revision: std::num::NonZeroU64,
    enable_cleartype: bool,
    frame: crate::composition::CacheSamplerRasterFrame,
}
impl CacheSamplerRaster {
    /// The original selected source identity.
    pub fn source_identity(&self) -> &CaptureSourceIdentity {
        &self.0.source
    }
    /// Original nonzero immutable recording revision.
    pub fn source_revision(&self) -> std::num::NonZeroU64 {
        self.0.revision
    }
    /// Complete original bounds, independent primary scale and actual device caps.
    pub fn frame(&self) -> crate::composition::CacheSamplerRasterFrame {
        self.0.frame
    }
    /// Original source text presentation policy.
    pub fn enable_cleartype(&self) -> bool {
        self.0.enable_cleartype
    }
    /// Actual completed source capture, including retained original recording.
    pub fn capture(&self) -> &CapturedSourceScene {
        &self.0.capture
    }
    /// Actual independently allocated raw pixels. Consumers must not mutate or
    /// explicitly destroy this completed texture while any sampler retains it.
    pub fn image(&self) -> &crate::image::GpuImage {
        self.0.capture.image()
    }
    /// Exact actual device owner; no current/global context is consulted.
    pub fn device(&self) -> &wgpu::Device {
        self.image().device()
    }
    /// Exact immutable reuse identity. A matching result is a retained capture,
    /// not proof that a mutable provider has produced the requested revision.
    pub fn matches(
        &self,
        source: &CaptureSourceIdentity,
        revision: std::num::NonZeroU64,
        frame: crate::composition::CacheSamplerRasterFrame,
        device: &wgpu::Device,
        enable_cleartype: bool,
    ) -> bool {
        self.0.source.same_source(source)
            && self.0.revision == revision
            && self.0.frame.same_generation(frame)
            && self.device() == device
            && self.0.enable_cleartype == enable_cleartype
    }
}
impl Renderer {
    /// Original explicit raw-cache realization. The caller supplies the actual
    /// immutable cache-content recording after applying its source root policy.
    /// Root opacity/transform/scrollable clip are not inferred from a flat Scene;
    /// descendant state already present in that recording is preserved.
    ///
    /// Every call creates a fresh generation, including a transparent one-texel
    /// result for proven empty input. Ownership, source policy and actual device
    /// caps are checked before that shortcut. Cancellation publishes no result.
    pub async fn capture_cache_sampler(
        &mut self,
        source: impl Into<Arc<Scene>>,
        frame: crate::composition::CacheSamplerRasterFrame,
        identity: CaptureSourceIdentity,
        revision: std::num::NonZeroU64,
        enable_cleartype: bool,
    ) -> Result<CacheSamplerRaster, Error> {
        let actual_device = self.device.clone();
        let target = SourceRenderFrame::cache(frame, enable_cleartype)?;
        let capture = self.capture_source_scene(source, target).await?;
        if capture.image().device() != &actual_device || self.device != actual_device {
            return Err(Error::ResourceDomain);
        }
        Ok(CacheSamplerRaster(Arc::new(CacheSamplerGeneration {
            capture,
            source: identity,
            revision,
            enable_cleartype,
            frame,
        })))
    }
}

/// Detached exact target-depth scope survives recursive exclusive renderer work
/// and restores its predecessor on failure, cancellation and ordinary return.
pub(super) struct TargetDepthGuard {
    state: std::rc::Rc<std::cell::Cell<u16>>,
    previous: u16,
}
impl Drop for TargetDepthGuard {
    fn drop(&mut self) {
        self.state.set(self.previous);
    }
}
impl Renderer {
    pub(super) fn enter_target_depth(&self, target: RenderFrame) -> TargetDepthGuard {
        let previous = self.preparation_mask_depth.replace(target.mask_depth);
        TargetDepthGuard {
            state: self.preparation_mask_depth.clone(),
            previous,
        }
    }
}
