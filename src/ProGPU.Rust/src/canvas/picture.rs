//! Original picture recorder lifecycle and independently owned picture metadata.
use super::*;
use std::sync::atomic::{AtomicU32, Ordering};

static NEXT_PICTURE_ID: AtomicU32 = AtomicU32::new(0);
#[derive(Debug)]
struct Data {
    picture: CanvasPicture,
    bounds: [f32; 4],
    id: u32,
}
/// Actual immutable recording with original cull bounds and nonzero identity.
/// Cloning shares this generation; a new recording always gets a new identity.
#[derive(Clone, Debug)]
pub struct RecordedPicture(Arc<Data>);
impl RecordedPicture {
    fn new(picture: CanvasPicture, bounds: [f32; 4]) -> Self {
        let bounds = if picture.is_empty() { [0.0; 4] } else { bounds };
        Self::from_archive(picture, bounds)
    }
    /// Decoded archives retain their original cull bounds even when empty.
    pub(crate) fn from_archive(picture: CanvasPicture, bounds: [f32; 4]) -> Self {
        let id = loop {
            let id = NEXT_PICTURE_ID
                .fetch_add(1, Ordering::Relaxed)
                .wrapping_add(1);
            if id != 0 {
                break id;
            }
        };
        Self(Arc::new(Data {
            picture,
            bounds,
            id,
        }))
    }
    /// Original wrapping identity skips zero even after counter overflow.
    pub fn unique_id(&self) -> u32 {
        self.0.id
    }
    /// Original independent left/top/right/bottom cull rectangle.
    pub fn cull_bounds(&self) -> [f32; 4] {
        self.0.bounds
    }
    /// Borrow the actual immutable command and resource generation.
    pub fn picture(&self) -> &CanvasPicture {
        &self.0.picture
    }
    /// Original painter-ordered replay; source metadata does not translate draws.
    pub fn playback(&self, canvas: &mut Canvas) -> Result<(), Error> {
        canvas.draw_picture(self.0.picture.clone(), None)
    }
    /// Count actual Rust recording operations, replacing nested picture records
    /// with their contents only when requested. Saturates at the original i32 cap.
    pub fn approximate_operation_count(&self, include_nested: bool) -> i32 {
        self.0.picture.approximate_operation_count(include_nested)
    }
    /// Approximate actual Rust command/payload storage, excluding font caches,
    /// geometry/texture allocations and allocator overhead. Shared occurrences
    /// are counted separately, matching the original diagnostic traversal policy.
    pub fn approximate_bytes_used(&self) -> i32 {
        let metadata = std::mem::size_of::<Data>() - std::mem::size_of::<CanvasPicture>();
        (self.0.picture.approximate_bytes_used() as usize)
            .saturating_add(metadata)
            .min(i32::MAX as usize) as i32
    }
    /// Retain this exact immutable generation as an independently owned drawable.
    pub fn drawable(&self) -> CanvasDrawable {
        CanvasDrawable::new(PictureSource(self.clone()))
    }
}

/// Exclusive reusable CPU recorder. A borrowed recording canvas cannot outlive
/// or race its recorder; no global context or GPU resource is created here.
#[derive(Default)]
pub struct PictureRecorder {
    canvas: Option<Canvas>,
    bounds: [f32; 4],
}
impl PictureRecorder {
    /// Original inactive recorder state.
    pub fn new() -> Self {
        Self::default()
    }
    /// Start a recording with original cull extent and independent origin.
    /// The existing finite nonnegative Canvas extent admission remains explicit.
    pub fn begin(&mut self, bounds: [f32; 4]) -> Result<&mut Canvas, Error> {
        if self.canvas.is_some() {
            return Err(Error::InvalidInput);
        }
        let canvas = Canvas::new([bounds[2] - bounds[0], bounds[3] - bounds[1]])?;
        self.bounds = bounds;
        Ok(self.canvas.insert(canvas))
    }
    /// Borrow the active CPU recording, if any.
    pub fn recording_canvas(&mut self) -> Option<&mut Canvas> {
        self.canvas.as_mut()
    }
    /// Complete original scopes/layers before publication. A completion error
    /// keeps the actual recorder owner active, without publishing a partial picture.
    pub fn end(&mut self) -> Result<RecordedPicture, Error> {
        let picture = self
            .canvas
            .as_mut()
            .ok_or(Error::InvalidInput)?
            .finish_recording()?;
        self.canvas = None;
        Ok(RecordedPicture::new(picture, self.bounds))
    }
    /// Complete the recording and retain it through the original drawable route.
    pub fn end_as_drawable(&mut self) -> Result<CanvasDrawable, Error> {
        Ok(self.end()?.drawable())
    }
    /// Abandon only the actual CPU recording owners. No callback or flush runs.
    pub fn cancel(&mut self) {
        self.canvas = None;
    }
}
struct PictureSource(RecordedPicture);
impl CanvasDrawableSource for PictureSource {
    fn draw(&self, canvas: &mut Canvas) -> Result<(), Error> {
        self.0.playback(canvas)
    }
    fn bounds(&self) -> [f32; 4] {
        self.0.cull_bounds()
    }
    fn approximate_bytes_used(&self) -> i32 {
        self.0.approximate_bytes_used()
    }
    fn snapshot(&self) -> Result<Option<DrawableSnapshot>, Error> {
        Ok(Some(DrawableSnapshot {
            picture: self.0.picture().clone(),
            bounds: self.0.cull_bounds(),
        }))
    }
}
impl CanvasPicture {
    /// Original nested-picture traversal policy over actual Rust recordings.
    pub fn approximate_operation_count(&self, include_nested: bool) -> i32 {
        let mut count = 0usize;
        let mut stack = vec![self];
        while let Some(picture) = stack.pop() {
            for command in picture.commands.iter() {
                count = count.saturating_add(match command {
                    Command::Picture { picture, .. } if include_nested => {
                        stack.push(picture);
                        0
                    }
                    Command::Archive { picture, .. } if include_nested => picture
                        .approximate_operation_count(true)
                        .unwrap_or(i32::MAX)
                        as usize,
                    Command::Leaf(scene) => scene.recorded_operation_count(),
                    _ => 1,
                });
                if count >= i32::MAX as usize {
                    return i32::MAX;
                }
            }
        }
        count as i32
    }
    /// Actual retained Rust command/payload estimate with the source saturation
    /// and nested occurrence policy; this is not C# struct-size compatibility.
    pub fn approximate_bytes_used(&self) -> i32 {
        let mut bytes = 0usize;
        let mut stack = vec![self];
        while let Some(picture) = stack.pop() {
            bytes = bytes
                .saturating_add(std::mem::size_of::<Self>())
                .saturating_add(std::mem::size_of_val(picture.commands.as_ref()));
            for command in picture.commands.iter() {
                bytes = bytes.saturating_add(match command {
                    Command::Picture { picture, .. } => {
                        stack.push(picture);
                        0
                    }
                    Command::Archive { picture, .. } => {
                        picture.approximate_bytes_used().unwrap_or(i32::MAX) as usize
                    }
                    Command::Leaf(scene) => scene.recorded_storage_bytes(),
                    Command::ImageBatch { layout, .. } => std::mem::size_of_val(layout.patches()),
                    _ => 0,
                });
                if bytes >= i32::MAX as usize {
                    return i32::MAX;
                }
            }
        }
        bytes.min(i32::MAX as usize) as i32
    }
}
