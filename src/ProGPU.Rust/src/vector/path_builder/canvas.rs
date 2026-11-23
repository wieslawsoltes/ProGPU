//! Small source-preserving adapters to the existing actual retained canvas.
use super::RetainedPath;
use crate::{
    Error,
    canvas::{Canvas, CanvasClipOperation},
    paint::Paint,
};
impl Canvas {
    /// Draw the actual path generation and its independent inverse-fill policy.
    /// First packed access materializes once; later commands share its owner.
    pub fn draw_retained_path(&mut self, path: &RetainedPath, paint: &Paint) -> Result<(), Error> {
        self.draw_path(path.geometry()?, path.is_inverse(), paint)
    }
    /// Clip by the actual source generation, preserving original inverse rules.
    pub fn clip_retained_path(
        &mut self,
        path: &RetainedPath,
        operation: CanvasClipOperation,
        antialias: bool,
    ) -> Result<(), Error> {
        self.clip_path(path.geometry()?, path.is_inverse(), operation, antialias)
    }
}
