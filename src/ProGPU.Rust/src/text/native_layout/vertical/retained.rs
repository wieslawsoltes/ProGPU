//! Immutable native shaped-run ownership around the original vertical producers.
//! Source maps stay explicit caller inputs; this adapter never synthesizes bidi
//! or cluster coverage from nominal glyph IDs or one fabricated paragraph run.
use super::*;
use crate::text::native_pipeline::NativeRun;
use std::sync::Arc;
#[derive(Debug)]
struct Source {
    run: Arc<NativeRun>,
    breaks: Vec<LineBreakKind>,
    cluster_ends: Vec<i32>,
    levels: Vec<i8>,
}
/// Complete retained vertical generation and actual native font/shaping source.
/// Projection consumes original Y-up metrics; all public placed metrics are Y-down.
#[derive(Debug)]
pub struct NativeVerticalText {
    source: Arc<Source>,
    options: NativeTextLayoutOptions,
    glyphs: Vec<NativePositionedTextGlyph>,
    columns: Vec<NativePositionedTextColumn>,
    boxes: Vec<NativeVerticalClusterBox>,
    carets: Vec<NativeVerticalCaretStop>,
    metrics: NativeTextLayoutMetrics,
}
fn allocate<T: Clone>(n: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(n)
        .map_err(|_| FontError::Capacity)?;
    result.resize(n, value);
    Ok(result)
}
fn copy<T: Clone>(source: &[T]) -> Result<Vec<T>, FontError> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(source.len())
        .map_err(|_| FontError::Capacity)?;
    result.extend_from_slice(source);
    Ok(result)
}
impl NativeVerticalText {
    /// Retain an actual native shaped run and the complete original caller maps.
    /// Mandatory column breaks, coverage ends and full bidi levels are supplied
    /// independently, as in the original native layout/interaction contracts.
    /// The complete inventory is admitted before any new generation is published.
    pub fn from_run(
        run: Arc<NativeRun>,
        breaks: &[LineBreakKind],
        cluster_ends: &[i32],
        levels: &[i8],
        options: NativeTextLayoutOptions,
    ) -> Result<Arc<Self>, FontError> {
        let n = run.glyphs().len();
        if breaks.len() != n
            || cluster_ends.len() != n
            || levels.len() != n
            || n > u32::MAX as usize
            || cluster_ends
                .iter()
                .zip(run.glyphs())
                .any(|(&end, g)| end <= g.cluster)
            || levels.iter().any(|&v| !(0..=125).contains(&v))
        {
            return Err(FontError::InvalidArgument);
        }
        let source = Arc::new(Source {
            run,
            breaks: copy(breaks)?,
            cluster_ends: copy(cluster_ends)?,
            levels: copy(levels)?,
        });
        Self::place(source, options)
    }
    fn place(
        source: Arc<Source>,
        options: NativeTextLayoutOptions,
    ) -> Result<Arc<Self>, FontError> {
        let run = &source.run;
        if options.direction != run.route().direction {
            return Err(FontError::InvalidArgument);
        }
        let required = native_vertical_requirements(run.glyphs(), &source.breaks, options)?;
        let mut glyphs = allocate(
            required.glyph_capacity,
            NativePositionedTextGlyph::default(),
        )?;
        let mut columns = allocate(
            required.column_capacity,
            NativePositionedTextColumn::default(),
        )?;
        let mut scratch = allocate(required.glyph_capacity, NativeVerticalMetrics::default())?;
        let count = native_layout_vertical_open_type_into(
            run.font(),
            run.normalized_coordinates(),
            run.glyphs(),
            &source.breaks,
            options,
            &mut scratch,
            &mut glyphs,
            &mut columns,
        )?;
        glyphs.truncate(count[0]);
        columns.truncate(count[1]);
        drop(scratch);
        let ends = &source.cluster_ends[..count[0]];
        let levels = &source.levels[..count[0]];
        let needed = native_vertical_interaction_requirements(
            &glyphs,
            &columns,
            ends,
            levels,
            options.direction,
        )?;
        let mut boxes = allocate(needed.cluster_capacity, NativeVerticalClusterBox::default())?;
        let mut carets = allocate(needed.caret_capacity, NativeVerticalCaretStop::default())?;
        let counts = native_build_vertical_interaction_into(
            &glyphs,
            &columns,
            ends,
            levels,
            options.direction,
            &mut boxes,
            &mut carets,
        )?;
        boxes.truncate(counts[0]);
        carets.truncate(counts[1]);
        let metrics = native_measure_text_columns(&columns, options.maximum_width)?;
        Ok(Arc::new(Self {
            source,
            options,
            glyphs,
            columns,
            boxes,
            carets,
            metrics,
        }))
    }
    /// Reproject and rewrite using the identical actual run, face, normalized
    /// instance and complete source maps. Direction changes require a new shape.
    pub fn reflow(&self, options: NativeTextLayoutOptions) -> Result<Arc<Self>, FontError> {
        Self::place(self.source.clone(), options)
    }
    pub fn run(&self) -> &Arc<NativeRun> {
        &self.source.run
    }
    pub fn breaks_after(&self) -> &[LineBreakKind] {
        &self.source.breaks
    }
    pub fn cluster_ends(&self) -> &[i32] {
        &self.source.cluster_ends
    }
    pub fn bidi_levels(&self) -> &[i8] {
        &self.source.levels
    }
    pub fn options(&self) -> NativeTextLayoutOptions {
        self.options
    }
    pub fn glyphs(&self) -> &[NativePositionedTextGlyph] {
        &self.glyphs
    }
    pub fn columns(&self) -> &[NativePositionedTextColumn] {
        &self.columns
    }
    pub fn cluster_boxes(&self) -> &[NativeVerticalClusterBox] {
        &self.boxes
    }
    pub fn caret_stops(&self) -> &[NativeVerticalCaretStop] {
        &self.carets
    }
    pub fn metrics(&self) -> NativeTextLayoutMetrics {
        self.metrics
    }
    pub fn hit_test(&self, position: [f32; 2]) -> Result<NativeVerticalHit, FontError> {
        native_hit_test_vertical_text(&self.boxes, position)
    }
    pub fn caret(&self, input: i32, trailing: bool) -> Result<NativeVerticalCaretStop, FontError> {
        native_vertical_caret(&self.carets, input, trailing)
    }
    pub fn move_caret(
        &self,
        input: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<NativeVerticalCaretStop, FontError> {
        native_move_vertical_caret(&self.carets, input, trailing, direction)
    }
    pub fn selection_into(
        &self,
        start: i32,
        end: i32,
        output: &mut [NativeVerticalRectangle],
    ) -> Result<usize, FontError> {
        native_vertical_selection_into(&self.boxes, start, end, output)
    }
    /// Exact complete used-prefix copies. All output capacities are checked before
    /// the first write; no source font execution or map reconstruction occurs.
    pub fn copy_into(
        &self,
        glyphs: &mut [NativePositionedTextGlyph],
        columns: &mut [NativePositionedTextColumn],
        boxes: &mut [NativeVerticalClusterBox],
        carets: &mut [NativeVerticalCaretStop],
    ) -> Result<[usize; 4], FontError> {
        if glyphs.len() < self.glyphs.len()
            || columns.len() < self.columns.len()
            || boxes.len() < self.boxes.len()
            || carets.len() < self.carets.len()
        {
            return Err(FontError::Capacity);
        }
        glyphs[..self.glyphs.len()].copy_from_slice(&self.glyphs);
        columns[..self.columns.len()].copy_from_slice(&self.columns);
        boxes[..self.boxes.len()].copy_from_slice(&self.boxes);
        carets[..self.carets.len()].copy_from_slice(&self.carets);
        Ok([
            self.glyphs.len(),
            self.columns.len(),
            self.boxes.len(),
            self.carets.len(),
        ])
    }
}
