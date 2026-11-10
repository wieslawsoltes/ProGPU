//! Ordered owned-source scopes whose target-dependent parameters remain deferred.
use super::*;
use crate::{shader_effect::OwnedShaderEffectRecording, vector::Matrix4};

/// One original ordered owned shader scope. Source capture/translation belong to
/// the recipe; this complete transform and opacity belong to its final output.
#[derive(Clone, Debug)]
pub struct OwnedShaderEffectDraw {
    /// Independent immutable picture and recipe generation.
    pub recording: OwnedShaderEffectRecording,
    /// Complete original final-output transform, before actual target planning.
    pub transform: Matrix4,
    /// Actual ancestor output opacity, independent of captured root opacity.
    pub opacity: f32,
    /// Original destination blend for the final programmable draw.
    pub blend: LayerBlend,
}
impl OwnedShaderEffectDraw {
    /// Retain an ordered identity-transformed, source-over output scope.
    pub fn new(recording: OwnedShaderEffectRecording) -> Self {
        Self {
            recording,
            transform: Matrix4::IDENTITY,
            opacity: 1.0,
            blend: LayerBlend::SrcOver,
        }
    }
    /// Whole source descriptor admission, without invoking its preparation recipe.
    pub fn validate(&self) -> Result<(), Error> {
        self.affine_transform()?;
        if !self.opacity.is_finite()
            || !(0.0..=1.0).contains(&self.opacity)
            || !crate::shader_effect::ManagedShaderEngine::supports_blend(self.blend)
        {
            return Err(Error::InvalidInput);
        }
        self.recording.scene().validate_owned_shader_source()
    }
    pub(crate) fn affine_transform(&self) -> Result<Affine, Error> {
        source_capture::affine(self.transform)
    }
}
impl Scene {
    pub(crate) fn validate_owned_shader_source(&self) -> Result<(), Error> {
        if self.scope_depth != 0 {
            Err(Error::InvalidInput)
        } else {
            Ok(())
        }
    }
    /// Record a source scope in painter order, independently retaining its
    /// picture/recipe. No capture, shader compilation or source callback occurs.
    pub fn draw_owned_shader_effect(&mut self, draw: OwnedShaderEffectDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::OwnedShaderEffect(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn owned_shader_effects(
        &self,
    ) -> impl Iterator<Item = (usize, &OwnedShaderEffectDraw)> {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(index, command)| match command {
                Command::OwnedShaderEffect(draw) => Some((index, draw.as_ref())),
                _ => None,
            })
    }
    pub(crate) fn replace_owned_shader_effects(
        &self,
        outputs: Vec<ManagedShaderEffectDraw>,
    ) -> Result<Self, Error> {
        let expected = self.owned_shader_effects().count();
        if expected != outputs.len() {
            return Err(Error::ResourceDomain);
        }
        // Admit the complete replacement inventory before publishing any output.
        for draw in &outputs {
            draw.validate()?;
        }
        let mut candidate = self.clone();
        let mut outputs = outputs.into_iter();
        for command in &mut candidate.commands {
            if matches!(command, Command::OwnedShaderEffect(_)) {
                *command = Command::ManagedShaderEffect(Box::new(
                    outputs.next().ok_or(Error::ResourceDomain)?,
                ));
            }
        }
        Ok(candidate)
    }
}
