//! Original native document block/table flow over actual formatted paragraphs.
//! Source formatting, block placement and explicit pagination remain separate.
use super::{native_layout::*, native_paragraph::*};
use std::sync::Arc;
use wide::{CmpGe, CmpLe, f64x2};
mod anchors;
mod arrange;
mod owned;
mod paginate;
mod rows;
pub use anchors::*;
pub use owned::*;
pub use paginate::*;
pub const NATIVE_DOCUMENT_ROOT: u32 = u32::MAX;
const LIMIT: usize = 1 << 20;
const ROOT: u32 = NATIVE_DOCUMENT_ROOT;
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
pub enum NativeDocumentError {
    #[error("invalid native document input")]
    InvalidArgument,
    #[error("insufficient native document output capacity")]
    InsufficientCapacity,
    #[error("native document allocation failed")]
    ResourceExhausted,
    #[error("native document placement does not fit the original constraints")]
    Unsupported,
}
type Result<T> = std::result::Result<T, NativeDocumentError>;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentBlock {
    pub parent_index: u32,
    pub subtree_end: u32,
    pub line_start: u32,
    pub line_count: u32,
    /// Original left, top, right, bottom source margins, in double DIPs.
    pub margins: [f64; 4],
    /// Resolved padding plus border, independent of margins.
    pub insets: [f64; 4],
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentBox {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentLine {
    pub width: f64,
    pub height: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentPosition {
    pub x: f64,
    pub y: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentObject {
    pub block_index: u32,
    pub width: f64,
    pub height: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentRow {
    pub block_index: u32,
    pub column_start: u32,
    pub column_count: u32,
    pub cell_spacing: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDocumentCell {
    pub block_index: u32,
    pub row_index: u32,
    pub column_start: u32,
    pub column_count: u32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentPositionedParagraph {
    pub block_index: u32,
    pub width: f64,
    pub height: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentResult {
    pub block_count: u32,
    pub line_count: u32,
    pub width: f64,
    pub height: f64,
    /// Actual content right extent, independent of ordinary allocated box widths.
    pub content_width: f64,
}
/// Original preorder forest and optional fixed shared table tracks.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeDocumentStructure<'a> {
    pub blocks: &'a [NativeDocumentBlock],
    pub rows: &'a [NativeDocumentRow],
    pub columns: &'a [f64],
    pub cells: &'a [NativeDocumentCell],
}
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeDocumentInput<'a> {
    pub structure: NativeDocumentStructure<'a>,
    pub width: f64,
    pub lines: &'a [NativeDocumentLine],
    pub objects: &'a [NativeDocumentObject],
    pub paragraphs: &'a [NativeDocumentPositionedParagraph],
    pub local_positions: &'a [NativeDocumentPosition],
}
impl NativeDocumentStructure<'_> {
    /// Resolve widths before formatting. Line ranges are deliberately ignored,
    /// as in the original first pass; all writes publish only after tree admission.
    pub fn resolve_widths_into(self, width: f64, output: &mut [NativeDocumentBox]) -> Result<()> {
        self.admit(width)?;
        if output.len() < self.blocks.len() {
            return Err(NativeDocumentError::InsufficientCapacity);
        }
        let mut candidate = filled(self.blocks.len(), NativeDocumentBox::default())?;
        let rows = rows::Rows::new(self)?;
        arrange::widths(self, width, &rows, &mut candidate)?;
        output[..candidate.len()].copy_from_slice(&candidate);
        Ok(())
    }
    fn admit(self, width: f64) -> Result<()> {
        if [
            self.blocks.len(),
            self.rows.len(),
            self.columns.len(),
            self.cells.len(),
        ]
        .into_iter()
        .any(|n| n > LIMIT)
            || !width.is_finite()
            || width < 0.0
        {
            return invalid();
        }
        Ok(())
    }
}
impl NativeDocumentInput<'_> {
    /// Complete original block/line/object/table/positioned-paragraph arrangement.
    /// Both arrays and the returned result remain unpublished on every failure.
    pub fn arrange_into(
        self,
        boxes: &mut [NativeDocumentBox],
        positions: &mut [NativeDocumentPosition],
    ) -> Result<NativeDocumentResult> {
        self.admit()?;
        if boxes.len() < self.structure.blocks.len() || positions.len() < self.lines.len() {
            return Err(NativeDocumentError::InsufficientCapacity);
        }
        let (candidate, placed, result) = arrange::arrange(self)?;
        boxes[..candidate.len()].copy_from_slice(&candidate);
        positions[..placed.len()].copy_from_slice(&placed);
        Ok(result)
    }
    fn admit(self) -> Result<()> {
        self.structure.admit(self.width)?;
        if [
            self.lines.len(),
            self.objects.len(),
            self.paragraphs.len(),
            self.local_positions.len(),
        ]
        .into_iter()
        .any(|n| n > LIMIT)
            || self.local_positions.len()
                != if self.paragraphs.is_empty() {
                    0
                } else {
                    self.lines.len()
                }
        {
            return invalid();
        }
        Ok(())
    }
}
fn invalid<T>() -> Result<T> {
    Err(NativeDocumentError::InvalidArgument)
}
fn pair(a: f64, b: f64) -> bool {
    let v = f64x2::from([a, b]);
    (v.cmp_ge(f64x2::splat(0.0)) & v.cmp_le(f64x2::splat(f64::MAX)))
        .to_array()
        .into_iter()
        .all(|v| v.to_bits() != 0)
}
fn max(a: f64, b: f64) -> f64 {
    if a < b { b } else { a }
}
fn reserve<T>(n: usize) -> Result<Vec<T>> {
    let mut v = Vec::new();
    v.try_reserve_exact(n)
        .map_err(|_| NativeDocumentError::ResourceExhausted)?;
    Ok(v)
}
fn filled<T: Clone>(n: usize, value: T) -> Result<Vec<T>> {
    let mut v = reserve(n)?;
    v.resize(n, value);
    Ok(v)
}
fn copy<T: Clone>(v: &[T]) -> Result<Vec<T>> {
    let mut result = reserve(v.len())?;
    result.extend_from_slice(v);
    Ok(result)
}
