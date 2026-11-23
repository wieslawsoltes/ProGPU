use super::*;
use std::{future::Future, pin::Pin, sync::Mutex};

/// Same-device child engines retain the actual parent's vector pipeline/layout
/// handles, while their scene pages, layer histories and nested caches are owned.
pub(super) struct NativeScenePictureFactory {
    pub queue: wgpu::Queue,
    pub residency: Arc<NativeResidencyBudget>,
    pub shared: Arc<Mutex<Option<NativePipelineCache>>>,
}
impl NativePictureExecutorFactory for NativeScenePictureFactory {
    fn preflight(
        &self,
        request: &Arc<NativePictureExecutionRequest>,
        external: &NativeExternalImages,
        pictures: &NativePictureRuntime,
    ) -> Result<NativePictureWorkMetrics, Error> {
        let options = request.plan().options();
        let core = NativeSceneGpuContext::new(
            external.device(),
            &self.queue,
            options.format,
            NativeSceneRendererOptions {
                engine_flags: options.engine_flags,
                antialias_padding_pixels: options.antialias_padding_pixels,
            },
            self.residency.clone(),
            self.shared.clone(),
            true,
        )?;
        core.admit_plan(request.plan(), external)?;
        let candidate =
            NativeSceneCandidate::new(request.plan().clone(), &core.layers, 1024, 1024, None)?;
        candidate.preflight(&core.pages, &core.masks, &core.shaders, pictures, external)
    }
    fn create(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        format: NativeTargetFormat,
        options: NativePictureRuntimeOptions,
    ) -> Result<Box<dyn NativePictureExecutor>, Error> {
        if queue != &self.queue {
            return Err(Error::ResourceDomain);
        }
        // The parent publishes this only after creating its actual pipeline.
        // No unrelated child layout can claim the original shared-owner policy.
        {
            let shared = self.shared.lock().map_err(|_| Error::ResourceDomain)?;
            let pipelines = shared.as_ref().ok_or(Error::ResourceDomain)?;
            if pipelines.device() != device || pipelines.target_format() != format.texture_format()
            {
                return Err(Error::ResourceDomain);
            }
        }
        Ok(Box::new(NativeSceneGpuContext::new(
            device,
            queue,
            format,
            NativeSceneRendererOptions {
                engine_flags: options.engine_flags,
                antialias_padding_pixels: options.antialias_padding_pixels,
            },
            self.residency.clone(),
            self.shared.clone(),
            true,
        )?))
    }
}
impl NativePictureExecutor for NativeSceneGpuContext {
    fn mark_device_lost(&mut self) -> Result<(), Error> {
        NativeSceneGpuContext::mark_device_lost(self)
            .map_err(recovery::NativeSceneRecoveryError::into_render_error)
    }
    fn execute<'a>(
        &'a mut self,
        request: Arc<NativePictureExecutionRequest>,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
        pictures: &'a mut NativePictureRuntime,
    ) -> Pin<Box<dyn Future<Output = Result<NativePictureWorkMetrics, Error>> + 'a>> {
        Box::pin(async move {
            let metrics = self
                .render_plan(request.plan().clone(), attachment, external, pictures)
                .await?;
            Ok(NativePictureWorkMetrics {
                frame: metrics.frame,
                texture_bytes: metrics.texture_bytes,
                texture_allocations: metrics.texture_allocations,
                render_passes: metrics.render_passes,
                compute_passes: metrics.compute_passes,
                path_rasters: metrics.path_rasters,
                nested_captures: metrics.nested_captures,
                cache_hits: metrics.cache_hits,
                live_blur_passes: metrics.live_blur_passes,
                seed_copy_bytes: 0,
            })
        })
    }
}
