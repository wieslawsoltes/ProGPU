//! Original CPU drawable callbacks, generation invalidation and picture snapshots.
use super::{Canvas, CanvasMatrix, CanvasPicture};
use crate::Error;
use std::sync::{
    Arc,
    atomic::{AtomicU32, Ordering},
};

/// Original drawable producer. Callbacks run only during explicit CPU recording,
/// never during preparation/submission of an immutable picture generation.
pub trait CanvasDrawableSource {
    /// Record the actual source commands. The default source paints nothing.
    fn draw(&self, _canvas: &mut Canvas) -> Result<(), Error> {
        Ok(())
    }
    /// Original source bounds, independent of any painted command envelope.
    fn bounds(&self) -> [f32; 4] {
        [0.0; 4]
    }
    /// Original producer-owned approximate byte count.
    fn approximate_bytes_used(&self) -> i32 {
        0
    }
    /// Optional original custom snapshot override. None uses a real recorder and
    /// invokes the source Draw callback under the ordinary saved state contract.
    fn snapshot(&self) -> Result<Option<DrawableSnapshot>, Error> {
        Ok(None)
    }
}
struct Data {
    source: Arc<dyn CanvasDrawableSource>,
    generation: AtomicU32,
}
/// Shared actual drawable owner. Invalidation changes only its generation;
/// snapshots already returned retain their immutable command/resource owners.
#[derive(Clone)]
pub struct CanvasDrawable(Arc<Data>);
/// Actual immutable recorded picture and its original independent cull rectangle.
#[derive(Clone, Debug)]
pub struct DrawableSnapshot {
    /// Actual retained commands and all resource owners.
    pub picture: CanvasPicture,
    /// Source cull rectangle; an empty recording retains the original zero value.
    pub bounds: [f32; 4],
}
impl CanvasDrawable {
    /// Own an actual callback producer with original generation one.
    pub fn new(source: impl CanvasDrawableSource + 'static) -> Self {
        Self::from_source(Arc::new(source))
    }
    /// Retain an existing actual producer identity.
    pub fn from_source(source: Arc<dyn CanvasDrawableSource>) -> Self {
        Self(Arc::new(Data {
            source,
            generation: AtomicU32::new(1),
        }))
    }
    /// Original atomic wrapping generation, without executing source callbacks.
    pub fn generation_id(&self) -> u32 {
        self.0.generation.load(Ordering::Acquire)
    }
    /// Mark source output changed without invalidating independent snapshots.
    pub fn notify_drawing_changed(&self) {
        self.0.generation.fetch_add(1, Ordering::AcqRel);
    }
    /// Read the original live producer bounds.
    pub fn bounds(&self) -> [f32; 4] {
        self.0.source.bounds()
    }
    /// Read the original producer-owned byte estimate.
    pub fn approximate_bytes_used(&self) -> i32 {
        self.0.source.approximate_bytes_used()
    }
    /// Original Save/Concat/OnDraw/Restore sequence. A callback failure still
    /// restores exactly one saved scope, without flushing or submitting anything.
    pub fn draw(&self, canvas: &mut Canvas, matrix: CanvasMatrix) -> Result<(), Error> {
        canvas.save()?;
        let mut saved = Restore {
            canvas,
            armed: true,
        };
        saved.canvas.concat(matrix);
        let result = self.0.source.draw(saved.canvas);
        saved.finish()?;
        result
    }
    /// Original scalar-position adapter uses an actual translation matrix.
    pub fn draw_at(&self, canvas: &mut Canvas, origin: [f32; 2]) -> Result<(), Error> {
        self.draw(canvas, CanvasMatrix::translation(origin))
    }
    /// Freeze a custom snapshot or the actual source Draw result. The original
    /// cull origin remains metadata and does not translate recorded coordinates.
    pub fn snapshot(&self) -> Result<DrawableSnapshot, Error> {
        if let Some(snapshot) = self.0.source.snapshot()? {
            return Ok(snapshot);
        }
        let bounds = self.bounds();
        let mut canvas = Canvas::new([bounds[2] - bounds[0], bounds[3] - bounds[1]])?;
        self.draw_at(&mut canvas, [0.0; 2])?;
        let picture = canvas.finish()?;
        let bounds = if picture.is_empty() { [0.0; 4] } else { bounds };
        Ok(DrawableSnapshot { picture, bounds })
    }
}
impl Canvas {
    /// Execute the actual drawable producer under its original scoped transform.
    pub fn draw_drawable(
        &mut self,
        drawable: &CanvasDrawable,
        matrix: CanvasMatrix,
    ) -> Result<(), Error> {
        drawable.draw(self, matrix)
    }
    /// Original point-position overload; no callback runs later during rendering.
    pub fn draw_drawable_at(
        &mut self,
        drawable: &CanvasDrawable,
        origin: [f32; 2],
    ) -> Result<(), Error> {
        drawable.draw_at(self, origin)
    }
}
struct Restore<'a> {
    canvas: &'a mut Canvas,
    armed: bool,
}
impl Restore<'_> {
    fn finish(&mut self) -> Result<(), Error> {
        self.armed = false;
        self.canvas.restore()
    }
}
impl Drop for Restore<'_> {
    fn drop(&mut self) {
        if self.armed {
            let _ = self.canvas.restore();
        }
    }
}
