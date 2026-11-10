//! Original native fragment cluster boxes, caret navigation and selection.
//! Cluster ends, resolved bidi levels and advance origins are actual caller
//! inputs, independent of outlines, shaping or inferred fragment bounds.
use super::*;
mod navigation;
mod owner;
mod queries;
mod writer;
pub use navigation::native_move_fragment_caret;
pub use owner::NativeFragmentInteraction;
pub use queries::{
    native_fragment_caret, native_fragment_caret_index, native_fragment_hit_test,
    native_fragment_move_visual_caret, native_fragment_selection_into,
    native_fragment_selection_requirements,
};

/// Original physical navigation directions; paragraph direction only affects
/// horizontal movement across rows, never physical left/right within one row.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeFragmentCaretDirection {
    Left = 0,
    Right = 1,
    Up = 2,
    Down = 3,
}

/// Positioned boxes use original glyph positions. Advance boxes use the
/// original caller's line origin and ordered advance sum, excluding offsets.
#[derive(Clone, Copy, Debug)]
pub enum NativeFragmentCoordinates<'a> {
    Positioned,
    AdvanceOrigins(&'a [f32]),
}

/// Complete immutable native inputs. The caller supplies already-resolved
/// cluster ends and bidi levels in positioned occurrence order, not source order.
#[derive(Clone, Copy, Debug)]
pub struct NativeFragmentInteractionInput<'a> {
    pub glyphs: &'a [NativeParagraphPositionedRecord],
    pub lines: &'a [NativePositionedTextLine],
    pub placements: &'a [NativeFragmentPlacement],
    pub cluster_ends: &'a [i32],
    pub bidi_levels: &'a [i8],
    pub coordinates: NativeFragmentCoordinates<'a>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeFragmentInteractionRequirements {
    pub cluster_box_capacity: u32,
    /// Original two-per-cluster upper bound, before caret deduplication.
    pub caret_stop_capacity: u32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeFragmentInteractionCounts {
    pub cluster_box_count: u32,
    pub caret_stop_count: u32,
}

// Preserve std::min/std::max first-operand ties, including signed zero.
fn min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn valid_box(b: NativeClusterBox) -> bool {
    let r = b.bounds;
    [r.x, r.y, r.width, r.height, r.x + r.width, r.y + r.height]
        .into_iter()
        .all(f32::is_finite)
        && r.width >= 0.0
        && r.height >= 0.0
        && b.input_end > b.input_start
        && (0..=125).contains(&b.bidi_level)
}
fn valid_caret(c: NativeCaretStop) -> bool {
    c.position.into_iter().all(f32::is_finite)
        && c.height.is_finite()
        && c.height >= 0.0
        && (0..=125).contains(&c.bidi_level)
}

/// Shared original fragment frame admission. Construction additionally supplies
/// lines for identical same-row baselines/heights and nonoverlapping row bands;
/// navigation preserves the original weaker, independent frame-only contract.
fn validate_placements(
    placements: &[NativeFragmentPlacement],
    lines: Option<&[NativePositionedTextLine]>,
) -> Result<(), FontError> {
    if placements.len() > u32::MAX as usize || lines.is_some_and(|v| v.len() != placements.len()) {
        return Err(FontError::InvalidArgument);
    }
    let mut row_direction = 0;
    for (i, &f) in placements.iter().enumerate() {
        if !f.left.is_finite()
            || !f.top.is_finite()
            || !f.width.is_finite()
            || f.width <= 0.0
            || !(f.left + f.width).is_finite()
        {
            return Err(FontError::InvalidArgument);
        }
        if i == 0 {
            if f.row_index != 0 {
                return Err(FontError::InvalidArgument);
            }
            continue;
        }
        let p = placements[i - 1];
        if f.row_index == p.row_index {
            if f.top != p.top
                || lines.is_some_and(|v| {
                    v[i].height != v[i - 1].height || v[i].baseline_y != v[i - 1].baseline_y
                })
            {
                return Err(FontError::InvalidArgument);
            }
            let direction = if f.left >= p.left + p.width {
                1
            } else if f.left + f.width <= p.left {
                -1
            } else {
                0
            };
            if direction == 0 || row_direction != 0 && direction != row_direction {
                return Err(FontError::InvalidArgument);
            }
            row_direction = direction;
        } else {
            let bottom = p.top + lines.map_or(0.0, |v| f64::from(v[i - 1].height));
            if p.row_index.checked_add(1) != Some(f.row_index) || f.top < bottom {
                return Err(FontError::InvalidArgument);
            }
            row_direction = 0;
        }
    }
    Ok(())
}
