//! Original native Myanmar preparation and in-place stable positional sort.
use super::*;
pub(super) fn prepare(
    b: &mut Buffer<'_>,
    categories: &mut [u8],
    syllables: &mut [u8],
) -> Result<(), FontError> {
    if categories.len() < b.count || syllables.len() < b.count {
        return Err(FontError::Capacity);
    }
    if b.count == 0 {
        return Ok(());
    }
    for (i, g) in b.glyphs().iter().enumerate() {
        categories[i] = ManagedIndicProperties::for_raw_code_point(g.code_point).category();
    }
    syllables::assign(
        NativeComplexKind::Myanmar,
        &categories[..b.count],
        &[],
        syllables,
    )?;
    for i in 0..b.count {
        set_category(&mut b.storage[i], categories[i]);
        set_syllable(&mut b.storage[i], syllables[i]);
    }
    mark_syllables_unsafe(b.glyphs_mut());
    Ok(())
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
        b.reserve(broken_count(b.glyphs(), 1))?;
        let mut i = 0;
        let mut previous = 0;
        while i < b.count {
            let current = syllable(b.storage[i]);
            if current != previous && current & 15 == 1 {
                let mut g = ShapingGlyph {
                    glyph_id: u32::from(dotted),
                    code_point: 0x25cc,
                    cluster: b.storage[i].cluster,
                    ..Default::default()
                };
                set_category(&mut g, 11);
                set_syllable(&mut g, current);
                b.insert(i, g)?;
                i += 1;
            }
            previous = current;
            i += 1;
        }
    }
    let glyphs = b.glyphs_mut();
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        if matches!(syllable(glyphs[start]) & 15, 0 | 1) {
            reorder_syllable(glyphs, start, end);
        }
        start = end;
    }
    Ok(())
}
fn reorder_syllable(glyphs: &mut [ShapingGlyph], start: usize, end: usize) {
    let reph = end - start >= 3
        && category(glyphs[start]) == 15
        && category(glyphs[start + 1]) == 32
        && category(glyphs[start + 2]) == 4;
    let limit = start + if reph { 3 } else { 0 };
    let mut base = if reph { start } else { limit };
    if !reph {
        for i in limit..end {
            if matches!(category(glyphs[i]), 1 | 18 | 15 | 2 | 10 | 11) {
                base = i;
                break;
            }
        }
    }
    let mut cursor = start;
    while cursor < start + if reph { 3 } else { 0 } {
        set_position(&mut glyphs[cursor], 5);
        cursor += 1;
    }
    while cursor < base {
        set_position(&mut glyphs[cursor], 3);
        cursor += 1;
    }
    if cursor < end {
        set_position(&mut glyphs[cursor], 4);
        cursor += 1;
    }
    let mut current = 5;
    while cursor < end {
        let c = category(glyphs[cursor]);
        let p = if c == 36 {
            3
        } else if c == 22 {
            2
        } else if c == 40 {
            position(glyphs[cursor - 1])
        } else if current == 5 && c == 21 {
            current = 8;
            current
        } else if current == 8 && c == 9 {
            7
        } else if current == 8 && c == 21 {
            current
        } else {
            if current == 8 && c != 9 {
                current = 9;
            }
            current
        };
        set_position(&mut glyphs[cursor], p);
        cursor += 1;
    }
    merge_cluster(glyphs, start, end);
    stable_sort_positions(glyphs, start, end);
    let first = (start..end).find(|&i| position(glyphs[i]) == 2);
    let last = (start..end).rfind(|&i| position(glyphs[i]) == 2);
    if let (Some(first), Some(last)) = (first, last)
        && first < last
    {
        glyphs[first..=last].reverse();
        let mut segment = first;
        for i in first..=last {
            if category(glyphs[i]) == 22 {
                glyphs[segment..=i].reverse();
                segment = i + 1;
            }
        }
    }
}
