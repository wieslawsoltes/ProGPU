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
            .any(|c| matches!(late_camera::split(c).0, Command::ArchiveGeometry(_)))
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
            let (inner, camera) = late_camera::split(command);
            if let Command::ArchiveGeometry(draw) = inner {
                let (resolved, final_target) = if let Some(camera) = camera {
                    draw.resolve_with_camera(dpi, offscreen, Some(camera))?
                } else {
                    draw.resolve(dpi, offscreen)?
                };
                candidate
                    .commands
                    .try_reserve(resolved.commands.len().max(1))
                    .map_err(|_| Error::Capacity)?;
                if let Some(camera) = camera {
                    candidate.append_archive_camera(resolved, camera)?;
                } else if draw.compile_only() {
                    // The original explicit extension records compiled vectors,
                    // advances the pending range, then invokes a no-op Render.
                    if resolved.commands.is_empty() {
                        candidate
                            .commands
                            .push(Command::CompileOnly(Box::new(Command::Empty), draw.clone()));
                    } else {
                        candidate.commands.extend(
                            resolved.commands.into_iter().map(|command| {
                                Command::CompileOnly(Box::new(command), draw.clone())
                            }),
                        );
                    }
                } else {
                    candidate.commands.extend(resolved.commands);
                }
                candidate.final_target_paths |= final_target;
            } else {
                candidate.commands.push(command.clone());
            }
        }
        Ok(Some(candidate))
    }
}
