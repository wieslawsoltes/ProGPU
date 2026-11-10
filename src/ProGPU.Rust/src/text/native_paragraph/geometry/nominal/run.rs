//! Original double source GlyphRun staging and exact supplied-metric admission.
use super::*;
struct Staged {
    advances: Vec<f64>,
    offsets: Vec<NativeSourceGlyphOffset>,
    frame: NativeSourceRunFrame,
}
impl NativeNominalParagraph {
    fn stage_source_run(
        &self,
        indices: &[u32],
        em: f64,
        dpi: f64,
        baseline: Option<NativeSourceGlyphOffset>,
    ) -> Result<Staged, NativeSourceError> {
        let source = self
            .geometry
            .source_paragraph
            .as_ref()
            .ok_or(NativeSourceError::Unsupported)?;
        let p = source.raster();
        let s = &p.source;
        let v = &self.geometry.records;
        require(
            !indices.is_empty()
                && em.is_finite()
                && em > 0.0
                && dpi.is_finite()
                && dpi > 0.0
                && source.glyphs().len() == p.glyphs.len()
                && source.lines().len() == p.lines.len()
                && p.owners.len() == p.glyphs.len()
                && p.levels.len() == p.glyphs.len()
                && self.metrics.len() == p.glyphs.len()
                && v.positioned_glyphs.len() == p.glyphs.len(),
        )?;
        support(matches!(
            p.options.direction,
            GposDirection::LeftToRight | GposDirection::RightToLeft
        ))?;
        let first = indices[0] as usize;
        require(first < p.glyphs.len())?;
        let line_index = source
            .lines()
            .iter()
            .position(|l| {
                first >= l.glyph_start as usize
                    && first - (l.glyph_start as usize) < l.glyph_count as usize
            })
            .ok_or(NativeSourceError::Unsupported)?;
        let line = source.lines()[line_index];
        let run_index = p.owners[first].run_index;
        let run = s
            .runs
            .get(run_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        let style = source
            .styles()
            .get(run.style_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?;
        let upm = v
            .font_sources
            .get(run.font_index as usize)
            .ok_or(NativeSourceError::InvalidArgument)?
            .units_per_em;
        let level = p.levels[first];
        require(upm != 0 && level >= 0 && line.baseline_y.is_finite())?;
        support(
            exact(em, style.em_size)
                && exact(dpi, source.options().pixels_per_dip)
                && f64::from(v.metadata.dpi_scale) == dpi,
        )?;
        let origin = baseline.unwrap_or(NativeSourceGlyphOffset {
            x: 0.0,
            y: line.baseline_y,
        });
        require(origin.x.is_finite() && origin.y.is_finite())?;
        let y = origin.y - line.baseline_y;
        let raster = [origin.x as f32, y as f32];
        support(
            raster.iter().all(|v| v.is_finite())
                && f64::from(raster[0]) == origin.x
                && f64::from(raster[1]) == y,
        )?;
        let frame = NativeSourceRunFrame {
            line_index: line_index as u32,
            font_index: run.font_index,
            bidi_level: level,
            original_baseline_y: line.baseline_y,
            source_baseline: origin,
            source_translation: NativeSourceGlyphOffset { x: origin.x, y },
            raster_translation: raster,
        };
        let mut result = Staged {
            advances: reserve(indices.len())?,
            offsets: reserve(indices.len())?,
            frame,
        };
        let mut prefix = 0.0;
        for &index in indices {
            let index = index as usize;
            require(index < p.glyphs.len())?;
            support(
                index >= line.glyph_start as usize
                    && index - (line.glyph_start as usize) < line.glyph_count as usize
                    && p.owners[index].run_index == run_index
                    && p.levels[index] == level,
            )?;
            let raw = p.glyphs[index];
            let g = source.glyphs()[index];
            let m = self.metrics[index];
            require(
                s.font_indices.get(raw.glyph_index as usize) == Some(&run.font_index)
                    && s.owners.get(raw.glyph_index as usize) == Some(&p.owners[index])
                    && m.positioned_index as usize == index
                    && m.font_index == run.font_index
                    && m.glyph_id == raw.glyph_id
                    && g.cluster == raw.cluster,
            )?;
            support(g.advance_y == 0.0)?;
            support(
                g.x.is_finite()
                    && g.y.is_finite()
                    && g.advance_x.is_finite()
                    && (raw.position[0] + raster[0]).is_finite()
                    && (raw.position[1] + raster[1]).is_finite(),
            )?;
            let advance = f64::from(m.advance_width_design_units) / f64::from(upm) * em;
            let offset = NativeSourceGlyphOffset {
                x: if level & 1 == 0 {
                    g.x - prefix
                } else {
                    -prefix - advance - g.x
                },
                y: -(g.y - line.baseline_y),
            };
            support(offset.x.is_finite() && offset.y.is_finite())?;
            result.advances.push(g.advance_x);
            result.offsets.push(offset);
            prefix += g.advance_x;
            support(prefix.is_finite())?;
        }
        Ok(result)
    }
    /// Copy exact source doubles and nominal offsets, preserving every unused
    /// destination tail. No writer-position or device advance is reconstructed.
    pub fn copy_source_metrics(
        &self,
        indices: &[u32],
        em: f64,
        dpi: f64,
        advances: &mut [f64],
        offsets: &mut [NativeSourceGlyphOffset],
    ) -> Result<usize, NativeSourceError> {
        require(advances.len() >= indices.len() && offsets.len() >= indices.len())?;
        let staged = self.stage_source_run(indices, em, dpi, None)?;
        advances[..indices.len()].copy_from_slice(&staged.advances);
        offsets[..indices.len()].copy_from_slice(&staged.offsets);
        Ok(indices.len())
    }
    /// The original source-double lane compares every supplied metric bit,
    /// including signed zero, before publishing its exact raster translation.
    pub fn admit_source_run(
        &self,
        indices: &[u32],
        em: f64,
        dpi: f64,
        baseline: NativeSourceGlyphOffset,
        advances: &[f64],
        offsets: &[NativeSourceGlyphOffset],
    ) -> Result<NativeSourceRunFrame, NativeSourceError> {
        require(advances.len() == indices.len() && offsets.len() == indices.len())?;
        let staged = self.stage_source_run(indices, em, dpi, Some(baseline))?;
        for i in 0..indices.len() {
            require(
                exact(advances[i], staged.advances[i])
                    && exact(offsets[i].x, staged.offsets[i].x)
                    && exact(offsets[i].y, staged.offsets[i].y),
            )?;
        }
        Ok(staged.frame)
    }
}
