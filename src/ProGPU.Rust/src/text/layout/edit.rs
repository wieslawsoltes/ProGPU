//! Original explicit EDIT ownership over captured pre-fallback graphemes.
use super::interaction::{ClusterBox, EmptyLine, selection_range};
use super::*;
use std::collections::HashMap;

/// Explicit EDIT interaction, separate from ordinary shaping clusters and
/// navigation. Interior source positions retain their actual owner's trailing
/// edge; unretained multi-grapheme interiors fail at the affected query only.
#[derive(Clone, Debug)]
pub struct TextEditInteractionSnapshot {
    original: TextInteractionSnapshot,
    geometry: TextInteractionSnapshot,
    trailing: Arc<[TextCaretStop]>,
    missing_interior_edges: Arc<[bool]>,
}
impl TextInteractionSnapshot {
    /// Capture original grapheme owners without reshaping or fabricating
    /// intermediate carets. Reject incompatible rows, levels, discontiguous
    /// ownership and ambiguous original trailing edges before publication.
    pub fn create_edit_interaction_snapshot(
        &self,
    ) -> Result<TextEditInteractionSnapshot, TextLayoutError> {
        self.ensure_rows()?;
        let boundaries = self
            .graphemes
            .as_ref()
            .ok_or(TextLayoutError::UnsupportedInteraction)?;
        let mut owners: HashMap<usize, (i32, i8, usize)> = HashMap::new();
        owners
            .try_reserve(boundaries.len())
            .map_err(|_| TextLayoutError::Capacity)?;
        let mut groups: Vec<ClusterBox> = Vec::new();
        let mut missing = Vec::new();
        let mut box_groups = Vec::new();
        reserve(&mut groups, self.boxes.len())?;
        reserve(&mut missing, self.boxes.len())?;
        reserve(&mut box_groups, self.boxes.len() + 1)?;
        for &b in self.boxes.iter() {
            let first = find_grapheme(boundaries, b.start)
                .ok_or(TextLayoutError::UnsupportedInteraction)?;
            let last = find_grapheme(boundaries, b.end - 1)
                .ok_or(TextLayoutError::UnsupportedInteraction)?;
            if b.row < 0 || last < first {
                return Err(TextLayoutError::UnsupportedInteraction);
            }
            if first != last {
                if b.start != boundaries[first] || b.end != boundaries[last + 1] {
                    return Err(TextLayoutError::UnsupportedInteraction);
                }
                for owner in first..=last {
                    if owners.contains_key(&owner) {
                        return Err(TextLayoutError::UnsupportedInteraction);
                    }
                    owners.insert(owner, (b.row, b.level, groups.len()));
                }
                box_groups.push(groups.len());
                groups.push(b);
                missing.push(true);
                continue;
            }
            let mut joins_previous = false;
            if let Some(&(row, level, group)) = owners.get(&first) {
                if missing[group] || row != b.row || level != b.level || group != groups.len() - 1 {
                    return Err(TextLayoutError::UnsupportedInteraction);
                }
                joins_previous = true;
            }
            let start = boundaries[first];
            let end = boundaries[last + 1];
            let group = if joins_previous {
                groups.len() - 1
            } else {
                groups.len()
            };
            box_groups.push(group);
            owners.insert(first, (b.row, b.level, group));
            if joins_previous {
                let previous = groups
                    .last_mut()
                    .ok_or(TextLayoutError::UnsupportedInteraction)?;
                if start < previous.end && end > previous.start {
                    if previous.row != b.row
                        || previous.level != b.level
                        || previous.bounds.y != b.bounds.y
                        || previous.bounds.height != b.bounds.height
                        || b.bounds.x > previous.bounds.right()
                        || b.bounds.right() < previous.bounds.x
                    {
                        return Err(TextLayoutError::UnsupportedInteraction);
                    }
                    let left = min(previous.bounds.x, b.bounds.x);
                    let right = max(previous.bounds.right(), b.bounds.right());
                    *previous = ClusterBox {
                        start: previous.start.min(start),
                        end: previous.end.max(end),
                        level: b.level,
                        bounds: TextBounds {
                            x: left,
                            y: b.bounds.y,
                            width: right - left,
                            height: b.bounds.height,
                        },
                        row: b.row,
                    };
                    continue;
                }
            }
            groups.push(ClusterBox { start, end, ..b });
            missing.push(false);
        }
        box_groups.push(groups.len());
        let mut retained: HashMap<(i32, i32, i8), TextCaretStop> = HashMap::new();
        retained
            .try_reserve(self.carets.len())
            .map_err(|_| TextLayoutError::Capacity)?;
        for (&caret, &row) in self.carets.iter().zip(self.caret_rows.iter()) {
            if !caret.is_trailing {
                continue;
            }
            let key = (caret.text_position, row, caret.bidi_level);
            if let Some(previous) = retained.get(&key)
                && (previous.position != caret.position || previous.height != caret.height)
            {
                return Err(TextLayoutError::UnsupportedInteraction);
            }
            retained.insert(key, caret);
        }
        let mut trailing = Vec::new();
        reserve(&mut trailing, groups.len())?;
        for b in &groups {
            trailing.push(
                *retained
                    .get(&(b.end, b.row, b.level))
                    .ok_or(TextLayoutError::UnsupportedInteraction)?,
            );
        }
        let mut empty = Vec::new();
        reserve(&mut empty, self.empty_lines.len())?;
        for &line in self.empty_lines.iter() {
            empty.push(EmptyLine {
                before: *box_groups
                    .get(line.before)
                    .ok_or(TextLayoutError::UnsupportedInteraction)?,
                ..line
            });
        }
        let geometry = TextInteractionSnapshot::new(
            self.text_length,
            self.empty_height,
            groups.into(),
            empty.into(),
            self.horizontal,
            self.source_row_starts.clone(),
            None,
        )?;
        Ok(TextEditInteractionSnapshot {
            original: self.clone(),
            geometry,
            trailing: trailing.into(),
            missing_interior_edges: missing.into(),
        })
    }
}
impl TextEditInteractionSnapshot {
    /// Original UTF-16 length.
    pub fn text_length(&self) -> i32 {
        self.geometry.text_length
    }
    /// Exact original source endpoint, preserving its caller affinity; only an
    /// owned grapheme interior maps to that owner's retained trailing edge.
    pub fn caret_stop(
        &self,
        position: i32,
        trailing: bool,
    ) -> Result<TextCaretStop, TextLayoutError> {
        self.geometry.validate_position(position)?;
        if let Some(i) = self.interior(position) {
            self.require_edge(i)?;
            return Ok(TextCaretStop {
                text_position: position,
                is_trailing: trailing,
                ..self.trailing[i]
            });
        }
        let caret = self.original.caret_stop(position, trailing);
        if caret.text_position != position {
            return Err(TextLayoutError::UnsupportedInteraction);
        }
        Ok(caret)
    }
    /// Original hit against retained grapheme owners, failing only when the
    /// selected owner needs an unavailable interior contract.
    pub fn hit_test_point(&self, point: [f32; 2]) -> Result<TextHitTestResult, TextLayoutError> {
        let (hit, index) = self.geometry.hit_with_box(point);
        if let Some(i) = index {
            self.require_edge(i)?;
        }
        Ok(hit)
    }
    /// Retained source point; original grapheme interiors use their trailing X
    /// with the owner's actual top, without moving the original source index.
    pub fn source_position_point(&self, position: i32) -> Result<[f32; 2], TextLayoutError> {
        self.geometry.validate_position(position)?;
        if let Some(i) = self.interior(position) {
            self.require_edge(i)?;
            return Ok([
                self.trailing[i].position[0],
                self.geometry.boxes[i].bounds.y,
            ]);
        }
        self.original.source_position_point(position)
    }
    /// Original whole-owner selection. Unavailable interiors reject only when
    /// a selected endpoint resolves inside that owner.
    pub fn selection_rectangles(
        &self,
        text_start: i32,
        text_length: i32,
    ) -> Result<Vec<TextBounds>, TextLayoutError> {
        let (start, end) = selection_range(text_start, text_length, self.text_length());
        if start != end {
            for (i, b) in self.geometry.boxes.iter().enumerate() {
                if (start > b.start && start < b.end) || (end > b.start && end < b.end) {
                    self.require_edge(i)?;
                }
            }
        }
        self.geometry.selection_rectangles(text_start, text_length)
    }
    fn interior(&self, position: i32) -> Option<usize> {
        self.geometry
            .boxes
            .iter()
            .position(|b| position > b.start && position < b.end)
    }
    fn require_edge(&self, at: usize) -> Result<(), TextLayoutError> {
        if self.missing_interior_edges[at] {
            return Err(TextLayoutError::UnsupportedInteraction);
        }
        Ok(())
    }
}
fn find_grapheme(boundaries: &[i32], position: i32) -> Option<usize> {
    if position < 0 || boundaries.len() < 2 || position >= *boundaries.last()? {
        return None;
    }
    Some(match boundaries.binary_search(&position) {
        Ok(i) => i,
        Err(i) => i - 1,
    })
}
