//! Original Indic stage metadata over complete source-owned glyph records.
use super::ManagedShapingGlyph;
use crate::text::unicode::ManagedIndicProperties;

#[path = "indic_syllables.rs"]
mod syllables;

pub(super) fn prepare(glyphs: &mut [ManagedShapingGlyph], enabled: bool) {
    if !enabled {
        return;
    }
    for glyph in glyphs.iter_mut() {
        let properties = ManagedIndicProperties::for_raw_code_point(glyph.code_point);
        glyph.script_shaper = 3;
        glyph.indic_category = properties.category();
        glyph.indic_position = properties.position();
    }
    let mut scanner = syllables::Scanner::default();
    let (mut start, mut serial) = (0, 1u8);
    while start < glyphs.len() {
        let (end, kind) = scanner.next(start, glyphs.len(), |i| glyphs[i].indic_category);
        for glyph in &mut glyphs[start..end] {
            glyph.use_syllable = serial << 4 | kind;
        }
        serial = serial % 15 + 1;
        start = end;
    }
    start = 0;
    while start < glyphs.len() {
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].use_syllable == glyphs[start].use_syllable {
            end += 1;
        }
        let minimum = glyphs[start..end].iter().map(|g| g.cluster).min().unwrap();
        for glyph in &mut glyphs[start..end] {
            if glyph.cluster != minimum {
                glyph.flags |= 3;
            }
        }
        start = end;
    }
}
