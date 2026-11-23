//! Submitted native render-bundle reuse, restricted to one exact immutable plan.
//! Original resource/hash rebinding across different scene owners is independent.
use super::*;

pub(super) struct NativeReplayReuse {
    resources: Arc<NativePreparedReplayResources>,
    external: NativeExternalImages,
    // Every layout is created together by NativeRenderLayouts::new; the actual
    // frame handle identifies that construction, not merely its descriptor.
    frame_layout: wgpu::BindGroupLayout,
}
impl NativeReplayReuse {
    pub fn submitted(
        resources: Arc<NativePreparedReplayResources>,
        external: NativeExternalImages,
        pipelines: &NativePipelineCache,
    ) -> Self {
        Self {
            resources,
            external,
            frame_layout: pipelines.layouts().frame.clone(),
        }
    }
    pub fn lookup(
        &self,
        plan: &Arc<NativeSceneResourcePlan>,
        pages: &Arc<PreparedNativeScenePages>,
        external: &NativeExternalImages,
        pipelines: &NativePipelineCache,
        pool: &NativeLayerTargetPool,
    ) -> Option<Arc<NativePreparedReplayResources>> {
        // Equal source owners carry identical frame/options/operation inventory.
        // The page cache independently certifies submitted coverage and exact
        // external generation, extent and actual texture views.
        if !Arc::ptr_eq(self.resources.source.source(), plan)
            || !Arc::ptr_eq(&self.resources.pages, pages)
            || !self.external.same_bindings(external)
            || self.frame_layout != pipelines.layouts().frame
            || pipelines.device() != self.resources.frames.targets().device()
            || pipelines.target_format() != self.resources.frames.targets().format()
        {
            return None;
        }
        // A retained target is not a current slot lease. Check the actual pool
        // owner and epoch without guessing from dimensions/generation IDs.
        pool.output_updates(self.resources.frames.targets()).ok()?;
        Some(self.resources.clone())
    }
}
fn retained<T: Clone>(source: &[T]) -> Result<Vec<T>, Error> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(source.len())
        .map_err(|_| Error::Capacity)?;
    result.extend_from_slice(source);
    Ok(result)
}
impl NativeSceneGpuContext {
    /// Reuse parameters/bundles and completed mask captures, never the prior
    /// destination, RGB coverage packet or output-cache decision. All callbacks
    /// already completed for this retained source generation.
    pub(super) async fn render_reused(
        &mut self,
        prior: Arc<NativePreparedReplayResources>,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
        pictures: &mut NativePictureRuntime,
    ) -> Result<NativeSceneRenderMetrics, Error> {
        let mut pool = self.layers.fork_candidate();
        let targets = prior.frames.targets();
        let updates = pool.output_updates(targets)?;
        let skipped = super::super::replay::native_replay_skipped_operations(
            &prior.source,
            targets,
            updates.clone(),
        )?;
        let mut rgb = compile::slots(prior.source.operations().len())?;
        for (index, operation) in prior.source.operations().iter().enumerate() {
            let NativeReplayOperation::Rgb {
                draw: Some(draw), ..
            } = operation
            else {
                continue;
            };
            if skipped[index] {
                continue;
            }
            let page = prior.rgb_pages[index]
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .clone();
            let source = targets
                .target(draw.target)
                .and_then(|t| t.color())
                .ok_or(Error::ResourceDomain)?;
            let target = Arc::new(NativeRgbTarget::from_materialized_layer(
                &self.device,
                source.texture(),
                source.view(),
                page.clone(),
                Arc::new(source.clone()),
            )?);
            let mask = if let Some(request) = draw.mask.as_ref() {
                let prepared = prior
                    .masks
                    .iter()
                    .find(|mask| Arc::ptr_eq(mask.request(), request))
                    .ok_or(Error::ResourceDomain)?;
                Some(Arc::new(NativeRgbMaskSource::from_source(
                    page.clone(),
                    prepared.binding().clone(),
                )?))
            } else {
                None
            };
            if self.rgb.is_none() {
                let pipelines = self.pipelines.as_ref().ok_or(Error::ResourceDomain)?;
                self.rgb = Some(NativeRgbGlyphEngine::new(
                    &self.device,
                    self.format.texture_format(),
                    pipelines.layouts().clone(),
                )?);
            }
            rgb[index] = self
                .rgb
                .as_mut()
                .ok_or(Error::ResourceDomain)?
                .prepare(page, target, mask)
                .await?;
        }
        let pipelines = self.pipelines.as_mut().ok_or(Error::ResourceDomain)?;
        // A new surface view cannot inherit the previous attachment's lease.
        // This wrapper reuses the original pipeline and source quad without
        // allocating a fake copy target or uploading geometry.
        let root_copy = if let Some(first) = prior.source.root_copy_vertex() {
            Some(Arc::new(
                PreparedNativeRootCopy::prepare(
                    pipelines,
                    targets,
                    attachment.clone(),
                    prior
                        .composite
                        .as_ref()
                        .ok_or(Error::ResourceDomain)?
                        .clone(),
                    first,
                )
                .await?,
            ))
        } else {
            None
        };
        let resources = Arc::new(NativePreparedReplayResources {
            source: prior.source.clone(),
            pages: prior.pages.clone(),
            frames: prior.frames.clone(),
            composite: prior.composite.clone(),
            effects: prior.effects.clone(),
            advanced: prior.advanced.clone(),
            masks: retained(&prior.masks)?,
            prepare_masks: false,
            bundles: retained(&prior.bundles)?,
            layer_draws: retained(&prior.layer_draws)?,
            backdrops: retained(&prior.backdrops)?,
            shaders: retained(&prior.shaders)?,
            rgb_pages: retained(&prior.rgb_pages)?,
            effect_indices: retained(&prior.effect_indices)?,
            advanced_indices: retained(&prior.advanced_indices)?,
            root_copy,
        });
        let mut result = metrics::reused(&resources, &rgb)?;
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("Native retained ordered scene replay"),
            });
        // View identity retains resources, not immutable external texture pixels.
        self.pages
            .encode_live_image_sources(&resources.pages, &mut encoder)?;
        let encoded = resources.clone().encode(
            encoder,
            Vec::new(),
            attachment,
            updates,
            resources.source.source().budget().preserve_target,
            self.clear.as_ref(),
            rgb,
        )?;
        let execution = encoded.submit(&self.queue, &mut pool)?;
        // No prepared candidate becomes reusable before actual submission.
        self.layers = pool;
        self.reuse = Some(NativeReplayReuse::submitted(resources, external, pipelines));
        metrics::executed(&mut result, execution)?;
        metrics::nested(
            &mut result,
            pictures.work_metrics(),
            pictures.mask_work_metrics(),
        )?;
        Ok(result)
    }
}
