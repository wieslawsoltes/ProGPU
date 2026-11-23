//! Original managed SfntFontFace name decoding, ranking and ordinal selection.
use super::{SfntFace, Tag, bytes};
use crate::text::unicode::{GeneralCategory, general_category};
use std::{cmp::Reverse, collections::HashSet, sync::Arc};

impl SfntFace {
    /// All original managed name candidates, ranked stably and deduplicated by
    /// exact ordinal value. Invalid records are skipped; a truncated inventory
    /// yields no names. Uses the original frozen Unicode 16 character categories,
    /// independently witnessed against the source runtime's complete BMP domain.
    /// This does not apply locale fallback, name normalization or native policies.
    pub fn managed_names(&self, name_id: u16) -> Vec<Arc<str>> {
        let Some(table) = self.table(Tag::new(*b"name")) else {
            return Vec::new();
        };
        let table = table.bytes;
        if table.len() < 6 {
            return Vec::new();
        }
        let count = usize::from(bytes::u16(table, 2).unwrap());
        let strings = usize::from(bytes::u16(table, 4).unwrap());
        if 6 + count * 12 > table.len() || strings > table.len() {
            return Vec::new();
        }
        let mut candidates = Vec::new();
        for index in 0..count {
            let at = 6 + index * 12;
            if bytes::u16(table, at + 6).unwrap() != name_id {
                continue;
            }
            let platform = bytes::u16(table, at).unwrap();
            let encoding = bytes::u16(table, at + 2).unwrap();
            let language = bytes::u16(table, at + 4).unwrap();
            let length = usize::from(bytes::u16(table, at + 8).unwrap());
            let offset = strings + usize::from(bytes::u16(table, at + 10).unwrap());
            let Some(value) = bytes::bytes(table, offset, length) else {
                continue;
            };
            let value = decode(value, platform, encoding);
            if !value.is_empty() {
                candidates.push((score(platform, language, &value), Arc::<str>::from(value)));
            }
        }
        candidates.sort_by_key(|c| Reverse(c.0));
        let mut seen = HashSet::new();
        candidates
            .into_iter()
            .filter_map(|(_, value)| seen.insert(value.clone()).then_some(value))
            .collect()
    }

    /// First original managed candidate, or None for a missing/empty inventory.
    /// Returned names own their bytes independently of the face generation.
    pub fn managed_name(&self, name_id: u16) -> Option<Arc<str>> {
        self.managed_names(name_id).into_iter().next()
    }
}

fn whitespace(ch: char) -> bool {
    matches!(ch, '\u{9}'..='\u{d}' | '\u{85}')
        || matches!(
            general_category(ch as u32),
            GeneralCategory::SpaceSeparator
                | GeneralCategory::LineSeparator
                | GeneralCategory::ParagraphSeparator
        )
}

pub(crate) fn decode(input: &[u8], platform: u16, encoding: u16) -> String {
    let mut value = if platform == 0 || platform == 3 {
        let mut pairs = input.chunks_exact(2);
        let mut value: String =
            char::decode_utf16(pairs.by_ref().map(|p| u16::from_be_bytes([p[0], p[1]])))
                .map(|r| r.unwrap_or(char::REPLACEMENT_CHARACTER))
                .collect();
        if !pairs.remainder().is_empty() {
            value.push(char::REPLACEMENT_CHARACTER);
        }
        value
    } else if platform == 1 && encoding == 0 {
        input.iter().map(|&byte| char::from(byte)).collect()
    } else {
        String::from_utf8_lossy(input).into_owned()
    };
    value.retain(|ch| ch != '\0');
    value.trim_matches(whitespace).to_owned()
}

pub(crate) fn score(platform: u16, language: u16, value: &str) -> u8 {
    let base = match (platform, language) {
        (3, 0x0409) => 4,
        (3, _) => 3,
        (0, _) => 2,
        _ => 1,
    };
    // Source Char.IsLetter examines UTF-16 code units, not decoded Runes. A
    // supplementary letter therefore never suppresses the original +10 bonus.
    for ch in value.encode_utf16() {
        if ch > 0x024f
            && matches!(
                general_category(u32::from(ch)),
                GeneralCategory::UppercaseLetter
                    | GeneralCategory::LowercaseLetter
                    | GeneralCategory::TitlecaseLetter
                    | GeneralCategory::ModifierLetter
                    | GeneralCategory::OtherLetter
            )
        {
            return base;
        }
    }
    base + 10
}
