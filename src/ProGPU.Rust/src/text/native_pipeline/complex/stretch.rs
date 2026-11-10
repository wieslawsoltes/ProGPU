//! Original native design-unit Arabic stretch run discovery and backward expansion.
use super::*;
use crate::font::HorizontalAdvanceInstance;
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Run {
    pub start: usize,
    pub end: usize,
    pub copies: u32,
    pub remaining: i32,
    pub overlap: i32,
}
pub(super) fn stretch(g: ShapingGlyph) -> bool {
    matches!(joining::action(g), 8 | 9)
}
pub(super) fn word(cp: u32) -> bool {
    use GeneralCategory::*;
    matches!(cp, 0xad | 0x34f | 0x61c)
        || matches!(
            unicode::general_category(cp),
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
        )
}
fn width(advance: &HorizontalAdvanceInstance, g: ShapingGlyph) -> Result<i32, FontError> {
    let id = u16::try_from(g.glyph_id).map_err(|_| FontError::InvalidGlyph)?;
    let rounded = f64::from(advance.design_advance_width(id)?).round();
    if !rounded.is_finite() || rounded < f64::from(i32::MIN) || rounded > f64::from(i32::MAX) {
        return Err(FontError::InvalidFace);
    }
    Ok(rounded as i32)
}
pub(super) fn runs(
    mut width_at: impl FnMut(usize, ShapingGlyph) -> Result<i32, FontError>,
    g: &[ShapingGlyph],
    rtl: bool,
    out: &mut [Run],
) -> Result<(usize, usize), FontError> {
    if g.len() > 1_048_576 {
        return Err(FontError::InvalidArgument);
    }
    let glyph = |i: usize| g[if rtl { i } else { g.len() - i - 1 }];
    let mut extra = 0usize;
    let mut written = 0;
    let mut index = g.len();
    while index > 0 {
        if !stretch(glyph(index - 1)) {
            index -= 1;
            continue;
        }
        let end = index;
        let mut fixed_width = 0i64;
        let mut repeating_width = 0i64;
        let mut fixed_count = 0usize;
        let mut repeating_count = 0usize;
        while index > 0 && stretch(glyph(index - 1)) {
            index -= 1;
            let source_index = if rtl { index } else { g.len() - index - 1 };
            let w = i64::from(width_at(source_index, glyph(index))?);
            if joining::action(glyph(index)) == 8 {
                fixed_width += w;
                fixed_count += 1;
            } else {
                repeating_width += w;
                repeating_count += 1;
            }
        }
        let start = index;
        let mut context = start;
        let mut total = 0i64;
        while context > 0 && !stretch(glyph(context - 1)) && word(glyph(context - 1).code_point) {
            context -= 1;
            total += i64::from(glyph(context).advance_x);
        }
        let mut remaining = total - fixed_width;
        let mut copies = if remaining > repeating_width && repeating_width > 0 {
            remaining / repeating_width - 1
        } else {
            0
        };
        let mut overlap = 0;
        let shortfall = remaining - repeating_width * (copies + 1);
        if shortfall > 0 && repeating_count > 0 {
            copies += 1;
            let excess = (copies + 1) * repeating_width - remaining;
            if excess > 0 {
                overlap = excess / (copies * repeating_count as i64);
                remaining = 0;
            }
        }
        let base = fixed_count + repeating_count;
        let max = if repeating_count > 0 && base < 256 {
            (256 - base) / repeating_count
        } else {
            0
        };
        copies = copies.clamp(0, max as i64);
        extra += copies as usize * repeating_count;
        if g.len() + extra > 1_048_576 {
            return Err(FontError::Capacity);
        }
        if written >= out.len()
            || remaining < i64::from(i32::MIN)
            || remaining > i64::from(i32::MAX)
            || overlap < i64::from(i32::MIN)
            || overlap > i64::from(i32::MAX)
        {
            return Err(FontError::Capacity);
        }
        out[written] = Run {
            start,
            end,
            copies: copies as u32,
            remaining: remaining as i32,
            overlap: overlap as i32,
        };
        written += 1;
    }
    Ok((g.len() + extra, written))
}
pub(super) fn apply(
    advance: &HorizontalAdvanceInstance,
    b: &mut Buffer<'_>,
    rtl: bool,
    scratch: &mut [Run],
) -> Result<(), FontError> {
    let (capacity, n) = runs(|_, g| width(advance, g), b.glyphs(), rtl, scratch)?;
    if b.storage.len() < capacity {
        return Err(FontError::Capacity);
    }
    if n == 0 {
        return Ok(());
    }
    let source_count = b.count;
    if !rtl {
        b.glyphs_mut().reverse();
    }
    for run in &scratch[..n] {
        let mut context = run.start;
        while context > 0
            && !stretch(b.storage[context - 1])
            && word(b.storage[context - 1].code_point)
        {
            context -= 1;
        }
        for g in &mut b.storage[context..run.end] {
            g.flags |= 3;
        }
    }
    let mut write = capacity;
    let mut source = source_count;
    let mut run_index = 0;
    while source > 0 {
        if !stretch(b.storage[source - 1]) {
            write -= 1;
            source -= 1;
            b.storage[write] = b.storage[source];
            continue;
        }
        let run = scratch[run_index];
        run_index += 1;
        source = run.start;
        let mut x = i64::from(run.remaining / 2);
        for index in (run.start..run.end).rev() {
            let mut glyph = b.storage[index];
            let w = i64::from(width(advance, glyph)?);
            let repeat = if joining::action(glyph) == 9 {
                run.copies + 1
            } else {
                1
            };
            glyph.advance_x = 0;
            for copy in 0..repeat {
                if rtl {
                    x -= w;
                    if copy > 0 {
                        x += i64::from(run.overlap);
                    }
                }
                glyph.offset_x = clamped(x);
                write -= 1;
                b.storage[write] = glyph;
                if !rtl {
                    x += w;
                    if copy > 0 {
                        x -= i64::from(run.overlap);
                    }
                }
            }
        }
    }
    b.count = capacity;
    if !rtl {
        b.glyphs_mut().reverse();
    }
    Ok(())
}
