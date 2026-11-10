//! Immutable interaction retains the actual fragment/source/run generation.
use super::*;

/// Complete fragment interaction generation. The original native API owns no
/// implicit cluster-end or L1-level producer; those exact caller arrays remain
/// explicit, copied and paired with this paragraph's positioned occurrences.
#[derive(Debug)]
pub struct NativeFragmentInteraction {
    paragraph: Arc<NativeFragmentParagraph>,
    cluster_ends: Vec<i32>,
    bidi_levels: Vec<i8>,
    origins: Option<Vec<f32>>,
    boxes: Vec<NativeClusterBox>,
    carets: Vec<NativeCaretStop>,
}

fn input<'a>(
    paragraph: &'a NativeFragmentParagraph,
    cluster_ends: &'a [i32],
    bidi_levels: &'a [i8],
    coordinates: NativeFragmentCoordinates<'a>,
) -> NativeFragmentInteractionInput<'a> {
    NativeFragmentInteractionInput {
        glyphs: paragraph.glyphs(),
        lines: paragraph.lines(),
        placements: paragraph.placements(),
        cluster_ends,
        bidi_levels,
        coordinates,
    }
}

fn admit_owner(p: &NativeFragmentParagraph) -> Result<(), FontError> {
    let s = p.source();
    if p.owners().len() != p.glyphs().len()
        || s.logical_glyphs().len() != s.logical_owners().len()
        || s.logical_glyphs().len() != s.logical_font_indices().len()
    {
        return Err(FontError::InvalidArgument);
    }
    for (g, owner) in p.glyphs().iter().zip(p.owners()) {
        let index = g.glyph_index as usize;
        let logical = s
            .logical_glyphs()
            .get(index)
            .ok_or(FontError::InvalidArgument)?;
        if s.logical_owners()[index] != *owner
            || s.logical_font_indices()[index] != g.font_index
            || logical.glyph_id != g.glyph_id
            || logical.cluster != g.cluster
        {
            return Err(FontError::InvalidArgument);
        }
        if let NativeDesignOccurrence::Glyph {
            run_index,
            run_glyph_index,
        } = *owner
        {
            let run = s
                .runs()
                .get(run_index as usize)
                .ok_or(FontError::InvalidArgument)?;
            let original = run
                .generation
                .glyphs()
                .get(run_glyph_index as usize)
                .ok_or(FontError::InvalidArgument)?;
            if run.font_index != g.font_index
                || g.font_index as usize >= s.fonts().len()
                || original.glyph_id != g.glyph_id
                || original.cluster != g.cluster
            {
                return Err(FontError::InvalidArgument);
            }
        }
    }
    Ok(())
}

impl NativeFragmentInteraction {
    /// Build from actual native fragment placement and caller-resolved metadata.
    /// The original design/source-width/hinted routes are not interchangeable.
    pub fn new(
        paragraph: Arc<NativeFragmentParagraph>,
        cluster_ends: &[i32],
        bidi_levels: &[i8],
        coordinates: NativeFragmentCoordinates<'_>,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Interaction;
        admit_owner(&paragraph).map_err(|e| error(stage, e))?;
        let view = input(&paragraph, cluster_ends, bidi_levels, coordinates);
        let requirements = view.requirements().map_err(|e| error(stage, e))?;
        let mut boxes = filled(
            requirements.cluster_box_capacity as usize,
            NativeClusterBox::default(),
        )
        .map_err(|e| error(stage, e))?;
        let mut carets = filled(
            requirements.caret_stop_capacity as usize,
            NativeCaretStop::default(),
        )
        .map_err(|e| error(stage, e))?;
        let counts = view
            .build_into(&mut boxes, &mut carets)
            .map_err(|e| error(stage, e))?;
        boxes.truncate(counts.cluster_box_count as usize);
        carets.truncate(counts.caret_stop_count as usize);
        Self::publish(
            paragraph,
            cluster_ends,
            bidi_levels,
            coordinates,
            boxes,
            carets,
        )
    }

    /// Import a complete retained record set against this actual owner. Every
    /// emitted coordinate bit and final deduplicated count must match before any
    /// new generation is returned. This does not import fonts or shape text.
    pub fn from_retained_records(
        paragraph: Arc<NativeFragmentParagraph>,
        cluster_ends: &[i32],
        bidi_levels: &[i8],
        coordinates: NativeFragmentCoordinates<'_>,
        boxes: &[NativeClusterBox],
        carets: &[NativeCaretStop],
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Interaction;
        admit_owner(&paragraph).map_err(|e| error(stage, e))?;
        input(&paragraph, cluster_ends, bidi_levels, coordinates)
            .validate_retained_records(boxes, carets)
            .map_err(|e| error(stage, e))?;
        let boxes = copied(boxes).map_err(|e| error(stage, e))?;
        let carets = copied(carets).map_err(|e| error(stage, e))?;
        Self::publish(
            paragraph,
            cluster_ends,
            bidi_levels,
            coordinates,
            boxes,
            carets,
        )
    }

    fn publish(
        paragraph: Arc<NativeFragmentParagraph>,
        cluster_ends: &[i32],
        bidi_levels: &[i8],
        coordinates: NativeFragmentCoordinates<'_>,
        boxes: Vec<NativeClusterBox>,
        carets: Vec<NativeCaretStop>,
    ) -> Result<Arc<Self>, NativeParagraphError> {
        let stage = NativeParagraphStage::Interaction;
        let cluster_ends = copied(cluster_ends).map_err(|e| error(stage, e))?;
        let bidi_levels = copied(bidi_levels).map_err(|e| error(stage, e))?;
        let origins = match coordinates {
            NativeFragmentCoordinates::Positioned => None,
            NativeFragmentCoordinates::AdvanceOrigins(v) => {
                Some(copied(v).map_err(|e| error(stage, e))?)
            }
        };
        Ok(Arc::new(Self {
            paragraph,
            cluster_ends,
            bidi_levels,
            origins,
            boxes,
            carets,
        }))
    }

    pub fn paragraph(&self) -> &Arc<NativeFragmentParagraph> {
        &self.paragraph
    }
    pub fn cluster_ends(&self) -> &[i32] {
        &self.cluster_ends
    }
    pub fn bidi_levels(&self) -> &[i8] {
        &self.bidi_levels
    }
    pub fn coordinates(&self) -> NativeFragmentCoordinates<'_> {
        self.origins.as_deref().map_or(
            NativeFragmentCoordinates::Positioned,
            NativeFragmentCoordinates::AdvanceOrigins,
        )
    }
    pub fn input(&self) -> NativeFragmentInteractionInput<'_> {
        input(
            &self.paragraph,
            &self.cluster_ends,
            &self.bidi_levels,
            self.coordinates(),
        )
    }
    pub fn boxes(&self) -> &[NativeClusterBox] {
        &self.boxes
    }
    pub fn carets(&self) -> &[NativeCaretStop] {
        &self.carets
    }
    pub fn hit_test(&self, x: f32, y: f32) -> Result<NativeTextHit, FontError> {
        native_fragment_hit_test(&self.boxes, x, y)
    }
    pub fn caret_index(&self, position: i32, trailing: bool) -> Result<u32, FontError> {
        native_fragment_caret_index(&self.carets, position, trailing)
    }
    pub fn caret(&self, position: i32, trailing: bool) -> Result<NativeCaretStop, FontError> {
        native_fragment_caret(&self.carets, position, trailing)
    }
    pub fn move_visually(
        &self,
        position: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<NativeCaretStop, FontError> {
        native_fragment_move_visual_caret(&self.carets, position, trailing, direction)
    }
    /// Physical row navigation uses the retained paragraph's original direction.
    /// Preferred X remains the caller's original unsnapped horizontal coordinate.
    pub fn move_caret(
        &self,
        current_index: u32,
        direction: NativeFragmentCaretDirection,
        preferred_x: f32,
    ) -> Result<u32, FontError> {
        native_move_fragment_caret(
            &self.carets,
            self.paragraph.placements(),
            current_index,
            direction,
            self.paragraph.source().paragraph_level(),
            preferred_x,
        )
    }
    pub fn selection_into(
        &self,
        start: i32,
        end: i32,
        rectangles: &mut [NativeTextRectangle],
    ) -> Result<u32, FontError> {
        native_fragment_selection_into(&self.boxes, start, end, rectangles)
    }
    pub fn selection(&self, start: i32, end: i32) -> Result<Vec<NativeTextRectangle>, FontError> {
        let count = native_fragment_selection_requirements(&self.boxes, start, end)?;
        let mut rectangles = filled(count as usize, NativeTextRectangle::default())?;
        let written = self.selection_into(start, end, &mut rectangles)?;
        rectangles.truncate(written as usize);
        Ok(rectangles)
    }
    pub fn validate_retained_records(
        &self,
        boxes: &[NativeClusterBox],
        carets: &[NativeCaretStop],
    ) -> Result<(), FontError> {
        self.input().validate_retained_records(boxes, carets)
    }

    /// Copy all source metadata and records after whole-capacity admission.
    /// The caller retains `paragraph()` separately; this is not a font archive.
    /// Positioned mode requires no origins and writes none to that destination.
    pub fn copy_into(
        &self,
        cluster_ends: &mut [i32],
        bidi_levels: &mut [i8],
        line_origins: &mut [f32],
        boxes: &mut [NativeClusterBox],
        carets: &mut [NativeCaretStop],
    ) -> Result<NativeFragmentInteractionCounts, FontError> {
        let origins = self.origins.as_deref().unwrap_or(&[]);
        if cluster_ends.len() < self.cluster_ends.len()
            || bidi_levels.len() < self.bidi_levels.len()
            || line_origins.len() < origins.len()
            || boxes.len() < self.boxes.len()
            || carets.len() < self.carets.len()
        {
            return Err(FontError::Capacity);
        }
        cluster_ends[..self.cluster_ends.len()].copy_from_slice(&self.cluster_ends);
        bidi_levels[..self.bidi_levels.len()].copy_from_slice(&self.bidi_levels);
        line_origins[..origins.len()].copy_from_slice(origins);
        boxes[..self.boxes.len()].copy_from_slice(&self.boxes);
        carets[..self.carets.len()].copy_from_slice(&self.carets);
        Ok(NativeFragmentInteractionCounts {
            cluster_box_count: self.boxes.len() as u32,
            caret_stop_count: self.carets.len() as u32,
        })
    }
}
