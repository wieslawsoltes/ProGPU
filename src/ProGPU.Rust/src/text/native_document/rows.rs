//! Original shared track partitions and direct row/cell ownership admission.
use super::*;
pub(super) struct Rows<'a> {
    pub source: NativeDocumentStructure<'a>,
    row_indices: Vec<u32>,
    cell_indices: Vec<u32>,
    pub column_x: Vec<f64>,
}
impl<'a> Rows<'a> {
    pub fn row(&self, index: usize) -> Option<&NativeDocumentRow> {
        self.row_indices
            .get(index)
            .filter(|&&i| i != ROOT)
            .map(|&i| &self.source.rows[i as usize])
    }
    pub fn cell(&self, index: usize) -> Option<&NativeDocumentCell> {
        self.cell_indices
            .get(index)
            .filter(|&&i| i != ROOT)
            .map(|&i| &self.source.cells[i as usize])
    }
    pub fn track_width(&self, start: u32, count: u32) -> f64 {
        let last = (start + count - 1) as usize;
        self.column_x[last] + self.source.columns[last] - self.column_x[start as usize]
    }
    pub fn new(source: NativeDocumentStructure<'a>) -> Result<Self> {
        let mut result = Self {
            source,
            row_indices: Vec::new(),
            cell_indices: Vec::new(),
            column_x: Vec::new(),
        };
        if source.rows.is_empty() {
            if !source.cells.is_empty() || !source.columns.is_empty() {
                return invalid();
            }
            return Ok(result);
        }
        result.row_indices = filled(source.blocks.len(), ROOT)?;
        result.cell_indices = filled(source.blocks.len(), ROOT)?;
        result.column_x = filled(source.columns.len(), 0.0)?;
        let mut owners = filled(source.columns.len(), ROOT)?;
        let mut ends = filled(source.columns.len(), ROOT)?;
        let mut next = filled(source.rows.len(), 0u32)?;
        for chunk in source.columns.chunks(2) {
            if !pair(chunk[0], chunk.get(1).copied().unwrap_or(0.0)) {
                return invalid();
            }
        }
        for (i, r) in source.rows.iter().enumerate() {
            let index = r.block_index as usize;
            if index >= source.blocks.len()
                || r.column_count == 0
                || r.column_start as usize >= source.columns.len()
                || r.column_count as usize > source.columns.len() - r.column_start as usize
                || i != 0 && source.rows[i - 1].block_index >= r.block_index
                || source.blocks[index].line_count != 0
                || !pair(r.cell_spacing, 0.0)
            {
                return invalid();
            }
            result.row_indices[index] = i as u32;
            let end = r.column_start + r.column_count;
            if ends[r.column_start as usize] != ROOT {
                if ends[r.column_start as usize] != end {
                    return invalid();
                }
            } else {
                ends[r.column_start as usize] = end;
                let mut x = 0.0;
                for column in r.column_start..end {
                    let c = column as usize;
                    if owners[c] != ROOT {
                        return invalid();
                    }
                    owners[c] = r.column_start;
                    result.column_x[c] = x;
                    x += source.columns[c];
                    if !x.is_finite() {
                        return invalid();
                    }
                }
            }
            if !(result.track_width(r.column_start, r.column_count)
                + r.cell_spacing * f64::from(r.column_count))
            .is_finite()
            {
                return invalid();
            }
        }
        for (i, c) in source.cells.iter().enumerate() {
            let index = c.block_index as usize;
            if index >= source.blocks.len()
                || c.row_index as usize >= source.rows.len()
                || i != 0 && source.cells[i - 1].block_index >= c.block_index
                || result.row(index).is_some()
            {
                return invalid();
            }
            let r = source.rows[c.row_index as usize];
            if source.blocks[index].parent_index != r.block_index
                || c.column_count == 0
                || c.column_start < next[c.row_index as usize]
                || c.column_start >= r.column_count
                || c.column_count > r.column_count - c.column_start
            {
                return invalid();
            }
            next[c.row_index as usize] = c.column_start + c.column_count;
            result.cell_indices[index] = i as u32;
        }
        for (i, b) in source.blocks.iter().enumerate() {
            if b.parent_index != ROOT
                && (b.parent_index as usize) < source.blocks.len()
                && result.row(b.parent_index as usize).is_some()
                && result.cell(i).is_none()
            {
                return invalid();
            }
        }
        Ok(result)
    }
}
