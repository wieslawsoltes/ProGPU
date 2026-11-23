//! Original writer-owned interaction, independent of glyph ink and GPU residency.
use super::*;
use std::cmp::Ordering;

/// Original axis-aligned interaction extent.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TextBounds {
    /// Left edge.
    pub x: f32,
    /// Top edge.
    pub y: f32,
    /// Original width.
    pub width: f32,
    /// Original height.
    pub height: f32,
}
impl TextBounds {
    /// Original floating right-edge addition.
    pub fn right(self) -> f32 {
        self.x + self.width
    }
    /// Original floating bottom-edge addition.
    pub fn bottom(self) -> f32 {
        self.y + self.height
    }
}
/// One retained original source caret, including bidi affinity.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TextCaretStop {
    /// Original UTF-16 insertion index.
    pub text_position: i32,
    /// Original trailing affinity.
    pub is_trailing: bool,
    /// Actual writer-owned physical edge.
    pub position: [f32; 2],
    /// Actual writer-owned row height.
    pub height: f32,
    /// Original resolved source level.
    pub bidi_level: i8,
}
/// Original point hit against retained advance intervals, not glyph ink.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TextHitTestResult {
    /// Original source insertion position.
    pub text_position: i32,
    /// Logical trailing side of selected cluster.
    pub is_trailing_hit: bool,
    /// Point lies inside the selected interval and row.
    pub is_inside: bool,
    /// Actual retained cluster extent.
    pub bounds: TextBounds,
    /// Original source level.
    pub bidi_level: i8,
}
/// Point hit and complete original shaping-cluster range.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TextClusterHitTestResult {
    /// Original selected hit.
    pub hit: TextHitTestResult,
    /// Original UTF-16 start.
    pub cluster_start: i32,
    /// Original UTF-16 length; zero for an empty row.
    pub cluster_length: i32,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct ClusterBox {
    pub start: i32,
    pub end: i32,
    pub level: i8,
    pub bounds: TextBounds,
    pub row: i32,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct EmptyLine {
    pub before: usize,
    pub caret: TextCaretStop,
    pub row: i32,
}
/// Immutable owned geometry from one completed layout generation. Clones share
/// arrays; queries never shape fonts, mutate glyphs or reconstruct source rows.
#[derive(Clone, Debug)]
pub struct TextInteractionSnapshot {
    pub(super) text_length: i32,
    pub(super) empty_height: f32,
    pub(super) boxes: Arc<[ClusterBox]>,
    pub(super) empty_lines: Arc<[EmptyLine]>,
    pub(super) carets: Arc<[TextCaretStop]>,
    pub(super) caret_rows: Arc<[i32]>,
    pub(super) horizontal: bool,
    pub(super) source_row_starts: Arc<[i32]>,
    pub(super) graphemes: Option<Arc<[i32]>>,
}
impl ManagedTextLayout {
    /// Freeze complete advance geometry and original row membership without
    /// retaining mutable glyph collections or depending on atlas realization.
    pub fn create_interaction_snapshot(&self) -> Result<TextInteractionSnapshot, TextLayoutError> {
        let mut clusters = Vec::new();
        reserve(&mut clusters, self.glyphs.len())?;
        clusters.extend(self.glyphs.iter().map(|g| g.cluster));
        clusters.sort_unstable();
        clusters.dedup();
        let cluster_end = |start: i32| {
            let index = clusters
                .binary_search(&start)
                .expect("cluster belongs to immutable glyph inventory");
            (start + 1).max(
                clusters
                    .get(index + 1)
                    .copied()
                    .unwrap_or(self.text.len() as i32),
            )
        };
        let mut boxes = Vec::new();
        let mut empty = Vec::new();
        reserve(&mut boxes, self.glyphs.len())?;
        reserve(&mut empty, self.lines.len())?;
        if !self.lines.is_empty() {
            for (row, line) in self.lines.iter().enumerate() {
                if line.count == 0 {
                    empty.push(EmptyLine {
                        before: boxes.len(),
                        row: row as i32,
                        caret: TextCaretStop {
                            text_position: line.source_start,
                            is_trailing: false,
                            position: [line.left, line.top],
                            height: line.height,
                            bidi_level: line.paragraph_level,
                        },
                    });
                }
                let mut pen = line.left;
                let mut at = line.start;
                let end = at + line.count;
                while at < end {
                    let first = &self.glyphs[at];
                    let mut left = pen;
                    let mut right = pen;
                    loop {
                        let next = pen + self.glyphs[at].advance;
                        left = min(left, min(pen, next));
                        right = max(right, max(pen, next));
                        pen = next;
                        at += 1;
                        if at == end || self.glyphs[at].cluster != first.cluster {
                            break;
                        }
                    }
                    boxes.push(ClusterBox {
                        start: first.cluster,
                        end: cluster_end(first.cluster).min(line.source_end),
                        level: first.bidi_level,
                        bounds: TextBounds {
                            x: left,
                            y: line.top,
                            width: max(0.0, right - left),
                            height: line.height,
                        },
                        row: row as i32,
                    });
                }
            }
        } else {
            let mut at = 0;
            while at < self.glyphs.len() {
                let first = &self.glyphs[at];
                let cluster = first.cluster;
                let baseline = first.position[1];
                let mut left = first.position[0];
                let mut right = left + max(0.0, first.advance);
                let scale = self.font_size / f32::from(first.font.metadata().units_per_em);
                let mut top = baseline - f32::from(first.font.metadata().ascender) * scale;
                let mut bottom = top + max(first.height as f32, self.font_size);
                at += 1;
                while at < self.glyphs.len()
                    && self.glyphs[at].cluster == cluster
                    && (self.glyphs[at].position[1] - baseline).abs() < 0.01
                {
                    let g = &self.glyphs[at];
                    at += 1;
                    left = min(left, g.position[0]);
                    right = max(right, g.position[0] + max(0.0, g.advance));
                    let scale = self.font_size / f32::from(g.font.metadata().units_per_em);
                    let glyph_top = g.position[1] - f32::from(g.font.metadata().ascender) * scale;
                    top = min(top, glyph_top);
                    bottom = max(bottom, glyph_top + max(g.height as f32, self.font_size));
                }
                boxes.push(ClusterBox {
                    start: cluster,
                    end: cluster_end(cluster),
                    level: first.bidi_level,
                    bounds: TextBounds {
                        x: left,
                        y: top,
                        width: max(0.0, right - left),
                        height: max(1.0, bottom - top),
                    },
                    row: -1,
                });
            }
        }
        let mut rows = Vec::new();
        reserve(&mut rows, self.lines.len().max(1))?;
        rows.extend(self.lines.iter().map(|l| l.source_start));
        if rows.is_empty() && self.text.is_empty() {
            rows.push(0);
        }
        TextInteractionSnapshot::new(
            self.text.len() as i32,
            self.font_size,
            boxes.into(),
            empty.into(),
            !self.vertical(),
            rows.into(),
            self.original_grapheme_boundaries.clone(),
        )
    }
}
impl TextInteractionSnapshot {
    pub(super) fn new(
        text_length: i32,
        empty_height: f32,
        boxes: Arc<[ClusterBox]>,
        empty_lines: Arc<[EmptyLine]>,
        horizontal: bool,
        source_row_starts: Arc<[i32]>,
        graphemes: Option<Arc<[i32]>>,
    ) -> Result<Self, TextLayoutError> {
        let mut carets = Vec::new();
        let mut rows = Vec::new();
        let count = boxes
            .len()
            .checked_mul(2)
            .and_then(|v| v.checked_add(empty_lines.len()))
            .ok_or(TextLayoutError::Capacity)?
            .max(1);
        reserve(&mut carets, count)?;
        reserve(&mut rows, count)?;
        if boxes.is_empty() && empty_lines.is_empty() {
            carets.push(TextCaretStop {
                height: max(0.0, empty_height),
                ..TextCaretStop::default()
            });
            rows.push(0);
        } else {
            let mut empty_at = 0;
            for at in 0..=boxes.len() {
                while empty_at < empty_lines.len() && empty_lines[empty_at].before == at {
                    rows.push(empty_lines[empty_at].row);
                    carets.push(empty_lines[empty_at].caret);
                    empty_at += 1;
                }
                if at == boxes.len() {
                    break;
                }
                let b = boxes[at];
                let rtl = b.level & 1 != 0;
                carets.push(TextCaretStop {
                    text_position: if rtl { b.end } else { b.start },
                    is_trailing: rtl,
                    position: [b.bounds.x, b.bounds.y],
                    height: b.bounds.height,
                    bidi_level: b.level,
                });
                carets.push(TextCaretStop {
                    text_position: if rtl { b.start } else { b.end },
                    is_trailing: !rtl,
                    position: [b.bounds.right(), b.bounds.y],
                    height: b.bounds.height,
                    bidi_level: b.level,
                });
                rows.extend([b.row, b.row]);
            }
            // Reverse deletion in the source compares original adjacent pairs.
            // A single compaction pass retains that same predecessor (including
            // a predecessor just removed), avoiding quadratic Vec removal.
            let mut previous: Option<(TextCaretStop, i32)> = None;
            let mut write = 0;
            for read in 0..carets.len() {
                let current = carets[read];
                let row = rows[read];
                let duplicate = previous.is_some_and(|(p, r)| {
                    let dx = current.position[0] - p.position[0];
                    let dy = current.position[1] - p.position[1];
                    current.text_position == p.text_position
                        && current.is_trailing == p.is_trailing
                        && row == r
                        && dx * dx + dy * dy < 0.0001
                });
                previous = Some((current, row));
                if !duplicate {
                    carets[write] = current;
                    rows[write] = row;
                    write += 1;
                }
            }
            carets.truncate(write);
            rows.truncate(write);
        }
        Ok(Self {
            text_length,
            empty_height,
            boxes,
            empty_lines,
            carets: carets.into(),
            caret_rows: rows.into(),
            horizontal,
            source_row_starts,
            graphemes,
        })
    }
    /// Original UTF-16 source length.
    pub fn text_length(&self) -> i32 {
        self.text_length
    }
    /// Complete physical caret order, retaining both distinct bidi affinities.
    pub fn caret_stops(&self) -> &[TextCaretStop] {
        &self.carets
    }
    /// Actual horizontal row count, including blank and trailing rows.
    pub fn row_count(&self) -> Result<usize, TextLayoutError> {
        self.ensure_rows()?;
        Ok(self.source_row_starts.len())
    }
    /// Original source start of a physical row, including soft wraps.
    pub fn row_source_start(&self, row: usize) -> Result<i32, TextLayoutError> {
        self.ensure_rows()?;
        self.source_row_starts
            .get(row)
            .copied()
            .ok_or(TextLayoutError::InvalidArgument)
    }
    /// Source row ownership: CR/LF belongs to the preceding row, a soft-wrap
    /// boundary to the following row, and terminal insertion to the last row.
    pub fn row_index_from_text_position(&self, position: i32) -> Result<usize, TextLayoutError> {
        self.ensure_rows()?;
        self.validate_position(position)?;
        Ok(match self.source_row_starts.binary_search(&position) {
            Ok(i) => i,
            Err(i) => i - 1,
        })
    }
    /// Actual caret row, retaining affinity at a shared wrap boundary.
    pub fn caret_row_index(&self, position: i32, trailing: bool) -> Result<i32, TextLayoutError> {
        self.ensure_rows()?;
        Ok(self.caret_rows[self.find_caret(position, trailing)])
    }
    /// Leading edge of the owning shaped cluster, or retained row end for hard
    /// delimiters. Never creates an interior-cluster caret.
    pub fn source_position_point(&self, position: i32) -> Result<[f32; 2], TextLayoutError> {
        let row = self.row_index_from_text_position(position)? as i32;
        for b in self.boxes.iter() {
            if b.row == row && position >= b.start && position < b.end {
                return Ok([
                    if b.level & 1 != 0 {
                        b.bounds.right()
                    } else {
                        b.bounds.x
                    },
                    b.bounds.y,
                ]);
            }
        }
        let mut first: Option<TextCaretStop> = None;
        let mut last: Option<TextCaretStop> = None;
        for (&caret, &r) in self.carets.iter().zip(self.caret_rows.iter()) {
            if r != row {
                continue;
            }
            if first.is_none_or(|f| {
                caret.text_position < f.text_position
                    || (caret.text_position == f.text_position && !caret.is_trailing)
            }) {
                first = Some(caret);
            }
            if last.is_none_or(|l| {
                caret.text_position > l.text_position
                    || (caret.text_position == l.text_position && caret.is_trailing)
            }) {
                last = Some(caret);
            }
        }
        let first = first.ok_or(TextLayoutError::UnsupportedInteraction)?;
        Ok(if position < first.text_position {
            first.position
        } else {
            last.ok_or(TextLayoutError::UnsupportedInteraction)?
                .position
        })
    }
    pub(super) fn ensure_rows(&self) -> Result<(), TextLayoutError> {
        self.ensure_horizontal()?;
        if self.source_row_starts.is_empty() {
            return Err(TextLayoutError::UnsupportedInteraction);
        }
        Ok(())
    }
    fn ensure_horizontal(&self) -> Result<(), TextLayoutError> {
        if !self.horizontal {
            return Err(TextLayoutError::UnsupportedInteraction);
        }
        Ok(())
    }
    pub(super) fn validate_position(&self, position: i32) -> Result<(), TextLayoutError> {
        if position < 0 || position > self.text_length {
            return Err(TextLayoutError::InvalidArgument);
        }
        Ok(())
    }
    /// Nearest original retained hit, with horizontal row-band precedence.
    pub fn hit_test_point(&self, point: [f32; 2]) -> TextHitTestResult {
        self.hit_with_box(point).0
    }
    /// Original hit together with complete cluster ownership.
    pub fn hit_test_cluster(&self, point: [f32; 2]) -> TextClusterHitTestResult {
        let (hit, index) = self.hit_with_box(point);
        if let Some(i) = index {
            TextClusterHitTestResult {
                hit,
                cluster_start: self.boxes[i].start,
                cluster_length: self.boxes[i].end - self.boxes[i].start,
            }
        } else {
            TextClusterHitTestResult {
                hit,
                cluster_start: hit.text_position,
                cluster_length: 0,
            }
        }
    }
    pub(super) fn hit_with_box(&self, point: [f32; 2]) -> (TextHitTestResult, Option<usize>) {
        let mut nearest_row = f32::INFINITY;
        let mut selected_row = -1;
        let mut selected_owns_y = false;
        for b in self.boxes.iter() {
            let distance = vertical_distance(point[1], b.bounds.y, b.bounds.height);
            let owns_y = point[1] >= b.bounds.y && point[1] < b.bounds.bottom();
            if distance < nearest_row || (distance == nearest_row && owns_y && !selected_owns_y) {
                nearest_row = distance;
                selected_row = b.row;
                selected_owns_y = owns_y;
            }
        }
        if let Some(first) = self.empty_lines.first() {
            let mut nearest_empty = f32::INFINITY;
            let mut empty = first.caret;
            for line in self.empty_lines.iter() {
                let caret = line.caret;
                let distance = vertical_distance(point[1], caret.position[1], caret.height);
                let owns_y =
                    point[1] >= caret.position[1] && point[1] < caret.position[1] + caret.height;
                if distance < nearest_empty || (distance == nearest_empty && owns_y) {
                    nearest_empty = distance;
                    empty = caret;
                }
            }
            let contains_y =
                point[1] >= empty.position[1] && point[1] < empty.position[1] + empty.height;
            if self.boxes.is_empty() || nearest_empty < nearest_row || contains_y {
                return (
                    TextHitTestResult {
                        text_position: empty.text_position,
                        is_trailing_hit: empty.is_trailing,
                        is_inside: false,
                        bounds: TextBounds {
                            x: empty.position[0],
                            y: empty.position[1],
                            width: 0.0,
                            height: empty.height,
                        },
                        bidi_level: empty.bidi_level,
                    },
                    None,
                );
            }
        }
        let Some(first) = self.boxes.first() else {
            return (
                TextHitTestResult {
                    bounds: TextBounds {
                        height: self.empty_height,
                        ..TextBounds::default()
                    },
                    ..TextHitTestResult::default()
                },
                None,
            );
        };
        let mut best_distance = f32::INFINITY;
        let mut best = *first;
        let mut best_index = 0;
        let mut inside = false;
        for (i, b) in self.boxes.iter().enumerate() {
            if selected_row >= 0 && b.row != selected_row {
                continue;
            }
            let dx = if point[0] < b.bounds.x {
                b.bounds.x - point[0]
            } else if point[0] > b.bounds.right() {
                point[0] - b.bounds.right()
            } else {
                0.0
            };
            let dy = vertical_distance(point[1], b.bounds.y, b.bounds.height);
            let distance = dx * dx + dy * dy;
            if distance >= best_distance {
                continue;
            }
            best_distance = distance;
            best = *b;
            best_index = i;
            inside = dx == 0.0 && dy == 0.0;
        }
        let visual_right = point[0] >= (best.bounds.x + best.bounds.right()) * 0.5;
        let trailing = if best.level & 1 != 0 {
            !visual_right
        } else {
            visual_right
        };
        (
            TextHitTestResult {
                text_position: if trailing { best.end } else { best.start },
                is_trailing_hit: trailing,
                is_inside: inside,
                bounds: best.bounds,
                bidi_level: best.level,
            },
            Some(best_index),
        )
    }
    fn find_caret(&self, position: i32, trailing: bool) -> usize {
        let mut best = 0;
        let mut best_distance = i64::MAX;
        for (i, c) in self.carets.iter().enumerate() {
            let distance = (i64::from(c.text_position) - i64::from(position)).abs();
            if distance < best_distance
                || (distance == best_distance
                    && c.is_trailing == trailing
                    && self.carets[best].is_trailing != trailing)
            {
                best = i;
                best_distance = distance;
            }
        }
        best
    }
    /// Nearest existing logical source caret, preserving the requested affinity.
    pub fn caret_stop(&self, position: i32, trailing: bool) -> TextCaretStop {
        self.carets[self.find_caret(position, trailing)]
    }
    /// Move through physical writer order, skipping coincident affinities at an
    /// ordinary shared edge while retaining distinct bidi/wrapped positions.
    pub fn move_caret_visually(
        &self,
        position: i32,
        trailing: bool,
        direction: i32,
    ) -> Result<TextCaretStop, TextLayoutError> {
        let mut current = 0;
        let mut best_distance = f32::INFINITY;
        for (i, c) in self.carets.iter().enumerate() {
            let penalty = if c.is_trailing == trailing { 0.0 } else { 0.25 };
            let delta = c
                .text_position
                .wrapping_sub(position)
                .checked_abs()
                .ok_or(TextLayoutError::InvalidArgument)?;
            let distance = delta as f32 * 1000.0 + penalty;
            if distance < best_distance {
                best_distance = distance;
                current = i;
            }
        }
        let step = direction.signum() as isize;
        let origin = self.carets[current];
        if step == 0 {
            return Ok(origin);
        }
        let mut next = current as isize + step;
        while next >= 0 && (next as usize) < self.carets.len() {
            let c = self.carets[next as usize];
            if c.text_position != origin.text_position
                || c.position != origin.position
                || c.bidi_level != origin.bidi_level
            {
                return Ok(c);
            }
            next += step;
        }
        Ok(origin)
    }
    /// Existing logical start/end caret of the current physical row.
    pub fn row_boundary(
        &self,
        position: i32,
        trailing: bool,
        end: bool,
    ) -> Result<TextCaretStop, TextLayoutError> {
        self.ensure_horizontal()?;
        let at = self.find_caret(position, trailing);
        let row = self.caret_rows[at];
        let mut best = self.carets[at];
        for (&c, &r) in self.carets.iter().zip(self.caret_rows.iter()) {
            if r != row {
                continue;
            }
            if (if end {
                c.text_position > best.text_position
            } else {
                c.text_position < best.text_position
            }) || (c.text_position == best.text_position
                && c.is_trailing == end
                && best.is_trailing != end)
            {
                best = c;
            }
        }
        Ok(best)
    }
    /// Move one retained horizontal row to the nearest existing preferred-X
    /// stop, preserving affinity on equal-distance edges.
    pub fn move_caret_vertically(
        &self,
        position: i32,
        trailing: bool,
        direction: i32,
        preferred_x: f32,
    ) -> Result<TextCaretStop, TextLayoutError> {
        self.ensure_horizontal()?;
        if !preferred_x.is_finite() {
            return Err(TextLayoutError::InvalidArgument);
        }
        let at = self.find_caret(position, trailing);
        let mut best = self.carets[at];
        let row = self.caret_rows[at] + direction.signum();
        let mut distance = f64::INFINITY;
        for (&c, &r) in self.carets.iter().zip(self.caret_rows.iter()) {
            if r != row {
                continue;
            }
            let next = (f64::from(c.position[0]) - f64::from(preferred_x)).abs();
            if next < distance
                || (next == distance && c.is_trailing == trailing && best.is_trailing != trailing)
            {
                best = c;
                distance = next;
            }
        }
        Ok(if direction == 0 {
            self.carets[at]
        } else {
            best
        })
    }
    /// Whole-cluster selection with the original same-row merge tolerances.
    pub fn selection_rectangles(
        &self,
        text_start: i32,
        text_length: i32,
    ) -> Result<Vec<TextBounds>, TextLayoutError> {
        let (start, end) = selection_range(text_start, text_length, self.text_length);
        if end <= start {
            return Ok(Vec::new());
        }
        let mut selected = Vec::new();
        reserve(&mut selected, self.boxes.len())?;
        selected.extend(
            self.boxes
                .iter()
                .filter(|b| b.end > start && b.start < end)
                .copied(),
        );
        selected.sort_unstable_by(|a, b| {
            float_compare(a.bounds.y, b.bounds.y)
                .then_with(|| float_compare(a.bounds.x, b.bounds.x))
        });
        let mut result: Vec<TextBounds> = Vec::new();
        reserve(&mut result, selected.len())?;
        for b in selected {
            if let Some(previous) = result.last_mut()
                && (previous.y - b.bounds.y).abs() < 0.01
                && (previous.height - b.bounds.height).abs() < 0.01
                && b.bounds.x <= previous.right() + 0.5
            {
                previous.width = max(previous.right(), b.bounds.right()) - previous.x;
                continue;
            }
            result.push(b.bounds);
        }
        Ok(result)
    }
}
pub(super) fn selection_range(start: i32, length: i32, text_length: i32) -> (i32, i32) {
    let end = start.wrapping_add(length);
    (
        start.min(end).clamp(0, text_length),
        start.max(end).clamp(0, text_length),
    )
}
fn vertical_distance(y: f32, top: f32, height: f32) -> f32 {
    if y < top {
        top - y
    } else if y > top + height {
        y - (top + height)
    } else {
        0.0
    }
}
fn float_compare(a: f32, b: f32) -> Ordering {
    match (a.is_nan(), b.is_nan()) {
        (true, true) => Ordering::Equal,
        (true, false) => Ordering::Less,
        (false, true) => Ordering::Greater,
        (false, false) => a.partial_cmp(&b).unwrap_or(Ordering::Equal),
    }
}
