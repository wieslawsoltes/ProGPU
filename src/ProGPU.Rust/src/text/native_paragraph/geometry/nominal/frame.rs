//! Original float writer-position to GlyphTypeface nominal offset frame.
use super::*;
fn offset(
    g: NativeParagraphPositionedRecord,
    m: NativeNominalGlyphMetrics,
    upm: u32,
    em: f32,
    level: i8,
    baseline: f32,
    prefix: f64,
) -> NativeSourceGlyphOffset {
    let advance = f64::from(m.advance_width_design_units) / f64::from(upm) * f64::from(em);
    NativeSourceGlyphOffset {
        x: if level & 1 == 0 {
            f64::from(g.x) - prefix
        } else {
            -prefix - advance - f64::from(g.x)
        },
        y: -(f64::from(g.y) - f64::from(baseline)),
    }
}
impl NativeNominalParagraph {
    /// Admit original supplied advances/offsets before exposing a raster frame.
    /// Offset equality follows the native float-source route; advance bits are exact.
    pub fn admit_source_frame(
        &self,
        indices: &[u32],
        em: f32,
        baseline: [f32; 2],
        advances: &[f64],
        offsets: &[NativeSourceGlyphOffset],
    ) -> Result<NativeSourceGlyphFrame, NativeSourceError> {
        support(self.geometry.source_paragraph.is_none())?;
        let v = &self.geometry.records;
        require(
            !indices.is_empty()
                && indices.len() == advances.len()
                && indices.len() == offsets.len()
                && self.metrics.len() == v.positioned_glyphs.len()
                && em.is_finite()
                && em > 0.0
                && baseline.iter().all(|v| v.is_finite()),
        )?;
        support(matches!(
            v.metadata.layout.direction,
            GposDirection::LeftToRight | GposDirection::RightToLeft
        ))?;
        let first = indices[0] as usize;
        require(first < v.positioned_glyphs.len())?;
        let line_index = v
            .lines
            .iter()
            .position(|l| {
                first >= l.glyph_start as usize
                    && first - (l.glyph_start as usize) < (l.glyph_count as usize)
            })
            .ok_or(NativeSourceError::Unsupported)?;
        let line = v.lines[line_index];
        let font = v.positioned_glyphs[first].font_index;
        let upm = v
            .font_sources
            .get(font as usize)
            .ok_or(NativeSourceError::InvalidArgument)?
            .units_per_em;
        let level = v.positioned_bidi_levels[first];
        require(upm != 0 && level >= 0 && line.baseline_y.is_finite())?;
        let translation = f64::from(baseline[1]) - f64::from(line.baseline_y);
        let draw_y = translation as f32;
        support(draw_y.is_finite() && f64::from(draw_y) == translation)?;
        let mut prefix = 0.0;
        for (i, &index) in indices.iter().enumerate() {
            let index = index as usize;
            require(index < v.positioned_glyphs.len())?;
            support(
                index >= line.glyph_start as usize
                    && index - (line.glyph_start as usize) < (line.glyph_count as usize),
            )?;
            let g = v.positioned_glyphs[index];
            let m = self.metrics[index];
            let run = v
                .runs
                .get(v.positioned_owners[index].run_index as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(
                m.positioned_index as usize == index
                    && m.font_index == g.font_index
                    && m.glyph_id == g.glyph_id,
            )?;
            support(
                g.font_index == font
                    && v.positioned_bidi_levels[index] == level
                    && em / upm as f32 == run.source_scale
                    && g.advance_y == 0.0,
            )?;
            require(
                g.x.is_finite()
                    && g.y.is_finite()
                    && g.advance_x.is_finite()
                    && advances[i].is_finite()
                    && exact(advances[i], f64::from(g.advance_x)),
            )?;
            support((g.x + baseline[0]).is_finite() && (g.y + draw_y).is_finite())?;
            let expected = offset(g, m, upm, em, level, line.baseline_y, prefix);
            require(
                offsets[i].x.is_finite()
                    && offsets[i].y.is_finite()
                    && offsets[i].x == expected.x
                    && offsets[i].y == expected.y,
            )?;
            prefix += advances[i];
            require(prefix.is_finite())?;
        }
        Ok(NativeSourceGlyphFrame {
            line_index: line_index as u32,
            font_index: font,
            bidi_level: level,
            original_baseline_y: line.baseline_y,
            source_baseline: baseline,
            raster_translation: [baseline[0], draw_y],
            source_rebase: [0.0, -line.baseline_y],
        })
    }
    /// Stage original nominal offsets privately and leave all output untouched
    /// if any later occurrence, frame, source-em or arithmetic admission fails.
    pub fn copy_source_offsets(
        &self,
        indices: &[u32],
        em: f32,
        out: &mut [NativeSourceGlyphOffset],
    ) -> Result<usize, NativeSourceError> {
        support(self.geometry.source_paragraph.is_none())?;
        let v = &self.geometry.records;
        require(
            !indices.is_empty()
                && out.len() >= indices.len()
                && self.metrics.len() == v.positioned_glyphs.len(),
        )?;
        let first = indices[0] as usize;
        require(first < v.positioned_glyphs.len())?;
        let line = v
            .lines
            .iter()
            .find(|l| {
                first >= l.glyph_start as usize
                    && first - (l.glyph_start as usize) < (l.glyph_count as usize)
            })
            .ok_or(NativeSourceError::Unsupported)?;
        let font = v.positioned_glyphs[first].font_index;
        let upm = v
            .font_sources
            .get(font as usize)
            .ok_or(NativeSourceError::InvalidArgument)?
            .units_per_em;
        require(upm != 0)?;
        let mut advances = reserve(indices.len())?;
        let mut offsets = reserve(indices.len())?;
        let mut prefix = 0.0;
        for &index in indices {
            let index = index as usize;
            let g = *v
                .positioned_glyphs
                .get(index)
                .ok_or(NativeSourceError::InvalidArgument)?;
            offsets.push(offset(
                g,
                self.metrics[index],
                upm,
                em,
                v.positioned_bidi_levels[first],
                line.baseline_y,
                prefix,
            ));
            advances.push(f64::from(g.advance_x));
            prefix += f64::from(g.advance_x);
        }
        self.admit_source_frame(indices, em, [0.0; 2], &advances, &offsets)?;
        out[..offsets.len()].copy_from_slice(&offsets);
        Ok(offsets.len())
    }
}
