//! Original source-owned 3D line and ACIS commands.
use super::*;
impl Scene {
    /// Retain the exact producer and defer actual shared camera selection to compilation.
    pub fn draw_archive_three_d(
        &mut self,
        draw: crate::picture_archive::ArchiveThreeDDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ArchiveThreeD(Arc::new(draw)));
        Ok(())
    }
}
