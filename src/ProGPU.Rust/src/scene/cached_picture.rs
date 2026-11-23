//! Ordered live cached-picture references; source callbacks stay in preparation.
use super::*;
mod dependencies;
use crate::composition::cached_picture::{CachedPicture, CachedPictureLease};
/// Final consumer placement is independent of the cached source capture.
#[derive(Clone, Debug)]
pub struct CachedPictureDraw {
    // Recording independently retains the lookup entry, not only its borrowed picture.
    lease: Option<CachedPictureLease>,
    /// Shared typed live/static source, retained by this command.
    pub picture: CachedPicture,
    /// Complete original caller transform outside the capture.
    pub transform: crate::vector::Matrix4,
    /// Final source-over/selected-blend opacity, outside captured content.
    pub opacity: f32,
    /// Final destination blend.
    pub blend: LayerBlend,
}
impl CachedPictureDraw {
    /// Retain one identity-transformed source-over cached-picture occurrence.
    pub fn new(picture: CachedPicture) -> Self {
        Self {
            lease: None,
            picture,
            transform: crate::vector::Matrix4::IDENTITY,
            opacity: 1.0,
            blend: LayerBlend::SrcOver,
        }
    }
    /// Retain an independent lookup lease through every recording clone.
    pub fn from_lease(lease: &CachedPictureLease) -> Self {
        let mut result = Self::new(lease.picture().clone());
        result.lease = Some(lease.clone());
        result
    }
    pub(crate) fn affine_transform(&self) -> Result<Affine, Error> {
        source_capture::affine(self.transform)
    }
    /// Admit only consumer metadata; never call a source while recording.
    pub fn validate(&self) -> Result<(), Error> {
        source_capture::affine(self.transform)?;
        if !self.opacity.is_finite() || !(0.0..=1.0).contains(&self.opacity) {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}
impl Scene {
    pub(crate) fn validate_cached_picture_source(&self) -> Result<(), Error> {
        if self.scope_depth != 0 {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    pub(crate) fn validate_cached_picture_dependencies(
        &self,
        owner: usize,
        depth: u16,
    ) -> Result<(), Error> {
        dependencies::validate(self, owner, depth)
    }
    /// Record an ordered independent source lease without refreshing or GPU work.
    pub fn draw_cached_picture(&mut self, draw: CachedPictureDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::CachedPicture(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn cached_pictures(&self) -> impl Iterator<Item = &CachedPictureDraw> {
        self.commands.iter().filter_map(|command| match command {
            Command::CachedPicture(draw) => Some(draw.as_ref()),
            _ => None,
        })
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn replace_cached_pictures(
        &self,
        outputs: Vec<Option<ImageDraw>>,
    ) -> Result<Self, Error> {
        if outputs.len() != self.cached_pictures().count() {
            return Err(Error::ResourceDomain);
        }
        for output in outputs.iter().flatten() {
            output.validate()?;
        }
        let mut candidate = self.clone();
        let mut outputs = outputs.into_iter();
        for command in &mut candidate.commands {
            if let Command::CachedPicture(draw) = command {
                let blend = draw.blend;
                *command = match outputs.next().ok_or(Error::ResourceDomain)? {
                    None => Command::Empty,
                    Some(output) => Command::Image(Box::new(output), blend, None, None),
                };
            }
        }
        Ok(candidate)
    }
}
