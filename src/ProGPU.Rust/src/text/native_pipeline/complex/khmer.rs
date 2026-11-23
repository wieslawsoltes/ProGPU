//! Original native Khmer capacity preflight, categories, circles and reordering.
use super::*;
pub(super) fn prepare(
    font: &SfntFace,
    flags: NativeBufferFlags,
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
        NativeComplexKind::Khmer,
        &categories[..b.count],
        &[],
        syllables,
    )?;
    let dotted = if flags.contains(NativeBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        0
    } else {
        font.glyph_index(0x25cc).unwrap_or(0)
    };
    if dotted != 0 {
        let mut previous = 0;
        let mut additions = 0;
        for &current in &syllables[..b.count] {
            if current != previous && current & 15 == 1 {
                additions += 1;
            }
            previous = current;
        }
        b.reserve(additions)?;
    }
    for i in 0..b.count {
        set_category(&mut b.storage[i], categories[i]);
        set_syllable(&mut b.storage[i], syllables[i]);
    }
    mark_syllables_unsafe(b.glyphs_mut());
    if dotted != 0 {
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
            reorder(glyphs, start, end);
        }
        start = end;
    }
    Ok(())
}
fn reorder(glyphs: &mut [ShapingGlyph], start: usize, end: usize) {
    if end - start <= 1 {
        return;
    }
    for g in &mut glyphs[start + 1..end] {
        add_feature(g, 2);
    }
    let mut coeng_count = 0;
    for i in start + 1..end {
        if category(glyphs[i]) == 4 && coeng_count <= 2 && i + 1 < end {
            coeng_count += 1;
            if category(glyphs[i + 1]) == 15 {
                add_feature(&mut glyphs[i], 1);
                add_feature(&mut glyphs[i + 1], 1);
                merge_cluster(glyphs, start, i + 2);
                glyphs[start..i + 2].rotate_right(2);
                for g in &mut glyphs[i + 2..end] {
                    add_feature(g, 4);
                }
                coeng_count = 2;
            }
        } else if category(glyphs[i]) == 22 {
            merge_cluster(glyphs, start, i + 1);
            glyphs[start..i + 1].rotate_right(1);
        }
    }
}
