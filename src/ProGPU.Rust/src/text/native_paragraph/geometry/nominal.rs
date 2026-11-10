//! Original producer-owned nominal metrics and independent source GlyphRun frames.
use super::*;
use crate::text::{hinted_replay::HintedReplayDraw, paint::TextPaintContext};
mod frame;
mod run;
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeNominalGlyphMetrics {
    pub positioned_index: u32,
    pub font_index: u32,
    pub glyph_id: u32,
    pub advance_width_design_units: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceGlyphOffset {
    pub x: f64,
    pub y: f64,
}
/// Original float positioned-resource source frame, with independent source rebase.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceGlyphFrame {
    pub line_index: u32,
    pub font_index: u32,
    pub bidi_level: i8,
    pub original_baseline_y: f32,
    pub source_baseline: [f32; 2],
    pub raster_translation: [f32; 2],
    pub source_rebase: [f32; 2],
}
/// Original double source-run frame. The physical draw translation must be exact f32.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeSourceRunFrame {
    pub line_index: u32,
    pub font_index: u32,
    pub bidi_level: i8,
    pub original_baseline_y: f64,
    pub source_baseline: NativeSourceGlyphOffset,
    pub source_translation: NativeSourceGlyphOffset,
    pub raster_translation: [f32; 2],
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeNominalFrame {
    Positioned(NativeSourceGlyphFrame),
    Source(NativeSourceRunFrame),
}
/// Optional nominal capture retains actual font/run owners. Flat imports are not
/// a font producer and cannot enter this API, even when they carry font bytes.
#[derive(Debug)]
pub struct NativeNominalParagraph {
    geometry: Arc<NativeParagraphGeometry>,
    metrics: Vec<NativeNominalGlyphMetrics>,
}
fn require(v: bool) -> Result<(), NativeSourceError> {
    if v {
        Ok(())
    } else {
        Err(NativeSourceError::InvalidArgument)
    }
}
fn support(v: bool) -> Result<(), NativeSourceError> {
    if v {
        Ok(())
    } else {
        Err(NativeSourceError::Unsupported)
    }
}
fn exact(a: f64, b: f64) -> bool {
    a.to_bits() == b.to_bits()
}
impl NativeNominalParagraph {
    /// Direct hmtx capture in original occurrence order. No half-em fallback,
    /// HVAR inference, glyph load, interpreter work or replacement shaping.
    pub fn new(geometry: Arc<NativeParagraphGeometry>) -> Result<Arc<Self>, NativeSourceError> {
        let p = geometry
            .paragraph
            .as_ref()
            .ok_or(NativeSourceError::Unsupported)?;
        let source = &p.source;
        support(source.shaping.normalized_coordinates.is_empty())?;
        require(p.glyphs.len() <= u32::MAX as usize && p.owners.len() == p.glyphs.len())?;
        let mut counts = reserve(source.fonts.len())?;
        for font in &source.fonts {
            counts.push(
                font.glyph_count()
                    .ok_or(NativeSourceError::InvalidArgument)?,
            );
        }
        let mut metrics = reserve(p.glyphs.len())?;
        for (i, g) in p.glyphs.iter().enumerate() {
            let owner = p.owners[i];
            let font_index = *source
                .font_indices
                .get(g.glyph_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            let font = source
                .fonts
                .get(font_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(g.glyph_id < u32::from(counts[font_index as usize]))?;
            let run = source
                .runs
                .get(owner.run_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            let style = source
                .styles
                .get(run.style_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(run.font_index == font_index)?;
            support(style.hinting.variation_coordinates_16_16.is_empty())?;
            let metric = font
                .horizontal_glyph_metrics(g.glyph_id as u16)
                .ok_or(NativeSourceError::Unsupported)?;
            metrics.push(NativeNominalGlyphMetrics {
                positioned_index: i as u32,
                font_index,
                glyph_id: g.glyph_id,
                advance_width_design_units: u32::from(metric.advance_width),
            });
        }
        Ok(Arc::new(Self { geometry, metrics }))
    }
    pub fn geometry(&self) -> &Arc<NativeParagraphGeometry> {
        &self.geometry
    }
    /// The original raw nominal view rejects source-double generations.
    pub fn nominal_metrics(&self) -> Result<&[NativeNominalGlyphMetrics], NativeSourceError> {
        support(self.geometry.source_paragraph.is_none())?;
        Ok(&self.metrics)
    }
    fn selected(
        self: &Arc<Self>,
        indices: &[u32],
        frame: NativeNominalFrame,
    ) -> Result<NativeNominalReplay, NativeSourceError> {
        let mut selected = reserve(indices.len())?;
        for &i in indices {
            selected.push(i as usize);
        }
        let replay = self.geometry.clone().into_replay()?;
        let replay = replay.select_occurrences(&selected)?;
        Ok(NativeNominalReplay {
            source: self.clone(),
            indices: copy(indices)?,
            geometry: Arc::new(replay),
            frame,
        })
    }
    /// Retain validated original positioned indices, duplicates and no-ink slots.
    pub fn admit_positioned_replay(
        self: &Arc<Self>,
        indices: &[u32],
        em: f32,
        baseline: [f32; 2],
        advances: &[f64],
        offsets: &[NativeSourceGlyphOffset],
    ) -> Result<NativeNominalReplay, NativeSourceError> {
        let frame = self.admit_source_frame(indices, em, baseline, advances, offsets)?;
        self.selected(indices, NativeNominalFrame::Positioned(frame))
    }
    pub fn admit_source_replay(
        self: &Arc<Self>,
        indices: &[u32],
        em: f64,
        dpi: f64,
        baseline: NativeSourceGlyphOffset,
        advances: &[f64],
        offsets: &[NativeSourceGlyphOffset],
    ) -> Result<NativeNominalReplay, NativeSourceError> {
        let frame = self.admit_source_run(indices, em, dpi, baseline, advances, offsets)?;
        self.selected(indices, NativeNominalFrame::Source(frame))
    }
}
/// Immutable admitted source frame and selected original captured geometry.
/// Existing Scene::draw_native_paragraph consumes geometry() and draw_position().
#[derive(Debug)]
pub struct NativeNominalReplay {
    source: Arc<NativeNominalParagraph>,
    indices: Vec<u32>,
    geometry: Arc<FontReplayGeometry>,
    frame: NativeNominalFrame,
}
impl NativeNominalReplay {
    pub fn source(&self) -> &Arc<NativeNominalParagraph> {
        &self.source
    }
    pub fn positioned_indices(&self) -> &[u32] {
        &self.indices
    }
    pub fn geometry(&self) -> &Arc<FontReplayGeometry> {
        &self.geometry
    }
    pub fn frame(&self) -> NativeNominalFrame {
        self.frame
    }
    pub fn draw_position(&self) -> [f32; 2] {
        match self.frame {
            NativeNominalFrame::Positioned(f) => f.raster_translation,
            NativeNominalFrame::Source(f) => f.raster_translation,
        }
    }
    pub fn prepare_draws(
        &self,
        paints: &[NativeParagraphPaint],
        context: TextPaintContext,
    ) -> Result<Vec<HintedReplayDraw>, crate::Error> {
        prepare_native_paragraph_draws(self.geometry.clone(), paints, self.draw_position(), context)
    }
}
