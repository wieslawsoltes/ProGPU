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
    /// Prepare and append the actual surface's completed root drawing, retaining
    /// matrix/save/clip state. Open layers keep their independent recording owners
    /// and are not painted into the surface until their original Restore boundary.
    pub async fn flush(&mut self) -> Result<SurfaceFlush, ImageResourceError> {
        let pending = RootFlush::new(&self.canvas)?;
        let picture = &pending.picture;
        let scene = if let Some(context) = self.surface.context().cloned() {
            let mut count = 0;
            picture.preflight(&context, 0, &mut count)?;
            let backdrop = if needs_backdrop(picture, 0)? {
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
        pending.accept(&mut self.canvas)?;
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

/// One immutable view of the actual surface/root context. The outermost layer
/// owns that context while any child recording is active. Its frozen prior-image
/// recipe and every child context retain their own original commands.
struct RootFlush {
    picture: CanvasPicture,
    layer: Option<usize>,
    prefix: Vec<Command>,
    clip_count: usize,
}
impl RootFlush {
    fn new(canvas: &Canvas) -> Result<Self, Error> {
        let layer = canvas.saved.iter().position(|saved| saved.layer.is_some());
        let (source, clip_count) = if let Some(index) = layer {
            let saved = &canvas.saved[index];
            let frame = saved.layer.as_ref().ok_or(Error::ResourceDomain)?;
            (&frame.parent, saved.clips)
        } else {
            (&canvas.commands, canvas.clips.len())
        };
        let mut commands = Vec::new();
        let count = source
            .len()
            .checked_add(clip_count)
            .ok_or(Error::Capacity)?;
        commands
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        commands.extend(source.iter().cloned());
        commands.extend((0..clip_count).map(|_| Command::Pop));
        // Prepare the existing Rust surface's live-clip continuation before
        // publishing commands to RenderSurface, so retirement cannot allocate.
        let mut prefix = Vec::new();
        prefix
            .try_reserve_exact(clip_count)
            .map_err(|_| Error::Capacity)?;
        prefix.extend(
            canvas.clips[..clip_count]
                .iter()
                .map(|clip| Command::Clip(clip.clip.clone())),
        );
        Ok(Self {
            picture: CanvasPicture {
                size: canvas.size,
                commands: commands.into(),
            },
            layer,
            prefix,
            clip_count,
        })
    }
    fn accept(self, canvas: &mut Canvas) -> Result<(), Error> {
        if let Some(index) = self.layer {
            // The exclusive SurfaceCanvas borrow prevents a restore/save while
            // preparation awaits. This exact captured owner is still present.
            let frame = canvas
                .saved
                .get_mut(index)
                .and_then(|saved| saved.layer.as_mut())
                .ok_or(Error::ResourceDomain)?;
            frame.parent = self.prefix;
            frame.parent_clip_base = 0;
        } else {
            canvas.commands = self.prefix;
            canvas.context_clip_base = 0;
        }
        for (index, clip) in canvas.clips[..self.clip_count].iter_mut().enumerate() {
            clip.index = index;
        }
        Ok(())
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
