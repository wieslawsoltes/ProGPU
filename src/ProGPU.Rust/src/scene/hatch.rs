//! Original separate hatch extension scheduling and source ownership.
use super::*;
impl Scene {
    /// Record an actual hatch generation without PathAtlas substitution.
    pub fn draw_archive_hatch(
        &mut self,
        draw: crate::picture_archive::ArchiveHatchDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        if draw.is_active() {
            draw.brush()?;
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ArchiveHatch(Arc::new(draw)));
        Ok(())
    }
}
impl CompiledScene {
    /// Original ordered records, retaining local bounds and exact weight bits.
    pub fn hatch_records(&self) -> &[crate::picture_archive::GpuHatchRecord] {
        &self.hatches.records
    }
    pub fn hatch_segments(&self) -> &[crate::picture_archive::GpuHatchSegment] {
        &self.hatches.segments
    }
}
