//! Actual native design-unit outlines and explicit vector replay. This composes
//! original native font decoders with original vector-glyph painting, without
//! constructing a managed shaping run or a hinted capture/provider identity.
use crate::{
    Error,
    font::{FontError, SfntFace, Tag},
    path::{Path, Segment},
    text::{native_layout::NativeVerticalText, native_paragraph::*},
};
use std::{collections::HashMap, sync::Arc};
mod outline;
mod paint;
mod source;
pub use outline::{NativeDesignOutline, NativeDesignOutlineKind};
pub use paint::{
    NativeDesignPaint, NativeDesignVectorDraw, NativeDesignVectorPainter, NativeDesignVectorPath,
    NativeDesignVectorPlan,
};
#[derive(Clone, Debug)]
pub enum NativeDesignGeometrySource {
    Paragraph(Arc<NativeDesignParagraph>),
    Fragments(Arc<NativeFragmentParagraph>),
    Vertical(Arc<NativeVerticalText>),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDesignPlacementOwner {
    Paragraph(NativeDesignOccurrence),
    Ellipsis,
    Vertical { run_glyph_index: u32 },
}
/// Actual source style and original independent geometry scale. Em size remains
/// caller-supplied paint metadata; it is never recovered by a lossy multiply.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeDesignGeometryStyle {
    pub font_index: u32,
    pub source_style_index: Option<u32>,
    pub scale: f32,
    pub units_per_em: u16,
}
/// Complete original placed inventory, including tabs, objects and no-ink glyphs.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeDesignGeometrySlot {
    pub positioned_index: u32,
    pub source_glyph_index: u32,
    pub glyph_id: u32,
    pub font_index: u32,
    pub style_index: Option<u32>,
    pub outline_index: Option<u32>,
    pub cluster: i32,
    pub position: [f32; 2],
    pub advance: [f32; 2],
    pub owner: NativeDesignPlacementOwner,
}
/// Full native source ownership. Outlines are immutable canonical design records;
/// prepared replay paths own this generation, while this owner contains no Path.
#[derive(Debug)]
pub struct NativeDesignGeometry {
    source: NativeDesignGeometrySource,
    styles: Vec<NativeDesignGeometryStyle>,
    outlines: Vec<Arc<NativeDesignOutline>>,
    slots: Vec<NativeDesignGeometrySlot>,
}
#[derive(Debug, thiserror::Error)]
pub enum NativeDesignGeometryError {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Drawing(#[from] Error),
}
type Result<T> = std::result::Result<T, NativeDesignGeometryError>;
fn reserved<T>(n: usize) -> Result<Vec<T>> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(n)
        .map_err(|_| FontError::Capacity)?;
    Ok(result)
}
impl NativeDesignGeometry {
    /// Compile actual native monochrome vector outlines once. Original color
    /// bitmap/layer presentation and atlas placement are separate source routes.
    pub fn new(source: NativeDesignGeometrySource) -> Result<Arc<Self>> {
        source::build(source)
    }
    pub fn source(&self) -> &NativeDesignGeometrySource {
        &self.source
    }
    pub fn styles(&self) -> &[NativeDesignGeometryStyle] {
        &self.styles
    }
    pub fn outlines(&self) -> &[Arc<NativeDesignOutline>] {
        &self.outlines
    }
    pub fn slots(&self) -> &[NativeDesignGeometrySlot] {
        &self.slots
    }
    /// Metadata-free copies retain complete actual placement records; no font
    /// execution, shaping or topology reconstruction occurs during copying.
    pub fn copy_slots_into(&self, output: &mut [NativeDesignGeometrySlot]) -> Result<usize> {
        if output.len() < self.slots.len() {
            return Err(FontError::Capacity.into());
        }
        output[..self.slots.len()].copy_from_slice(&self.slots);
        Ok(self.slots.len())
    }
}
