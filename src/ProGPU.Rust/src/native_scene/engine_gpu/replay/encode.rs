use super::super::{
    cache::{NativeLayerCacheSelection, NativeReplayCacheIdentity},
    pass::NativeReplayPassEncoder,
};
use super::*;
use crate::native_scene::execution::budget::LAYER_SLOT_COUNT;

impl NativePreparedReplayResources {
    /// Consume an encoder after original coverage/live-image preparation. The
    /// preceding buffers are retained unsubmitted until the complete replay is
    /// encoded successfully. RGB work belongs to this actual frame only.
    #[allow(clippy::too_many_arguments)]
    pub fn encode(
        self: Arc<Self>,
        mut encoder: wgpu::CommandEncoder,
        preceding: Vec<wgpu::CommandBuffer>,
        root: NativeRenderAttachment,
        mut updates: NativeLayerOutputUpdates,
        preserve_root: bool,
        clear: Option<&NativeTargetClearEngine>,
        mut rgb: Vec<Option<PreparedNativeRgbGlyphPage>>,
    ) -> Result<NativeEncodedReplay, Error> {
        let identity = NativeReplayCacheIdentity::from_plan(self.source.source());
        let skipped = super::skip::skipped_with_identity(
            &self.source,
            self.frames.targets(),
            updates.clone(),
            &identity,
        )?;
        self.admit(&root, &rgb, &skipped)?;
        if self
            .source
            .operations()
            .iter()
            .any(|op| matches!(op, NativeReplayOperation::Clear { draw: Some(_), .. }))
            && clear.is_none()
        {
            return Err(Error::ResourceDomain);
        }
        let mut packets = Vec::new();
        packets
            .try_reserve_exact(rgb.iter().filter(|p| p.is_some()).count())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = NativeReplayExecutionMetrics::default();
        // Masks contain only immutable retained work. Vector/picture captures
        // already completed during preparation; brush/composite work is encoded
        // before its first consumer, never through a callback in a render pass.
        if self.prepare_masks {
            for mask in &self.masks {
                mask.encode(&mut encoder);
            }
        }
        let source = self.source.source();
        let draw_count = source.draws().metrics().draw_count;
        let layered = source.layers().metrics().has_materialized_layers;
        let clears = source.draws().metrics().clear_count != 0;
        let empty_clear = draw_count == 0 && !layered;
        let mut pass = NativeReplayPassEncoder::new(
            encoder,
            root.clone(),
            self.frames.targets().clone(),
            preserve_root && !empty_clear,
            self.frames.root_glyph_coverage_certified(),
            source.budget().destination_sampling,
        )?;
        let initial_depth = if !layered && !clears {
            self.source.operations().first().is_some_and(
                |op| matches!(op,NativeReplayOperation::Bundle(span) if span.uses_depth),
            )
        } else {
            false
        };
        // Original zero-draw/no-layer rendering still clears the actual target,
        // including when ordinary preserve-target metadata was supplied.
        pass.begin_root(initial_depth)?;
        let mut skipped_depth = 0u32;
        let mut cached = [false; LAYER_SLOT_COUNT as usize];
        for (index, operation) in self.source.operations().iter().enumerate() {
            if skipped_depth != 0 {
                match operation {
                    NativeReplayOperation::PushLayer(_) => {
                        skipped_depth = skipped_depth.checked_add(1).ok_or(Error::Capacity)?;
                        continue;
                    }
                    NativeReplayOperation::PopLayer(_) => {
                        skipped_depth -= 1;
                        if skipped_depth != 0 {
                            continue;
                        }
                    }
                    _ => continue,
                }
            }
            match operation {
                NativeReplayOperation::Bundle(span) => {
                    pass.ensure(span.target, span.uses_depth)?;
                    let bundle = self.bundles[index].as_ref().ok_or(Error::ResourceDomain)?;
                    bundle.encode(pass.pass()?);
                    let draws = bundle.command_ranges() as u64;
                    metrics.draw_calls += draws;
                    metrics.encoded_draws += bundle.encoded_draw_count();
                }
                NativeReplayOperation::PushLayer(push) => {
                    if push.final_sample_shader {
                        continue;
                    }
                    let slot = slot(push.target)?;
                    if push.can_skip_content_on_effect_cache {
                        let target = self
                            .frames
                            .targets()
                            .target(push.target)
                            .ok_or(Error::ResourceDomain)?;
                        let selected = NativeLayerCacheSelection::push(&identity, push, target)?;
                        if updates.hit(selected.slot, selected.key) {
                            cached[slot as usize] = true;
                            metrics.effect_cache_hits += 1;
                            skipped_depth = 1;
                            pass.history_mut()
                                .retain_cached_layer(slot, push.ignore_alpha)?;
                            continue;
                        }
                    }
                    pass.finish_pass();
                    if push.backdrop {
                        let backdrop = self.backdrops[index]
                            .as_ref()
                            .ok_or(Error::ResourceDomain)?;
                        if push.effect_count != 0 {
                            metrics.effect_operations += 1;
                        }
                        let m = backdrop.encode(pass.encoder()?)?;
                        metrics.texture_copy_bytes += m.texture_copy_bytes;
                        metrics.render_passes += u64::from(m.render_passes);
                        metrics.encoded_draws += u64::from(m.draws);
                        metrics.draw_calls += u64::from(m.source_draw_counter);
                        metrics.effect_passes += u64::from(m.effect_passes);
                        metrics.compute_passes += u64::from(m.effect_passes);
                    }
                    pass.begin_layer(slot, push.ignore_alpha, push.backdrop)?;
                    metrics.layer_content_passes += 1;
                }
                NativeReplayOperation::PopLayer(pop) => {
                    if pop
                        .effect
                        .as_ref()
                        .is_some_and(NativeReplayEffect::final_sample)
                    {
                        if !pop.composite.drawable {
                            continue;
                        }
                        let shader = self.shaders[index].as_ref().ok_or(Error::ResourceDomain)?;
                        if shader.suppressed() {
                            return Err(Error::ResourceDomain);
                        }
                        metrics.effect_operations += 1;
                        pass.resume(pop.target, false)?;
                        shader.draw_final(pass.pass()?, None)?;
                        metrics.effect_passes += 1;
                        metrics.draw_calls += 1;
                        metrics.encoded_draws += 1;
                        let size = pass.attachment(pop.target)?.size();
                        let current = pass.pass()?;
                        current.set_viewport(0.0, 0.0, size[0] as f32, size[1] as f32, 0.0, 1.0);
                        current.set_scissor_rect(0, 0, size[0], size[1]);
                        continue;
                    }
                    let slot = slot(pop.source)?;
                    let content_cached = std::mem::replace(&mut cached[slot as usize], false);
                    let advanced = pop.composite.advanced.is_some();
                    if !content_cached || advanced || pass.active().is_some_and(|(_, depth)| depth)
                    {
                        pass.finish_pass();
                    }
                    if let Some(effect) = pop.effect.as_ref().filter(|_| !pop.backdrop) {
                        metrics.effect_operations += 1;
                        let target = self
                            .frames
                            .targets()
                            .target(pop.source)
                            .ok_or(Error::ResourceDomain)?;
                        let selected = NativeLayerCacheSelection::pop(&identity, pop, target)?;
                        if !content_cached {
                            if updates.hit(selected.slot, selected.key) {
                                metrics.effect_cache_hits += 1;
                            } else {
                                self.encode_effect(index, effect, pass.encoder()?, &mut metrics)?;
                                updates.record_encoded_output(
                                    self.frames.targets(),
                                    selected.slot,
                                    selected.key,
                                    selected.effected,
                                )?;
                            }
                        }
                    } else if pop.cache_content && !content_cached {
                        let target = self
                            .frames
                            .targets()
                            .target(pop.source)
                            .ok_or(Error::ResourceDomain)?;
                        let selected = NativeLayerCacheSelection::pop(&identity, pop, target)?;
                        updates.record_encoded_output(
                            self.frames.targets(),
                            selected.slot,
                            selected.key,
                            selected.effected,
                        )?;
                    }
                    if advanced {
                        if pop.composite.drawable {
                            let operation =
                                self.advanced_indices[index].ok_or(Error::ResourceDomain)?;
                            self.advanced
                                .as_ref()
                                .ok_or(Error::ResourceDomain)?
                                .encode(pass.encoder()?, operation)?;
                            metrics.advanced_passes += 3;
                            metrics.render_passes += 3;
                            metrics.draw_calls += 3;
                            metrics.encoded_draws += 3;
                        }
                        pass.resume(pop.target, false)?;
                    } else {
                        // A content-cache hit can continue the still-open parent
                        // pass; cold content always resumes that parent with Load.
                        pass.ensure(pop.target, false)?;
                        if pop.composite.drawable {
                            self.layer_draws[index]
                                .as_ref()
                                .ok_or(Error::ResourceDomain)?
                                .encode(pass.pass()?);
                            metrics.draw_calls += 1;
                            metrics.encoded_draws += 1;
                        }
                    }
                }
                NativeReplayOperation::Clear {
                    draw: Some(operation),
                    ..
                } => {
                    pass.finish_pass();
                    let attachment = pass.attachment(operation.target())?.clone();
                    let m = pass.with_encoder(|encoder, history| {
                        clear.ok_or(Error::ResourceDomain)?.encode(
                            encoder,
                            &attachment,
                            history,
                            operation,
                        )
                    })?;
                    metrics.draw_calls += u64::from(m.draws);
                    metrics.encoded_draws += u64::from(m.draws);
                    metrics.target_clears += u64::from(m.target_clears);
                    metrics.clear_uniform_upload_bytes += m.uniform_upload_bytes;
                    metrics.render_passes += 1;
                    pass.resume(operation.target(), false)?;
                }
                NativeReplayOperation::Rgb {
                    draw: Some(draw), ..
                } => {
                    pass.finish_pass();
                    slot(draw.target)?;
                    pass.history()
                        .admit_target_clear(draw.target, draw.allocation.size, true)?;
                    let packet = rgb[index]
                        .take()
                        .ok_or(Error::ResourceDomain)?
                        .encode(pass.encoder()?);
                    let m = packet.metrics();
                    metrics.draw_calls += u64::from(m.draw_calls);
                    metrics.encoded_draws += u64::from(m.draw_calls);
                    metrics.render_passes += u64::from(m.raster_passes);
                    metrics.compute_passes += u64::from(m.compute_passes);
                    metrics.texture_copy_bytes += m.destination_copy_bytes;
                    add_rgb(&mut metrics.rgb, m)?;
                    packets.push(packet);
                    pass.resume(draw.target, false)?;
                }
                NativeReplayOperation::Clear { draw: None, .. }
                | NativeReplayOperation::Rgb { draw: None, .. } => {}
            }
        }
        if skipped_depth != 0 {
            return Err(Error::InvalidInput);
        }
        pass.finish_pass();
        if let Some(copy) = &self.root_copy {
            copy.encode(pass.encoder()?)?;
            metrics.root_copies = 1;
            metrics.render_passes += 1;
            metrics.draw_calls += 1;
            metrics.encoded_draws += 1;
        }
        metrics.render_passes += pass.passes();
        let command = pass.finish()?;
        Ok(NativeEncodedReplay {
            resources: self,
            root,
            preceding,
            command,
            updates,
            rgb: packets,
            metrics,
        })
    }
    fn encode_effect(
        &self,
        index: usize,
        effect: &NativeReplayEffect,
        encoder: &mut wgpu::CommandEncoder,
        metrics: &mut NativeReplayExecutionMetrics,
    ) -> Result<(), Error> {
        match effect {
            NativeReplayEffect::Builtin(plan) => {
                let i = self.effect_indices[index].ok_or(Error::ResourceDomain)?;
                self.effects
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .encode(encoder, i)?;
                metrics.effect_passes += u64::from(plan.pass_count());
                metrics.compute_passes += u64::from(plan.pass_count());
            }
            NativeReplayEffect::Shader(_) => {
                let m = self.shaders[index]
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?
                    .encode_legacy(encoder)?;
                metrics.effect_passes += 1;
                metrics.render_passes += 1;
                metrics.encoded_draws += u64::from(m.draws);
            }
        }
        Ok(())
    }
}
fn slot(target: NativeLayerTargetId) -> Result<u32, Error> {
    match target {
        NativeLayerTargetId::Slot(slot) if slot < LAYER_SLOT_COUNT => Ok(slot),
        _ => Err(Error::ResourceDomain),
    }
}
fn add_rgb(total: &mut NativeRgbGlyphMetrics, m: NativeRgbGlyphMetrics) -> Result<(), Error> {
    macro_rules! add {
        ($field:ident) => {
            total.$field = total.$field.checked_add(m.$field).ok_or(Error::Capacity)?;
        };
    }
    add!(vertex_upload_bytes);
    add!(uniform_upload_bytes);
    add!(texture_bytes);
    add!(coverage_staging_bytes);
    add!(draw_calls);
    add!(compute_passes);
    add!(raster_passes);
    add!(destination_copy_bytes);
    Ok(())
}
