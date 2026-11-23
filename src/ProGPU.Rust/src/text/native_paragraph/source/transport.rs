//! Additive version-two source resource transport and exact original witnesses.
use super::*;
mod admission;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSourcePositioningRun {
    pub prepared_start: u32,
    pub prepared_count: u32,
    pub reserved: [u32; 2],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSourcePositioningSlice {
    pub run_index: u32,
    pub prepared_start: u32,
    pub prepared_count: u32,
    pub logical_start: u32,
    pub logical_count: u32,
    pub reserved: u32,
}
/// Independent source-v2 metadata; typed options exclude unknown ABI enum values.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeSourceResourceMetadata {
    pub version: u32,
    pub flags: u32,
    pub reserved: u32,
    pub first_logical_glyph: u32,
    pub options: NativeSourceOptions,
    pub intrinsic_widths: NativeSourceIntrinsicWidths,
}
/// Complete original source extension. Import admits this together with every
/// base font, run, descriptor, outline and source occurrence record.
#[derive(Debug)]
pub struct NativeSourceResource {
    pub metadata: NativeSourceResourceMetadata,
    pub styles: Vec<NativeSourceStyle>,
    pub logical_metrics: Vec<NativeSourceGlyphMetrics>,
    pub glyph_metrics: Vec<NativeSourceGlyphPosition>,
    pub line_metrics: Vec<NativeSourceLineMetrics>,
    pub boxes: Vec<NativeSourceClusterBox>,
    pub carets: Vec<NativeSourceCaretStop>,
    pub raw_logical_glyphs: Vec<ShapingGlyph>,
    pub effective_logical_glyphs: Vec<ShapingGlyph>,
    pub positioning_runs: Vec<NativeSourcePositioningRun>,
    pub prepared_glyphs: Vec<ShapingGlyph>,
    pub slices: Vec<NativeSourcePositioningSlice>,
    pub slice_indices: Vec<u32>,
    pub fitted_lines: Vec<NativeSourceFittedLine>,
    pub breaks_after: Vec<LineBreakKind>,
}
/// Safe disjoint spans replace original full-capacity output alias checks.
pub struct NativeSourceResourceBuffers<'a> {
    pub styles: &'a mut [NativeSourceStyle],
    pub logical_metrics: &'a mut [NativeSourceGlyphMetrics],
    pub glyph_metrics: &'a mut [NativeSourceGlyphPosition],
    pub line_metrics: &'a mut [NativeSourceLineMetrics],
    pub boxes: &'a mut [NativeSourceClusterBox],
    pub carets: &'a mut [NativeSourceCaretStop],
    pub raw_logical_glyphs: &'a mut [ShapingGlyph],
    pub effective_logical_glyphs: &'a mut [ShapingGlyph],
    pub positioning_runs: &'a mut [NativeSourcePositioningRun],
    pub prepared_glyphs: &'a mut [ShapingGlyph],
    pub slices: &'a mut [NativeSourcePositioningSlice],
    pub slice_indices: &'a mut [u32],
    pub fitted_lines: &'a mut [NativeSourceFittedLine],
    pub breaks_after: &'a mut [LineBreakKind],
}
impl NativeSourceResource {
    fn empty(metadata: NativeSourceResourceMetadata) -> Self {
        Self {
            metadata,
            styles: Vec::new(),
            logical_metrics: Vec::new(),
            glyph_metrics: Vec::new(),
            line_metrics: Vec::new(),
            boxes: Vec::new(),
            carets: Vec::new(),
            raw_logical_glyphs: Vec::new(),
            effective_logical_glyphs: Vec::new(),
            positioning_runs: Vec::new(),
            prepared_glyphs: Vec::new(),
            slices: Vec::new(),
            slice_indices: Vec::new(),
            fitted_lines: Vec::new(),
            breaks_after: Vec::new(),
        }
    }
    pub fn try_copy(&self) -> Result<Self, NativeSourceError> {
        Ok(Self {
            metadata: self.metadata,
            styles: copy(&self.styles)?,
            logical_metrics: copy(&self.logical_metrics)?,
            glyph_metrics: copy(&self.glyph_metrics)?,
            line_metrics: copy(&self.line_metrics)?,
            boxes: copy(&self.boxes)?,
            carets: copy(&self.carets)?,
            raw_logical_glyphs: copy(&self.raw_logical_glyphs)?,
            effective_logical_glyphs: copy(&self.effective_logical_glyphs)?,
            positioning_runs: copy(&self.positioning_runs)?,
            prepared_glyphs: copy(&self.prepared_glyphs)?,
            slices: copy(&self.slices)?,
            slice_indices: copy(&self.slice_indices)?,
            fitted_lines: copy(&self.fitted_lines)?,
            breaks_after: copy(&self.breaks_after)?,
        })
    }
    pub(crate) fn preflight_copy(
        &self,
        out: &NativeSourceResourceBuffers<'_>,
    ) -> Result<(), NativeSourceError> {
        if out.styles.len() < self.styles.len()
            || out.logical_metrics.len() < self.logical_metrics.len()
            || out.glyph_metrics.len() < self.glyph_metrics.len()
            || out.line_metrics.len() < self.line_metrics.len()
            || out.boxes.len() < self.boxes.len()
            || out.carets.len() < self.carets.len()
            || out.raw_logical_glyphs.len() < self.raw_logical_glyphs.len()
            || out.effective_logical_glyphs.len() < self.effective_logical_glyphs.len()
            || out.positioning_runs.len() < self.positioning_runs.len()
            || out.prepared_glyphs.len() < self.prepared_glyphs.len()
            || out.slices.len() < self.slices.len()
            || out.slice_indices.len() < self.slice_indices.len()
            || out.fitted_lines.len() < self.fitted_lines.len()
            || out.breaks_after.len() < self.breaks_after.len()
        {
            return Err(FontError::Capacity.into());
        }
        Ok(())
    }
    /// Preserve unused tails and publish metadata after every complete array.
    pub fn copy_into(
        &self,
        out: NativeSourceResourceBuffers<'_>,
    ) -> Result<NativeSourceResourceMetadata, NativeSourceError> {
        self.preflight_copy(&out)?;
        out.styles[..self.styles.len()].copy_from_slice(&self.styles);
        out.logical_metrics[..self.logical_metrics.len()].copy_from_slice(&self.logical_metrics);
        out.glyph_metrics[..self.glyph_metrics.len()].copy_from_slice(&self.glyph_metrics);
        out.line_metrics[..self.line_metrics.len()].copy_from_slice(&self.line_metrics);
        out.boxes[..self.boxes.len()].copy_from_slice(&self.boxes);
        out.carets[..self.carets.len()].copy_from_slice(&self.carets);
        out.raw_logical_glyphs[..self.raw_logical_glyphs.len()]
            .copy_from_slice(&self.raw_logical_glyphs);
        out.effective_logical_glyphs[..self.effective_logical_glyphs.len()]
            .copy_from_slice(&self.effective_logical_glyphs);
        out.positioning_runs[..self.positioning_runs.len()].copy_from_slice(&self.positioning_runs);
        out.prepared_glyphs[..self.prepared_glyphs.len()].copy_from_slice(&self.prepared_glyphs);
        out.slices[..self.slices.len()].copy_from_slice(&self.slices);
        out.slice_indices[..self.slice_indices.len()].copy_from_slice(&self.slice_indices);
        out.fitted_lines[..self.fitted_lines.len()].copy_from_slice(&self.fitted_lines);
        out.breaks_after[..self.breaks_after.len()].copy_from_slice(&self.breaks_after);
        Ok(self.metadata)
    }
    pub(super) fn storage_bytes(&self) -> Option<usize> {
        let mut bytes = std::mem::size_of::<Self>();
        bytes = bytes.checked_add(
            self.styles
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceStyle>())?,
        )?;
        bytes = bytes.checked_add(
            self.logical_metrics
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceGlyphMetrics>())?,
        )?;
        bytes = bytes.checked_add(
            self.glyph_metrics
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceGlyphPosition>())?,
        )?;
        bytes = bytes.checked_add(
            self.line_metrics
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceLineMetrics>())?,
        )?;
        bytes = bytes.checked_add(
            self.boxes
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceClusterBox>())?,
        )?;
        bytes = bytes.checked_add(
            self.carets
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceCaretStop>())?,
        )?;
        bytes = bytes.checked_add(
            self.raw_logical_glyphs
                .len()
                .checked_mul(std::mem::size_of::<ShapingGlyph>())?,
        )?;
        bytes = bytes.checked_add(
            self.effective_logical_glyphs
                .len()
                .checked_mul(std::mem::size_of::<ShapingGlyph>())?,
        )?;
        bytes = bytes.checked_add(
            self.positioning_runs
                .len()
                .checked_mul(std::mem::size_of::<NativeSourcePositioningRun>())?,
        )?;
        bytes = bytes.checked_add(
            self.prepared_glyphs
                .len()
                .checked_mul(std::mem::size_of::<ShapingGlyph>())?,
        )?;
        bytes = bytes.checked_add(
            self.slices
                .len()
                .checked_mul(std::mem::size_of::<NativeSourcePositioningSlice>())?,
        )?;
        bytes = bytes.checked_add(
            self.slice_indices
                .len()
                .checked_mul(std::mem::size_of::<u32>())?,
        )?;
        bytes = bytes.checked_add(
            self.fitted_lines
                .len()
                .checked_mul(std::mem::size_of::<NativeSourceFittedLine>())?,
        )?;
        bytes = bytes.checked_add(
            self.breaks_after
                .len()
                .checked_mul(std::mem::size_of::<LineBreakKind>())?,
        )?;
        Some(bytes)
    }
}

fn wire(mut g: ShapingGlyph) -> Result<ShapingGlyph, NativeSourceError> {
    g.advance_y = g
        .advance_y
        .checked_neg()
        .ok_or(NativeSourceError::InvalidArgument)?;
    g.offset_y = g
        .offset_y
        .checked_neg()
        .ok_or(NativeSourceError::InvalidArgument)?;
    Ok(g)
}
impl NativeSourceResource {
    pub fn from_paragraph(p: &NativeSourceParagraph) -> Result<Self, NativeSourceError> {
        fitting::admit(p)?;
        let s = &p.raster.source;
        let f = &p.fitting;
        let n = s.logical.len();
        require(
            n <= 1 << 24
                && s.runs.len() <= 1 << 24
                && f.slices.len() <= 1 << 24
                && f.lines.len() == p.lines.len(),
        )?;
        let metadata = NativeSourceResourceMetadata {
            version: 2,
            flags: 0,
            reserved: 0,
            first_logical_glyph: f.first as u32,
            options: p.options,
            intrinsic_widths: p.intrinsic,
        };
        let mut result = Self::empty(metadata);
        result.styles = copy(&p.styles)?;
        result.logical_metrics = copy(&f.metrics)?;
        result.glyph_metrics = copy(&p.glyphs)?;
        result.line_metrics = copy(&p.lines)?;
        result.boxes = copy(&p.boxes)?;
        result.carets = copy(&p.carets)?;
        result.raw_logical_glyphs = reserve(n)?;
        result.effective_logical_glyphs = reserve(n)?;
        result.slice_indices = copy(&f.slice_indices)?;
        result.breaks_after = copy(&s.breaks)?;
        result.slices = alloc(f.slices.len(), NativeSourcePositioningSlice::default())?;
        let mut starts = alloc(f.slices.len(), u32::MAX)?;
        let mut counts = alloc(f.slices.len(), 0u32)?;
        for i in 0..n {
            let owner = s.owners[i];
            let raw = wire(
                s.runs[owner.run_index as usize].generation.glyphs()
                    [owner.run_glyph_index as usize],
            )?;
            result.raw_logical_glyphs.push(raw);
            let si = f.slice_indices[i];
            let effective = if si == u32::MAX {
                raw
            } else {
                let si = si as usize;
                let slice = &f.slices[si];
                let gi = f.slice_glyph_indices[i] as usize;
                require(gi < slice.placement.glyphs().len())?;
                if starts[si] == u32::MAX {
                    starts[si] = i as u32;
                }
                require(i == starts[si] as usize + counts[si] as usize)?;
                counts[si] += 1;
                wire(slice.placement.glyphs()[gi])?
            };
            result.effective_logical_glyphs.push(effective);
        }
        result.positioning_runs = reserve(s.runs.len())?;
        result.prepared_glyphs = reserve(n)?;
        for run in &s.runs {
            let first = result.prepared_glyphs.len();
            let prepared = run.generation.prepared_glyphs();
            require(
                prepared.len() <= n - first
                    && (prepared.is_empty()
                        || prepared.len()
                            == (run.logical_range.end - run.logical_range.start) as usize),
            )?;
            for &g in prepared {
                result.prepared_glyphs.push(wire(g)?);
            }
            result.positioning_runs.push(NativeSourcePositioningRun {
                prepared_start: first as u32,
                prepared_count: prepared.len() as u32,
                reserved: [0; 2],
            });
        }
        for (i, slice) in f.slices.iter().enumerate() {
            let prepared = slice.placement.prepared_range();
            require(starts[i] != u32::MAX && counts[i] == prepared.end - prepared.start)?;
            result.slices[i] = NativeSourcePositioningSlice {
                run_index: slice.run_index,
                prepared_start: prepared.start,
                prepared_count: prepared.end - prepared.start,
                logical_start: starts[i],
                logical_count: counts[i],
                reserved: 0,
            };
        }
        result.fitted_lines = copy(&f.lines)?;
        Ok(result)
    }
    pub(crate) fn admit(&self, base: &NativeParagraphResource) -> Result<(), NativeSourceError> {
        admission::admit_records(base, self)
    }
    pub(crate) fn interaction(&self) -> NativeSourceInteraction<'_> {
        NativeSourceInteraction {
            boxes: &self.boxes,
            carets: &self.carets,
            lines: &self.line_metrics,
        }
    }
    /// Admit the complete paired base/source resource without executing fonts,
    /// then retain independent immutable copies through the common geometry owner.
    pub fn import_geometry(
        &self,
        base: &NativeParagraphResource,
    ) -> Result<NativeParagraphGeometry, NativeSourceError> {
        NativeParagraphGeometry::import_source(base, self)
    }
}
