//! Original managed UTF-16 paragraph projection, inline isolates and L2 ordering.
//! Retains the exact source units; malformed surrogates stay original raw units.
use super::*;
use crate::text::script_actions::ManagedShapingDirection as Direction;
use std::sync::Arc;

/// One original consecutive level run in unchanged UTF-16 source coordinates.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedBidiRun {
    /// First original code-unit slot.
    pub start: usize,
    /// Original number of code units, including both halves of paired scalars.
    pub length: usize,
    /// Resolved signed source embedding level.
    pub level: i8,
}
impl ManagedBidiRun {
    /// Original odd-level direction predicate.
    pub fn is_right_to_left(self) -> bool {
        self.level & 1 != 0
    }
}
/// Immutable source-owned managed paragraph, separate from strict scalar text.
#[derive(Clone, Debug)]
pub struct ManagedBidiParagraph {
    text: Arc<[u16]>,
    level: i8,
    levels: Vec<i8>,
    runs: Vec<ManagedBidiRun>,
}
impl ManagedBidiParagraph {
    /// Resolve the original managed paragraph; None/vertical infer base level.
    /// Non-RTL ASCII retains the original level-zero fast path, including controls.
    pub fn resolve(
        text: impl Into<Arc<[u16]>>,
        direction: Option<Direction>,
    ) -> Result<Self, UnicodeError> {
        let text = text.into();
        if text.len() > i32::MAX as usize {
            return Err(UnicodeError::Capacity);
        }
        if text.is_empty() {
            return Self::create(
                text,
                i8::from(direction == Some(Direction::RightToLeft)),
                Vec::new(),
            );
        }
        if direction != Some(Direction::RightToLeft) && text.iter().all(|&u| u <= 127) {
            let levels = allocate(text.len())?;
            return Self::create(text, 0, levels);
        }
        let requested = match direction {
            Some(Direction::LeftToRight) => 0,
            Some(Direction::RightToLeft) => 1,
            _ => 2,
        };
        let mut scalars = Vec::new();
        scalars
            .try_reserve_exact(text.len())
            .map_err(|_| UnicodeError::Capacity)?;
        let mut at = 0;
        while at < text.len() {
            let start = at;
            let first = text[at];
            at += 1;
            let cp = if (0xd800..=0xdbff).contains(&first)
                && text.get(at).is_some_and(|u| (0xdc00..=0xdfff).contains(u))
            {
                let low = text[at];
                at += 1;
                0x10000 + ((u32::from(first) - 0xd800) << 10) + (u32::from(low) - 0xdc00)
            } else {
                u32::from(first)
            };
            scalars.push(UnicodeScalar {
                code_point: cp,
                input_index: start as u32,
                input_length: (at - start) as u16,
                ..UnicodeScalar::default()
            });
        }
        let mut scratch = BidiWorkspace::new(scalars.len())?;
        let mut resolved = allocate(scalars.len())?;
        let result = scratch.resolve_into(&scalars, requested, &mut resolved)?;
        let mut levels = allocate(text.len())?;
        for scalar in resolved {
            let start = scalar.input_index as usize;
            levels[start..start + usize::from(scalar.input_length)].fill(scalar.level);
        }
        Self::create(text, result.paragraph_level, levels)
    }
    /// Original higher-level inline directions, represented only in temporary
    /// synthetic isolates. Retained text/source indices never acquire controls.
    pub fn resolve_inline(
        text: impl Into<Arc<[u16]>>,
        directions: &[Option<Direction>],
        base: Option<Direction>,
    ) -> Result<Self, UnicodeError> {
        let text = text.into();
        if directions.len() != text.len() {
            return Err(UnicodeError::InvalidArgument);
        }
        let mut active = None;
        let mut count = 0usize;
        for &direction in directions {
            let direction = normalize(direction);
            if direction != active {
                if direction.is_some() {
                    count += 1;
                }
                active = direction;
            }
        }
        if count == 0 {
            return Self::resolve(text, base);
        }
        let size = text
            .len()
            .checked_add(count.checked_mul(2).ok_or(UnicodeError::Capacity)?)
            .filter(|n| *n <= i32::MAX as usize)
            .ok_or(UnicodeError::Capacity)?;
        let mut expanded = Vec::new();
        expanded
            .try_reserve_exact(size)
            .map_err(|_| UnicodeError::Capacity)?;
        let mut projection = Vec::new();
        projection
            .try_reserve_exact(text.len())
            .map_err(|_| UnicodeError::Capacity)?;
        active = None;
        for (i, &unit) in text.iter().enumerate() {
            let direction = normalize(directions[i]);
            if direction != active {
                if active.is_some() {
                    expanded.push(0x2069);
                }
                if let Some(direction) = direction {
                    expanded.push(if direction == Direction::RightToLeft {
                        0x2067
                    } else {
                        0x2066
                    });
                }
                active = direction;
            }
            projection.push(expanded.len());
            expanded.push(unit);
        }
        if active.is_some() {
            expanded.push(0x2069);
        }
        let result = Self::resolve(expanded, base)?;
        let mut levels = allocate(text.len())?;
        for (i, &slot) in projection.iter().enumerate() {
            levels[i] = result.levels[slot];
        }
        Self::create(text, result.level, levels)
    }
    fn create(text: Arc<[u16]>, level: i8, levels: Vec<i8>) -> Result<Self, UnicodeError> {
        let count =
            usize::from(!levels.is_empty()) + levels.windows(2).filter(|w| w[0] != w[1]).count();
        let mut runs = Vec::new();
        runs.try_reserve_exact(count)
            .map_err(|_| UnicodeError::Capacity)?;
        let mut start = 0;
        while start < levels.len() {
            let level = levels[start];
            let mut end = start + 1;
            while end < levels.len() && levels[end] == level {
                end += 1;
            }
            runs.push(ManagedBidiRun {
                start,
                length: end - start,
                level,
            });
            start = end;
        }
        Ok(Self {
            text,
            level,
            levels,
            runs,
        })
    }
    /// Exact original UTF-16 source; malformed units remain unchanged.
    pub fn text(&self) -> &[u16] {
        &self.text
    }
    /// Original paragraph embedding level.
    pub fn paragraph_level(&self) -> i8 {
        self.level
    }
    /// One resolved level per original UTF-16 unit.
    pub fn levels(&self) -> &[i8] {
        &self.levels
    }
    /// Original logical run order, before line-level L2 reordering.
    pub fn runs(&self) -> &[ManagedBidiRun] {
        &self.runs
    }
}
fn normalize(direction: Option<Direction>) -> Option<Direction> {
    direction.filter(|d| matches!(d, Direction::LeftToRight | Direction::RightToLeft))
}
/// Original visual-to-logical L2 map for an already-broken line. None is the
/// identity map. Raw signed levels retain original sentinel and comparison rules.
/// O(N * L) work for N units and at most 255 visited levels; O(N) output only.
pub fn managed_visual_order_if_needed(levels: &[i8]) -> Result<Option<Vec<usize>>, UnicodeError> {
    let (mut maximum, mut lowest_odd) = (0i8, i8::MAX);
    for &level in levels {
        maximum = maximum.max(level);
        if level & 1 != 0 {
            lowest_odd = lowest_odd.min(level);
        }
    }
    if lowest_odd == i8::MAX {
        return Ok(None);
    }
    let mut order = Vec::new();
    order
        .try_reserve_exact(levels.len())
        .map_err(|_| UnicodeError::Capacity)?;
    order.extend(0..levels.len());
    for level in (i32::from(lowest_odd)..=i32::from(maximum)).rev() {
        let mut start = 0;
        while start < order.len() {
            while start < order.len() && i32::from(levels[order[start]]) < level {
                start += 1;
            }
            let mut end = start;
            while end < order.len() && i32::from(levels[order[end]]) >= level {
                end += 1;
            }
            order[start..end].reverse();
            start = end;
        }
    }
    Ok(Some(order))
}
/// Original L2 map with an allocated identity inventory when no reversal is needed.
pub fn managed_visual_order(levels: &[i8]) -> Result<Vec<usize>, UnicodeError> {
    if let Some(order) = managed_visual_order_if_needed(levels)? {
        return Ok(order);
    }
    let mut order = Vec::new();
    order
        .try_reserve_exact(levels.len())
        .map_err(|_| UnicodeError::Capacity)?;
    order.extend(0..levels.len());
    Ok(order)
}
