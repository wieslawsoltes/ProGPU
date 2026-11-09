//! Full original action/state transport shared with browser AOT.
use progpu::{
    font::{
        ManagedFont,
        layout::{ManagedFeatureSetting, ManagedGlyphBuffer},
    },
    text::{
        ManagedShapingGlyph,
        preparation::*,
        script_actions::*,
        unicode::{
            grapheme::ManagedGraphemeProfile, normalization::ManagedNormalizationProfile, *,
        },
    },
};
#[path = "managed_preparation_replay.rs"]
#[allow(dead_code)]
pub mod preparation;
use preparation::{records, units};
#[derive(Clone, Debug)]
pub struct Case {
    pub source: preparation::Case,
    pub actions: Vec<u32>,
    pub raw: Option<Vec<ManagedShapingGlyph>>,
}
pub fn action(code: u32) -> Option<ManagedScriptAction> {
    use ManagedScriptAction::*;
    Some(match code {
        0 => BeginningDottedCircle,
        1..=8 => DirectionalFallback {
            direction: [
                ManagedShapingDirection::LeftToRight,
                ManagedShapingDirection::RightToLeft,
                ManagedShapingDirection::TopToBottom,
                ManagedShapingDirection::BottomToTop,
            ][((code - 1) / 2) as usize],
            has_vertical_substitution: !(code - 1).is_multiple_of(2),
        },
        9 => Hangul,
        10 => ModifiedCombiningMarks,
        11 | 12 => HebrewComposition {
            has_mark_feature: code == 12,
        },
        13 | 14 => UseDiacritics {
            enabled: code == 14,
        },
        15 => ThaiLao,
        16 => Fractions {
            digits: ManagedDigitProfile::Unicode16_0,
        },
        17 => ClearSubstitutionFlags,
        18 => ClearSyllables,
        19 => RecordArabicStretch,
        20 => Reverse,
        21 => ArabicJoining,
        68 => VowelConstraints,
        22..=28 | 32..=63 => ArabicFallback {
            enabled: code != 22,
            features: fallback_features(code),
        },
        _ => return None,
    })
}
fn fallback_features(code: u32) -> ManagedArabicFallbackFeatures {
    let mut settings = Vec::new();
    for (index, tag) in [*b"init", *b"medi", *b"fina", *b"isol", *b"rlig"]
        .into_iter()
        .enumerate()
    {
        let mut add =
            |tag: [u16; 4], value| settings.push(ManagedFeatureSetting::new(tag, value).unwrap());
        let tag = tag.map(u16::from);
        match code {
            22 => add(tag, 1),
            23 => {}
            24 => {
                add(tag, 1);
                add(tag, 0);
            }
            25 => {
                add(tag, 0);
                add(tag, i32::MIN);
            }
            26 => {
                add(tag.map(|v| v - 32), 1);
            }
            27 => {
                let mut bad = tag;
                bad[3] = 0xd800;
                add(bad, 1);
            }
            28 => {
                add(tag, -1);
                add(tag, 1);
            }
            _ => add(tag, ((code - 32) >> index & 1) as i32),
        }
    }
    ManagedArabicFallbackFeatures::from_features(&settings)
}
impl Case {
    pub fn request(&self) -> Vec<u32> {
        let mut out = self.source.request();
        out.push(self.actions.len() as u32);
        out.extend(&self.actions);
        match &self.raw {
            None => out.push(u32::MAX),
            Some(glyphs) => {
                out.push(glyphs.len() as u32);
                for &g in glyphs {
                    out.extend(records::words(g));
                }
            }
        }
        out
    }
    #[allow(dead_code)]
    pub fn from_request(font: usize, request: &[u32]) -> Option<Self> {
        fn skip(request: &[u32], at: &mut usize) -> Option<()> {
            let count = *request.get(*at)? as usize;
            *at = at.checked_add(count)?.checked_add(1)?;
            request.get(..*at)?;
            Some(())
        }
        let mut at = 0;
        skip(request, &mut at)?;
        at = at.checked_add(2)?;
        for _ in 0..3 {
            skip(request, &mut at)?;
        }
        let source = preparation::Case::from_request(font, request.get(..at)?)?;
        let count = *request.get(at)? as usize;
        at += 1;
        let end = at.checked_add(count)?;
        let actions = request.get(at..end)?.to_vec();
        if actions
            .iter()
            .any(|&c| action(c).is_none() && !(64..=67).contains(&c))
        {
            return None;
        }
        at = end;
        let count = *request.get(at)?;
        at += 1;
        let raw = if count == u32::MAX {
            None
        } else {
            let end = at.checked_add((count as usize).checked_mul(31)?)?;
            let records = request
                .get(at..end)?
                .chunks_exact(31)
                .map(records::from_words)
                .collect();
            at = end;
            Some(records)
        };
        if at != request.len() {
            return None;
        }
        Some(Self {
            source,
            actions,
            raw,
        })
    }
}
pub fn execute(
    font: Option<&ManagedFont>,
    case: &Case,
    profile: ManagedNormalizationProfile,
    out: &mut Vec<u32>,
) {
    out.clear();
    let Some(font) = font else {
        out.push(1);
        return;
    };
    let c = &case.source;
    let selected = resolve_managed_layout_script(font, &c.script);
    out.extend([0, u32::from(selected.use_mark_order)]);
    units(
        out,
        &selected.layout_script.encode_utf16().collect::<Vec<_>>(),
    );
    let Ok(level) = ManagedClusterLevel::try_from(c.level) else {
        out.push(2);
        return;
    };
    let mut options = ManagedPreparationOptions::new(
        profile,
        ManagedMarkProfile::Unicode16_0,
        ManagedGraphemeProfile::Unicode16_0,
    );
    options.cluster_level = level;
    options.buffer_flags = ManagedBufferFlags::from_bits(c.flags);
    options.pre_context = c.pre.clone().into();
    options.post_context = c.post.clone().into();
    let mut buffer = match ManagedGlyphBuffer::from_text(
        c.text.clone(),
        font.clone(),
        c.script.clone(),
        options,
    ) {
        Ok(buffer) => {
            out.push(0);
            buffer
        }
        Err(e) => {
            out.push(match e {
                PreparationError::GlyphLimit => 3,
                PreparationError::Font(_) => 4,
                _ => 2,
            });
            return;
        }
    };
    if let Some(raw) = &case.raw {
        if raw.len() != buffer.glyphs().len() {
            out.push(5);
            return;
        }
        buffer.glyphs_mut().copy_from_slice(raw);
    }
    let mut attempted = 0;
    let mut error = 0;
    for &code in &case.actions {
        attempted += 1;
        if (64..=67).contains(&code) {
            let mut state = buffer.state();
            match code {
                64 | 65 => state.traversal.manual_joiners = code == 64,
                66 => {
                    state.traversal.restrict_to_syllable = true;
                    let Some(first) = buffer.glyphs().first() else {
                        buffer.set_state(state);
                        error = 4;
                        break;
                    };
                    state.traversal.lookup_syllable = first.use_syllable;
                }
                _ => {
                    state.traversal.restrict_to_syllable = false;
                    state.traversal.lookup_syllable = 0;
                }
            }
            buffer.set_state(state);
            continue;
        }
        let Some(action) = action(code) else {
            error = 5;
            break;
        };
        if let Err(e) = buffer.apply_script_action(action) {
            error = match e {
                PreparationError::Font(_) => 4,
                _ => 2,
            };
            break;
        }
    }
    out.extend([attempted, error]);
    let source = buffer.preparation().unwrap();
    let options = source.options();
    let state = buffer.state();
    out.extend([
        buffer.glyphs().len() as u32,
        u32::from(source.font() == font),
        options.cluster_level as u32,
        options.buffer_flags.bits() as u32,
    ]);
    units(out, &options.pre_context);
    units(out, &options.post_context);
    out.extend([
        state.context_match_end as u32,
        state.random_state,
        buffer.mark_filtering_coverage() as u32,
        buffer.nested_lookup_depth(),
        u32::from(state.traversal.manual_joiners),
        u32::from(state.traversal.restrict_to_syllable),
        state.traversal.lookup_syllable as u32,
        u32::from(buffer.has_fraction_actions()),
    ]);
    for &g in buffer.glyphs() {
        out.extend(records::words(g));
    }
}
