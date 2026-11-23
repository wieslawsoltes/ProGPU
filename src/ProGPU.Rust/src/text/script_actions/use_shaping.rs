//! Original ProGPU USE actions over complete retained glyph records.
use super::{ManagedShapingGlyph, insert, merge};
use crate::text::{
    preparation::PreparationError,
    unicode::{ManagedMarkProfile, managed_use_category},
};
use crate::{
    font::{FontError, ManagedFont},
    text::preparation::ManagedBufferFlags,
};
#[path = "use_indices.rs"]
mod indices;
#[path = "use_syllables.rs"]
mod syllables;

pub(super) fn prepare(
    glyphs: &mut [ManagedShapingGlyph],
    enabled: bool,
    script: &str,
    marks: ManagedMarkProfile,
) -> Result<(), PreparationError> {
    if !enabled {
        return Ok(());
    }
    for glyph in glyphs.iter_mut() {
        glyph.script_shaper = 2;
        glyph.use_category = managed_use_category(glyph.code_point);
    }
    // Original allocation and whole filtered-index preflight precede syllable
    // writes. A mark-query error keeps classification but no later mutations.
    let capacity = glyphs.len().checked_add(1).ok_or(FontError::Capacity)?;
    let mut lease = indices::Indices::rent(capacity)?;
    let indices = lease.records();
    for index in 0..glyphs.len() {
        let category = glyphs[index].use_category;
        if category == 6 {
            continue;
        }
        if category == 14 {
            let mut following = index + 1;
            while following < glyphs.len() && glyphs[following].use_category == 6 {
                following += 1;
            }
            if following < glyphs.len() && marks.is_mark(glyphs[following].code_point)? {
                continue;
            }
        }
        indices.push(u32::try_from(index).map_err(|_| FontError::Capacity)?);
    }
    indices.push(u32::try_from(glyphs.len()).map_err(|_| FontError::Capacity)?);
    let count = indices.len() - 1;
    let (mut start, mut serial) = (0, 1u8);
    let mut scanner = syllables::Scanner::default();
    while start < count {
        let (end, kind) = scanner.next(start, count, |i| glyphs[indices[i] as usize].use_category);
        for glyph in &mut glyphs[indices[start] as usize..indices[end] as usize] {
            glyph.use_syllable = serial << 4 | kind;
        }
        serial = serial % 15 + 1;
        start = end;
    }
    // Release the index lease at the same boundary as the original helper.
    drop(lease);
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
    start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        let limit = if glyphs[start].use_category == 18 {
            1
        } else {
            (end - start).min(3)
        };
        for glyph in &mut glyphs[start..start + limit] {
            glyph.use_rphf_eligible = 1;
        }
        start = end;
    }
    if !matches!(
        script,
        "adlm"
            | "arab"
            | "chrs"
            | "rohg"
            | "mand"
            | "mani"
            | "mong"
            | "nkoo"
            | "ougr"
            | "phag"
            | "phlp"
            | "sogd"
            | "syrc"
    ) {
        topographical(glyphs);
    }
    Ok(())
}

fn topographical(glyphs: &mut [ManagedShapingGlyph]) {
    let (mut start, mut previous_start, mut previous_form) = (0, 0, 7);
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if matches!(glyphs[start].use_syllable & 15, 6 | 8) {
            previous_form = 7;
        } else {
            let joins = matches!(previous_form, 1 | 0);
            if joins {
                previous_form = if previous_form == 1 { 4 } else { 6 };
                for glyph in &mut glyphs[previous_start..start] {
                    glyph.arabic_action = previous_form;
                }
            }
            previous_form = u8::from(joins);
            for glyph in &mut glyphs[start..end] {
                glyph.arabic_action = previous_form;
            }
        }
        previous_start = start;
        start = end;
    }
}

pub(super) fn record_repha(glyphs: &mut [ManagedShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        for glyph in &mut glyphs[start..end] {
            if glyph.use_rphf_eligible == 0 {
                break;
            }
            if glyph.substituted != 0 {
                glyph.use_category = 18;
                break;
            }
        }
        start = end;
    }
}
pub(super) fn record_prebase(glyphs: &mut [ManagedShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if let Some(glyph) = glyphs[start..end].iter_mut().find(|g| g.substituted != 0) {
            glyph.use_category = 22;
        }
        start = end;
    }
}
pub(super) fn reorder(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    flags: ManagedBufferFlags,
    enabled: bool,
) -> Result<(), FontError> {
    if !enabled {
        return Ok(());
    }
    if !flags.contains(ManagedBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE) {
        let dotted = font.glyph_index(0x25cc)?;
        if dotted != 0 {
            let (mut index, mut previous) = (0, 0);
            while index < glyphs.len() {
                let current = glyphs[index];
                if current.use_syllable != previous && current.use_syllable & 15 == 7 {
                    previous = current.use_syllable;
                    while index < glyphs.len()
                        && glyphs[index].use_syllable == previous
                        && glyphs[index].use_category == 18
                    {
                        index += 1;
                    }
                    let mut circle = ManagedShapingGlyph::new(dotted, current.cluster, 0x25cc);
                    circle.script_shaper = 2;
                    circle.use_category = 1;
                    circle.use_syllable = previous;
                    circle.arabic_action = current.arabic_action;
                    insert(glyphs, index, circle)?;
                } else {
                    previous = current.use_syllable;
                }
                index += 1;
            }
        }
    }
    let mut start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if matches!(glyphs[start].use_syllable & 15, 0 | 1 | 2 | 5 | 7) {
            reorder_syllable(glyphs, start, end)?;
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
fn halant(glyph: &ManagedShapingGlyph) -> bool {
    matches!(glyph.use_category, 12 | 53 | 44) && glyph.ligated == 0
}
fn post_base(category: u8) -> bool {
    matches!(category, 24..=30 | 33..=35 | 22 | 37..=39 | 23 | 45..=47)
}
fn reorder_syllable(
    glyphs: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
) -> Result<(), FontError> {
    if glyphs[start].use_category == 18 && end - start > 1 {
        for index in start + 1..end {
            let post = post_base(glyphs[index].use_category) || halant(&glyphs[index]);
            if post || index == end - 1 {
                let destination = if post { index - 1 } else { index };
                merge(glyphs, start as i32, destination + 1)?;
                // Original repha is captured after merging its cluster.
                glyphs[start..=destination].rotate_left(1);
                break;
            }
        }
    }
    let mut target = start;
    for index in start..end {
        let glyph = glyphs[index];
        if halant(&glyph) {
            target = index + 1;
        } else if matches!(glyph.use_category, 22 | 23)
            && glyph.multiple_substitution_component == 0
            && target < index
        {
            merge(glyphs, target as i32, index + 1)?;
            // Original pre-base record was captured BEFORE MergeCluster; its
            // own cluster must survive while the other merged records change.
            glyphs[index] = glyph;
            glyphs[target..=index].rotate_right(1);
        }
    }
    Ok(())
}
