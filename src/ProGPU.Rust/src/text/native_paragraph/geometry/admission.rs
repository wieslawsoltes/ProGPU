//! Complete original actual-owner paragraph admission before outline conversion.
use super::*;
pub(super) fn paragraph(p: &NativeHintedParagraph) -> Result<(), NativeParagraphGeometryError> {
    let s = &p.source;
    let n = s.logical.len();
    let count = p.glyphs.len();
    let scalars = s.source.len();
    checked(
        matches!(
            p.options.direction,
            GposDirection::Unspecified | GposDirection::LeftToRight | GposDirection::RightToLeft
        ) && p.options.trimming == NativeTextTrimming::None
            && matches!(s.paragraph_level, 0 | 1)
            && s.input.len() == scalars
            && s.scalar_levels.len() == scalars
            && s.line_classes.len() == scalars
            && s.scalar_breaks.len() == scalars
            && s.owners.len() == n
            && s.levels.len() == n
            && s.ends.len() == n
            && s.font_indices.len() == n
            && s.source_scales.len() == n
            && s.scales.len() == n
            && s.breaks.len() == n
            && s.metrics.len() == n
            && (s.justification.is_empty() || s.justification.len() == n)
            && p.owners.len() == count
            && p.levels.len() == count
            && p.ends.len() == count
            && p.lines.len() == p.origins.len(),
    )?;
    let mut source_end = 0u64;
    for i in 0..scalars {
        let source = s.source[i];
        let admitted = s.input[i];
        let level = s.scalar_levels[i];
        let end = u64::from(source.input_index) + u64::from(source.input_length);
        checked(
            source.input_length != 0
                && u64::from(source.input_index) >= source_end
                && end <= i32::MAX as u64
                && admitted.input_index == source.input_index
                && admitted.input_length == source.input_length
                && !matches!(admitted.code_point, 9 | 0xfffc)
                && level.input_index == source.input_index
                && level.input_length == source.input_length
                && (0..=125).contains(&level.level),
        )?;
        source_end = end;
    }
    let mut style_end = 0;
    for style in &s.styles {
        let hint = &style.hinting;
        checked(
            style.scalar_start as usize == style_end
                && style.scalar_count != 0
                && style.scalar_count as usize <= scalars - style_end
                && (style.font_index as usize) < s.fonts.len()
                && !s.fonts[style.font_index as usize].data().is_empty()
                && hint
                    .pixels_per_em_26_6
                    .iter()
                    .all(|&v| v != 0 && v <= i32::MAX as u32)
                && hint.phase_26_6.iter().all(|&v| v < 64)
                && style.source_scale.is_finite()
                && style.source_scale > 0.0
                && style.logical_units_per_physical_pixel.is_finite()
                && style.logical_units_per_physical_pixel > 0.0
                && (style.logical_units_per_physical_pixel / 64.0).is_finite()
                && style.logical_units_per_physical_pixel / 64.0 > 0.0
                && style.metrics.ascent.is_finite()
                && style.metrics.ascent >= 0.0
                && style.metrics.descent.is_finite()
                && style.metrics.descent >= 0.0
                && (style.metrics.ascent + style.metrics.descent).is_finite(),
        )?;
        style_end += style.scalar_count as usize;
    }
    checked(style_end == scalars)?;
    let mut next_scalar = 0usize;
    let mut next_logical = 0usize;
    for (run_index, r) in s.runs.iter().enumerate() {
        let run = &r.generation;
        let range = &r.scalar_range;
        let logical = &r.logical_range;
        checked(
            range.start as usize == next_scalar
                && range.end > range.start
                && range.end as usize <= scalars
                && logical.start as usize == next_logical
                && logical.end >= logical.start
                && logical.end as usize <= n
                && (logical.end - logical.start) as usize == run.glyphs().len()
                && (r.style_index as usize) < s.styles.len()
                && (r.font_index as usize) < s.fonts.len()
                && run.source().len() == (range.end - range.start) as usize
                && run.normalized_coordinates() == s.shaping.normalized_coordinates.as_slice()
                && run.descriptor_indices().len() == run.glyphs().len(),
        )?;
        let style = &s.styles[r.style_index as usize];
        let actual = run.batch().font();
        let font = &s.fonts[r.font_index as usize];
        let style_end = u64::from(style.scalar_start) + u64::from(style.scalar_count);
        checked(
            r.font_index == style.font_index
                && r.source_scale == style.source_scale
                && range.start >= style.scalar_start
                && u64::from(range.end) <= style_end
                && r.logical_units_per_physical_pixel == style.logical_units_per_physical_pixel
                && std::ptr::eq(actual.data().as_ptr(), font.data().as_ptr())
                && actual.data().len() == font.data().len()
                && actual.face_index() == font.face_index()
                && !actual.data().is_empty()
                && run.batch().request() == &style.hinting
                && (0..=125).contains(&r.bidi_level)
                && run.direction()
                    == if r.bidi_level & 1 == 0 {
                        GposDirection::LeftToRight
                    } else {
                        GposDirection::RightToLeft
                    },
        )?;
        for (i, &scalar) in run.source().iter().enumerate() {
            checked(
                scalar == s.input[next_scalar + i]
                    && s.scalar_levels[next_scalar + i].level == r.bidi_level,
            )?;
        }
        let mut logical = next_logical;
        let mut group = |first: usize, last: usize| -> Result<(), NativeParagraphGeometryError> {
            for original in first..last {
                let descriptor = run.descriptor_indices()[original];
                let g = s.logical[logical];
                let mut expected = run.glyphs()[original];
                if let Some(policies) = &s.source_policies {
                    expected = policies
                        .get(r.style_index as usize)
                        .ok_or(NativeParagraphGeometryError::InvalidLayout)?
                        .project_advance(expected)
                        .map_err(|_| NativeParagraphGeometryError::InvalidLayout)?;
                }
                expected.advance_y = expected
                    .advance_y
                    .checked_neg()
                    .ok_or(NativeParagraphGeometryError::InvalidLayout)?;
                expected.offset_y = expected
                    .offset_y
                    .checked_neg()
                    .ok_or(NativeParagraphGeometryError::InvalidLayout)?;
                checked(
                    s.owners[logical]
                        == NativeParagraphGlyphOwner {
                            run_index: run_index as u32,
                            run_glyph_index: original as u32,
                            descriptor_index: descriptor,
                        }
                        && (descriptor as usize) < run.source_descriptor_count()
                        && run
                            .batch()
                            .glyphs()
                            .get(descriptor as usize)
                            .is_some_and(|d| d.glyph_id == g.glyph_id)
                        && g == expected
                        && g.cluster >= 0
                        && s.ends[logical] > g.cluster
                        && s.levels[logical] == r.bidi_level
                        && s.font_indices[logical] == r.font_index
                        && s.source_scales[logical] == r.source_scale
                        && s.scales[logical] == r.logical_units_per_physical_pixel / 64.0
                        && s.metrics[logical].ascent == style.metrics.ascent
                        && s.metrics[logical].descent == style.metrics.descent,
                )?;
                logical += 1;
            }
            Ok(())
        };
        if r.bidi_level & 1 == 0 {
            group(0, run.glyphs().len())?;
        } else {
            let mut end = run.glyphs().len();
            while end != 0 {
                let mut first = end - 1;
                while first != 0 && run.glyphs()[first - 1].cluster == run.glyphs()[end - 1].cluster
                {
                    first -= 1;
                }
                group(first, end)?;
                end = first;
            }
        }
        next_scalar = range.end as usize;
        next_logical = r.logical_range.end as usize;
    }
    checked(next_scalar == scalars && next_logical == n)?;
    for i in 1..n {
        let previous = s.logical[i - 1].cluster;
        let current = s.logical[i].cluster;
        checked(
            current >= previous
                && (current <= previous || s.ends[i - 1] <= current)
                && (current != previous || s.ends[i] == s.ends[i - 1]),
        )?;
    }
    for (i, g) in p.glyphs.iter().enumerate() {
        let l = g.glyph_index as usize;
        checked(l < n)?;
        checked(
            p.owners[i] == s.owners[l]
                && g.glyph_id == s.logical[l].glyph_id
                && g.cluster == s.logical[l].cluster
                && p.ends[i] == s.ends[l]
                && (0..=125).contains(&p.levels[i])
                && g.position.iter().chain(&g.advance).all(|v| v.is_finite())
                && (g.position[0] + g.advance[0]).is_finite()
                && (g.position[1] + g.advance[1]).is_finite(),
        )?;
    }
    let mut covered = 0;
    for (i, l) in p.lines.iter().enumerate() {
        checked(
            l.glyph_start as usize == covered
                && l.glyph_count != 0
                && l.glyph_count as usize <= count - covered
                && l.width.is_finite()
                && l.width >= 0.0
                && l.baseline_y.is_finite()
                && l.height.is_finite()
                && l.height >= 0.0
                && p.origins[i].is_finite()
                && l.flags & !1 == 0,
        )?;
        covered += l.glyph_count as usize;
    }
    checked(covered == count)
}
