//! Original legal-break pagination over already formatted source line records.
use super::*;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentFragmentLine {
    pub allow_break_before: bool,
    pub force_column_before: bool,
    pub force_page_before: bool,
    pub height: f64,
    pub space_before: f64,
    pub leading_space: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeDocumentFragmentPosition {
    pub page: u32,
    pub column: u32,
    pub y: f64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDocumentPaginationResult {
    pub line_count: u32,
    pub fragment_count: u32,
    pub page_count: u32,
}
/// Furthest-fitting legal break, respecting original forced boundaries and exact
/// local prefix differences. No emergency split or constraint relaxation occurs.
pub fn native_document_paginate_into(
    lines: &[NativeDocumentFragmentLine],
    height: f64,
    columns: u32,
    output: &mut [NativeDocumentFragmentPosition],
) -> Result<NativeDocumentPaginationResult> {
    let count = lines.len();
    if count > LIMIT || !height.is_finite() || height <= 0.0 || columns == 0 || columns > 1024 {
        return invalid();
    }
    if output.len() < count {
        return Err(NativeDocumentError::InsufficientCapacity);
    }
    let mut prefix = filled(count + 1, 0.0)?;
    let mut previous = filled(count + 1, 0usize)?;
    let mut forced = filled(count + 1, count)?;
    let mut placed = filled(count, NativeDocumentFragmentPosition::default())?;
    for (i, l) in lines.iter().enumerate() {
        if l.force_column_before && l.force_page_before
            || !pair(l.height, l.space_before)
            || l.height == 0.0
            || !pair(l.leading_space, 0.0)
        {
            return invalid();
        }
        let top = prefix[i] + l.space_before;
        prefix[i + 1] = top + l.height;
        if !prefix[i + 1].is_finite() || prefix[i + 1] <= top {
            return invalid();
        }
        previous[i] = if l.allow_break_before || l.force_column_before || l.force_page_before {
            i
        } else if i == 0 {
            0
        } else {
            previous[i - 1]
        };
    }
    previous[count] = count;
    for i in (0..count).rev() {
        forced[i] = if lines[i].force_column_before || lines[i].force_page_before {
            i
        } else {
            forced[i + 1]
        };
    }
    let (mut start, mut page, mut column, mut fragments) = (0usize, 0u32, 0u32, 0u32);
    while start < count {
        let fits = |end: usize| {
            prefix[end] - prefix[start] - lines[start].space_before
                <= height - lines[start].leading_space
        };
        let stop = forced[start + 1];
        let (mut low, mut high) = (start, stop);
        while low < high {
            let middle = low + (high - low + 1) / 2;
            if fits(middle) {
                low = middle;
            } else {
                high = middle - 1;
            }
        }
        let end = previous[low];
        if end <= start {
            return Err(NativeDocumentError::Unsupported);
        }
        for i in start..end {
            let y = lines[start].leading_space
                + (prefix[i] - prefix[start])
                + (lines[i].space_before - lines[start].space_before);
            if !y.is_finite() || y < 0.0 || y + lines[i].height > height {
                return invalid();
            }
            placed[i] = NativeDocumentFragmentPosition { page, column, y };
        }
        fragments += 1;
        start = end;
        if start < count {
            if lines[start].force_page_before || column + 1 == columns {
                page += 1;
                column = 0;
            } else {
                column += 1;
            }
        }
    }
    output[..count].copy_from_slice(&placed);
    Ok(NativeDocumentPaginationResult {
        line_count: count as u32,
        fragment_count: fragments,
        page_count: if count == 0 { 0 } else { page + 1 },
    })
}

/// Retained pagination of this exact document line inventory. Explicit source
/// keep/widow/orphan and forced-break decisions are not inferred from glyphs.
#[derive(Debug)]
pub struct NativeDocumentPagination {
    document: Arc<NativeDocumentLayout>,
    lines: Arc<[NativeDocumentFragmentLine]>,
    height: f64,
    columns: u32,
    positions: Vec<NativeDocumentFragmentPosition>,
    result: NativeDocumentPaginationResult,
}
impl NativeDocumentPagination {
    pub fn capture(
        document: Arc<NativeDocumentLayout>,
        lines: &[NativeDocumentFragmentLine],
        height: f64,
        columns: u32,
    ) -> Result<Arc<Self>> {
        if lines.len() != document.source().lines().len()
            || lines
                .iter()
                .zip(document.source().lines())
                .any(|(a, b)| a.height.to_bits() != b.height.to_bits())
        {
            return invalid();
        }
        let mut positions = filled(lines.len(), NativeDocumentFragmentPosition::default())?;
        let result = native_document_paginate_into(lines, height, columns, &mut positions)?;
        Ok(Arc::new(Self {
            document,
            lines: Arc::from(copy(lines)?),
            height,
            columns,
            positions,
            result,
        }))
    }
    pub fn repaginate(&self, height: f64, columns: u32) -> Result<Arc<Self>> {
        let mut positions = filled(self.lines.len(), NativeDocumentFragmentPosition::default())?;
        let result = native_document_paginate_into(&self.lines, height, columns, &mut positions)?;
        Ok(Arc::new(Self {
            document: self.document.clone(),
            lines: self.lines.clone(),
            height,
            columns,
            positions,
            result,
        }))
    }
    pub fn document(&self) -> &Arc<NativeDocumentLayout> {
        &self.document
    }
    pub fn lines(&self) -> &[NativeDocumentFragmentLine] {
        &self.lines
    }
    pub fn content_height(&self) -> f64 {
        self.height
    }
    pub fn columns(&self) -> u32 {
        self.columns
    }
    pub fn positions(&self) -> &[NativeDocumentFragmentPosition] {
        &self.positions
    }
    pub fn result(&self) -> NativeDocumentPaginationResult {
        self.result
    }
    pub fn copy_into(
        &self,
        lines: &mut [NativeDocumentFragmentLine],
        positions: &mut [NativeDocumentFragmentPosition],
    ) -> Result<NativeDocumentPaginationResult> {
        let count = self.lines.len();
        if lines.len() < count || positions.len() < count {
            return Err(NativeDocumentError::InsufficientCapacity);
        }
        lines[..count].copy_from_slice(&self.lines);
        positions[..count].copy_from_slice(&self.positions);
        Ok(self.result)
    }
    pub fn import(
        document: Arc<NativeDocumentLayout>,
        lines: &[NativeDocumentFragmentLine],
        height: f64,
        columns: u32,
        positions: &[NativeDocumentFragmentPosition],
        result: NativeDocumentPaginationResult,
    ) -> Result<Arc<Self>> {
        if positions.len() != lines.len() {
            return invalid();
        }
        let candidate = Self::capture(document, lines, height, columns)?;
        if candidate.result != result
            || positions.iter().zip(&candidate.positions).any(|(a, b)| {
                a.page != b.page || a.column != b.column || a.y.to_bits() != b.y.to_bits()
            })
        {
            return invalid();
        }
        Ok(candidate)
    }
}
