//! Original measured native advance interaction, independent of outline ink.
use super::*;
/// Original full-cluster advance rectangle, including L1-adjusted output level.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeClusterBox {
    pub input_start: i32,
    pub input_end: i32,
    pub line_index: u32,
    pub bidi_level: i8,
    pub bounds: NativeTextRectangle,
}
/// Exact emitted native caret and affinity; no empty-hard-row carets are invented.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeCaretStop {
    pub input_position: i32,
    pub line_index: u32,
    pub position: [f32; 2],
    pub height: f32,
    pub bidi_level: i8,
    pub trailing: bool,
}
/// Original horizontal interaction rectangle.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextRectangle {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}
/// Original nearest-cluster hit result with independent inside state.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeTextHit {
    pub input_position: i32,
    pub line_index: u32,
    pub bounds: NativeTextRectangle,
    pub bidi_level: i8,
    pub trailing: bool,
    pub inside: bool,
}
/// Retains the exact paragraph generation and original measured interaction.
#[derive(Debug)]
pub struct NativeParagraphInteraction {
    paragraph: Arc<NativeHintedParagraph>,
    boxes: Vec<NativeClusterBox>,
    carets: Vec<NativeCaretStop>,
}
// Preserve original std::min/std::max first-operand ties, including signed zero.
fn source_min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn source_max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn valid_box(b: NativeClusterBox) -> bool {
    let r = b.bounds;
    [r.x, r.y, r.width, r.height, r.x + r.width, r.y + r.height]
        .iter()
        .all(|v| v.is_finite())
        && r.width >= 0.0
        && r.height >= 0.0
        && b.input_end > b.input_start
        && (0..=125).contains(&b.bidi_level)
}
fn valid_caret(c: NativeCaretStop) -> bool {
    c.position.iter().all(|v| v.is_finite())
        && c.height.is_finite()
        && c.height >= 0.0
        && (0..=125).contains(&c.bidi_level)
}
impl NativeParagraphInteraction {
    /// Build only from the original immutable paragraph. All descriptor/source
    /// maps and every line/glyph are admitted before any generation is returned.
    pub fn new(paragraph: Arc<NativeHintedParagraph>) -> Result<Self, NativeParagraphError> {
        let stage = NativeParagraphStage::Interaction;
        let p = &paragraph;
        let source = &p.source;
        if p.glyphs.len() > u32::MAX as usize / 2
            || p.lines.len() > u32::MAX as usize
            || p.owners.len() != p.glyphs.len()
            || p.levels.len() != p.glyphs.len()
            || p.ends.len() != p.glyphs.len()
            || p.origins.len() != p.lines.len()
        {
            return Err(invalid(stage));
        }
        let mut expected = 0;
        let mut top = 0f64;
        let mut clusters = 0;
        for (line_index, line) in p.lines.iter().enumerate() {
            let bottom = top + f64::from(line.height);
            if !line.baseline_y.is_finite()
                || !line.height.is_finite()
                || line.height < 0.0
                || !bottom.is_finite()
                || bottom > f64::from(f32::MAX)
                || line.baseline_y < top as f32
                || line.baseline_y > bottom as f32
                || line.glyph_start as usize != expected
                || line.glyph_count as usize > p.glyphs.len() - expected
                || !p.origins[line_index].is_finite()
            {
                return Err(invalid(stage));
            }
            let end = expected + line.glyph_count as usize;
            let mut pen = p.origins[line_index];
            for i in expected..end {
                let g = p.glyphs[i];
                let index = g.glyph_index as usize;
                let owner = p.owners[i];
                if index >= source.logical.len()
                    || source.owners[index] != owner
                    || source.logical[index].glyph_id != g.glyph_id
                    || source.logical[index].cluster != g.cluster
                    || source.ends[index] != p.ends[i]
                    || p.ends[i] <= g.cluster
                    || !(0..=125).contains(&p.levels[i])
                    || !g
                        .position
                        .iter()
                        .chain(g.advance.iter())
                        .all(|v| v.is_finite())
                    || !(g.position[0] + g.advance[0]).is_finite()
                {
                    return Err(invalid(stage));
                }
                let run = source
                    .runs
                    .get(owner.run_index as usize)
                    .ok_or_else(|| invalid(stage))?;
                let original = run
                    .generation
                    .glyphs()
                    .get(owner.run_glyph_index as usize)
                    .ok_or_else(|| invalid(stage))?;
                if source.font_indices[index] != run.font_index
                    || run
                        .generation
                        .descriptor_indices()
                        .get(owner.run_glyph_index as usize)
                        != Some(&owner.descriptor_index)
                    || owner.descriptor_index as usize >= run.generation.source_descriptor_count()
                    || run
                        .generation
                        .batch()
                        .glyphs()
                        .get(owner.descriptor_index as usize)
                        .is_none_or(|d| d.glyph_id != g.glyph_id)
                    || original.glyph_id != g.glyph_id
                    || original.cluster != g.cluster
                {
                    return Err(invalid(stage));
                }
                pen += g.advance[0];
                if !pen.is_finite() {
                    return Err(invalid(stage));
                }
                if i == expected || p.glyphs[i - 1].cluster != g.cluster {
                    clusters += 1;
                }
            }
            expected = end;
            top = bottom;
        }
        if expected != p.glyphs.len() {
            return Err(invalid(stage));
        }
        let mut boxes = reserved(clusters).map_err(|e| error(stage, e))?;
        let mut carets: Vec<NativeCaretStop> =
            reserved(clusters * 2).map_err(|e| error(stage, e))?;
        top = 0.0;
        for (line_index, line) in p.lines.iter().enumerate() {
            let mut pen = p.origins[line_index];
            let mut i = line.glyph_start as usize;
            let end = i + line.glyph_count as usize;
            while i < end {
                let first = i;
                let cluster = p.glyphs[i].cluster;
                let mut left = pen;
                let mut right = pen;
                let mut cluster_end = p.ends[i];
                loop {
                    let next = pen + p.glyphs[i].advance[0];
                    left = source_min(left, source_min(pen, next));
                    right = source_max(right, source_max(pen, next));
                    pen = next;
                    cluster_end = cluster_end.max(p.ends[i]);
                    i += 1;
                    if i == end || p.glyphs[i].cluster != cluster {
                        break;
                    }
                }
                let b = NativeClusterBox {
                    input_start: cluster,
                    input_end: cluster_end,
                    line_index: line_index as u32,
                    bidi_level: p.levels[first],
                    bounds: NativeTextRectangle {
                        x: left,
                        y: top as f32,
                        width: source_max(0.0, right - left),
                        height: line.height,
                    },
                };
                boxes.push(b);
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
                    if carets.last().is_some_and(|p| {
                        p.input_position == c.input_position
                            && p.trailing == c.trailing
                            && (p.position[0] - c.position[0]).abs() < 0.0001
                            && (p.position[1] - c.position[1]).abs() < 0.0001
                    }) {
                        continue;
                    }
                    carets.push(c);
                }
            }
            top += f64::from(line.height);
        }
        Ok(Self {
            paragraph,
            boxes,
            carets,
        })
    }
    pub fn paragraph(&self) -> &Arc<NativeHintedParagraph> {
        &self.paragraph
    }
    pub fn boxes(&self) -> &[NativeClusterBox] {
        &self.boxes
    }
    pub fn carets(&self) -> &[NativeCaretStop] {
        &self.carets
    }
    /// Preserve first minimum-distance selection, midpoint direction and inside
    /// classification from the complete original box inventory.
    pub fn hit_test(&self, x: f32, y: f32) -> Result<NativeTextHit, FontError> {
        if self.boxes.is_empty() || !x.is_finite() || !y.is_finite() {
            return Err(FontError::InvalidArgument);
        }
        let mut best = None;
        let mut best_distance = f32::INFINITY;
        let mut inside = false;
        for &b in &self.boxes {
            if !valid_box(b) {
                return Err(FontError::InvalidArgument);
            }
            let r = b.bounds;
            let right = r.x + r.width;
            let bottom = r.y + r.height;
            let dx = if x < r.x {
                r.x - x
            } else if x > right {
                x - right
            } else {
                0.0
            };
            let dy = if y < r.y {
                r.y - y
            } else if y > bottom {
                y - bottom
            } else {
                0.0
            };
            let distance = dx * dx + dy * dy;
            if distance >= best_distance {
                continue;
            }
            best_distance = distance;
            best = Some(b);
            inside = dx == 0.0 && dy == 0.0;
        }
        let b = best.ok_or(FontError::InvalidArgument)?;
        let visual_right = x >= b.bounds.x + b.bounds.width * 0.5;
        let trailing = if b.bidi_level & 1 != 0 {
            !visual_right
        } else {
            visual_right
        };
        Ok(NativeTextHit {
            input_position: if trailing { b.input_end } else { b.input_start },
            line_index: b.line_index,
            bounds: b.bounds,
            bidi_level: b.bidi_level,
            trailing,
            inside,
        })
    }
    /// Original nearest source position and affinity tie-break; input positions
    /// are not forced onto a UTF-16 unit or reconstructed grapheme boundary.
    pub fn caret(
        &self,
        input_position: i32,
        trailing_affinity: bool,
    ) -> Result<NativeCaretStop, FontError> {
        let index = self.caret_index(input_position, trailing_affinity, false)?;
        Ok(self.carets[index])
    }
    fn caret_index(
        &self,
        input_position: i32,
        trailing: bool,
        weighted: bool,
    ) -> Result<usize, FontError> {
        if self.carets.is_empty() {
            return Err(FontError::InvalidArgument);
        }
        let mut best = 0;
        let mut best_distance = i64::MAX;
        for (i, &c) in self.carets.iter().enumerate() {
            if !valid_caret(c) {
                return Err(FontError::InvalidArgument);
            }
            let distance = (i64::from(c.input_position) - i64::from(input_position)).abs();
            let distance = if weighted {
                distance * 4 + i64::from(c.trailing != trailing)
            } else {
                distance
            };
            if distance < best_distance
                || !weighted
                    && distance == best_distance
                    && c.trailing == trailing
                    && self.carets[best].trailing != trailing
            {
                best = i;
                best_distance = distance;
            }
        }
        Ok(best)
    }
    /// Original ordered visual caret movement. Direction magnitude has no effect.
    pub fn move_caret(
        &self,
        input_position: i32,
        trailing_affinity: bool,
        direction: i32,
    ) -> Result<NativeCaretStop, FontError> {
        let mut i = self.caret_index(input_position, trailing_affinity, true)?;
        if direction < 0 && i != 0 {
            i -= 1;
        } else if direction > 0 && i + 1 < self.carets.len() {
            i += 1;
        }
        Ok(self.carets[i])
    }
    fn selection_count(&self, start: i32, end: i32) -> Result<usize, FontError> {
        if end < start {
            return Err(FontError::InvalidArgument);
        }
        let mut count = 0;
        let mut previous_line = u32::MAX;
        let mut previous_right = 0.0;
        for &b in &self.boxes {
            if !valid_box(b) {
                return Err(FontError::InvalidArgument);
            }
            if b.input_end <= start || b.input_start >= end {
                continue;
            }
            let r = b.bounds;
            if count == 0 || b.line_index != previous_line || r.x > previous_right + 0.5 {
                count += 1;
                previous_right = r.x + r.width;
            } else {
                previous_right = source_max(previous_right, r.x + r.width);
            }
            previous_line = b.line_index;
        }
        Ok(count)
    }
    /// Whole-box selection with original same-line half-pixel merge policy.
    /// Validate every box and complete capacity before changing any output.
    pub fn selection_into(
        &self,
        start: i32,
        end: i32,
        rectangles: &mut [NativeTextRectangle],
    ) -> Result<usize, FontError> {
        let count = self.selection_count(start, end)?;
        if rectangles.len() < count {
            return Err(FontError::Capacity);
        }
        let mut written = 0;
        let mut previous_line = u32::MAX;
        for &b in &self.boxes {
            if b.input_end <= start || b.input_start >= end {
                continue;
            }
            let r = b.bounds;
            if written != 0
                && b.line_index == previous_line
                && r.x <= rectangles[written - 1].x + rectangles[written - 1].width + 0.5
            {
                let p = &mut rectangles[written - 1];
                let right = source_max(p.x + p.width, r.x + r.width);
                p.x = source_min(p.x, r.x);
                p.width = right - p.x;
            } else {
                rectangles[written] = r;
                written += 1;
            }
            previous_line = b.line_index;
        }
        Ok(written)
    }
    pub fn selection(&self, start: i32, end: i32) -> Result<Vec<NativeTextRectangle>, FontError> {
        let count = self.selection_count(start, end)?;
        let mut output = filled(count, NativeTextRectangle::default())?;
        let n = self.selection_into(start, end, &mut output)?;
        output.truncate(n);
        Ok(output)
    }
    /// Full-capacity copy admission precedes either output write.
    pub fn copy_into(
        &self,
        boxes: &mut [NativeClusterBox],
        carets: &mut [NativeCaretStop],
    ) -> Result<(), FontError> {
        if boxes.len() < self.boxes.len() || carets.len() < self.carets.len() {
            return Err(FontError::Capacity);
        }
        boxes[..self.boxes.len()].copy_from_slice(&self.boxes);
        carets[..self.carets.len()].copy_from_slice(&self.carets);
        Ok(())
    }
    /// Original retained transport admission compares every coordinate bit,
    /// including signed zero, against this generation's authoritative writer.
    pub fn validate_retained_records(
        &self,
        boxes: &[NativeClusterBox],
        carets: &[NativeCaretStop],
    ) -> Result<(), FontError> {
        let exact = |a: f32, b: f32| a.to_bits() == b.to_bits();
        if boxes.len() != self.boxes.len() || carets.len() != self.carets.len() {
            return Err(FontError::InvalidArgument);
        }
        for (a, b) in boxes.iter().zip(&self.boxes) {
            let x = a.bounds;
            let y = b.bounds;
            if a.input_start != b.input_start
                || a.input_end != b.input_end
                || a.line_index != b.line_index
                || a.bidi_level != b.bidi_level
                || !exact(x.x, y.x)
                || !exact(x.y, y.y)
                || !exact(x.width, y.width)
                || !exact(x.height, y.height)
            {
                return Err(FontError::InvalidArgument);
            }
        }
        for (a, b) in carets.iter().zip(&self.carets) {
            if a.input_position != b.input_position
                || a.line_index != b.line_index
                || a.bidi_level != b.bidi_level
                || a.trailing != b.trailing
                || !exact(a.position[0], b.position[0])
                || !exact(a.position[1], b.position[1])
                || !exact(a.height, b.height)
            {
                return Err(FontError::InvalidArgument);
            }
        }
        Ok(())
    }
}
