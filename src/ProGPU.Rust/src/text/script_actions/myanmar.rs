//! Original ProGPU Myanmar stages, with independent syllable recognition.
use super::{ManagedShapingGlyph, insert, merge};
use crate::{
    font::{FontError, ManagedFont},
    text::{preparation::ManagedBufferFlags, unicode::ManagedIndicProperties},
};
#[path = "myanmar_sort_storage.rs"]
mod storage;
#[path = "myanmar_syllables.rs"]
mod syllables;

pub(super) fn prepare(glyphs: &mut [ManagedShapingGlyph], enabled: bool) {
    if !enabled {
        return;
    }
    for glyph in glyphs.iter_mut() {
        glyph.script_shaper = 5;
        glyph.indic_category =
            ManagedIndicProperties::for_raw_code_point(glyph.code_point).category();
    }
    let (mut start, mut serial) = (0, 1u8);
    while start < glyphs.len() {
        let (end, kind) = syllables::next(start, glyphs.len(), |i| glyphs[i].indic_category);
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
}

pub(super) fn reorder(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    flags: ManagedBufferFlags,
) -> Result<(), FontError> {
    if !flags.contains(ManagedBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        // Query even empty/non-broken runs, before any sorting or scratch growth.
        let dotted = font.glyph_index(0x25cc)?;
        if dotted != 0 {
            let (mut index, mut previous) = (0, 0);
            while index < glyphs.len() {
                let current = glyphs[index];
                if current.use_syllable != previous && current.use_syllable & 15 == 1 {
                    let mut inserted = ManagedShapingGlyph::new(dotted, current.cluster, 0x25cc);
                    inserted.script_shaper = 5;
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
    let mut scratch = storage::Scratch::new();
    let mut start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if matches!(glyphs[start].use_syllable & 15, 0 | 1) {
            reorder_syllable(glyphs, start, end, &mut scratch)?;
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

fn reorder_syllable(
    glyphs: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
    scratch: &mut storage::Scratch,
) -> Result<(), FontError> {
    let has_reph = end - start >= 3
        && glyphs[start].indic_category == 15
        && glyphs[start + 1].indic_category == 32
        && glyphs[start + 2].indic_category == 4;
    let limit = start + if has_reph { 3 } else { 0 };
    let base = (limit..end)
        .find(|&i| {
            glyphs[i].ligated == 0 && matches!(glyphs[i].indic_category, 1 | 18 | 15 | 2 | 10 | 11)
        })
        .unwrap_or(start);
    for glyph in &mut glyphs[start..limit] {
        glyph.indic_position = 5;
    }
    let mut cursor = limit;
    while cursor < base {
        glyphs[cursor].indic_position = 3;
        cursor += 1;
    }
    if cursor < end {
        glyphs[cursor].indic_position = 4;
        cursor += 1;
    }
    let mut position = 5;
    while cursor < end {
        let category = glyphs[cursor].indic_category;
        glyphs[cursor].indic_position = match category {
            36 => 3,
            22 => 2,
            40 => glyphs[cursor - 1].indic_position,
            21 if position == 5 => {
                position = 8;
                position
            }
            9 if position == 8 => 7,
            21 if position == 8 => position,
            _ => {
                if position == 8 && category != 9 {
                    position = 9;
                }
                position
            }
        };
        cursor += 1;
    }
    merge(glyphs, start as i32, end)?;
    let run = &mut glyphs[start..end];
    if run.len() >= 2 {
        let mut buckets = [0usize; 16];
        for glyph in run.iter() {
            buckets[glyph.indic_position.min(14) as usize + 1] += 1;
        }
        for index in 1..buckets.len() {
            buckets[index] += buckets[index - 1];
        }
        let ordered = scratch.records(run.len())?;
        for &glyph in run.iter() {
            let bucket = &mut buckets[glyph.indic_position.min(14) as usize];
            ordered[*bucket] = glyph;
            *bucket += 1;
        }
        run.copy_from_slice(ordered);
    }
    let first = run.iter().position(|g| g.indic_position == 2);
    let last = run.iter().rposition(|g| g.indic_position == 2);
    if let (Some(first), Some(last)) = (first, last)
        && first < last
    {
        run[first..=last].reverse();
        let mut segment_start = first;
        for index in first..=last {
            if run[index].indic_category == 22 {
                run[segment_start..=index].reverse();
                segment_start = index + 1;
            }
        }
    }
    Ok(())
}
