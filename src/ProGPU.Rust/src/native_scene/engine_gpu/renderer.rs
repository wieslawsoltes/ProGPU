//! Owned native scene execution on one actual WebGPU device and queue.
//! CPU admission, immutable GPU preparation and submitted output publication
//! remain separate stages. Nested pictures use this same renderer.
use super::cache::NativeSceneCache;
use super::replay::{NativePreparedReplayResources, NativeReplayExecutionMetrics};
use super::*;
use crate::{
    native_scene::{
        NativeScene,
        engine_resources::{
            advanced_blend::*, effects::*, layers::*, masks::*, pictures::*, shader_effects::*, *,
        },
        execution::{self, budget::NativeResidencyBudget, *},
    },
    path::ExecutionPreference,
};
mod compile;
mod metrics;
mod pictures;
mod prepare;
use compile::NativeSceneCandidate;

/// Explicit native policies. Ordinary rendering remains on the owned shaders;
/// the original CPU glyph flags select their separate explicit producers.
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneRendererOptions {
    pub engine_flags: u32,
    pub antialias_padding_pixels: f32,
}
impl Default for NativeSceneRendererOptions {
    fn default() -> Self {
        Self {
            engine_flags: 0,
            antialias_padding_pixels: 1.5,
        }
    }
}
impl NativeSceneRendererOptions {
    fn validate(self) -> Result<Self, Error> {
        if !self.antialias_padding_pixels.is_finite() || self.antialias_padding_pixels <= 0.0 {
            return Err(Error::InvalidInput);
        }
        Ok(self)
    }
}

/// Physical work counters belong to this invocation, including nested source
/// captures. Source budgeting remains available on the compiled resource plan.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeSceneRenderMetrics {
    pub frame: wire::FrameMetrics,
    pub pages: NativeScenePageMetrics,
    pub frames: NativeSceneFrameMetrics,
    pub layers: NativeLayerResourceMetrics,
    pub composite: NativeLayerCompositeMetrics,
    pub effects: NativeEffectMetrics,
    pub advanced: NativeAdvancedBlendMetrics,
    pub masks: NativeMaskMetrics,
    pub shader_uniform_upload_bytes: u64,
    pub execution: NativeReplayExecutionMetrics,
    pub texture_bytes: u64,
    pub texture_allocations: u64,
    pub render_passes: u32,
    pub compute_passes: u32,
    pub path_rasters: u32,
    pub nested_captures: u32,
    pub cache_hits: u32,
    pub live_blur_passes: u64,
    pub nested_work: NativePictureWorkMetrics,
}

/// Each renderer owns its mutable scene history. Prepared and submitted work
/// retains the original source generation independently of later replacements.
pub struct NativeSceneRenderer {
    core: NativeSceneGpuContext,
    pictures: NativePictureRuntime,
}
impl NativeSceneRenderer {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativeSceneRendererOptions,
    ) -> Result<Self, Error> {
        let options = options.validate()?;
        let residency = Arc::new(NativeResidencyBudget::new(
            execution::budget::MAX_TOTAL_COMPILED_BYTES,
        ));
        let external = NativeExternalImages::new(device, Vec::new())?;
        let shared = Arc::new(std::sync::Mutex::new(None));
        let factory = Arc::new(pictures::NativeScenePictureFactory {
            queue: queue.clone(),
            residency: residency.clone(),
            shared: shared.clone(),
        });
        let pictures = NativePictureRuntime::new(
            device,
            queue,
            format,
            NativePictureRuntimeOptions {
                engine_flags: options.engine_flags,
                antialias_padding_pixels: options.antialias_padding_pixels,
            },
            external,
            residency.clone(),
            factory,
        )?;
        Ok(Self {
            core: NativeSceneGpuContext::new(
                device, queue, format, options, residency, shared, false,
            )?,
            pictures,
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.core.device
    }
    pub fn target_format(&self) -> NativeTargetFormat {
        self.core.format
    }
    pub fn options(&self) -> NativeSceneRendererOptions {
        self.core.options
    }
    pub fn residency(&self) -> &Arc<NativeResidencyBudget> {
        &self.core.residency
    }
    pub fn cached_picture_count(&self) -> usize {
        self.pictures.cached_image_count() + self.pictures.cached_mask_count()
    }
    /// Source replacement never discards textures retained by submitted work.
    pub fn clear_picture_cache(&mut self) {
        self.pictures.clear_cache();
    }
    /// Render and submit a complete admitted native scene to the selected
    /// attachment. The caller presents its surface only after this succeeds.
    pub async fn render(
        &mut self,
        scene: Arc<NativeScene>,
        frame: NativeSceneFrame,
        resource_scope: u64,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
    ) -> Result<NativeSceneRenderMetrics, Error> {
        self.core.admit_attachment(frame, &attachment, &external)?;
        self.core.bind_cache(resource_scope, &external)?;
        let options = self.core.compile_options(resource_scope);
        let retained = self
            .core
            .cache
            .as_ref()
            .and_then(|c| c.resource_plan(&scene, frame, options, &external));
        let plan = if let Some(plan) = retained {
            plan
        } else {
            Arc::new(NativeSceneResourcePlan::compile(
                scene,
                frame,
                external.metadata(),
                options,
            )?)
        };
        self.pictures.bind_external_images(external.clone())?;
        self.core
            .render_plan(plan, attachment, external, &mut self.pictures)
            .await
    }
    /// An existing plan must retain this actual device's limits, source flags
    /// and exact external metadata; it cannot relabel another target's proof.
    pub async fn render_plan(
        &mut self,
        plan: Arc<NativeSceneResourcePlan>,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
    ) -> Result<NativeSceneRenderMetrics, Error> {
        self.core.admit_plan(&plan, &external)?;
        self.core
            .admit_attachment(plan.frame(), &attachment, &external)?;
        self.pictures.bind_external_images(external.clone())?;
        self.core
            .render_plan(plan, attachment, external, &mut self.pictures)
            .await
    }
}

struct NativeSceneGpuContext {
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: NativeTargetFormat,
    options: NativeSceneRendererOptions,
    residency: Arc<NativeResidencyBudget>,
    pipelines: Option<NativePipelineCache>,
    shared: Arc<std::sync::Mutex<Option<NativePipelineCache>>>,
    borrows_shared_vector_pipeline: bool,
    pages: NativeScenePageEngine,
    layers: NativeLayerTargetPool,
    composite: NativeLayerCompositeEngine,
    masks: NativeMaskEngine,
    effects: NativeEffectEngine,
    advanced: NativeAdvancedBlendEngine,
    shaders: NativeShaderEffectEngine,
    clear: Option<NativeTargetClearEngine>,
    rgb: Option<NativeRgbGlyphEngine>,
    path_atlas_size: u32,
    glyph_atlas_size: u32,
    previous_frames: Option<Arc<NativeSceneFrames>>,
    cache: Option<NativeSceneCache>,
}
impl NativeSceneGpuContext {
    fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativeSceneRendererOptions,
        residency: Arc<NativeResidencyBudget>,
        shared: Arc<std::sync::Mutex<Option<NativePipelineCache>>>,
        borrows_shared_vector_pipeline: bool,
    ) -> Result<Self, Error> {
        let options = options.validate()?;
        let target = format.texture_format();
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            format,
            options,
            residency: residency.clone(),
            pipelines: None,
            shared,
            borrows_shared_vector_pipeline,
            pages: NativeScenePageEngine::new(device, queue, format),
            layers: NativeLayerTargetPool::new(device, target, residency.clone())?,
            composite: NativeLayerCompositeEngine::new(device, queue),
            masks: NativeMaskEngine::new(device, queue, target, u64::from(options.engine_flags))?,
            effects: NativeEffectEngine::new(device, queue),
            advanced: NativeAdvancedBlendEngine::new(device, queue, target, residency)?,
            shaders: NativeShaderEffectEngine::new(
                device,
                queue,
                target,
                ExecutionPreference::GpuShader,
            )?,
            clear: None,
            rgb: None,
            path_atlas_size: 1024,
            glyph_atlas_size: 1024,
            previous_frames: None,
            cache: None,
        })
    }
    fn compile_options(&self, resource_scope: u64) -> NativeSceneCompileOptions {
        let mut options =
            compile::compile_options(&self.device, self.format, self.options, resource_scope);
        options.borrows_shared_vector_pipeline = self.borrows_shared_vector_pipeline;
        options
    }
    fn bind_cache(&mut self, scope: u64, external: &NativeExternalImages) -> Result<(), Error> {
        if self
            .cache
            .as_ref()
            .is_none_or(|c| c.resource_scope() != scope)
        {
            self.cache = Some(NativeSceneCache::new(
                &self.device,
                scope,
                external.clone(),
            )?);
        } else if let Some(cache) = &mut self.cache {
            let update = cache.prepare_external(external.clone())?;
            cache.commit_external(update)?;
        }
        Ok(())
    }
    fn admit_plan(
        &self,
        plan: &NativeSceneResourcePlan,
        external: &NativeExternalImages,
    ) -> Result<(), Error> {
        if plan.options().borrows_shared_vector_pipeline != self.borrows_shared_vector_pipeline {
            return Err(Error::ResourceDomain);
        }
        compile::admit_plan(&self.device, self.format, self.options, plan, external)
    }
    fn admit_attachment(
        &self,
        frame: NativeSceneFrame,
        attachment: &NativeRenderAttachment,
        external: &NativeExternalImages,
    ) -> Result<(), Error> {
        if attachment.device() != &self.device
            || external.device() != &self.device
            || attachment.format() != self.format
            || attachment.size() != [frame.width, frame.height]
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}
