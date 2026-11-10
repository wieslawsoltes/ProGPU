//! Original per-command late-camera metadata, separate from model geometry.
use super::*;
use crate::vector::Matrix4;

pub(super) fn split(command: &Command) -> (&Command, Option<Matrix4>) {
    if let Command::LateCamera(command, camera) = command {
        (command, Some(*camera))
    } else {
        (command, None)
    }
}
pub(super) fn validate(command: &Command, camera: Matrix4) -> Result<(), Error> {
    if camera.0.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    match command {
        Command::Empty => Ok(()),
        Command::ArchiveVector(draw) => draw.validate(),
        Command::ArchiveGeometry(draw) => draw.validate(),
        Command::Stroke(draw) | Command::PlacedStroke(draw, _) => draw.validate(),
        Command::TextPath(..) | Command::MaterialPath(..) | Command::Path(_) => Ok(()),
        _ => Err(Error::UnsupportedExecution),
    }
}
impl Scene {
    /// Private source adapter: every child belongs to one actual marked archive
    /// command. Empty commands still select the original global frame camera.
    pub(crate) fn append_archive_camera(
        &mut self,
        source: Scene,
        camera: Matrix4,
    ) -> Result<(), Error> {
        if source.scope_depth != 0 || source.has_scopes || source.inherited_clip.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        for command in &source.commands {
            validate(command, camera)?;
        }
        if camera.0.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        self.commands
            .try_reserve(source.commands.len().max(1))
            .map_err(|_| Error::Capacity)?;
        if source.commands.is_empty() {
            self.commands
                .push(Command::LateCamera(Box::new(Command::Empty), camera));
        } else {
            self.commands.extend(
                source
                    .commands
                    .into_iter()
                    .map(|c| Command::LateCamera(Box::new(c), camera)),
            );
        }
        Ok(())
    }
}
/// Original ordinary-path policy for prepared stroke fill fallback. Explicit
/// text-derived path policy remains unchanged, including its separate scale.
pub(super) fn path_policy(command: &Command, dpi: f32) -> Option<TextPathPolicy> {
    match command {
        Command::TextPath(_, _, policy) => Some(*policy),
        Command::Path(draw) | Command::MaterialPath(draw, _) => Some(TextPathPolicy {
            aliased: draw.sampling.sample_grid() == 1,
            sample_grid: draw.sampling.sample_grid(),
            coverage_gamma: 0.0,
            device_phase_grid: 64,
            quantize_scale: false,
            raster_scale: dpi,
            coverage_transfer: Default::default(),
        }),
        _ => None,
    }
}
