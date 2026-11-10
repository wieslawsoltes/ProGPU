//! One original record emitter serves construction and exact retained admission.
use super::*;

impl<'a> NativeFragmentInteractionInput<'a> {
    fn origins(self) -> Option<&'a [f32]> {
        match self.coordinates {
            NativeFragmentCoordinates::Positioned => None,
            NativeFragmentCoordinates::AdvanceOrigins(v) => Some(v),
        }
    }

    /// Original whole-span admission, followed by complete cluster capacities.
    /// No output or allocation is changed when a later fragment is invalid.
    pub fn requirements(self) -> Result<NativeFragmentInteractionRequirements, FontError> {
        if self.glyphs.len() > u32::MAX as usize / 2
            || self.lines.len() > u32::MAX as usize
            || self.cluster_ends.len() != self.glyphs.len()
            || self.bidi_levels.len() != self.glyphs.len()
            || self.placements.len() != self.lines.len()
            || self.origins().is_some_and(|v| v.len() != self.lines.len())
        {
            return Err(FontError::InvalidArgument);
        }
        validate_placements(self.placements, Some(self.lines))?;
        let mut expected = 0;
        let mut clusters = 0u32;
        for (line_index, line) in self.lines.iter().enumerate() {
            let top = self.placements[line_index].top;
            let bottom = top + f64::from(line.height);
            if !line.baseline_y.is_finite()
                || !line.height.is_finite()
                || line.height < 0.0
                || !bottom.is_finite()
                || bottom > f64::from(f32::MAX)
                || line.baseline_y < top as f32
                || line.baseline_y > bottom as f32
                || line.glyph_start as usize != expected
                || line.glyph_count as usize > self.glyphs.len() - expected
            {
                return Err(FontError::InvalidArgument);
            }
            let end = expected + line.glyph_count as usize;
            let mut pen = self.origins().map_or(0.0, |v| v[line_index]);
            if self.origins().is_some() && !pen.is_finite() {
                return Err(FontError::InvalidArgument);
            }
            for i in expected..end {
                let g = self.glyphs[i];
                if ![g.x, g.y, g.advance_x, g.advance_y, g.x + g.advance_x]
                    .into_iter()
                    .all(f32::is_finite)
                    || self.cluster_ends[i] <= g.cluster
                    || !(0..=125).contains(&self.bidi_levels[i])
                {
                    return Err(FontError::InvalidArgument);
                }
                if self.origins().is_some() {
                    pen += g.advance_x;
                    if !pen.is_finite() {
                        return Err(FontError::InvalidArgument);
                    }
                }
                if i == expected || self.glyphs[i - 1].cluster != g.cluster {
                    clusters += 1;
                }
            }
            expected = end;
        }
        if expected != self.glyphs.len() || clusters > u32::MAX / 2 {
            return Err(FontError::InvalidArgument);
        }
        Ok(NativeFragmentInteractionRequirements {
            cluster_box_capacity: clusters,
            caret_stop_capacity: clusters * 2,
        })
    }

    fn emit(
        self,
        mut emit_box: impl FnMut(NativeClusterBox) -> bool,
        mut emit_caret: impl FnMut(NativeCaretStop) -> bool,
    ) -> bool {
        let mut previous: Option<NativeCaretStop> = None;
        for (line_index, line) in self.lines.iter().enumerate() {
            let top = self.placements[line_index].top;
            let mut pen = self.origins().map_or(0.0, |v| v[line_index]);
            let mut i = line.glyph_start as usize;
            let end = i + line.glyph_count as usize;
            while i < end {
                let start = i;
                let cluster = self.glyphs[i].cluster;
                let g = self.glyphs[i];
                let mut left = if self.origins().is_none() {
                    min(g.x, g.x + g.advance_x)
                } else {
                    pen
                };
                let mut right = if self.origins().is_none() {
                    max(g.x, g.x + g.advance_x)
                } else {
                    pen
                };
                let mut cluster_end = self.cluster_ends[i];
                loop {
                    let g = self.glyphs[i];
                    if self.origins().is_none() {
                        left = min(left, min(g.x, g.x + g.advance_x));
                        right = max(right, max(g.x, g.x + g.advance_x));
                    } else {
                        let next = pen + g.advance_x;
                        left = min(left, min(pen, next));
                        right = max(right, max(pen, next));
                        pen = next;
                    }
                    cluster_end = cluster_end.max(self.cluster_ends[i]);
                    i += 1;
                    if i == end || self.glyphs[i].cluster != cluster {
                        break;
                    }
                }
                let b = NativeClusterBox {
                    input_start: cluster,
                    input_end: cluster_end,
                    line_index: line_index as u32,
                    bidi_level: self.bidi_levels[start],
                    bounds: NativeTextRectangle {
                        x: left,
                        y: top as f32,
                        width: max(0.0, right - left),
                        height: line.height,
                    },
                };
                if !emit_box(b) {
                    return false;
                }
                let rtl = b.bidi_level & 1 != 0;
                let leading = NativeCaretStop {
                    input_position: if rtl { b.input_end } else { b.input_start },
                    line_index: b.line_index,
                    position: [b.bounds.x, b.bounds.y],
                    height: b.bounds.height,
                    bidi_level: b.bidi_level,
                    trailing: rtl,
                };
                let trailing = NativeCaretStop {
                    input_position: if rtl { b.input_start } else { b.input_end },
                    position: [b.bounds.x + b.bounds.width, b.bounds.y],
                    trailing: !rtl,
                    ..leading
                };
                for c in [leading, trailing] {
                    if previous.is_some_and(|p| {
                        p.input_position == c.input_position
                            && p.trailing == c.trailing
                            && (p.position[0] - c.position[0]).abs() < 0.0001
                            && (p.position[1] - c.position[1]).abs() < 0.0001
                    }) {
                        continue;
                    }
                    if !emit_caret(c) {
                        return false;
                    }
                    previous = Some(c);
                }
            }
        }
        true
    }

    /// Admission and both complete capacities precede either output write.
    /// Caret capacity is the original upper bound, not a deduplicated estimate.
    pub fn build_into(
        self,
        boxes: &mut [NativeClusterBox],
        carets: &mut [NativeCaretStop],
    ) -> Result<NativeFragmentInteractionCounts, FontError> {
        let required = self.requirements()?;
        if boxes.len() < required.cluster_box_capacity as usize
            || carets.len() < required.caret_stop_capacity as usize
        {
            return Err(FontError::Capacity);
        }
        let mut count = NativeFragmentInteractionCounts::default();
        self.emit(
            |b| {
                boxes[count.cluster_box_count as usize] = b;
                count.cluster_box_count += 1;
                true
            },
            |c| {
                carets[count.caret_stop_count as usize] = c;
                count.caret_stop_count += 1;
                true
            },
        );
        Ok(count)
    }

    /// Reexecute the original emitter without shaping or allocations. Every
    /// source coordinate bit, including signed zero, must match the retained copy.
    pub fn validate_retained_records(
        self,
        boxes: &[NativeClusterBox],
        carets: &[NativeCaretStop],
    ) -> Result<(), FontError> {
        let required = self.requirements()?;
        if boxes.len() != required.cluster_box_capacity as usize
            || carets.len() > required.caret_stop_capacity as usize
        {
            return Err(FontError::InvalidArgument);
        }
        let exact = |a: f32, b: f32| a.to_bits() == b.to_bits();
        let mut bi = 0;
        let mut ci = 0;
        let success = self.emit(
            |e| {
                let Some(a) = boxes.get(bi) else { return false };
                bi += 1;
                a.input_start == e.input_start
                    && a.input_end == e.input_end
                    && a.line_index == e.line_index
                    && a.bidi_level == e.bidi_level
                    && exact(a.bounds.x, e.bounds.x)
                    && exact(a.bounds.y, e.bounds.y)
                    && exact(a.bounds.width, e.bounds.width)
                    && exact(a.bounds.height, e.bounds.height)
            },
            |e| {
                let Some(a) = carets.get(ci) else {
                    return false;
                };
                ci += 1;
                a.input_position == e.input_position
                    && a.line_index == e.line_index
                    && a.bidi_level == e.bidi_level
                    && a.trailing == e.trailing
                    && exact(a.position[0], e.position[0])
                    && exact(a.position[1], e.position[1])
                    && exact(a.height, e.height)
            },
        );
        if !success || bi != boxes.len() || ci != carets.len() {
            return Err(FontError::InvalidArgument);
        }
        Ok(())
    }
}
