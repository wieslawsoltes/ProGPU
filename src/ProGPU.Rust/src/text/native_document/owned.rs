//! Immutable Rust ownership around the original borrowed document-flow contract.
//! Formatting, local paragraph placement and source interaction stay caller-owned.
use super::*;

/// The actual formatted generation; all font, run, style, bidi and source leases
/// remain in the selected paragraph. No shaping record is reconstructed here.
#[derive(Clone, Debug)]
pub enum NativeDocumentParagraphOwner {
    Design(Arc<NativeDesignParagraph>),
    Fragments(Arc<NativeFragmentParagraph>),
    Hinted(Arc<NativeHintedParagraph>),
    Source(Arc<NativeSourceParagraph>),
}
impl NativeDocumentParagraphOwner {
    pub fn line_count(&self) -> usize {
        match self {
            Self::Design(p) => p.lines().len(),
            Self::Fragments(p) => p.lines().len(),
            Self::Hinted(p) => p.lines().len(),
            Self::Source(p) => p.lines().len(),
        }
    }
    pub fn line(&self, index: usize) -> Option<NativeDocumentLine> {
        let line = match self {
            Self::Design(p) => p.lines().get(index),
            Self::Fragments(p) => p.lines().get(index),
            Self::Hinted(p) => p.lines().get(index),
            Self::Source(p) => {
                return p.lines().get(index).map(|l| NativeDocumentLine {
                    width: l.width,
                    height: l.height,
                });
            }
        }?;
        Some(NativeDocumentLine {
            width: f64::from(line.width),
            height: f64::from(line.height),
        })
    }
}
/// Optional typed source binding. The complete document line slice must match
/// this actual generation. Explicit document-local positions remain independent:
/// the original native arranger consumes source-resolved positions, not glyphs.
#[derive(Clone, Debug)]
pub struct NativeDocumentParagraphBinding {
    pub block_index: u32,
    pub paragraph: NativeDocumentParagraphOwner,
}
#[derive(Debug)]
pub struct NativeDocumentSource {
    blocks: Vec<NativeDocumentBlock>,
    rows: Vec<NativeDocumentRow>,
    columns: Vec<f64>,
    cells: Vec<NativeDocumentCell>,
    lines: Vec<NativeDocumentLine>,
    objects: Vec<NativeDocumentObject>,
    paragraphs: Vec<NativeDocumentPositionedParagraph>,
    local_positions: Vec<NativeDocumentPosition>,
    bindings: Vec<NativeDocumentParagraphBinding>,
}
impl NativeDocumentSource {
    pub fn structure(&self) -> NativeDocumentStructure<'_> {
        NativeDocumentStructure {
            blocks: &self.blocks,
            rows: &self.rows,
            columns: &self.columns,
            cells: &self.cells,
        }
    }
    pub fn input(&self, width: f64) -> NativeDocumentInput<'_> {
        NativeDocumentInput {
            structure: self.structure(),
            width,
            lines: &self.lines,
            objects: &self.objects,
            paragraphs: &self.paragraphs,
            local_positions: &self.local_positions,
        }
    }
    pub fn lines(&self) -> &[NativeDocumentLine] {
        &self.lines
    }
    pub fn objects(&self) -> &[NativeDocumentObject] {
        &self.objects
    }
    pub fn paragraphs(&self) -> &[NativeDocumentPositionedParagraph] {
        &self.paragraphs
    }
    pub fn local_positions(&self) -> &[NativeDocumentPosition] {
        &self.local_positions
    }
    pub fn bindings(&self) -> &[NativeDocumentParagraphBinding] {
        &self.bindings
    }
    fn capture(
        input: NativeDocumentInput<'_>,
        bindings: &[NativeDocumentParagraphBinding],
    ) -> Result<Arc<Self>> {
        admit_bindings(input, bindings)?;
        Ok(Arc::new(Self {
            blocks: copy(input.structure.blocks)?,
            rows: copy(input.structure.rows)?,
            columns: copy(input.structure.columns)?,
            cells: copy(input.structure.cells)?,
            lines: copy(input.lines)?,
            objects: copy(input.objects)?,
            paragraphs: copy(input.paragraphs)?,
            local_positions: copy(input.local_positions)?,
            bindings: copy(bindings)?,
        }))
    }
}
/// One atomic original arrangement and all actual source owners that produced it.
#[derive(Debug)]
pub struct NativeDocumentLayout {
    source: Arc<NativeDocumentSource>,
    width: f64,
    boxes: Vec<NativeDocumentBox>,
    positions: Vec<NativeDocumentPosition>,
    result: NativeDocumentResult,
}
impl NativeDocumentLayout {
    pub fn capture(
        input: NativeDocumentInput<'_>,
        bindings: &[NativeDocumentParagraphBinding],
    ) -> Result<Arc<Self>> {
        let (boxes, positions, result) = arrange::arrange(input)?;
        let source = NativeDocumentSource::capture(input, bindings)?;
        Ok(Arc::new(Self {
            source,
            width: input.width,
            boxes,
            positions,
            result,
        }))
    }
    /// Repositions the same already formatted source at a new document width.
    /// The caller must resolve widths and format new paragraphs before `capture`
    /// when text reflow is wanted. No hidden reshaping or font callback occurs.
    pub fn rearrange(&self, width: f64) -> Result<Arc<Self>> {
        let (boxes, positions, result) = arrange::arrange(self.source.input(width))?;
        Ok(Arc::new(Self {
            source: self.source.clone(),
            width,
            boxes,
            positions,
            result,
        }))
    }
    pub fn source(&self) -> &Arc<NativeDocumentSource> {
        &self.source
    }
    pub fn width(&self) -> f64 {
        self.width
    }
    pub fn boxes(&self) -> &[NativeDocumentBox] {
        &self.boxes
    }
    pub fn positions(&self) -> &[NativeDocumentPosition] {
        &self.positions
    }
    pub fn result(&self) -> NativeDocumentResult {
        self.result
    }
    /// Original block ownership maps a complete source line range. Positions
    /// include the document content offset, including all parent margins/insets.
    pub fn block_lines(
        &self,
        block: usize,
    ) -> Option<(&[NativeDocumentLine], &[NativeDocumentPosition])> {
        let b = self.source.blocks.get(block)?;
        let range = b.line_start as usize..(b.line_start + b.line_count) as usize;
        Some((&self.source.lines[range.clone()], &self.positions[range]))
    }
    pub fn requirements(&self) -> NativeDocumentRequirements {
        NativeDocumentRequirements {
            blocks: self.source.blocks.len(),
            rows: self.source.rows.len(),
            columns: self.source.columns.len(),
            cells: self.source.cells.len(),
            lines: self.source.lines.len(),
            objects: self.source.objects.len(),
            paragraphs: self.source.paragraphs.len(),
            local_positions: self.source.local_positions.len(),
            bindings: self.source.bindings.len(),
        }
    }
    /// Copies complete original records and real paragraph leases. All output
    /// capacities are admitted before the first write, and spare tails survive.
    pub fn copy_into(&self, output: NativeDocumentBuffers<'_>) -> Result<NativeDocumentResult> {
        let n = self.requirements();
        if output.blocks.len() < n.blocks
            || output.boxes.len() < n.blocks
            || output.rows.len() < n.rows
            || output.columns.len() < n.columns
            || output.cells.len() < n.cells
            || output.lines.len() < n.lines
            || output.positions.len() < n.lines
            || output.objects.len() < n.objects
            || output.paragraphs.len() < n.paragraphs
            || output.local_positions.len() < n.local_positions
            || output.bindings.len() < n.bindings
        {
            return Err(NativeDocumentError::InsufficientCapacity);
        }
        output.blocks[..n.blocks].copy_from_slice(&self.source.blocks);
        output.boxes[..n.blocks].copy_from_slice(&self.boxes);
        output.rows[..n.rows].copy_from_slice(&self.source.rows);
        output.columns[..n.columns].copy_from_slice(&self.source.columns);
        output.cells[..n.cells].copy_from_slice(&self.source.cells);
        output.lines[..n.lines].copy_from_slice(&self.source.lines);
        output.positions[..n.lines].copy_from_slice(&self.positions);
        output.objects[..n.objects].copy_from_slice(&self.source.objects);
        output.paragraphs[..n.paragraphs].copy_from_slice(&self.source.paragraphs);
        output.local_positions[..n.local_positions].copy_from_slice(&self.source.local_positions);
        for (out, binding) in output.bindings.iter_mut().zip(&self.source.bindings) {
            *out = Some(binding.clone());
        }
        Ok(self.result)
    }
    /// Imports a complete record snapshot against the original source arranger.
    /// Every output double must retain its exact bits, including signed zero.
    /// This is an in-process owned snapshot, not a native ABI/archive version.
    pub fn import(
        input: NativeDocumentInput<'_>,
        bindings: &[NativeDocumentParagraphBinding],
        boxes: &[NativeDocumentBox],
        positions: &[NativeDocumentPosition],
        result: NativeDocumentResult,
    ) -> Result<Arc<Self>> {
        if boxes.len() != input.structure.blocks.len() || positions.len() != input.lines.len() {
            return invalid();
        }
        let (expected_boxes, expected_positions, expected_result) = arrange::arrange(input)?;
        if !result_same(result, expected_result)
            || !boxes
                .iter()
                .zip(&expected_boxes)
                .all(|(a, b)| box_same(*a, *b))
            || !positions
                .iter()
                .zip(&expected_positions)
                .all(|(a, b)| position_same(*a, *b))
        {
            return invalid();
        }
        let source = NativeDocumentSource::capture(input, bindings)?;
        Ok(Arc::new(Self {
            source,
            width: input.width,
            boxes: expected_boxes,
            positions: expected_positions,
            result: expected_result,
        }))
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDocumentRequirements {
    pub blocks: usize,
    pub rows: usize,
    pub columns: usize,
    pub cells: usize,
    pub lines: usize,
    pub objects: usize,
    pub paragraphs: usize,
    pub local_positions: usize,
    pub bindings: usize,
}
/// The complete flat document source/result inventory. Optional slots allow
/// callers to allocate lease output without inventing a placeholder paragraph.
#[derive(Debug)]
pub struct NativeDocumentBuffers<'a> {
    pub blocks: &'a mut [NativeDocumentBlock],
    pub boxes: &'a mut [NativeDocumentBox],
    pub rows: &'a mut [NativeDocumentRow],
    pub columns: &'a mut [f64],
    pub cells: &'a mut [NativeDocumentCell],
    pub lines: &'a mut [NativeDocumentLine],
    pub positions: &'a mut [NativeDocumentPosition],
    pub objects: &'a mut [NativeDocumentObject],
    pub paragraphs: &'a mut [NativeDocumentPositionedParagraph],
    pub local_positions: &'a mut [NativeDocumentPosition],
    pub bindings: &'a mut [Option<NativeDocumentParagraphBinding>],
}
fn admit_bindings(
    input: NativeDocumentInput<'_>,
    bindings: &[NativeDocumentParagraphBinding],
) -> Result<()> {
    if bindings.len() > LIMIT {
        return invalid();
    }
    for (index, binding) in bindings.iter().enumerate() {
        let block = input
            .structure
            .blocks
            .get(binding.block_index as usize)
            .ok_or(NativeDocumentError::InvalidArgument)?;
        if index != 0 && bindings[index - 1].block_index >= binding.block_index
            || block.subtree_end != binding.block_index + 1
            || block.line_count as usize != binding.paragraph.line_count()
        {
            return invalid();
        }
        for i in 0..block.line_count as usize {
            let actual = binding
                .paragraph
                .line(i)
                .ok_or(NativeDocumentError::InvalidArgument)?;
            let original = input.lines[block.line_start as usize + i];
            if !same(actual.width, original.width) || !same(actual.height, original.height) {
                return invalid();
            }
        }
    }
    Ok(())
}
fn same(a: f64, b: f64) -> bool {
    a.to_bits() == b.to_bits()
}
fn position_same(a: NativeDocumentPosition, b: NativeDocumentPosition) -> bool {
    same(a.x, b.x) && same(a.y, b.y)
}
fn box_same(a: NativeDocumentBox, b: NativeDocumentBox) -> bool {
    same(a.x, b.x) && same(a.y, b.y) && same(a.width, b.width) && same(a.height, b.height)
}
fn result_same(a: NativeDocumentResult, b: NativeDocumentResult) -> bool {
    a.block_count == b.block_count
        && a.line_count == b.line_count
        && same(a.width, b.width)
        && same(a.height, b.height)
        && same(a.content_width, b.content_width)
}
