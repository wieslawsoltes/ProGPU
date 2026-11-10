//! Exclusive surface recording preserves actual context and snapshot ownership.
use super::*;
use crate::image::{ImageResourceError, RenderSurface, SurfaceFlush, TextureImage};
use std::ops::{Deref, DerefMut};
/// One mutable canvas lease over one actual surface. No writable surface alias
/// can be used while this lease exists. Drop does not submit, poll or flush.
pub struct SurfaceCanvas<'surface, 'pixels> {
    surface: &'surface mut RenderSurface<'pixels>,
    canvas: Canvas,
}
impl<'surface, 'pixels> SurfaceCanvas<'surface, 'pixels> {
    /// Record against the source surface's original logical extent and context.
    pub fn new(surface: &'surface mut RenderSurface<'pixels>) -> Result<Self, Error> {
        let info = surface.info();
        let canvas = Canvas::new([info.width.max(0) as f32, info.height.max(0) as f32])?;
        Ok(Self { surface, canvas })
    }
    /// Actual borrowed surface identity; read-only access cannot mutate snapshots.
    pub fn surface(&self) -> &RenderSurface<'pixels> {
        self.surface
    }
    /// Prepare and append completed drawing, retaining matrix/save/clip state.
    /// An open isolated layer must be restored before submission; partial-layer
    /// flushing never silently flattens or discards its independent destination.
    pub async fn flush(&mut self) -> Result<SurfaceFlush, ImageResourceError> {
        if self.canvas.saved.iter().any(|s| s.layer.is_some()) {
            return Err(Error::UnsupportedExecution.into());
        }
        let picture = self.canvas.snapshot_current();
        let scene = if let Some(context) = self.surface.context().cloned() {
            let mut count = 0;
            picture.preflight(&context, 0, &mut count)?;
            let backdrop = if needs_backdrop(&picture, 0)? {
                Some(self.surface.snapshot().await?)
            } else {
                None
            };
            picture
                .prepare_with_backdrop(&context, backdrop.as_ref())
                .await?
        } else {
            picture.to_scene()?
        };
        self.surface
            .set_readback_regions(self.canvas.cpu_readback_regions.clone())?;
        self.surface.record(&scene)?;
        // Commands now belong to the surface even if the subsequent submission
        // fails; retrying flush must not append the same painter operations twice.
        self.canvas.commands.clear();
        for (index, clip) in self.canvas.clips.iter_mut().enumerate() {
            clip.index = index;
            self.canvas.commands.push(Command::Clip(clip.clip.clone()));
        }
        self.canvas.context_clip_base = 0;
        self.surface.flush().await
    }
    /// Restore outstanding layers and submit this lease's complete recording.
    pub async fn finish(mut self) -> Result<SurfaceFlush, ImageResourceError> {
        self.canvas.restore_to_count(1)?;
        self.flush().await
    }
    /// Flush completed drawing and return an immutable actual surface generation.
    pub async fn snapshot(&mut self) -> Result<TextureImage, ImageResourceError> {
        self.flush().await?;
        self.surface.snapshot().await
    }
}
impl Deref for SurfaceCanvas<'_, '_> {
    type Target = Canvas;
    fn deref(&self) -> &Canvas {
        &self.canvas
    }
}
impl DerefMut for SurfaceCanvas<'_, '_> {
    fn deref_mut(&mut self) -> &mut Canvas {
        &mut self.canvas
    }
}
fn needs_backdrop(picture: &CanvasPicture, depth: u16) -> Result<bool, Error> {
    if depth >= 256 {
        return Err(Error::Capacity);
    }
    for command in picture.commands.iter() {
        match command {
            Command::Layer(layer) => {
                if layer.prior.is_some() || needs_backdrop(&layer.source, depth + 1)? {
                    return Ok(true);
                }
            }
            Command::Picture { picture, .. } => {
                if needs_backdrop(picture, depth + 1)? {
                    return Ok(true);
                }
            }
            _ => {}
        }
    }
    Ok(false)
}
