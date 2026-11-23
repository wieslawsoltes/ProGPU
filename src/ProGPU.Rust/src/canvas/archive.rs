//! Lossless ordered archive ownership inside a retained Canvas recording.
use super::*;
use crate::picture_archive::{ArchiveError, ArchivePicture, ArchiveReplayOptions};
impl Canvas {
    /// Retain the actual raw generation in painter order. Execution occurs during
    /// picture preparation, before consumer encoding; no source array is rebuilt.
    pub fn draw_archive_picture(&mut self, picture: Arc<ArchivePicture>) -> Result<(), Error> {
        self.draw_archive_picture_with(picture, ArchiveReplayOptions::default())
    }
    /// Retain explicit original text preparation and receiving-frame metadata.
    pub fn draw_archive_picture_with(
        &mut self,
        picture: Arc<ArchivePicture>,
        options: ArchiveReplayOptions,
    ) -> Result<(), Error> {
        self.push(Command::Archive {
            picture,
            matrix: self.matrix,
            opacity: self.opacity,
            options,
        })
    }
}
impl CanvasPicture {
    pub(crate) fn from_archive(
        picture: Arc<ArchivePicture>,
        bounds: [f32; 4],
    ) -> Result<Self, ArchiveError> {
        // Source archive cull fields are raw transport metadata, including empty
        // and nonfinite values. No target allocation is implied by this owner.
        let size = [bounds[2] - bounds[0], bounds[3] - bounds[1]];
        Ok(Self {
            size,
            commands: Arc::from([Command::Archive {
                picture,
                matrix: CanvasMatrix::IDENTITY,
                opacity: 1.0,
                options: ArchiveReplayOptions::default(),
            }]),
        })
    }
    pub(crate) fn archive_commands(&self) -> &[Command] {
        &self.commands
    }
}
