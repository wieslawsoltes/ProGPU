//! Original ProGPU Khmer preparation surrounding independent syllable parsing.
use super::{ManagedShapingGlyph, insert, merge};
use crate::{
    font::{FontError, ManagedFont},
    text::{preparation::ManagedBufferFlags, unicode::ManagedIndicProperties},
};
#[path = "khmer_syllables.rs"]
mod syllables;

pub(super) fn prepare(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    flags: ManagedBufferFlags,
    script: &str,
) -> Result<(), FontError> {
    if script != "khmr" {
        return Ok(());
    }
    for glyph in glyphs.iter_mut() {
        glyph.script_shaper = 1;
        glyph.indic_category =
            ManagedIndicProperties::for_raw_code_point(glyph.code_point).category();
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
        let end = syllable_end(glyphs, start);
        let minimum = glyphs[start..end].iter().map(|g| g.cluster).min().unwrap();
        for glyph in &mut glyphs[start..end] {
            if glyph.cluster != minimum {
                glyph.flags |= 3;
            }
        }
        start = end;
    }
    if !flags.contains(ManagedBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        // Original query also runs on an empty buffer or with no broken syllable.
        let dotted = font.glyph_index(0x25cc)?;
        if dotted != 0 {
            let (mut index, mut previous) = (0, 0);
            while index < glyphs.len() {
                let current = glyphs[index];
                if current.use_syllable != previous && current.use_syllable & 15 == 1 {
                    let mut inserted = ManagedShapingGlyph::new(dotted, current.cluster, 0x25cc);
                    inserted.script_shaper = 1;
                    inserted.indic_category = 11;
                    inserted.use_syllable = current.use_syllable;
                    insert(glyphs, index, inserted)?;
                    index += 1;
                }
                previous = current.use_syllable;
                index += 1;
            }
        }
    }
    start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if matches!(glyphs[start].use_syllable & 15, 0 | 1) {
            reorder(glyphs, start, end)?;
        }
        start = end;
    }
    Ok(())
}

fn syllable_end(glyphs: &[ManagedShapingGlyph], start: usize) -> usize {
    let mut end = start + 1;
    while end < glyphs.len() && glyphs[end].use_syllable == glyphs[start].use_syllable {
        end += 1;
    }
    end
}

fn reorder(glyphs: &mut [ManagedShapingGlyph], start: usize, end: usize) -> Result<(), FontError> {
    for glyph in &mut glyphs[start + 1..end] {
        glyph.script_feature_mask |= 2;
    }
    let mut coeng_count = 0;
    for index in start + 1..end {
        if glyphs[index].indic_category == 4 && coeng_count <= 2 && index + 1 < end {
            coeng_count += 1;
            if glyphs[index + 1].indic_category == 15 {
                let mut coeng = glyphs[index];
                let mut ro = glyphs[index + 1];
                coeng.script_feature_mask |= 1;
                ro.script_feature_mask |= 1;
                let cluster = merge(glyphs, start as i32, index + 2)?;
                coeng.cluster = cluster;
                ro.cluster = cluster;
                glyphs[index] = coeng;
                glyphs[index + 1] = ro;
                glyphs[start..index + 2].rotate_right(2);
                for glyph in &mut glyphs[index + 2..end] {
                    glyph.script_feature_mask |= 4;
                }
                coeng_count = 2;
            }
        } else if glyphs[index].indic_category == 22 {
            let mut vowel = glyphs[index];
            vowel.cluster = merge(glyphs, start as i32, index + 1)?;
            glyphs[index] = vowel;
            glyphs[start..index + 1].rotate_right(1);
        }
    }
    Ok(())
}
