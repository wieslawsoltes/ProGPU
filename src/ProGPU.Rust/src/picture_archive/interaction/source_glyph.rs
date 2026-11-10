//! Authoritative source-glyph bounds and retained hinted text interaction owners.
//! Source input never derives ink from baseline arrays, atlas slots or coverage.
use super::*;
use crate::text::{
    font_policy::FontReplayGeometry,
    native_paragraph::{
        NativeCaretStop, NativeClusterBox, NativeParagraphGeometry, NativeSourceCaretStop,
        NativeSourceHit, NativeSourceInteraction, NativeSourceRectangle, NativeTextHit,
        NativeTextRectangle, native_fragment_caret, native_fragment_hit_test,
        native_fragment_move_visual_caret, native_fragment_selection_into,
        native_fragment_selection_requirements,
    },
};

/// Synchronous actual-source glyph callback. It must publish the command's
/// original ink rectangle and retain its actual source generation. Calling this
/// is not an archive upgrade or qualification of a platform source adapter.
pub trait SourceGlyphProvider {
    fn capture_source_glyph(&self) -> Result<Arc<SourceGlyphInput>>;
}
/// Original DrawGlyphRun input assertion. The private immutable pair preserves
/// the complete recorded command and an independently retained producer lease.
/// The constructor is an explicit source contract: decoded archive bounds are
/// not automatically promoted to authoritative source geometry.
#[derive(Clone, Debug)]
pub struct SourceGlyphInput {
    command: Arc<CapturedHitCommand>,
    owner: Arc<dyn SourceInputOwner>,
}
impl SourceGlyphInput {
    pub fn new(command: Arc<CapturedHitCommand>, owner: Arc<dyn SourceInputOwner>) -> Result<Self> {
        let result = Self { command, owner };
        result.admit()?;
        Ok(result)
    }
    pub fn command(&self) -> &Arc<CapturedHitCommand> {
        &self.command
    }
    pub fn owner(&self) -> &Arc<dyn SourceInputOwner> {
        &self.owner
    }
    /// Original x/y/width/height. No final raster or advance bounds are queried.
    pub fn ink_bounds(&self) -> [f32; 4] {
        self.command.raster.rect
    }
    pub(super) fn admit(&self) -> Result<()> {
        let c = &self.command.raster;
        // Preserve original Rect.IsEmpty comparisons. Subsequent primitive
        // admission owns nonfinite transformed bounds; no bounds are repaired.
        if c.kind != K::DrawGlyphRun || c.rect[2] <= 0.0 || c.rect[3] <= 0.0 {
            return Err(ArchiveError::Unsupported);
        }
        Ok(())
    }
}
impl ArchiveHitTestCache {
    /// Bind the actual authoritative glyph generation to the immutable index.
    pub fn add_source_glyph(
        &mut self,
        source: Arc<SourceGlyphInput>,
        transform: Matrix4,
        id: Option<i32>,
    ) -> Result<()> {
        self.source_capture_operation(|cache| {
            source.admit()?;
            push(&mut cache.source_glyphs, source.clone())?;
            cache.add_captured_command(source.command.clone(), transform, id)
        })
    }

    pub fn capture_source_glyph(
        &mut self,
        provider: &dyn SourceGlyphProvider,
        transform: Matrix4,
        id: Option<i32>,
    ) -> Result<()> {
        self.source_capture_operation(|cache| {
            let source = provider.capture_source_glyph()?;
            cache.add_source_glyph(source, transform, id)
        })
    }
}

/// The original hinted renderer does not publish source hit primitives. This
/// binding retains the originating complete paragraph interaction generation
/// alongside the selected raster view, without inventing glyph ink input.
/// Imported resources retain their validated records, not a live provider claim.
#[derive(Debug)]
pub struct HintedTextInput {
    replay: Arc<FontReplayGeometry>,
    paragraph: Arc<NativeParagraphGeometry>,
    boxes: Arc<[NativeClusterBox]>,
    carets: Arc<[NativeCaretStop]>,
}
impl HintedTextInput {
    /// Bind only an actual paragraph-backed generation. Metadata-free captured
    /// batches, nominal glyph placements and single-run shaping without a
    /// retained interaction generation remain unavailable for this operation.
    /// A selected view still retains all no-ink slots and its complete original
    /// paragraph; selecting painted occurrences does not prune source input.
    pub fn from_replay(replay: Arc<FontReplayGeometry>) -> Result<Self> {
        if !replay.has_shaping_metadata() || replay.paragraph_occurrences().is_none() {
            return Err(ArchiveError::Unsupported);
        }
        let paragraph = replay
            .paragraph_source_geometry()
            .cloned()
            .ok_or(ArchiveError::Unsupported)?;
        let mut boxes = Vec::new();
        let mut carets = Vec::new();
        if paragraph.source_interaction().is_none() {
            // These are complete already-admitted original resource records.
            // Copy exact values; never reconstruct them from outline bounds or
            // selected raster occurrences. The original raw-float and source-
            // double routes remain separate even when values happen to agree.
            let records = paragraph.records();
            reserve(&mut boxes, records.boxes.len())?;
            reserve(&mut carets, records.carets.len())?;
            for b in &records.boxes {
                boxes.push(NativeClusterBox {
                    input_start: b.input_start,
                    input_end: b.input_end,
                    line_index: b.line_index,
                    bidi_level: b.bidi_level,
                    bounds: NativeTextRectangle {
                        x: b.x,
                        y: b.y,
                        width: b.width,
                        height: b.height,
                    },
                });
            }
            for c in &records.carets {
                carets.push(NativeCaretStop {
                    input_position: c.input_position,
                    line_index: c.line_index,
                    position: [c.x, c.y],
                    height: c.height,
                    bidi_level: c.bidi_level,
                    trailing: c.trailing != 0,
                });
            }
        }
        Ok(Self {
            replay,
            paragraph,
            boxes: boxes.into(),
            carets: carets.into(),
        })
    }
    pub fn replay(&self) -> &Arc<FontReplayGeometry> {
        &self.replay
    }
    pub fn paragraph_geometry(&self) -> &Arc<NativeParagraphGeometry> {
        &self.paragraph
    }
    pub fn is_imported(&self) -> bool {
        self.paragraph.is_imported()
    }
    /// Original double/ideal-unit interaction, never narrowed to raster floats.
    pub fn source_interaction(&self) -> Option<NativeSourceInteraction<'_>> {
        self.paragraph.source_interaction()
    }
    pub fn float_boxes(&self) -> Result<&[NativeClusterBox]> {
        self.require_float()?;
        Ok(&self.boxes)
    }
    pub fn float_carets(&self) -> Result<&[NativeCaretStop]> {
        self.require_float()?;
        Ok(&self.carets)
    }
    fn require_float(&self) -> Result<()> {
        if self.source_interaction().is_some() {
            Err(ArchiveError::Unsupported)
        } else {
            Ok(())
        }
    }
    /// Input coordinates belong to the originating paragraph, before any
    /// caller-owned source/world transform. No inverse raster frame is inferred.
    pub fn hit_test_float(&self, point: [f32; 2]) -> Result<NativeTextHit> {
        self.require_float()?;
        Ok(native_fragment_hit_test(&self.boxes, point[0], point[1])?)
    }
    pub fn caret_float(&self, input: i32, trailing: bool) -> Result<NativeCaretStop> {
        self.require_float()?;
        Ok(native_fragment_caret(&self.carets, input, trailing)?)
    }
    pub fn move_caret_float(
        &self,
        input: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<NativeCaretStop> {
        self.require_float()?;
        Ok(native_fragment_move_visual_caret(
            &self.carets,
            input,
            trailing,
            direction,
        )?)
    }
    pub fn selection_float_into(
        &self,
        start: i32,
        end: i32,
        output: &mut [NativeTextRectangle],
    ) -> Result<u32> {
        self.require_float()?;
        Ok(native_fragment_selection_into(
            &self.boxes,
            start,
            end,
            output,
        )?)
    }
    pub fn selection_float(&self, start: i32, end: i32) -> Result<Vec<NativeTextRectangle>> {
        self.require_float()?;
        let count = native_fragment_selection_requirements(&self.boxes, start, end)? as usize;
        let mut output = Vec::new();
        reserve(&mut output, count)?;
        output.resize(count, NativeTextRectangle::default());
        native_fragment_selection_into(&self.boxes, start, end, &mut output)?;
        Ok(output)
    }
    pub fn hit_test_source(&self, point: [f64; 2]) -> Result<NativeSourceHit> {
        Ok(self
            .source_interaction()
            .ok_or(ArchiveError::Unsupported)?
            .hit_test(point[0], point[1])?)
    }
    pub fn caret_source(&self, input: i32, trailing: bool) -> Result<NativeSourceCaretStop> {
        Ok(self
            .source_interaction()
            .ok_or(ArchiveError::Unsupported)?
            .caret(input, trailing)?)
    }
    pub fn move_caret_source(
        &self,
        input: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<NativeSourceCaretStop> {
        Ok(self
            .source_interaction()
            .ok_or(ArchiveError::Unsupported)?
            .move_caret(input, trailing, direction)?)
    }
    pub fn selection_source_into(
        &self,
        start: i32,
        end: i32,
        output: &mut [NativeSourceRectangle],
    ) -> Result<usize> {
        Ok(self
            .source_interaction()
            .ok_or(ArchiveError::Unsupported)?
            .selection_into(start, end, output)?)
    }
    /// No hit-test index builder accepts this as DrawHintedGlyphs. Applications
    /// explicitly query the owning source generation through the methods above.
    pub fn retain(self: &Arc<Self>) -> Arc<dyn SourceInputOwner> {
        self.clone()
    }
}
