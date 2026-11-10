//! Immutable final-device programmable shader commands.
use super::*;
use crate::{
    image::GpuImage,
    shader_effect::{SampleLattice, ShaderEffect, ShaderFrame, ShaderVectorMask},
};

/// Original final-device shader input. Captures, constants, source frame and typed
/// vector mask are independently retained; the effect is evaluated on the actual
/// parent attachment, never rendered and resampled as an ordinary image.
#[derive(Clone)]
pub struct ShaderEffectDraw {
    /// Original immutable program and this draw's constants/filter/revision.
    pub effect: ShaderEffect,
    /// Exact original v5/v6 source frame.
    pub frame: ShaderFrame,
    /// Actual parent physical target lattice.
    pub target: SampleLattice,
    /// Complete implicit-input capture at its original scale-space size.
    pub input: GpuImage,
    /// Independent full sampler capture; None samples the implicit input.
    pub sampler: Option<GpuImage>,
    /// Optional original c0..c31 UV derivative register.
    pub derivative: Option<u32>,
    /// Original renderer-owned vector coverage at the same physical target.
    pub vector_mask: Option<Arc<ShaderVectorMask>>,
}
impl std::fmt::Debug for ShaderEffectDraw {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ShaderEffectDraw")
            .field("effect", &self.effect)
            .field("frame", &self.frame)
            .field("target", &self.target)
            .field("input", &self.input)
            .field("sampler", &self.sampler)
            .field("derivative", &self.derivative)
            .field("has_vector_mask", &self.vector_mask.is_some())
            .finish()
    }
}
impl ShaderEffectDraw {
    /// Validate complete source geometry and parameters without GPU mutation.
    pub fn validate(&self) -> Result<(), Error> {
        self.effect.validate()?;
        let frame = match self.frame {
            ShaderFrame::Samples(f) => f.resolve()?,
            ShaderFrame::Affine(f) => f.resolve()?,
            _ => return Err(Error::InvalidInput),
        };
        if self.input.size() != frame.capture.size || self.derivative.is_some_and(|r| r >= 32) {
            return Err(Error::InvalidInput);
        }
        frame.project(self.target)?;
        if self
            .vector_mask
            .as_ref()
            .is_some_and(|m| m.target() != self.target)
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}
impl Scene {
    /// Record original final-device shader evaluation in painter order. Ordinary
    /// mask scopes are not relabeled as vector-source proof; use vector_mask.
    pub fn draw_shader_effect(&mut self, draw: ShaderEffectDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ShaderEffect(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn shader_effects(&self) -> impl Iterator<Item = &ShaderEffectDraw> {
        self.commands.iter().filter_map(|command| match command {
            Command::ShaderEffect(draw) => Some(draw.as_ref()),
            _ => None,
        })
    }
}
