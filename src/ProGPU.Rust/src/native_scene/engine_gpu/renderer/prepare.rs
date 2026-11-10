use super::*;
use compile::slots;

impl NativeSceneGpuContext {
    async fn ensure_pipelines(&mut self) -> Result<(), Error> {
        if self.pipelines.is_some() {
            return Ok(());
        }
        let pipelines = if self.borrows_shared_vector_pipeline {
            self.shared
                .lock()
                .map_err(|_| Error::ResourceDomain)?
                .clone()
                .ok_or(Error::ResourceDomain)?
        } else {
            let mut pipelines =
                NativePipelineCache::new(&self.device, self.format.texture_format()).await?;
            // The original child borrows the parent's actual vector pipeline.
            // Clone the owned WebGPU handles, never an unrelated layout witness.
            pipelines
                .prepare(NativeRenderPipeline::Vector(NativeMaskMode::None))
                .await?;
            *self.shared.lock().map_err(|_| Error::ResourceDomain)? = Some(pipelines.clone());
            pipelines
        };
        if pipelines.device() != &self.device
            || pipelines.target_format() != self.format.texture_format()
        {
            return Err(Error::ResourceDomain);
        }
        self.pipelines = Some(pipelines);
        Ok(())
    }
    pub(super) async fn render_plan(
        &mut self,
        plan: Arc<NativeSceneResourcePlan>,
        attachment: NativeRenderAttachment,
        external: NativeExternalImages,
        pictures: &mut NativePictureRuntime,
    ) -> Result<NativeSceneRenderMetrics, Error> {
        self.recovery.require_gpu()?;
        self.admit_plan(&plan, &external)?;
        self.admit_attachment(plan.frame(), &attachment, &external)?;
        // Only the actual same-parent scratch child may replace independent
        // captures reusing public scene IDs/generations. Public roots retain
        // the original monotone source-update contract.
        self.recovery
            .retain_plan(&plan, self.borrows_shared_vector_pipeline)?;
        if !pictures.external_images().same_bindings(&external) {
            return Err(Error::ResourceDomain);
        }
        pictures.begin_work();
        self.bind_cache(plan.options().resource_scope, &external)?;
        self.cache
            .as_mut()
            .ok_or(Error::ResourceDomain)?
            .store_resource_plan(plan.clone(), &external)?;
        let retained = self
            .cache
            .as_ref()
            .and_then(|c| c.prepared_pages(&plan, &external));
        let warm_pages = retained.is_some();
        if let (Some(reuse), Some(pages), Some(pipelines)) =
            (&self.reuse, retained.as_ref(), self.pipelines.as_ref())
        {
            if let Some(resources) = reuse.lookup(&plan, pages, &external, pipelines, &self.layers)
            {
                return self
                    .render_reused(resources, attachment, external, pictures)
                    .await;
            }
        }
        let candidate = NativeSceneCandidate::new(
            plan.clone(),
            &self.layers,
            self.path_atlas_size,
            self.glyph_atlas_size,
            retained.as_ref().map(|p| p.source().clone()),
        )?;
        candidate.preflight(&self.pages, &self.masks, &self.shaders, pictures, &external)?;
        if !candidate.replay.vertices().is_empty() {
            self.composite.preflight(candidate.replay.vertices())?;
        }
        self.ensure_pipelines().await?;
        let pipelines = self.pipelines.as_mut().ok_or(Error::ResourceDomain)?;
        let pages = if let Some(pages) = retained {
            pages
        } else {
            self.pages
                .prepare(pipelines, candidate.raster.clone(), &external, pictures)
                .await?
        };
        let mut pool = self.layers.fork_candidate();
        let targets = Arc::new(
            pool.prepare_plan(
                pipelines,
                pages.material().clone(),
                candidate.replay.targets().clone(),
            )
            .await?,
        );
        let frames = NativeSceneFrames::prepare(
            pipelines,
            candidate.replay.clone(),
            &pages,
            targets.clone(),
            self.previous_frames.as_deref(),
        )
        .await?;
        let composite = if candidate.replay.vertices().is_empty() {
            None
        } else {
            Some(Arc::new(
                self.composite
                    .prepare(Arc::from(candidate.replay.vertices()))
                    .await?,
            ))
        };
        let mut masks = Vec::new();
        masks
            .try_reserve_exact(candidate.masks.len())
            .map_err(|_| Error::Capacity)?;
        for request in &candidate.masks {
            masks.push(Arc::new(
                self.masks.prepare(request.clone(), pictures).await?,
            ));
        }
        let mask_at = |index: usize| -> Option<Arc<PreparedNativeMask>> {
            candidate.mask_indices[index].map(|i| masks[i].clone())
        };
        let operations = candidate.replay.operations();
        let count = operations.len();
        let mut effect_indices = slots(count)?;
        let mut requests = Vec::new();
        requests.try_reserve(count).map_err(|_| Error::Capacity)?;
        for (index, operation) in operations.iter().enumerate() {
            let selected = match operation {
                NativeReplayOperation::PushLayer(p) if p.backdrop => Some((p.target, &p.effect)),
                NativeReplayOperation::PopLayer(p) if !p.backdrop => Some((p.source, &p.effect)),
                _ => None,
            };
            if let Some((target, Some(NativeReplayEffect::Builtin(effect)))) = selected {
                effect_indices[index] = Some(requests.len());
                requests.push(NativeEffectRequest {
                    plan: effect.clone(),
                    target: targets.target(target).ok_or(Error::ResourceDomain)?.clone(),
                });
            }
        }
        let effects = if plan.layers().metrics().has_builtin_effects {
            Some(Arc::new(
                self.effects
                    .prepare_for_plan(plan.layers(), &requests)
                    .await?,
            ))
        } else {
            None
        };
        let mut shaders = slots(count)?;
        for (index, shader) in candidate.shaders.iter().enumerate() {
            let Some(shader) = shader else {
                continue;
            };
            let source = if shader.final_sample() {
                None
            } else {
                Some(
                    targets
                        .target(shader.replay().source)
                        .ok_or(Error::ResourceDomain)?
                        .as_ref()
                        .clone(),
                )
            };
            let mask = if shader.final_sample() && shader.replay().source_vector_mask {
                mask_at(index)
            } else {
                None
            };
            shaders[index] = Some(Arc::new(
                self.shaders
                    .prepare(shader.clone(), source, mask, pictures)
                    .await?,
            ));
        }
        let mut advanced_indices = slots(count)?;
        let mut requests = Vec::new();
        requests
            .try_reserve(candidate.replay.metrics().advanced_count as usize)
            .map_err(|_| Error::Capacity)?;
        for (index, operation) in operations.iter().enumerate() {
            let NativeReplayOperation::PopLayer(pop) = operation else {
                continue;
            };
            let Some(advanced) = pop.composite.advanced else {
                continue;
            };
            if !pop.composite.drawable {
                continue;
            }
            advanced_indices[index] = Some(requests.len());
            requests.push(NativeAdvancedBlendRequest {
                source: targets
                    .target(pop.source)
                    .ok_or(Error::ResourceDomain)?
                    .clone(),
                parent: targets
                    .target(pop.target)
                    .ok_or(Error::ResourceDomain)?
                    .clone(),
                sampling: advanced.sampling,
                final_effect_texture: if pop.backdrop {
                    None
                } else {
                    pop.effect.as_ref().map(NativeReplayEffect::final_texture)
                },
                mask: mask_at(index).map(|m| m.binding().clone()),
                first_resolve_vertex: advanced.first_resolve_vertex,
                first_copy_vertex: advanced.first_copy_vertex,
                target_ignores_alpha: pop.target_ignores_alpha,
            });
        }
        let advanced = if requests.is_empty() {
            None
        } else {
            Some(Arc::new(
                self.advanced
                    .prepare(
                        pipelines,
                        pages.material().clone(),
                        NativeAdvancedScratchRequest::from_layer_plan(plan.layers()),
                        &requests,
                        composite.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                    )
                    .await?,
            ))
        };
        let mut bundles = slots(count)?;
        let mut layer_draws = slots(count)?;
        let mut backdrops = slots(count)?;
        let mut rgb = slots(count)?;
        let skipped = super::super::replay::native_replay_skipped_operations(
            &candidate.replay,
            &targets,
            pool.output_updates(&targets)?,
        )?;
        for (index, operation) in operations.iter().enumerate() {
            match operation {
                NativeReplayOperation::Bundle(_) => {
                    bundles[index] = Some(
                        PreparedNativeReplayBundle::prepare(
                            pipelines,
                            candidate.replay.clone(),
                            index,
                            pages.clone(),
                            &frames,
                            mask_at(index),
                        )
                        .await?,
                    );
                }
                NativeReplayOperation::PushLayer(push) if push.backdrop => {
                    let effect = effect_indices[index]
                        .map(|i| {
                            effects
                                .as_ref()
                                .map(|e| (e.clone(), i))
                                .ok_or(Error::ResourceDomain)
                        })
                        .transpose()?;
                    backdrops[index] = Some(
                        PreparedNativeBackdrop::prepare(
                            pipelines,
                            candidate.replay.clone(),
                            index,
                            &frames,
                            composite.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                            effect,
                        )
                        .await?,
                    );
                }
                NativeReplayOperation::PopLayer(pop)
                    if pop.composite.advanced.is_none()
                        && !pop
                            .effect
                            .as_ref()
                            .is_some_and(NativeReplayEffect::final_sample) =>
                {
                    layer_draws[index] = PreparedNativeLayerDraw::prepare(
                        pipelines,
                        candidate.replay.clone(),
                        index,
                        &frames,
                        composite.as_ref().ok_or(Error::ResourceDomain)?.clone(),
                        mask_at(index),
                    )
                    .await?;
                }
                NativeReplayOperation::Clear { draw: Some(_), .. } => {
                    if self.clear.is_none() {
                        self.clear = Some(
                            NativeTargetClearEngine::new(&self.device, &self.queue, self.format)
                                .await?,
                        );
                    }
                }
                NativeReplayOperation::Rgb {
                    draw: Some(draw), ..
                } if !skipped[index] => {
                    let page = candidate.rgb[index]
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
                    let mask = mask_at(index)
                        .map(|m| {
                            NativeRgbMaskSource::from_source(page.clone(), m.binding().clone())
                                .map(Arc::new)
                        })
                        .transpose()?;
                    if self.rgb.is_none() {
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
                _ => {}
            }
        }
        let root_copy = candidate.replay.root_copy_vertex().map(|first| {
            (
                first,
                composite.as_ref().cloned().ok_or(Error::ResourceDomain),
            )
        });
        let root_copy = if let Some((first, composite)) = root_copy {
            Some(Arc::new(
                PreparedNativeRootCopy::prepare(
                    pipelines,
                    &targets,
                    attachment.clone(),
                    composite?,
                    first,
                )
                .await?,
            ))
        } else {
            None
        };
        let resources = Arc::new(NativePreparedReplayResources {
            source: candidate.replay.clone(),
            pages: pages.clone(),
            frames: frames.clone(),
            composite,
            effects,
            advanced,
            masks,
            prepare_masks: true,
            bundles,
            layer_draws,
            backdrops,
            shaders,
            effect_indices,
            advanced_indices,
            root_copy,
            rgb_pages: candidate.rgb,
        });
        let mut result = metrics::prepared(&resources, &rgb, warm_pages)?;
        let encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("Native ordered scene coverage and replay"),
            });
        let mut coverage = if warm_pages {
            NativePathCoverageCommands {
                preceding: Vec::new(),
                encoder,
            }
        } else {
            self.pages.encode_initial_coverage(&pages, encoder)?
        };
        self.pages
            .encode_live_image_sources(&pages, &mut coverage.encoder)?;
        let updates = pool.output_updates(&targets)?;
        let encoded = resources.clone().encode(
            coverage.encoder,
            coverage.preceding,
            attachment,
            updates,
            plan.budget().preserve_target,
            self.clear.as_ref(),
            rgb,
        )?;
        let execution = encoded.submit(&self.queue, &mut pool)?;
        // Only successful submission makes this candidate the engine's current
        // target/cache generation. Every previous owner survives its consumers.
        self.layers = pool;
        self.pages.commit_submitted(&pages, &self.queue)?;
        self.cache
            .as_mut()
            .ok_or(Error::ResourceDomain)?
            .commit_prepared_submitted(pages, &self.queue)?;
        self.previous_frames = Some(frames);
        self.reuse = Some(NativeReplayReuse::submitted(resources, external, pipelines));
        if let Some(page) = candidate.raster.paths() {
            self.path_atlas_size = page.atlas_size();
        }
        if let Some(page) = candidate.raster.glyphs() {
            self.glyph_atlas_size = page.atlas_size();
        }
        if !self.borrows_shared_vector_pipeline {
            *self.shared.lock().map_err(|_| Error::ResourceDomain)? = Some(pipelines.clone());
        }
        metrics::executed(&mut result, execution)?;
        metrics::nested(
            &mut result,
            pictures.work_metrics(),
            pictures.mask_work_metrics(),
        )?;
        Ok(result)
    }
}
