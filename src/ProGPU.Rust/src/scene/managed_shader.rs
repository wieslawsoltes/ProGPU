//! Retained original managed WGSL draw generations.
use super::*;
use crate::shader_effect::{
    ManagedShaderMask, ManagedShaderParameters, ManagedShaderQuad, SampleLattice,
};

/// An immutable managed WGSL command evaluated on its actual parent attachment.
/// Parameters retain all source textures and sampler-register precedence. The
/// explicit mask is independent of the ordinary retained binary clip.
#[derive(Clone)]
pub struct ManagedShaderEffectDraw {
    /// Frozen WGSL/constants/source and independent sampler generations.
    pub parameters: ManagedShaderParameters,
    /// Original source quad/UVs, optional exact output edges and local transform.
    pub quad: ManagedShaderQuad,
    /// Actual parent physical target origin and extent.
    pub target: SampleLattice,
    /// Original fixed-function destination blend policy.
    pub blend: LayerBlend,
    /// Optional exact managed coverage record and source owner.
    pub mask: Option<Arc<ManagedShaderMask>>,
}
impl std::fmt::Debug for ManagedShaderEffectDraw {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ManagedShaderEffectDraw")
            .field("target", &self.target)
            .field("blend", &self.blend)
            .field("has_mask", &self.mask.is_some())
            .finish_non_exhaustive()
    }
}
impl ManagedShaderEffectDraw {
    /// Whole immutable input admission without GPU work.
    pub fn validate(&self) -> Result<(), Error> {
        self.parameters.validate()?;
        self.quad.validate()?;
        if self.target.size.contains(&0)
            || !crate::shader_effect::ManagedShaderEngine::supports_blend(self.blend)
        {
            return Err(Error::InvalidInput);
        }
        if self
            .mask
            .as_ref()
            .is_some_and(|mask| mask.target() != self.target)
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}
impl Scene {
    /// Retain original managed WGSL evaluation in painter order. No shader
    /// compilation, texture capture or source callbacks occur while recording.
    pub fn draw_managed_shader_effect(
        &mut self,
        draw: ManagedShaderEffectDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::ManagedShaderEffect(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn managed_shader_effects(
        &self,
    ) -> impl Iterator<Item = (usize, &ManagedShaderEffectDraw)> {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(index, command)| match command {
                Command::ManagedShaderEffect(draw) => Some((index, draw.as_ref())),
                _ => None,
            })
    }
}
