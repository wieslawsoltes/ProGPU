//! Original archive analytic vector records remain deferred to actual Scene compilation.
use super::*;
impl Scene {
    pub fn draw_archive_vector(
        &mut self,
        draw: crate::picture_archive::ArchiveVectorDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        if draw.is_renderable() {
            draw.brush()?;
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ArchiveVector(Arc::new(draw)));
        Ok(())
    }
    /// Complete original Scene inventory for the explicit archive capture bridge.
    /// Borrowing retains actual source owners; it does not reconstruct commands.
    pub(crate) fn archive_commands(&self) -> Result<&[Command], Error> {
        if self.scope_depth != 0 {
            return Err(Error::InvalidInput);
        }
        if self.inherited_clip.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        Ok(&self.commands)
    }
}
