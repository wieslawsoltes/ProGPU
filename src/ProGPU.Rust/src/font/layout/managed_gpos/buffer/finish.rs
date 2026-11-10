//! Original post-GPOS legacy kerning, visual ordering and Arabic stretch.
//! Traversals retain source order and per-operation saturation. Stretch expansion
//! is bounded by the original 256 records per run and one-million-record total.
use super::*;
use crate::{
    font::ManagedFont,
    text::{
        preparation::MAX_MANAGED_SHAPED_GLYPHS,
        script_actions::managed_modified_combining_class,
        unicode::{GeneralCategory, general_category},
    },
};

impl ManagedPositionBuffer {
    /// Original complete record reversal; attachment resolution precedes this.
    pub fn reverse(&mut self) {
        self.glyphs.reverse();
    }
    /// Original monotone-character repair following backward record reversal.
    /// Only cluster fields move inside modified-combining-class runs.
    pub fn reverse_combining_clusters(&mut self) {
        let mut start = 0;
        while start < self.glyphs.len() {
            while start < self.glyphs.len()
                && managed_modified_combining_class(self.glyphs[start].code_point) == 0
            {
                start += 1;
            }
            let mut end = start;
            while end < self.glyphs.len()
                && managed_modified_combining_class(self.glyphs[end].code_point) != 0
            {
                end += 1;
            }
            for offset in 0..(end - start) / 2 {
                let left = start + offset;
                let right = end - 1 - offset;
                let cluster = self.glyphs[left].cluster;
                self.glyphs[left].cluster = self.glyphs[right].cluster;
                self.glyphs[right].cluster = cluster;
            }
            start = end + 1;
        }
    }
    /// Original Windows/Apple kern formats 0 and 2, including cross-stream
    /// adjustments, mark skipping, signed halves and whole-table weak reads.
    /// Caller selects the original GPOS/script/direction/global-feature admission.
    pub fn apply_legacy_kern(&mut self) -> Result<(), FontError> {
        let font = self.font.clone();
        let Some(table) = font.table(Tag::new(*b"kern")) else {
            return Ok(());
        };
        let data = table.bytes;
        let apple = fits(data, 0, 8) && u32_at(data, 0) == 0x10000;
        let (count, mut subtable) = if apple {
            let count = u32_at(data, 4);
            if count > i32::MAX as u32 {
                return Ok(());
            }
            (count as usize, 8)
        } else {
            if !fits(data, 0, 4) || u16_at(data, 0) != 0 {
                return Ok(());
            }
            (usize::from(u16_at(data, 2)), 4)
        };
        for _ in 0..count {
            let header = if apple { 8 } else { 6 };
            if !fits(data, subtable, header) {
                break;
            }
            let length = if apple {
                u32_at(data, subtable)
            } else {
                u32::from(u16_at(data, subtable + 2))
            } as usize;
            if length < header || length > i32::MAX as usize || !fits(data, subtable, length) {
                break;
            }
            let format = data[subtable + if apple { 5 } else { 4 }];
            let coverage = data[subtable + if apple { 4 } else { 5 }];
            let horizontal = if apple {
                coverage & 0x80 == 0
            } else {
                coverage & 1 != 0
            };
            let cross = if apple {
                coverage & 0x40 != 0
            } else {
                coverage & 4 != 0
            };
            if horizontal && matches!(format, 0 | 2) {
                let body = subtable + header;
                if fits(data, body, 8) {
                    let pair_count = usize::from(u16_at(data, body));
                    let records = body + 8;
                    if format != 0 || fits(data, records, pair_count * 6) {
                        for left in 0..self.glyphs.len().saturating_sub(1) {
                            let mut right = left + 1;
                            while right < self.glyphs.len() && self.glyph_class(right)? == 3 {
                                right += 1;
                            }
                            if right == self.glyphs.len() {
                                break;
                            }
                            let adjustment = if format == 0 {
                                kern_pair(
                                    data,
                                    records,
                                    pair_count,
                                    self.glyphs[left].glyph_index,
                                    self.glyphs[right].glyph_index,
                                )
                            } else {
                                let l = kern_class(
                                    data,
                                    subtable,
                                    length,
                                    usize::from(u16_at(data, body + 2)),
                                    self.glyphs[left].glyph_index,
                                );
                                let r = kern_class(
                                    data,
                                    subtable,
                                    length,
                                    usize::from(u16_at(data, body + 4)),
                                    self.glyphs[right].glyph_index,
                                );
                                let offset = l + r;
                                if offset < usize::from(u16_at(data, body + 6))
                                    || offset > length - 2
                                {
                                    0
                                } else {
                                    i32::from(u16_at(data, subtable + offset) as i16)
                                }
                            };
                            if adjustment == 0 {
                                continue;
                            }
                            self.dependency(left, right + 1);
                            if cross {
                                self.glyphs[right].offset_y =
                                    add(self.glyphs[right].offset_y, adjustment);
                            } else {
                                let first = adjustment >> 1;
                                let second = adjustment - first;
                                self.glyphs[left].advance_x =
                                    add(self.glyphs[left].advance_x, first);
                                self.glyphs[right].advance_x =
                                    add(self.glyphs[right].advance_x, second);
                                self.glyphs[right].offset_x =
                                    add(self.glyphs[right].offset_x, second);
                            }
                        }
                    }
                }
            }
            subtable += length;
        }
        Ok(())
    }
    /// Original post-positioning stretch expansion using the actual selected font.
    /// The font must own this buffer's source. The caller supplies the resolved
    /// RTL decision; earlier reversals/dependency writes survive source failures.
    pub fn apply_arabic_stretch(
        &mut self,
        font: &ManagedFont,
        right_to_left: bool,
    ) -> Result<(), FontError> {
        if !self.glyphs.iter().any(|g| stretch(g.arabic_action)) {
            return Ok(());
        }
        if !right_to_left {
            self.glyphs.reverse();
        }
        let em = f32::from(font.metadata().units_per_em);
        let mut runs = Vec::new();
        let mut extra = 0usize;
        let mut index = self.glyphs.len();
        while index > 0 {
            if !stretch(self.glyphs[index - 1].arabic_action) {
                index -= 1;
                continue;
            }
            let end = index;
            let (mut fixed_width, mut repeating_width) = (0i32, 0i32);
            let (mut fixed_count, mut repeating_count) = (0i32, 0i32);
            while index > 0 && stretch(self.glyphs[index - 1].arabic_action) {
                index -= 1;
                let glyph = self.glyphs[index];
                let width = font.advance_width(glyph.glyph_index, em)?.round_ties_even() as i32;
                if glyph.arabic_action == 8 {
                    fixed_width = fixed_width.wrapping_add(width);
                    fixed_count += 1;
                } else {
                    repeating_width = repeating_width.wrapping_add(width);
                    repeating_count += 1;
                }
            }
            let start = index;
            let mut context = start;
            let mut total_width = 0i32;
            while context > 0 && !stretch(self.glyphs[context - 1].arabic_action) {
                let g = self.glyphs[context - 1];
                if !default_ignorable(g.code_point) && !stretch_word(g.code_point)? {
                    break;
                }
                context -= 1;
                total_width = total_width.wrapping_add(i32::from(g.advance_x));
            }
            self.dependency(context, end);
            let mut remaining = total_width.wrapping_sub(fixed_width);
            let mut copies = if remaining > repeating_width && repeating_width > 0 {
                remaining / repeating_width - 1
            } else {
                0
            };
            let mut overlap = 0;
            let shortfall = i64::from(remaining)
                - i64::from(repeating_width) * i64::from(copies.wrapping_add(1));
            if shortfall > 0 && repeating_count > 0 {
                copies = copies.wrapping_add(1);
                let excess = i64::from(copies.wrapping_add(1)) * i64::from(repeating_width)
                    - i64::from(remaining);
                if excess > 0 {
                    let divisor = copies.wrapping_mul(repeating_count);
                    if divisor == 0 {
                        return Err(FontError::InvalidGlyph);
                    }
                    overlap = i32::try_from(excess / i64::from(divisor))
                        .map_err(|_| FontError::Capacity)?;
                    remaining = 0;
                }
            }
            let base = fixed_count + repeating_count;
            let maximum = if repeating_count > 0 && base < 256 {
                (256 - base) / repeating_count
            } else {
                0
            };
            copies = copies.min(maximum);
            let added = copies
                .checked_mul(repeating_count)
                .and_then(|v| usize::try_from(v).ok())
                .ok_or(FontError::Capacity)?;
            extra = extra.checked_add(added).ok_or(FontError::Capacity)?;
            if self
                .glyphs
                .len()
                .checked_add(extra)
                .is_none_or(|v| v > MAX_MANAGED_SHAPED_GLYPHS)
            {
                return Err(FontError::Capacity);
            }
            runs.try_reserve(1).map_err(|_| FontError::Capacity)?;
            runs.push(StretchRun {
                start,
                end,
                copies,
                remaining,
                overlap,
            });
        }
        let count = self.glyphs.len() + extra;
        let mut result = Vec::new();
        result
            .try_reserve_exact(count)
            .map_err(|_| FontError::Capacity)?;
        result.resize(count, ManagedShapingGlyph::default());
        let mut write = count;
        let mut source = self.glyphs.len();
        let mut run_index = 0;
        while source > 0 {
            if !stretch(self.glyphs[source - 1].arabic_action) {
                source -= 1;
                write -= 1;
                result[write] = self.glyphs[source];
                continue;
            }
            let run = &runs[run_index];
            run_index += 1;
            source = run.start;
            let mut x = run.remaining / 2;
            for i in (run.start..run.end).rev() {
                let mut glyph = self.glyphs[i];
                let width = font.advance_width(glyph.glyph_index, em)?.round_ties_even() as i32;
                let repeats = if glyph.arabic_action == 9 {
                    run.copies + 1
                } else {
                    1
                };
                glyph.advance_x = 0;
                for copy in 0..repeats {
                    if right_to_left {
                        x = x.wrapping_sub(width);
                        if copy > 0 {
                            x = x.wrapping_add(run.overlap);
                        }
                    }
                    glyph.offset_x = clamp(x);
                    write -= 1;
                    result[write] = glyph;
                    if !right_to_left {
                        x = x.wrapping_add(width);
                        if copy > 0 {
                            x = x.wrapping_sub(run.overlap);
                        }
                    }
                }
            }
        }
        debug_assert_eq!(write, 0);
        self.glyphs = result;
        if !right_to_left {
            self.glyphs.reverse();
        }
        Ok(())
    }
}
struct StretchRun {
    start: usize,
    end: usize,
    copies: i32,
    remaining: i32,
    overlap: i32,
}
fn stretch(action: u8) -> bool {
    matches!(action, 8 | 9)
}
fn stretch_word(point: u32) -> Result<bool, FontError> {
    if char::from_u32(point).is_none() {
        return Err(FontError::InvalidGlyph);
    }
    use GeneralCategory::*;
    Ok(matches!(
        general_category(point),
        OtherNotAssigned
            | PrivateUse
            | ModifierLetter
            | OtherLetter
            | SpacingCombiningMark
            | EnclosingMark
            | NonspacingMark
            | DecimalDigitNumber
            | LetterNumber
            | OtherNumber
            | CurrencySymbol
            | ModifierSymbol
            | MathSymbol
            | OtherSymbol
    ))
}
fn kern_pair(data: &[u8], records: usize, count: usize, left: u16, right: u16) -> i32 {
    let key = (u32::from(left) << 16) | u32::from(right);
    let (mut low, mut high) = (0i32, count as i32 - 1);
    while low <= high {
        let mid = (low + high) >> 1;
        let at = records + mid as usize * 6;
        let candidate = u32_at(data, at);
        if key < candidate {
            high = mid - 1;
        } else if key > candidate {
            low = mid + 1;
        } else {
            return i32::from(u16_at(data, at + 4) as i16);
        }
    }
    0
}
fn kern_class(data: &[u8], subtable: usize, length: usize, relative: usize, glyph: u16) -> usize {
    if relative > length.saturating_sub(4) {
        return 0;
    }
    let at = subtable + relative;
    let index = i32::from(glyph) - i32::from(u16_at(data, at));
    if index >= 0
        && index < i32::from(u16_at(data, at + 2))
        && fits(data, at + 4 + index as usize * 2, 2)
    {
        usize::from(u16_at(data, at + 4 + index as usize * 2))
    } else {
        0
    }
}
