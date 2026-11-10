//! Deferred original archive geometry decisions at the actual target boundary.
use super::*;
impl Scene {
    pub fn draw_archive_geometry(
        &mut self,
        draw: crate::picture_archive::ArchiveGeometryDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ArchiveGeometry(Arc::new(draw)));
        Ok(())
    }
    pub(super) fn expand_archive_geometry(
        &self,
        dpi: f32,
        offscreen: bool,
    ) -> Result<Option<Self>, Error> {
        if !self
            .commands
            .iter()
            .any(|c| matches!(c, Command::ArchiveGeometry(_)))
        {
            return Ok(None);
        }
        let mut candidate = self.clone();
        candidate.commands = Vec::new();
        candidate
            .commands
            .try_reserve(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            if let Command::ArchiveGeometry(draw) = command {
                let (resolved, final_target) = draw.resolve(dpi, offscreen)?;
                candidate
                    .commands
                    .try_reserve(resolved.commands.len())
                    .map_err(|_| Error::Capacity)?;
                candidate.commands.extend(resolved.commands);
                candidate.final_target_paths |= final_target;
            } else {
                candidate.commands.push(command.clone());
            }
        }
        Ok(Some(candidate))
    }
}
