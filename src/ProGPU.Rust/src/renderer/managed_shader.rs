//! Original managed WGSL preparation in the actual retained target.
use super::*;
use crate::{
    scene::ManagedShaderEffectDraw,
    shader_effect::{
        ManagedShaderEngine, ManagedShaderMask, ManagedShaderMetrics, ManagedShaderRequest,
        PreparedManagedShader, SampleLattice,
    },
};

pub(super) struct PreparedManagedShaders {
    draws: Vec<PreparedManagedShader>,
    metrics: ManagedShaderMetrics,
}
impl PreparedManagedShaders {
    pub fn draw_count(&self, index: usize) -> Result<u32, Error> {
        Ok(self
            .draws
            .get(index)
            .ok_or(Error::ResourceDomain)?
            .metrics()
            .draws)
    }
    pub fn draw(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        index: usize,
        scissor: Option<[u32; 4]>,
    ) -> Result<(), Error> {
        self.draws
            .get(index)
            .ok_or(Error::ResourceDomain)?
            .draw_owned(pass, scissor)?;
        Ok(())
    }
}
impl PreparedScene {
    /// Actual managed programmable-effect resources and per-replay draws on this
    /// target. Earlier source capture remains separately accounted.
    pub fn managed_shader_metrics(&self) -> ManagedShaderMetrics {
        self.managed_shaders
            .as_ref()
            .map_or_else(Default::default, |s| s.metrics)
    }
}
fn request<'a>(
    draw: &'a ManagedShaderEffectDraw,
    frame: RenderFrame,
    maximum: u32,
    mask: Option<&'a ManagedShaderMask>,
) -> Result<ManagedShaderRequest<'a>, Error> {
    draw.validate()?;
    let target = SampleLattice {
        origin: [
            i32::try_from(frame.origin[0]).map_err(|_| Error::Capacity)?,
            i32::try_from(frame.origin[1]).map_err(|_| Error::Capacity)?,
        ],
        size: [frame.width, frame.height],
    };
    if draw.target != target {
        return Err(Error::ResourceDomain);
    }
    Ok(ManagedShaderRequest {
        parameters: &draw.parameters,
        quad: draw.quad,
        target,
        projection: frame.uniforms(maximum)?.projection,
        viewport: [0.0, 0.0, frame.width as f32, frame.height as f32],
        blend: draw.blend,
        mask,
    })
}
impl Renderer {
    pub(super) fn preflight_managed_shaders(
        &self,
        scene: &Scene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        let (states, changes) = scene.compile_states(frame.dpi_scale)?;
        let mut cursor = 0;
        let mut current = 0;
        let mut count = 0;
        for (index, draw) in scene.managed_shader_effects() {
            for next in changes.get(cursor..=index).unwrap_or(&[]).iter().flatten() {
                current = *next;
            }
            cursor = index + 1;
            let req = request(
                draw,
                frame,
                self.device.limits().max_texture_dimension_2d,
                draw.mask.as_deref(),
            )?;
            ManagedShaderEngine::preflight_request(&self.device, &req)?;
            if states[current].count != 0 {
                if frame.origin != [0; 2] && !states[current].materialized {
                    // Original managed nested targets do not rebase completed
                    // root-coordinate inverse rows. Keep that boundary explicit.
                    return Err(Error::UnsupportedExecution);
                }
                ManagedShaderEngine::preflight_mask(&self.device, &draw.parameters)?;
            }
            count += 1;
            if count > 16384 {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    pub(super) fn validate_managed_shaders(
        &self,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        if compiled.managed_shader_effects.len() > 16384 {
            return Err(Error::Capacity);
        }
        let mut current = 0;
        for command in &compiled.draws {
            match command {
                DrawRange::State(index) => current = *index,
                DrawRange::ManagedShaderEffect(index) => {
                    let draw = compiled
                        .managed_shader_effects
                        .get(*index)
                        .ok_or(Error::ResourceDomain)?;
                    let req = request(
                        draw,
                        frame,
                        self.device.limits().max_texture_dimension_2d,
                        draw.mask.as_deref(),
                    )?;
                    ManagedShaderEngine::preflight_request(&self.device, &req)?;
                    let state = compiled.states.get(current).ok_or(Error::ResourceDomain)?;
                    if state.count != 0 {
                        if frame.origin != [0; 2] && !state.materialized {
                            return Err(Error::UnsupportedExecution);
                        }
                        ManagedShaderEngine::preflight_mask(&self.device, &draw.parameters)?;
                    }
                }
                _ => {}
            }
        }
        Ok(())
    }
    pub(super) async fn prepare_managed_shaders(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.managed_shader_effects.is_empty() {
            return Ok(());
        }
        self.validate_managed_shaders(compiled, frame)?;
        // Resolve the original per-draw coverage before borrowing the engine.
        // PreparedMasks already owns/uploads the actual resolved source generation.
        let mut masks = vec![None; compiled.managed_shader_effects.len()];
        let mut current = 0;
        for command in &compiled.draws {
            if let DrawRange::State(index) = command {
                current = *index;
                continue;
            }
            let DrawRange::ManagedShaderEffect(index) = command else {
                continue;
            };
            let draw = &compiled.managed_shader_effects[*index];
            masks[*index] = draw.mask.clone();
            let state = prepared
                .masks
                .as_ref()
                .and_then(|m| m.resolved.get(current))
                .unwrap_or(&compiled.states[current]);
            if state.count == 0 {
                continue;
            }
            if draw.mask.is_some() || state.count != 1 {
                return Err(Error::UnsupportedExecution);
            }
            let mut uniforms = state.masks[0];
            if state.local_coverage {
                // This actual full-target R8 producer has a proven one-to-one
                // local texture lattice. Preserve reciprocals and all other words.
                uniforms.coordinate0[0] = 0.0;
                uniforms.coordinate0[1] = 0.0;
            } else if frame.origin != [0; 2] {
                return Err(Error::UnsupportedExecution);
            }
            let mask = if let Some(coverage) = &state.coverage {
                let source = self.masks.as_mut().ok_or(Error::ResourceDomain)?.image(
                    &self.device,
                    &self.queue,
                    &coverage.source,
                    &mut UploadMetrics::default(),
                    &mut std::collections::HashSet::new(),
                )?;
                ManagedShaderMask::sampled(
                    draw.target,
                    uniforms,
                    source,
                    if coverage.sampling == crate::mask::MaskSampling::Linear {
                        crate::image::ImageSampling::Linear
                    } else {
                        crate::image::ImageSampling::Nearest
                    },
                )?
            } else {
                ManagedShaderMask::analytic(draw.target, uniforms)?
            };
            masks[*index] = Some(Arc::new(mask));
        }
        if self.managed_shader_engine.is_none() {
            self.managed_shader_engine = Some(ManagedShaderEngine::new(
                &self.device,
                &self.queue,
                self.format,
                1,
                ExecutionPreference::Automatic,
            )?);
        }
        let engine = self
            .managed_shader_engine
            .as_mut()
            .ok_or(Error::ResourceDomain)?;
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(masks.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = ManagedShaderMetrics::default();
        for (draw, mask) in compiled.managed_shader_effects.iter().zip(&masks) {
            let req = request(
                draw,
                frame,
                self.device.limits().max_texture_dimension_2d,
                mask.as_deref(),
            )?;
            let value = engine.prepare(req).await?;
            metrics.merge(value.metrics())?;
            draws.push(value);
        }
        prepared.managed_shaders = Some(PreparedManagedShaders { draws, metrics });
        Ok(())
    }
}

impl PreparedManagedShaders {
    pub(super) fn retain_submitted(&self, queue: &wgpu::Queue) {
        for draw in &self.draws {
            draw.retain_submitted(queue);
        }
    }
}
