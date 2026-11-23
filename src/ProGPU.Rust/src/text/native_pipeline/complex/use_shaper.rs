//! Original native USE filtered syllables, feature observations and reordering.
use super::*;
pub(super) fn prepare(
    b: &mut Buffer<'_>,
    categories: &mut [u8],
    syllables: &mut [u8],
    indices: &mut [u32],
) -> Result<(), FontError> {
    if categories.len() < b.count || syllables.len() < b.count || indices.len() <= b.count {
        return Err(FontError::Capacity);
    }
    if b.count == 0 {
        return Ok(());
    }
    for (i, g) in b.glyphs().iter().enumerate() {
        categories[i] = unicode::managed_use_category(g.code_point);
    }
    let mut n = 0;
    for i in 0..b.count {
        if categories[i] == 6 {
            continue;
        }
        if categories[i] == 14 {
            let mut following = i + 1;
            while following < b.count && categories[following] == 6 {
                following += 1;
            }
            if following < b.count
                && matches!(
                    grapheme::break_class(b.storage[following].code_point),
                    grapheme::GraphemeBreakClass::Extend
                        | grapheme::GraphemeBreakClass::SpacingMark
                )
            {
                continue;
            }
        }
        indices[n] = i as u32;
        n += 1;
    }
    if n == 0 {
        syllables[..b.count].fill(0);
    } else {
        indices[n] = b.count as u32;
        syllables::assign(
            NativeComplexKind::Use,
            &categories[..b.count],
            &indices[..n + 1],
            syllables,
        )?;
    }
    for i in 0..b.count {
        set_category(&mut b.storage[i], categories[i]);
        set_syllable(&mut b.storage[i], syllables[i]);
    }
    let glyphs = b.glyphs_mut();
    mark_syllables_unsafe(glyphs);
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        let limit = if category(glyphs[start]) == 18 {
            1
        } else {
            3.min(end - start)
        };
        for g in &mut glyphs[start..start + limit] {
            add_feature(g, 1);
        }
        start = end;
    }
    Ok(())
}
pub(super) fn record_repha(glyphs: &mut [ShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        for g in &mut glyphs[start..end] {
            if feature(*g) & 1 == 0 {
                break;
            }
            if substituted(*g) {
                set_category(g, 18);
                break;
            }
        }
        start = end;
    }
}
pub(super) fn record_prebase(glyphs: &mut [ShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        if let Some(g) = glyphs[start..end].iter_mut().find(|g| substituted(**g)) {
            set_category(g, 22);
        }
        start = end;
    }
}
pub(super) fn reorder(
    font: &SfntFace,
    flags: NativeBufferFlags,
    b: &mut Buffer<'_>,
) -> Result<(), FontError> {
    let dotted = if flags.contains(NativeBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        0
    } else {
        font.glyph_index(0x25cc).unwrap_or(0)
    };
    if dotted != 0 {
        b.reserve(broken_count(b.glyphs(), 7))?;
        let mut i = 0;
        let mut previous = 0;
        while i < b.count {
            let current = syllable(b.storage[i]);
            if current != previous && current & 15 == 7 {
                previous = current;
                while i < b.count
                    && syllable(b.storage[i]) == previous
                    && category(b.storage[i]) == 18
                {
                    i += 1;
                }
                let cluster = if i < b.count {
                    b.storage[i].cluster
                } else {
                    b.storage[i - 1].cluster
                };
                let mut g = ShapingGlyph {
                    glyph_id: u32::from(dotted),
                    code_point: 0x25cc,
                    cluster,
                    ..Default::default()
                };
                set_category(&mut g, 1);
                set_syllable(&mut g, previous);
                b.insert(i, g)?;
            } else {
                previous = current;
            }
            i += 1;
        }
    }
    let glyphs = b.glyphs_mut();
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        if matches!(syllable(glyphs[start]) & 15, 0 | 1 | 2 | 5 | 7) {
            reorder_syllable(glyphs, start, end);
        }
        start = end;
    }
    Ok(())
}
fn halant(c: u8) -> bool {
    matches!(c, 12 | 53 | 44)
}
fn post_base(c: u8) -> bool {
    matches!(c, 24..=30 | 33..=35 | 22 | 37..=39 | 23 | 45..=47)
}
fn reorder_syllable(glyphs: &mut [ShapingGlyph], start: usize, end: usize) {
    if category(glyphs[start]) == 18 && end - start > 1 {
        for i in start + 1..end {
            let post = post_base(category(glyphs[i])) || halant(category(glyphs[i]));
            if !post && i != end - 1 {
                continue;
            }
            let destination = if post { i - 1 } else { i };
            merge_cluster(glyphs, start, destination + 1);
            glyphs[start..destination + 1].rotate_left(1);
            break;
        }
    }
    let mut target = start;
    for i in start..end {
        let c = category(glyphs[i]);
        if halant(c) {
            target = i + 1;
        } else if matches!(c, 22 | 23) && target < i {
            merge_cluster(glyphs, target, i + 1);
            glyphs[target..i + 1].rotate_right(1);
        }
    }
}
