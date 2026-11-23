//! Safe typed mutable run views; Rust borrowing replaces pinned pointer exposure.
use super::*;
use std::marker::PhantomData;
/// Exclusive builder-owned glyph/position/text/cluster lease. Its lifetime ends
/// before Build or a new allocation can replace the original storage generation.
#[derive(Debug)]
pub struct TextBlobRunBuffer<'a, P> {
    run: &'a mut Run,
    clear_position_tail: bool,
    position: PhantomData<P>,
}
impl<'a, P> TextBlobRunBuffer<'a, P> {
    pub(super) fn new(run: &'a mut Run, clear_position_tail: bool) -> Self {
        Self {
            run,
            clear_position_tail,
            position: PhantomData,
        }
    }
    /// Complete original glyph capacity, including zero/no-ink slots.
    pub fn len(&self) -> usize {
        self.run.glyphs.len()
    }
    /// Whether this allocated run has no glyph slots.
    pub fn is_empty(&self) -> bool {
        self.run.glyphs.is_empty()
    }
    /// Original lazily zero-initialized glyph span.
    pub fn glyphs(&mut self) -> &mut [u16] {
        self.run.glyphs_mut()
    }
    /// Copy glyphs and clear the original unprovided tail.
    pub fn set_glyphs(&mut self, values: &[u16]) -> Result<()> {
        if values.len() > self.len() {
            return Err(Error::InvalidInput.into());
        }
        let target = self.run.glyphs_mut();
        copy_into(target, values, true)
    }
    /// Original complete writable source text inventory; it is not shaping data.
    pub fn text(&mut self) -> &mut [u8] {
        &mut self.run.text
    }
    /// Original caller-supplied cluster inventory. Build does not fabricate a
    /// paragraph from these optional source bytes or carry unsupported metadata.
    pub fn clusters(&mut self) -> &mut [u32] {
        &mut self.run.clusters
    }
    /// Original text copy preserves any unprovided tail.
    pub fn set_text(&mut self, text: &[u8]) -> Result<()> {
        copy_into(&mut self.run.text, text, false)
    }
    /// Original cluster copy preserves any unprovided tail.
    pub fn set_clusters(&mut self, clusters: &[u32]) -> Result<()> {
        copy_into(&mut self.run.clusters, clusters, false)
    }
}
impl TextBlobRunBuffer<'_, f32> {
    /// Horizontal X positions; default nominal runs expose an empty position span.
    pub fn positions(&mut self) -> &mut [f32] {
        match &mut self.run.placement {
            Placement::Horizontal { positions, .. } => positions,
            Placement::Default(_) => &mut [],
            _ => unreachable!("typed horizontal/default buffer"),
        }
    }
    /// Original horizontal prefix copy, without clearing a previously written tail.
    pub fn set_positions(&mut self, values: &[f32]) -> Result<()> {
        copy_into(self.positions(), values, false)
    }
}
impl TextBlobRunBuffer<'_, [f32; 2]> {
    /// Original complete lazily initialized positioned coordinates.
    pub fn positions(&mut self) -> &mut [[f32; 2]] {
        self.run.points_mut()
    }
    /// Typed positioned setter clears its unprovided tail; text/raw adapters keep
    /// their original plain span-copy behavior.
    pub fn set_positions(&mut self, values: &[[f32; 2]]) -> Result<()> {
        if values.len() > self.len() {
            return Err(Error::InvalidInput.into());
        }
        let clear = self.clear_position_tail;
        copy_into(self.positions(), values, clear)
    }
}
impl TextBlobRunBuffer<'_, RotationScaleMatrix> {
    /// Original complete four-coefficient frames, with zero default values.
    pub fn positions(&mut self) -> &mut [RotationScaleMatrix] {
        let Placement::RotationScale(positions) = &mut self.run.placement else {
            unreachable!("typed rotation/scale buffer")
        };
        positions
    }
    /// Original frame prefix copy preserves the previous tail.
    pub fn set_positions(&mut self, values: &[RotationScaleMatrix]) -> Result<()> {
        copy_into(self.positions(), values, false)
    }
}
fn copy_into<T: Copy + Default>(target: &mut [T], source: &[T], clear: bool) -> Result<()> {
    if source.len() > target.len() {
        return Err(Error::InvalidInput.into());
    }
    target[..source.len()].copy_from_slice(source);
    if clear {
        target[source.len()..].fill(T::default());
    }
    Ok(())
}
