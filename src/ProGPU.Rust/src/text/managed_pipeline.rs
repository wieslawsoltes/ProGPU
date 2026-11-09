//! Original managed shaping plan and complete substitution-stage orchestration.
//! Owns font/options and reusable GSUB/GPOS plans; each execution owns independent
//! text/context/record state. The TrueType route also performs complete original
//! positioning and optional safe-boundary verification before publication.
use super::{preparation::*, script_actions::*, unicode};
use crate::font::{
    FontError, ManagedFont, Tag,
    layout::{
        ManagedFeatureOptions, ManagedGlyphBuffer, ManagedPositioningPlan, ManagedSubstitutionPlan,
        ManagedSubstitutionStage,
    },
};
use std::sync::Arc;
mod position;
pub use position::{ManagedShapedRun, ManagedShapingError};

/// Frozen original source selection, independent of per-execution text context.
#[derive(Clone, Debug, Default)]
pub struct ManagedShapingOptions {
    /// Exact Unicode script override; None infers from the original text.
    pub script: Option<Arc<str>>,
    /// Exact optional language UTF-16, retaining malformed units for source policy.
    pub language: Option<Arc<[u16]>>,
    /// None resolves the original script's default horizontal direction.
    pub direction: Option<ManagedShapingDirection>,
    /// Original global, explicit, baseline and ranged feature policies.
    pub features: ManagedFeatureOptions,
}
/// Immutable original font-dependent plan. Reuse it for runs with the selected
/// script; the inference constructor freezes the script of its supplied text.
#[derive(Clone, Debug)]
pub struct ManagedShapingPlan {
    font: ManagedFont,
    unicode_script: Arc<str>,
    layout_script: Arc<str>,
    layout_utf16: Arc<[u16]>,
    requested: ManagedShapingOptions,
    features: ManagedFeatureOptions,
    direction: ManagedShapingDirection,
    use_shaper: bool,
    indic: bool,
    khmer: bool,
    myanmar: bool,
    arabic: bool,
    has_mark_feature: bool,
    substitution: Option<ManagedSubstitutionPlan>,
    positioning: Option<ManagedPositioningPlan>,
}
impl ManagedShapingPlan {
    /// Port of CreateShapingPlan with the actual source font and raw tables.
    /// Nothing is published until both selected lookup plans are complete.
    pub fn new(
        font: ManagedFont,
        text: &[u16],
        requested: ManagedShapingOptions,
    ) -> Result<Self, FontError> {
        let unicode_script = requested
            .script
            .clone()
            .unwrap_or_else(|| infer_script(text));
        let selection = resolve_managed_layout_script(&font, &unicode_script);
        let layout_script: Arc<str> = Arc::from(selection.layout_script);
        let use_shaper = selection.use_mark_order;
        let indic = !use_shaper
            && matches!(
                unicode_script.as_ref(),
                "beng" | "deva" | "gujr" | "guru" | "knda" | "mlym" | "orya" | "taml" | "telu"
            );
        let khmer = layout_script.as_ref() == "khmr";
        let myanmar = unicode_script.as_ref() == "mymr";
        let arabic = uses_arabic_joining(&unicode_script);
        let direction = requested.direction.unwrap_or_else(|| {
            if matches!(
                unicode_script.as_ref(),
                "arab" | "hebr" | "syrc" | "thaa" | "nkoo" | "adlm" | "rohg"
            ) {
                ManagedShapingDirection::RightToLeft
            } else {
                ManagedShapingDirection::LeftToRight
            }
        });
        let features =
            requested
                .features
                .for_shaping(&layout_script, use_shaper, indic, direction)?;
        let layout_utf16: Arc<[u16]> = layout_script.encode_utf16().collect();
        let substitution = if font.source().table(Tag::new(*b"GSUB")).is_some() {
            ManagedSubstitutionPlan::from_face(
                font.source().clone(),
                features.clone(),
                &layout_utf16,
                requested.language.as_deref(),
                &font.normalized_coordinates()?,
            )?
        } else {
            None
        };
        let positioning = if font.source().table(Tag::new(*b"GPOS")).is_some() {
            ManagedPositioningPlan::from_font(
                &font,
                features.clone(),
                &layout_utf16,
                requested.language.as_deref(),
            )?
        } else {
            None
        };
        let has_mark_feature = unicode_script.as_ref() == "hebr" && has_feature(&font, *b"mark");
        Ok(Self {
            font,
            unicode_script,
            layout_script,
            layout_utf16,
            requested,
            features,
            direction,
            use_shaper,
            indic,
            khmer,
            myanmar,
            arabic,
            has_mark_feature,
            substitution,
            positioning,
        })
    }
    /// Actual font instance retained by both plans and produced buffers.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
    /// Original inferred or explicitly selected Unicode script.
    pub fn unicode_script(&self) -> &str {
        &self.unicode_script
    }
    /// Actual selected first/second/third generation OpenType script.
    pub fn layout_script(&self) -> &str {
        &self.layout_script
    }
    /// Exact original requested policy, before implicit feature insertion.
    pub fn requested_options(&self) -> &ManagedShapingOptions {
        &self.requested
    }
    /// Ordered effective features and preserved explicit/range policy.
    pub fn features(&self) -> &ManagedFeatureOptions {
        &self.features
    }
    /// Resolved physical direction.
    pub fn direction(&self) -> ManagedShapingDirection {
        self.direction
    }
    /// Actual GSUB plan, with missing table distinct from an empty selection.
    pub fn substitution(&self) -> Option<&ManagedSubstitutionPlan> {
        self.substitution.as_ref()
    }
    /// Actual GPOS plan for the same selected font and feature generation.
    pub fn positioning(&self) -> Option<&ManagedPositioningPlan> {
        self.positioning.as_ref()
    }
    /// Original policy consumed by position metric initialization.
    pub fn zero_mark_advances_early(&self) -> bool {
        self.use_shaper || self.myanmar
    }
    /// Original independent fallback mark-positioning policy.
    pub fn uses_fallback_mark_positioning(&self) -> bool {
        !self.use_shaper
            && !self.indic
            && !self.khmer
            && !matches!(
                self.unicode_script.as_ref(),
                "thai" | "lao " | "mymr" | "qaag"
            )
    }
    fn has_lookup(&self, tags: &[[u8; 4]]) -> bool {
        self.substitution.as_ref().is_some_and(|p| {
            p.selection()
                .lookups()
                .iter()
                .any(|l| tags.contains(&l.tag))
        })
    }
    fn stage(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        stage: ManagedSubstitutionStage,
    ) -> Result<(), PreparationError> {
        if let Some(plan) = &self.substitution {
            plan.apply_stage(buffer, stage)?;
        } else {
            // Original ApplySubstitutions resets this even for missing GSUB.
            let mut state = buffer.state();
            state.traversal.manual_joiners = false;
            buffer.set_state(state);
        }
        Ok(())
    }
    fn stages(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        stages: &[ManagedSubstitutionStage],
    ) -> Result<(), PreparationError> {
        for &stage in stages {
            self.stage(buffer, stage)?;
        }
        Ok(())
    }
    /// Execute the complete original pre-positioning sequence on a fresh owned
    /// generation. The selected script is frozen in this reusable plan. Source
    /// errors discard this candidate; no partially shaped result is published.
    pub fn substitute(
        &self,
        text: impl Into<Arc<[u16]>>,
        preparation: ManagedPreparationOptions,
    ) -> Result<ManagedGlyphBuffer, PreparationError> {
        use ManagedScriptAction as A;
        use ManagedSubstitutionStage as S;
        let text = text.into();
        let simple_latin = self.direction == ManagedShapingDirection::LeftToRight
            && self.unicode_script.as_ref() == "latn"
            && text.iter().all(|u| (0x20..=0x7e).contains(u));
        let mut buffer = ManagedGlyphBuffer::from_text(
            text,
            self.font.clone(),
            self.unicode_script.clone(),
            preparation,
        )?;
        buffer.apply_script_action(A::BeginningDottedCircle)?;
        if !simple_latin {
            for action in [
                A::DirectionalFallback {
                    direction: self.direction,
                    has_vertical_substitution: self.has_lookup(&[*b"vert", *b"vrt2"]),
                },
                A::Hangul,
                A::ModifiedCombiningMarks,
                A::HebrewComposition {
                    has_mark_feature: self.has_mark_feature,
                },
                A::VowelConstraints,
                A::UseDiacritics {
                    enabled: self.use_shaper,
                },
                A::ThaiLao,
                A::Fractions {
                    digits: ManagedDigitProfile::Unicode16_0,
                },
            ] {
                buffer.apply_script_action(action)?;
            }
            buffer.prepare_khmer_layout(&self.layout_script)?;
            for action in [
                A::IndicPreparation {
                    enabled: self.indic,
                },
                A::MyanmarPreparation {
                    enabled: self.myanmar,
                },
                A::UsePreparation {
                    enabled: self.use_shaper,
                },
                A::ArabicJoining,
            ] {
                buffer.apply_script_action(action)?;
            }
        }
        if self.use_shaper || self.indic || self.khmer || self.arabic {
            self.stage(&mut buffer, S::Directional)?;
        }
        if self.use_shaper {
            self.stage(&mut buffer, S::Preprocessing)?;
            buffer.apply_script_action(A::ClearSubstitutionFlags)?;
            self.stage(&mut buffer, S::Repha)?;
            buffer.apply_script_action(A::UseRecordRepha)?;
            buffer.apply_script_action(A::ClearSubstitutionFlags)?;
            self.stage(&mut buffer, S::Prebase)?;
            buffer.apply_script_action(A::UseRecordPrebase)?;
            self.stage(&mut buffer, S::Basic)?;
            buffer.apply_script_action(A::UseReordering { enabled: true })?;
            self.stages(&mut buffer, &[S::Topographical, S::Presentation])?;
        } else if self.indic {
            self.stage(&mut buffer, S::IndicPreprocessing)?;
            buffer.initial_reorder_indic(&self.layout_utf16, self.substitution.as_ref())?;
            self.stages(
                &mut buffer,
                &[
                    S::IndicNukta,
                    S::IndicAkhand,
                    S::Repha,
                    S::IndicRakar,
                    S::Prebase,
                    S::IndicBelow,
                    S::IndicAbove,
                    S::IndicHalf,
                    S::IndicPost,
                    S::IndicVattu,
                    S::IndicConjunct,
                ],
            )?;
            buffer.apply_script_action(A::IndicFinalReordering {
                continuation: ManagedIndicContinuationProfile::Unicode16_0,
            })?;
            self.stage(&mut buffer, S::IndicPresentation)?;
        } else if self.khmer {
            self.stage(&mut buffer, S::KhmerBasic)?;
            buffer.apply_script_action(A::ClearSyllables)?;
            self.stage(&mut buffer, S::KhmerPresentation)?;
        } else if self.myanmar {
            self.stage(&mut buffer, S::MyanmarPreprocessing)?;
            buffer.apply_script_action(A::MyanmarReordering)?;
            self.stages(
                &mut buffer,
                &[
                    S::MyanmarRepha,
                    S::MyanmarPrebase,
                    S::MyanmarBelow,
                    S::MyanmarPostbase,
                ],
            )?;
            buffer.apply_script_action(A::ClearSyllables)?;
            self.stage(&mut buffer, S::MyanmarPresentation)?;
        } else if self.arabic {
            self.stage(&mut buffer, S::ArabicStretch)?;
            buffer.apply_script_action(A::RecordArabicStretch)?;
            self.stages(
                &mut buffer,
                &[
                    S::ArabicPreprocessing,
                    S::ArabicIsolated,
                    S::ArabicFinal,
                    S::ArabicFinal2,
                    S::ArabicFinal3,
                    S::ArabicMedial,
                    S::ArabicMedial2,
                    S::ArabicInitial,
                    S::ArabicRequiredLigatures,
                ],
            )?;
            let fallback = !self.has_lookup(&[
                *b"isol", *b"fina", *b"fin2", *b"fin3", *b"medi", *b"med2", *b"init",
            ]);
            buffer.apply_script_action(A::ArabicFallback {
                enabled: self.unicode_script.as_ref() == "arab" && fallback,
                features: ManagedArabicFallbackFeatures::from_features(self.features.features()),
            })?;
            let reverse = self.direction == ManagedShapingDirection::LeftToRight && !fallback;
            if reverse {
                buffer.apply_script_action(A::Reverse)?;
            }
            if self.has_lookup(&[*b"rclt"]) {
                self.stage(&mut buffer, S::ArabicPostRequired)?;
            } else {
                self.stages(&mut buffer, &[S::ArabicContextual, S::ArabicPostContextual])?;
            }
            if reverse {
                buffer.apply_script_action(A::Reverse)?;
            }
        } else {
            self.stage(&mut buffer, S::All)?;
        }
        Ok(buffer)
    }
}
// Original script inference enumerates UTF-16 as Rune, replacing malformed units.
fn infer_script(text: &[u16]) -> Arc<str> {
    for scalar in char::decode_utf16(text.iter().copied()) {
        let tag = unicode::script(scalar.unwrap_or(char::REPLACEMENT_CHARACTER) as u32);
        if tag != Tag::new(*b"DFLT") {
            return Arc::from(
                std::str::from_utf8(&tag.bytes()).expect("original script tags are ASCII"),
            );
        }
    }
    Arc::from("DFLT")
}
pub(crate) fn uses_arabic_joining(script: &str) -> bool {
    matches!(
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
    )
}
fn has_feature(font: &ManagedFont, tag: [u8; 4]) -> bool {
    for table in [*b"GSUB", *b"GPOS"] {
        let Some(table) = font.source().table(Tag::new(table)) else {
            continue;
        };
        let data = table.bytes;
        let word = |at: usize| {
            data.get(at..at + 2)
                .map(|v| u16::from_be_bytes([v[0], v[1]]) as usize)
        };
        let Some(start) = word(6) else {
            continue;
        };
        let Some(count) = word(start) else {
            continue;
        };
        for i in 0..count {
            let Some(record) = data.get(start + 2 + i * 6..start + 8 + i * 6) else {
                break;
            };
            if record[..4] == tag {
                return true;
            }
        }
    }
    false
}
