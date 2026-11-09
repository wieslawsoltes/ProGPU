//! Painter-ordered final-device shader resources and original retained ownership.
use super::*;
use crate::{
    scene::ShaderEffectDraw,
    shader_effect::{
        PreparedShaderEffect, SampleLattice, SampleShaderRequest, ShaderEffectEngine,
        ShaderEffectMetrics, ShaderVectorMask,
    },
};
pub(super) struct PreparedShaders {
    draws: Vec<PreparedShaderEffect>,
    metrics: ShaderEffectMetrics,
}
impl PreparedShaders {
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
pub(super) fn merge_metrics(
    target: &mut ShaderEffectMetrics,
    value: ShaderEffectMetrics,
) -> Result<(), Error> {
    target.bytecode_bytes = target
        .bytecode_bytes
        .checked_add(value.bytecode_bytes)
        .ok_or(Error::Capacity)?;
    target.uniform_bytes = target
        .uniform_bytes
        .checked_add(value.uniform_bytes)
        .ok_or(Error::Capacity)?;
    target.output_texture_bytes = target
        .output_texture_bytes
        .checked_add(value.output_texture_bytes)
        .ok_or(Error::Capacity)?;
    target.draws = target
        .draws
        .checked_add(value.draws)
        .ok_or(Error::Capacity)?;
    Ok(())
}
impl PreparedScene {
    /// Original final-device shader resources for this target, excluding earlier
    /// source captures. Replays issue the owned draw without uniform mutation.
    pub fn shader_effect_metrics(&self) -> ShaderEffectMetrics {
        self.shaders
            .as_ref()
            .map_or_else(Default::default, |s| s.metrics)
    }
}
impl Renderer {
    fn validate_shader_draw(
        &self,
        draw: &ShaderEffectDraw,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        draw.validate()?;
        let expected = SampleLattice {
            origin: [
                i32::try_from(frame.origin[0]).map_err(|_| Error::Capacity)?,
                i32::try_from(frame.origin[1]).map_err(|_| Error::Capacity)?,
            ],
            size: [frame.width, frame.height],
        };
        if draw.target != expected
            || draw.input.device() != &self.device
            || draw
                .sampler
                .as_ref()
                .is_some_and(|s| s.device() != &self.device)
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
    pub(super) fn preflight_shaders(&self, scene: &Scene, frame: RenderFrame) -> Result<(), Error> {
        let mut count = 0;
        for draw in scene.shader_effects() {
            self.validate_shader_draw(draw, frame)?;
            count += 1;
            if count > 16384 {
                return Err(Error::Capacity);
            }
        }
        Ok(())
    }
    pub(super) fn validate_shaders(
        &self,
        compiled: &CompiledScene,
        frame: RenderFrame,
    ) -> Result<(), Error> {
        if compiled.shader_effects.len() > 16384 {
            return Err(Error::Capacity);
        }
        for draw in &compiled.shader_effects {
            self.validate_shader_draw(draw, frame)?;
        }
        Ok(())
    }
    /// Produce typed original path coverage through this renderer's actual shader
    /// engine owner. Origin and extent remain part of final-device mask identity.
    pub async fn prepare_shader_vector_mask(
        &mut self,
        plan: &crate::mask::ClipPlan,
        origin: [i32; 2],
    ) -> Result<ShaderVectorMask, Error> {
        if self.shader_engine.is_none() {
            self.shader_engine = Some(ShaderEffectEngine::new(
                &self.device,
                &self.queue,
                self.format,
                ExecutionPreference::Automatic,
            )?);
        }
        self.shader_engine
            .as_mut()
            .ok_or(Error::ResourceDomain)?
            .prepare_vector_mask(plan, origin)
            .await
    }
    pub(super) async fn prepare_shaders(
        &mut self,
        compiled: &CompiledScene,
        frame: RenderFrame,
        prepared: &mut PreparedScene,
    ) -> Result<(), Error> {
        if compiled.shader_effects.is_empty() {
            return Ok(());
        }
        self.validate_shaders(compiled, frame)?;
        if self.shader_engine.is_none() {
            self.shader_engine = Some(ShaderEffectEngine::new(
                &self.device,
                &self.queue,
                self.format,
                ExecutionPreference::Automatic,
            )?);
        }
        // The engine owns the domain of existing typed vector masks. A failed
        // candidate must not discard that domain or invalidate earlier masks.
        let engine = self.shader_engine.as_mut().ok_or(Error::ResourceDomain)?;
        let mut draws = Vec::new();
        draws
            .try_reserve_exact(compiled.shader_effects.len())
            .map_err(|_| Error::Capacity)?;
        let mut metrics = ShaderEffectMetrics::default();
        for draw in &compiled.shader_effects {
            let value = engine
                .prepare_sample(SampleShaderRequest {
                    effect: &draw.effect,
                    frame: draw.frame,
                    target: draw.target,
                    input: &draw.input,
                    sampler: draw.sampler.as_ref(),
                    derivative: draw.derivative,
                    vector_mask: draw.vector_mask.as_deref(),
                })
                .await?;
            merge_metrics(&mut metrics, value.metrics())?;
            draws.push(value);
        }
        prepared.shaders = Some(PreparedShaders { draws, metrics });
        Ok(())
    }
}
