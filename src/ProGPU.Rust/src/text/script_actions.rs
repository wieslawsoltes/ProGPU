//! Original source glyph actions surrounding managed OpenType lookup stages.
//! These explicit operations preserve full records and original partial writes
//! on source errors. They do not select a reduced complete-shaping pipeline.
//! Linear traversals use O(1) scratch except normalization; original stable
//! insertion/reordering and Vec shifts retain worst-case O(N²) work.
use super::{ManagedShapingGlyph, preparation::*, unicode::*};
use crate::font::{FontError, ManagedFont, layout::ManagedGlyphBuffer};
use normalization::{ManagedNormalizationProfile, NormalizationForm};

/// Original four physical shaping directions, independent of bidi level types.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ManagedShapingDirection {
    /// Horizontal forward shaping.
    LeftToRight,
    /// Horizontal backward shaping.
    RightToLeft,
    /// Vertical forward shaping.
    TopToBottom,
    /// Vertical backward shaping.
    BottomToTop,
}
/// Independently witnessed source Rune decimal-digit repertoire.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ManagedDigitProfile {
    /// Frozen Unicode 16 DecimalDigitNumber facts, independently source-validated.
    Unicode16_0,
}
impl ManagedDigitProfile {
    /// Original Rune admission and decimal category, without allocation.
    pub fn is_decimal_digit(self, point: u32) -> Result<bool, UnicodeError> {
        if char::from_u32(point).is_none() {
            return Err(UnicodeError::InvalidArgument);
        }
        Ok(general_category(point) == GeneralCategory::DecimalDigitNumber)
    }
}
/// One explicit original operation. The full script planner must preserve source
/// order and feature decisions; applying a subset does not constitute shaping.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ManagedScriptAction {
    /// Original beginning/context/flag/coverage-dependent dotted-circle insertion.
    BeginningDottedCircle,
    /// Original mirror followed by independent vertical presentation fallback.
    DirectionalFallback {
        /// Actual selected source direction.
        direction: ManagedShapingDirection,
        /// Whether the selected original plan contains vert or vrt2 lookups.
        has_vertical_substitution: bool,
    },
    /// Original font-dependent Hangul composition/decomposition and Jamo masks.
    Hangul,
    /// Original modified-class stable ordering and Arabic modifier rotation.
    ModifiedCombiningMarks,
    /// Original Hebrew presentation composition, only when the mark feature is absent.
    HebrewComposition {
        /// Actual selected shaping plan's mark-feature presence.
        has_mark_feature: bool,
    },
    /// Original mark-first canonical decomposition for the selected USE policy.
    UseDiacritics {
        /// Actual plan's USE selection, independent of raw script spelling.
        enabled: bool,
    },
    /// Original Thai/Lao Sara Am expansion, ordering and cluster policy.
    ThaiLao,
    /// Original complete decimal numerator/denominator flags and private marker.
    Fractions {
        /// Independently witnessed Rune decimal facts.
        digits: ManagedDigitProfile,
    },
    /// Clear only the original substituted byte before another lookup stage.
    ClearSubstitutionFlags,
    /// Clear only the original USE syllable byte.
    ClearSyllables,
    /// Record original multiplied-component parity as fixed/repeating stretch.
    RecordArabicStretch,
    /// Reverse complete original records without reconstructing clusters.
    Reverse,
}
impl ManagedGlyphBuffer {
    /// Apply one original action to this live prepared generation. Missing source
    /// metadata rejects before mutation. Original failures retain earlier writes;
    /// font/text/context owners, lookup state and unrelated record fields survive.
    pub fn apply_script_action(
        &mut self,
        action: ManagedScriptAction,
    ) -> Result<(), PreparationError> {
        let source = self
            .preparation
            .as_ref()
            .ok_or(PreparationError::MissingPreparation)?
            .clone();
        let font = source.font();
        let options = source.options();
        let script = source.script();
        match action {
            ManagedScriptAction::BeginningDottedCircle => {
                if !options
                    .buffer_flags
                    .contains(ManagedBufferFlags::BEGINNING_OF_TEXT)
                    || options
                        .buffer_flags
                        .contains(ManagedBufferFlags::DO_NOT_INSERT_DOTTED_CIRCLE)
                    || !options.pre_context.is_empty()
                    || self.glyphs.is_empty()
                    || !options.marks.is_mark(self.glyphs[0].code_point)?
                {
                    return Ok(());
                }
                let id = font.glyph_index(0x25cc)?;
                if id != 0 {
                    let first = self.glyphs[0];
                    let mut glyph = ManagedShapingGlyph::new(id, first.cluster, 0x25cc);
                    glyph.grapheme_cluster = first.grapheme_cluster;
                    insert(&mut self.glyphs, 0, glyph)?;
                }
            }
            ManagedScriptAction::DirectionalFallback {
                direction,
                has_vertical_substitution,
            } => {
                use ManagedShapingDirection::*;
                let backward = matches!(direction, RightToLeft | BottomToTop);
                let vertical =
                    !has_vertical_substitution && matches!(direction, TopToBottom | BottomToTop);
                if !backward && !vertical {
                    return Ok(());
                }
                for glyph in &mut self.glyphs {
                    let mut cp = glyph.code_point;
                    if backward {
                        let mapped = mirrored_code_point(cp);
                        if mapped != cp && font.glyph_index(mapped)? != 0 {
                            cp = mapped;
                        }
                    }
                    if vertical {
                        let mapped = vertical_code_point(cp);
                        if mapped != cp && font.glyph_index(mapped)? != 0 {
                            cp = mapped;
                        }
                    }
                    if cp != glyph.code_point {
                        glyph.code_point = cp;
                        glyph.glyph_index = font.glyph_index(cp)?;
                    }
                }
            }
            ManagedScriptAction::Hangul => {
                if script == "hang" {
                    hangul(&mut self.glyphs, font)?;
                }
            }
            ManagedScriptAction::ModifiedCombiningMarks => {
                reorder_marks(&mut self.glyphs, options.cluster_level, script)
            }
            ManagedScriptAction::HebrewComposition { has_mark_feature } => {
                if script == "hebr" && !has_mark_feature {
                    hebrew(&mut self.glyphs, font)?;
                }
            }
            ManagedScriptAction::UseDiacritics { enabled } => {
                if enabled {
                    use_diacritics(&mut self.glyphs, font, options.normalization, options.marks)?;
                }
            }
            ManagedScriptAction::ThaiLao => {
                thai_lao(&mut self.glyphs, font, script, options.cluster_level)?
            }
            ManagedScriptAction::Fractions { digits } => {
                for slash in 0..self.glyphs.len() {
                    if self.glyphs[slash].code_point != 0x2044 {
                        continue;
                    }
                    let mut start = slash;
                    while start > 0 && digits.is_decimal_digit(self.glyphs[start - 1].code_point)? {
                        start -= 1;
                    }
                    let mut end = slash + 1;
                    while end < self.glyphs.len()
                        && digits.is_decimal_digit(self.glyphs[end].code_point)?
                    {
                        end += 1;
                    }
                    if start == slash || end == slash + 1 {
                        continue;
                    }
                    self.set_fraction_actions_present(true);
                    let minimum = self.glyphs[start..end]
                        .iter()
                        .map(|g| g.cluster)
                        .min()
                        .unwrap();
                    for glyph in &mut self.glyphs[start..end] {
                        if glyph.cluster != minimum {
                            glyph.flags |= 3;
                        }
                    }
                    for glyph in &mut self.glyphs[start..slash] {
                        glyph.fraction_action = 1;
                    }
                    for glyph in &mut self.glyphs[slash + 1..end] {
                        glyph.fraction_action = 2;
                    }
                    self.glyphs[slash].fraction_action = 3;
                }
            }
            ManagedScriptAction::ClearSubstitutionFlags => {
                for g in &mut self.glyphs {
                    g.substituted = 0;
                }
            }
            ManagedScriptAction::ClearSyllables => {
                for g in &mut self.glyphs {
                    g.use_syllable = 0;
                }
            }
            ManagedScriptAction::RecordArabicStretch => {
                for g in &mut self.glyphs {
                    if g.multiplied != 0 {
                        g.arabic_action = if g.multiple_substitution_component & 1 != 0 {
                            9
                        } else {
                            8
                        };
                    }
                }
            }
            ManagedScriptAction::Reverse => self.glyphs.reverse(),
        }
        Ok(())
    }
}
fn insert(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    index: usize,
    glyph: ManagedShapingGlyph,
) -> Result<(), FontError> {
    if glyphs.len() >= i32::MAX as usize {
        return Err(FontError::Capacity);
    }
    glyphs.try_reserve(1).map_err(|_| FontError::Capacity)?;
    glyphs.insert(index, glyph);
    Ok(())
}
fn merge(glyphs: &mut [ManagedShapingGlyph], start: i32, end: usize) -> Result<i32, FontError> {
    let mut start = usize::try_from(start).map_err(|_| FontError::InvalidGlyph)?;
    if start >= end {
        return Ok(glyphs.get(start).map_or(0, |g| g.cluster));
    }
    let first = glyphs.get(start).ok_or(FontError::InvalidGlyph)?.cluster;
    let last = glyphs.get(end - 1).ok_or(FontError::InvalidGlyph)?.cluster;
    let mut end = end;
    while start > 0 && glyphs[start - 1].cluster == first {
        start -= 1;
    }
    while end < glyphs.len() && glyphs[end].cluster == last {
        end += 1;
    }
    let minimum = glyphs[start..end].iter().map(|g| g.cluster).min().unwrap();
    for glyph in &mut glyphs[start..end] {
        glyph.cluster = minimum;
    }
    Ok(minimum)
}
fn thai_lao(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    script: &str,
    level: ManagedClusterLevel,
) -> Result<(), FontError> {
    let (am, nikhahit) = match script {
        "thai" => (0xe33, 0xe4d),
        "lao " => (0xeb3, 0xecd),
        _ => return Ok(()),
    };
    let mut index = 0;
    while index < glyphs.len() {
        let source = glyphs[index];
        if source.code_point == am {
            let n = ManagedShapingGlyph::new(font.glyph_index(nikhahit)?, source.cluster, nikhahit);
            let a = ManagedShapingGlyph::new(font.glyph_index(am - 1)?, source.cluster, am - 1);
            glyphs[index] = n;
            insert(glyphs, index + 1, a)?;
            let mut start = index;
            while start > 0
                && matches!(glyphs[start-1].code_point & !0x80,0xe34..=0xe37|0xe47..=0xe4e|0xe31|0xe3b)
            {
                start -= 1;
            }
            if start < index {
                glyphs.remove(index);
                glyphs.insert(start, n);
            }
            let end = index + 2;
            match level {
                ManagedClusterLevel::MonotoneGraphemes | ManagedClusterLevel::Graphemes => {
                    merge(glyphs, start as i32 - 1, end)?;
                }
                ManagedClusterLevel::MonotoneCharacters => {
                    merge(glyphs, start as i32, end)?;
                }
                ManagedClusterLevel::Characters => {}
            }
            index += 1;
        }
        index += 1;
    }
    Ok(())
}
fn is_l(cp: u32) -> bool {
    matches!(cp,0x1100..=0x115f|0xa960..=0xa97c)
}
fn is_v(cp: u32) -> bool {
    matches!(cp,0x1160..=0x11a7|0xd7b0..=0xd7c6)
}
fn is_t(cp: u32) -> bool {
    matches!(cp,0x11a8..=0x11ff|0xd7cb..=0xd7fb)
}
fn hangul(glyphs: &mut Vec<ManagedShapingGlyph>, font: &ManagedFont) -> Result<(), FontError> {
    for glyph in glyphs.iter_mut() {
        glyph.script_shaper = 4;
        glyph.script_feature_mask = 0;
    }
    let mut index = 0;
    while index < glyphs.len() {
        let cp = glyphs[index].code_point;
        if is_l(cp) && index + 1 < glyphs.len() && is_v(glyphs[index + 1].code_point) {
            let trailing = if index + 2 < glyphs.len() && is_t(glyphs[index + 2].code_point) {
                glyphs[index + 2].code_point
            } else {
                0
            };
            let count = if trailing == 0 { 2 } else { 3 };
            if (0x1100..=0x1112).contains(&cp)
                && (0x1161..=0x1175).contains(&glyphs[index + 1].code_point)
                && (trailing == 0 || (0x11a8..=0x11c2).contains(&trailing))
            {
                let composed = 0xac00
                    + (cp - 0x1100) * 588
                    + (glyphs[index + 1].code_point - 0x1161) * 28
                    + if trailing == 0 { 0 } else { trailing - 0x11a7 };
                let id = font.glyph_index(composed)?;
                if id != 0 {
                    let mut record = glyphs[index];
                    record.code_point = composed;
                    record.glyph_index = id;
                    record.cluster = merge(glyphs, index as i32, index + count)?;
                    record.script_feature_mask = 0;
                    glyphs[index] = record;
                    glyphs.drain(index + 1..index + count);
                    index += 1;
                    continue;
                }
            }
            glyphs[index].script_feature_mask = 1;
            glyphs[index + 1].script_feature_mask = 2;
            if trailing != 0 {
                glyphs[index + 2].script_feature_mask = 4;
            }
            merge(glyphs, index as i32, index + count)?;
            index += count;
            continue;
        }
        if (0xac00..=0xd7a3).contains(&cp) {
            let syllable = cp - 0xac00;
            let trailing = syllable % 28;
            if trailing == 0
                && index + 1 < glyphs.len()
                && (0x11a8..=0x11c2).contains(&glyphs[index + 1].code_point)
            {
                let composed = cp + glyphs[index + 1].code_point - 0x11a7;
                let id = font.glyph_index(composed)?;
                if id != 0 {
                    let mut record = glyphs[index];
                    record.code_point = composed;
                    record.glyph_index = id;
                    record.cluster = merge(glyphs, index as i32, index + 2)?;
                    glyphs[index] = record;
                    glyphs.remove(index + 1);
                    index += 1;
                    continue;
                }
            }
            let covered = font.glyph_index(cp)? != 0;
            let extra = trailing == 0
                && index + 1 < glyphs.len()
                && is_t(glyphs[index + 1].code_point)
                && !(0x11a8..=0x11c2).contains(&glyphs[index + 1].code_point);
            if !covered || extra {
                let leading = 0x1100 + syllable / 588;
                let vowel = 0x1161 + syllable % 588 / 28;
                let tail = 0x11a7 + trailing;
                let l = font.glyph_index(leading)?;
                let v = font.glyph_index(vowel)?;
                let t = if trailing == 0 {
                    0
                } else {
                    font.glyph_index(tail)?
                };
                if l != 0 && v != 0 && (trailing == 0 || t != 0) {
                    let mut source = glyphs[index];
                    source.code_point = leading;
                    source.glyph_index = l;
                    source.script_feature_mask = 1;
                    glyphs[index] = source;
                    let mut record = source;
                    record.code_point = vowel;
                    record.glyph_index = v;
                    record.script_feature_mask = 2;
                    insert(glyphs, index + 1, record)?;
                    let mut count = 2;
                    if trailing != 0 {
                        record = source;
                        record.code_point = tail;
                        record.glyph_index = t;
                        record.script_feature_mask = 4;
                        insert(glyphs, index + 2, record)?;
                        count += 1;
                    }
                    if extra {
                        glyphs[index + count].script_shaper = 4;
                        glyphs[index + count].script_feature_mask = 4;
                        count += 1;
                    }
                    index += count;
                    continue;
                }
            }
        }
        index += 1;
    }
    Ok(())
}
/// Original source modified combining class, not canonical Unicode normalization.
/// Raw unassigned/invalid values retain the original table's zero class.
pub fn managed_modified_combining_class(cp: u32) -> u8 {
    if matches!(cp, 0x1a60 | 0xfc6) {
        return 254;
    }
    if cp == 0xf39 {
        return 127;
    }
    match canonical_combining_class(cp) {
        10 => 22,
        11 => 15,
        12 => 16,
        13 => 17,
        14 => 23,
        15 => 18,
        16 => 19,
        17 => 20,
        18 => 21,
        19 => 14,
        20 => 24,
        21 => 12,
        22 => 25,
        23 => 13,
        24 => 10,
        25 => 11,
        27 => 28,
        28 => 29,
        29 => 30,
        30 => 31,
        31 => 32,
        32 => 33,
        33 => 27,
        84 => 4,
        91 => 5,
        103 => 3,
        130 => 132,
        132 => 131,
        c => c,
    }
}
fn reorder_marks(glyphs: &mut [ManagedShapingGlyph], level: ManagedClusterLevel, script: &str) {
    let mut start = 0;
    while start < glyphs.len() {
        while start < glyphs.len()
            && managed_modified_combining_class(glyphs[start].code_point) == 0
        {
            start += 1;
        }
        let mut end = start;
        while end < glyphs.len() && managed_modified_combining_class(glyphs[end].code_point) != 0 {
            end += 1;
        }
        for index in start + 1..end {
            let value = glyphs[index];
            let class = managed_modified_combining_class(value.code_point);
            let mut destination = index;
            let mut crossed = i32::MAX;
            while destination > start
                && managed_modified_combining_class(glyphs[destination - 1].code_point) > class
            {
                crossed = crossed.min(glyphs[destination - 1].cluster);
                glyphs[destination] = glyphs[destination - 1];
                destination -= 1;
            }
            glyphs[destination] = value;
            if level == ManagedClusterLevel::MonotoneCharacters && destination < index {
                for g in &mut glyphs[destination + 1..=index] {
                    g.cluster = crossed;
                }
            }
        }
        if matches!(
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
            let mut begin = start;
            for class in [220, 230] {
                let mut first = begin;
                while first < end
                    && managed_modified_combining_class(glyphs[first].code_point) < class
                {
                    first += 1;
                }
                if first == end
                    || managed_modified_combining_class(glyphs[first].code_point) != class
                {
                    continue;
                }
                let mut last = first;
                while last < end
                    && managed_modified_combining_class(glyphs[last].code_point) == class
                    && matches!(
                        glyphs[last].code_point,
                        0x654
                            | 0x655
                            | 0x658
                            | 0x6dc
                            | 0x6e3
                            | 0x6e7
                            | 0x6e8
                            | 0x8ca
                            | 0x8cb
                            | 0x8cd
                            | 0x8ce
                            | 0x8cf
                            | 0x8d3
                            | 0x8f3
                    )
                {
                    last += 1;
                }
                let count = last - first;
                if count == 0 {
                    continue;
                }
                glyphs[begin..first].reverse();
                glyphs[first..last].reverse();
                glyphs[begin..last].reverse();
                begin += count;
            }
        }
        start = end + 1;
    }
}
fn hebrew(glyphs: &mut Vec<ManagedShapingGlyph>, font: &ManagedFont) -> Result<(), FontError> {
    let mut start = 0;
    while start < glyphs.len() {
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].grapheme_cluster == glyphs[start].grapheme_cluster {
            end += 1;
        }
        let starter = (start..end)
            .find(|&i| matches!(glyphs[i].code_point,0x5d0..=0x5ea|0xfb1d..=0xfb4e|0x5f2));
        if let Some(starter) = starter {
            let mut index = starter + 1;
            while index < end {
                let composed = hebrew_pair(glyphs[starter].code_point, glyphs[index].code_point);
                if composed != 0 {
                    let id = font.glyph_index(composed)?;
                    if id != 0 {
                        glyphs[starter].code_point = composed;
                        glyphs[starter].glyph_index = id;
                        glyphs.remove(index);
                        end -= 1;
                        continue;
                    }
                }
                index += 1;
            }
        }
        start = end;
    }
    Ok(())
}
fn hebrew_pair(first: u32, second: u32) -> u32 {
    if second == 0x5bc && (0x5d0..=0x5ea).contains(&first) {
        const DAGESH: [u32; 27] = [
            0xfb30, 0xfb31, 0xfb32, 0xfb33, 0xfb34, 0xfb35, 0xfb36, 0, 0xfb38, 0xfb39, 0xfb3a,
            0xfb3b, 0xfb3c, 0, 0xfb3e, 0, 0xfb40, 0xfb41, 0, 0xfb43, 0xfb44, 0, 0xfb46, 0xfb47,
            0xfb48, 0xfb49, 0xfb4a,
        ];
        return DAGESH[(first - 0x5d0) as usize];
    }
    match (first, second) {
        (0x5d9, 0x5b4) => 0xfb1d,
        (0x5f2, 0x5b7) => 0xfb1f,
        (0x5d0, 0x5b7) => 0xfb2e,
        (0x5d0, 0x5b8) => 0xfb2f,
        (0x5d5, 0x5b9) => 0xfb4b,
        (0x5d1, 0x5bf) => 0xfb4c,
        (0x5db, 0x5bf) => 0xfb4d,
        (0x5e4, 0x5bf) => 0xfb4e,
        (0x5e9, 0x5c1) => 0xfb2a,
        (0x5e9, 0x5c2) => 0xfb2b,
        (0xfb49, 0x5c1) | (0xfb2a, 0x5bc) => 0xfb2c,
        (0xfb49, 0x5c2) | (0xfb2b, 0x5bc) => 0xfb2d,
        _ => 0,
    }
}
fn use_diacritics(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    profile: ManagedNormalizationProfile,
    marks: ManagedMarkProfile,
) -> Result<(), PreparationError> {
    let mut normalizer = super::preparation::Normalizer::default();
    let mut normalized = Vec::new();
    let mut index = 0;
    while index < glyphs.len() {
        let source = glyphs[index];
        let point = char::from_u32(source.code_point).ok_or(UnicodeError::InvalidArgument)?;
        let mut units = [0; 2];
        let input = point.encode_utf16(&mut units);
        normalizer.normalize(
            input,
            profile,
            NormalizationForm::CanonicalDecomposition,
            &mut normalized,
        )?;
        let first = char::decode_utf16(normalized.iter().copied())
            .next()
            .expect("one normalized scalar")
            .expect("normalized UTF-16");
        if normalized != input && marks.is_mark(first as u32)? {
            glyphs.remove(index);
            let mut count = 0;
            for component in char::decode_utf16(normalized.iter().copied()) {
                let cp = component.expect("normalized UTF-16") as u32;
                let glyph = ManagedShapingGlyph::new(font.glyph_index(cp)?, source.cluster, cp);
                insert(glyphs, index + count, glyph)?;
                count += 1;
            }
            index += count;
        } else {
            index += 1;
        }
    }
    Ok(())
}
