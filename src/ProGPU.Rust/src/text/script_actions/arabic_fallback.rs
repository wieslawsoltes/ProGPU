//! Original presentation fallback after contextual Arabic substitution stages.
//! O(N(log F + R K Q)) work for N records, F forms, R ligature rules,
//! K visited component records and Q nominal font-query cost; O(1) scratch.
//! Original repeated cluster merges and vector removals add O(N²) worst-case work.
//! Reuses the actual GSUB traversal and full-record ligature mutation contract.
use crate::{
    font::{
        FontError, ManagedFont,
        layout::{ManagedFeatureSetting, ManagedGlyphBuffer},
    },
    text::{preparation::PreparationError, unicode::UnicodeError},
};
#[path = "arabic_fallback_data.rs"]
mod data;

/// Original whole-buffer Arabic fallback feature choices. Per-position ranges
/// and baseline features do not participate in this source operation.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ManagedArabicFallbackFeatures {
    /// Apply initial presentation forms (`init`).
    pub initial: bool,
    /// Apply medial presentation forms (`medi`).
    pub medial: bool,
    /// Apply final presentation forms (`fina`).
    pub final_form: bool,
    /// Apply isolated presentation forms (`isol`).
    pub isolated: bool,
    /// Apply all three original ordered ligature passes (`rlig`).
    pub required_ligatures: bool,
}
impl ManagedArabicFallbackFeatures {
    /// Capture the original final matching setting for each exact UTF-16 tag.
    /// Missing settings are disabled; every nonzero signed value is enabled.
    /// This O(F) snapshot allocates nothing for F settings.
    pub fn from_features(features: &[ManagedFeatureSetting]) -> Self {
        let enabled = |tag: [u8; 4]| {
            let tag = tag.map(u16::from);
            features
                .iter()
                .rev()
                .find(|value| value.tag() == tag)
                .is_some_and(|value| value.value() != 0)
        };
        Self {
            initial: enabled(*b"init"),
            medial: enabled(*b"medi"),
            final_form: enabled(*b"fina"),
            isolated: enabled(*b"isol"),
            required_ligatures: enabled(*b"rlig"),
        }
    }
}

pub(super) fn apply(
    buffer: &mut ManagedGlyphBuffer,
    font: &ManagedFont,
    features: ManagedArabicFallbackFeatures,
) -> Result<(), PreparationError> {
    for index in 0..buffer.glyphs.len() {
        let glyph = buffer.glyphs[index];
        let slot = match glyph.arabic_action {
            6 if features.initial => 0,
            4 if features.medial => 1,
            1 if features.final_form => 2,
            0 if features.isolated => 3,
            _ => continue,
        };
        if !(data::FORMS[0].0..=data::FORMS[data::FORMS.len() - 1].0).contains(&glyph.code_point) {
            continue;
        }
        let Ok(row) = data::FORMS.binary_search_by_key(&glyph.code_point, |entry| entry.0) else {
            continue;
        };
        let point = data::FORMS[row].1[slot];
        if point == 0 || glyph.glyph_index != font.glyph_index(glyph.code_point)? {
            continue;
        }
        let replacement = font.glyph_index(u32::from(point))?;
        if replacement != 0 && replacement != glyph.glyph_index {
            buffer.replace(index, replacement)?;
        }
    }
    if features.required_ligatures {
        ligatures(buffer, font, data::TRIPLES, true)?;
        ligatures(buffer, font, data::PAIRS, true)?;
        ligatures(buffer, font, data::MARKS, false)?;
    }
    Ok(())
}

fn ligatures<const N: usize>(
    buffer: &mut ManagedGlyphBuffer,
    font: &ManagedFont,
    rules: &[([u16; N], u16)],
    ignore_marks: bool,
) -> Result<(), PreparationError> {
    let flags = if ignore_marks { 8 } else { 0 };
    let mut components = [0; 3];
    let mut position = 0;
    while position < buffer.glyphs.len() {
        let first = buffer.glyphs[position].glyph_index;
        for &(points, result) in rules {
            let expected = font.glyph_index(u32::from(points[0]))?;
            if expected == 0 || first != expected {
                continue;
            }
            components[0] = position;
            let mut candidate = position;
            let mut matched = true;
            for component in 1..N {
                let expected = font.glyph_index(u32::from(points[component]))?;
                if expected == 0 {
                    matched = false;
                    break;
                }
                // Indices come only from this immutable traversal. Its InvalidGlyph
                // is therefore the original Rune-category rejection, not a font
                // mapping failure. Preserve the source action's argument error.
                let next = buffer
                    .next_component(candidate + 1, flags, expected)
                    .map_err(|error| match error {
                        FontError::InvalidGlyph => {
                            PreparationError::Unicode(UnicodeError::InvalidArgument)
                        }
                        error => PreparationError::Font(error),
                    })?;
                let Some(next) = next else {
                    matched = false;
                    break;
                };
                candidate = next;
                if buffer.glyphs[candidate].glyph_index != expected {
                    matched = false;
                    break;
                }
                components[component] = candidate;
            }
            // The original queries this glyph even after a component mismatch.
            let replacement = font.glyph_index(u32::from(result))?;
            if matched && replacement != 0 {
                buffer.replace_ligature(&components[..N], replacement)?;
                break;
            }
        }
        position += 1;
    }
    Ok(())
}
