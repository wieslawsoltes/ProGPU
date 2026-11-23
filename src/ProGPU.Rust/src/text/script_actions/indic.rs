//! Original Indic stage metadata over complete source-owned glyph records.
use super::{ManagedShapingGlyph, insert, merge};
use crate::{
    font::{FontError, ManagedFont, layout::ManagedSubstitutionPlan},
    text::{
        preparation::{ManagedBufferFlags, ManagedPreparationOptions, PreparationError},
        unicode::{
            GeneralCategory, ManagedIndicProperties, ManagedMarkProfile, UnicodeError,
            general_category,
        },
    },
};

#[path = "indic_final.rs"]
mod final_stage;
pub(super) use final_stage::final_reorder;

/// Explicit original Rune repertoire used only for final Indic init boundaries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ManagedIndicContinuationProfile {
    /// Original frozen Unicode 16 categories, independent of normalization.
    Unicode16_0,
}
impl ManagedIndicContinuationProfile {
    /// Reject invalid scalars at the original query, retaining earlier stage work.
    pub fn is_continuation(self, point: u32) -> Result<bool, UnicodeError> {
        if char::from_u32(point).is_none() {
            return Err(UnicodeError::InvalidArgument);
        }
        use GeneralCategory::*;
        Ok(matches!(
            general_category(point),
            Format
                | OtherNotAssigned
                | PrivateUse
                | Surrogate
                | LowercaseLetter
                | ModifierLetter
                | OtherLetter
                | TitlecaseLetter
                | UppercaseLetter
                | SpacingCombiningMark
                | EnclosingMark
                | NonspacingMark
        ))
    }
}

const REPH: u8 = 1;
const PRE_MATRA: u8 = 2;
const PRE_CONSONANT: u8 = 3;
const BASE: u8 = 4;
const AFTER_MAIN: u8 = 5;
const BELOW: u8 = 8;
const AFTER_SUB: u8 = 9;
const POST: u8 = 11;
const AFTER_POST: u8 = 12;
const MODIFIER: u8 = 13;
const RPHF: u8 = 1;
const PREF: u8 = 2;
const BLWF: u8 = 4;
const ABVF: u8 = 8;
const HALF: u8 = 16;
const PSTF: u8 = 32;
const INIT: u8 = 64;

fn consonant(g: ManagedShapingGlyph) -> bool {
    g.ligated == 0 && matches!(g.indic_category, 1 | 18 | 15 | 16 | 2 | 10 | 11)
}
fn joiner(g: ManagedShapingGlyph) -> bool {
    g.ligated == 0 && matches!(g.indic_category, 5 | 6)
}
fn halant(g: ManagedShapingGlyph) -> bool {
    g.ligated == 0 && g.indic_category == 4
}
fn virama(script: &str) -> u32 {
    match script {
        "deva" => 0x94d,
        "beng" => 0x9cd,
        "guru" => 0xa4d,
        "gujr" => 0xacd,
        "orya" => 0xb4d,
        "taml" => 0xbcd,
        "telu" => 0xc4d,
        "knda" => 0xccd,
        "mlym" => 0xd4d,
        _ => 0,
    }
}
fn syllable_end(glyphs: &[ManagedShapingGlyph], start: usize) -> usize {
    let mut end = start + 1;
    while end < glyphs.len() && glyphs[end].use_syllable == glyphs[start].use_syllable {
        end += 1;
    }
    end
}
struct Probe<'a> {
    font: &'a ManagedFont,
    marks: ManagedMarkProfile,
    plan: Option<&'a ManagedSubstitutionPlan>,
}
impl Probe<'_> {
    fn substitutes(&self, tag: [u8; 4], glyphs: &[u16]) -> Result<bool, FontError> {
        self.plan.map_or(Ok(false), |p| {
            p.indic_would_substitute(self.font, self.marks, tag, glyphs)
        })
    }
}

pub(super) fn initial(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    options: &ManagedPreparationOptions,
    script: &str,
    layout_script: &[u16],
    plan: Option<&ManagedSubstitutionPlan>,
) -> Result<(), FontError> {
    let old = layout_script.get(3) != Some(&u16::from(b'2'));
    let probe = Probe {
        font,
        marks: options.marks,
        plan,
    };
    let virama = font.glyph_index(virama(script))?;
    if virama != 0 {
        for g in glyphs.iter_mut() {
            if g.indic_position != BASE {
                continue;
            }
            let first = [virama, g.glyph_index];
            let second = [g.glyph_index, virama];
            if probe.substitutes(*b"blwf", &first)?
                || probe.substitutes(*b"blwf", &second)?
                || probe.substitutes(*b"vatu", &first)?
                || probe.substitutes(*b"vatu", &second)?
            {
                g.indic_position = BELOW;
            } else if probe.substitutes(*b"pstf", &first)?
                || probe.substitutes(*b"pstf", &second)?
                || probe.substitutes(*b"pref", &first)?
                || probe.substitutes(*b"pref", &second)?
            {
                g.indic_position = POST;
            }
        }
    }
    if !options
        .buffer_flags
        .contains(ManagedBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE)
    {
        let dotted = font.glyph_index(0x25cc)?;
        if dotted != 0 {
            let mut previous = 0;
            let mut i = 0;
            while i < glyphs.len() {
                let current = glyphs[i];
                if current.use_syllable == previous || current.use_syllable & 15 != 4 {
                    previous = current.use_syllable;
                    i += 1;
                    continue;
                }
                previous = current.use_syllable;
                while i < glyphs.len()
                    && glyphs[i].use_syllable == previous
                    && glyphs[i].indic_category == 14
                {
                    i += 1;
                }
                let mut circle = ManagedShapingGlyph::new(dotted, current.cluster, 0x25cc);
                circle.script_shaper = 3;
                circle.indic_category = 11;
                circle.indic_position = 14;
                circle.use_syllable = previous;
                insert(glyphs, i, circle)?;
                i += 1;
            }
        }
    }
    let mut start = 0;
    while start < glyphs.len() {
        let end = syllable_end(glyphs, start);
        if matches!(glyphs[start].use_syllable & 15, 0..=2 | 4) {
            initial_syllable(glyphs, start, end, script, old, &probe)?;
        }
        start = end;
    }
    Ok(())
}

fn initial_syllable(
    g: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
    script: &str,
    old: bool,
    probe: &Probe<'_>,
) -> Result<(), FontError> {
    if script == "knda"
        && end - start >= 3
        && g[start].indic_category == 15
        && g[start + 1].indic_category == 4
        && g[start + 2].indic_category == 6
    {
        merge(g, (start + 1) as i32, start + 3)?;
        g.swap(start + 1, start + 2);
    }
    let mut limit = start;
    let mut base = end;
    let mut reph = false;
    if end - start >= 3
        && ((script != "telu" && script != "mlym" && !joiner(g[start + 2]))
            || script == "telu" && g[start + 2].indic_category == 6)
    {
        let ids = [
            g[start].glyph_index,
            g[start + 1].glyph_index,
            g[start + 2].glyph_index,
        ];
        if probe.substitutes(*b"rphf", &ids[..2])?
            || script == "telu" && probe.substitutes(*b"rphf", &ids)?
        {
            limit += 2;
            while limit < end && joiner(g[limit]) {
                limit += 1;
            }
            base = start;
            reph = true;
        }
    } else if script == "mlym" && g[start].indic_category == 14 {
        limit += 1;
        while limit < end && joiner(g[limit]) {
            limit += 1;
        }
        base = start;
        reph = true;
    }
    let mut seen_below = false;
    for i in (limit..end).rev() {
        let glyph = g[i];
        if consonant(glyph) {
            if glyph.indic_position != BELOW && (glyph.indic_position != POST || seen_below) {
                base = i;
                break;
            }
            if glyph.indic_position == BELOW {
                seen_below = true;
            }
            base = i;
        } else if i > start && glyph.indic_category == 6 && g[i - 1].indic_category == 4 {
            break;
        }
    }
    if reph && base == start && limit - base <= 2 {
        reph = false;
    }
    for glyph in &mut g[start..base] {
        glyph.indic_position = glyph.indic_position.min(PRE_CONSONANT);
    }
    if base < end {
        g[base].indic_position = BASE;
    }
    if reph {
        g[start].indic_position = REPH;
    }
    if old && base < end {
        for i in base + 1..end {
            if g[i].indic_category != 4 {
                continue;
            }
            let mut dest = end - 1;
            while dest > i
                && !consonant(g[dest])
                && !(script == "knda" && g[dest].indic_category == 4)
            {
                dest -= 1;
            }
            if g[dest].indic_category != 4 && dest > i {
                g[i..=dest].rotate_left(1);
            }
            break;
        }
    }
    attach_marks(g, start, end, base);
    for (i, glyph) in g[start..end].iter_mut().enumerate() {
        glyph.indic_original_order = i.min(255) as u8;
    }
    // Original stable insertion order, including raw positions outside 0..14.
    for i in start + 1..end {
        let value = g[i];
        let mut at = i;
        while at > start && g[at - 1].indic_position > value.indic_position {
            g[at] = g[at - 1];
            at -= 1;
        }
        g[at] = value;
    }
    reverse_matras(g, start, end);
    base = (start..end)
        .find(|&i| g[i].indic_position == BASE)
        .unwrap_or(end);
    merge_sorted_clusters(g, start, end, base, old)?;
    feature_masks(g, start, end, base, old, script, probe)
}

fn attach_marks(g: &mut [ManagedShapingGlyph], start: usize, end: usize, base: usize) {
    let mut last_position = 0;
    for i in start..end {
        let mut glyph = g[i];
        if joiner(glyph) || matches!(glyph.indic_category, 3 | 12 | 16 | 4) {
            glyph.indic_position = last_position;
            if glyph.indic_category == 4
                && glyph.indic_position == PRE_MATRA
                && let Some(prior) = (start..i).rev().find(|&j| g[j].indic_position != PRE_MATRA)
            {
                glyph.indic_position = g[prior].indic_position;
            }
            g[i] = glyph;
        } else if glyph.indic_position != MODIFIER {
            if glyph.indic_category == 13 && i > start && g[i - 1].indic_category == 8 {
                g[i - 1].indic_position = glyph.indic_position;
            }
            last_position = glyph.indic_position;
        }
    }
    let mut last = base.min(end - 1);
    for i in last + 1..end {
        if consonant(g[i]) {
            let position = g[i].indic_position;
            for glyph in &mut g[last + 1..i] {
                if glyph.indic_position < MODIFIER {
                    glyph.indic_position = position;
                }
            }
            last = i;
        } else if matches!(g[i].indic_category, 7 | 13) {
            last = i;
        }
    }
}
fn reverse_matras(g: &mut [ManagedShapingGlyph], start: usize, end: usize) {
    let (mut first, mut last) = (end, end);
    for (i, glyph) in g.iter().enumerate().take(end).skip(start) {
        if glyph.indic_position == BASE {
            break;
        }
        if glyph.indic_position == PRE_MATRA {
            first = first.min(i);
            last = i;
        }
    }
    if first >= last {
        return;
    }
    g[first..=last].reverse();
    let mut group = first;
    for i in first..=last {
        if matches!(g[i].indic_category, 7 | 13) {
            g[group..=i].reverse();
            group = i + 1;
        }
    }
}
fn merge_sorted_clusters(
    g: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
    base: usize,
    old: bool,
) -> Result<(), FontError> {
    if base >= end {
        return Ok(());
    }
    if old || end - start > 127 {
        merge(g, base as i32, end)?;
        return Ok(());
    }
    for i in base..end {
        if g[i].indic_original_order == 255 {
            continue;
        }
        let (mut minimum, mut maximum) = (i, i);
        let mut cursor = start + g[i].indic_original_order as usize;
        while cursor != i && cursor < end {
            minimum = minimum.min(cursor);
            maximum = maximum.max(cursor);
            let next = start + g[cursor].indic_original_order as usize;
            g[cursor].indic_original_order = 255;
            cursor = next;
        }
        merge(g, base.max(minimum) as i32, maximum + 1)?;
    }
    Ok(())
}
fn feature_masks(
    g: &mut [ManagedShapingGlyph],
    start: usize,
    end: usize,
    base: usize,
    old: bool,
    script: &str,
    probe: &Probe<'_>,
) -> Result<(), FontError> {
    for glyph in &mut g[start..end] {
        if glyph.indic_position != REPH {
            break;
        }
        glyph.script_feature_mask |= RPHF;
    }
    let pre = HALF
        | if !old && !matches!(script, "telu" | "knda") {
            BLWF
        } else {
            0
        };
    for glyph in &mut g[start..base] {
        glyph.script_feature_mask |= pre;
    }
    if base < end {
        for glyph in &mut g[base + 1..end] {
            glyph.script_feature_mask |= BLWF | ABVF | PSTF;
        }
    }
    if base + 2 < end {
        for i in base + 1..end - 1 {
            if probe.substitutes(*b"pref", &[g[i].glyph_index, g[i + 1].glyph_index])? {
                g[i].script_feature_mask |= PREF;
                g[i + 1].script_feature_mask |= PREF;
                break;
            }
        }
    }
    for i in start + 1..end {
        if !joiner(g[i]) || g[i].indic_category != 5 {
            continue;
        }
        for prior in (start..i).rev() {
            g[prior].script_feature_mask &= !HALF;
            if consonant(g[prior]) {
                break;
            }
        }
    }
    Ok(())
}

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
