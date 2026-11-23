//! Original managed GlyphSubstitutionBuffer.Create, before script shaping stages.
//! Retains source UTF-16 and independent grapheme, character and shaping clusters.
//! Mapping is O(N log R) plus original normalization work (quadratic in a worst
//! case combining sequence), with O(N + D) retained/scratch storage for N input
//! units and D decomposed scalars. Ordered state remains reusable CPU text data.
use super::{ManagedShapingGlyph, unicode::*};
use crate::font::{FontError, ManagedFont, Tag, layout::ManagedGlyphBuffer};
use grapheme::{ManagedGraphemeProfile, managed_next_grapheme_length};
use normalization::{
    ManagedNormalizationProfile, NormalizationForm, managed_profile_capacity,
    normalize_managed_profile_into,
};
use std::sync::Arc;
mod script;
pub use script::{ManagedScriptSelection, resolve_managed_layout_script};
mod khmer_facts;
pub(crate) mod pool;

/// Original maximum input units and produced preparation records.
pub const MAX_MANAGED_SHAPED_GLYPHS: usize = 1_048_576;

/// Original source cluster policy, independent of grapheme repertoire.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum ManagedClusterLevel {
    /// Original monotone grapheme policy.
    #[default]
    MonotoneGraphemes = 0,
    /// Keep character clusters, marking grapheme interiors unsafe.
    MonotoneCharacters = 1,
    /// Keep nonmonotone character clusters, marking grapheme interiors unsafe.
    Characters = 2,
    /// Original nonmonotone grapheme policy.
    Graphemes = 3,
}
impl TryFrom<u8> for ManagedClusterLevel {
    type Error = PreparationError;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::MonotoneGraphemes),
            1 => Ok(Self::MonotoneCharacters),
            2 => Ok(Self::Characters),
            3 => Ok(Self::Graphemes),
            _ => Err(PreparationError::InvalidClusterLevel),
        }
    }
}
impl ManagedClusterLevel {
    fn characters(self) -> bool {
        matches!(self, Self::MonotoneCharacters | Self::Characters)
    }
}
/// All original source flag bits; combinations retain their raw independent intent.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ManagedBufferFlags(u8);
impl ManagedBufferFlags {
    /// BeginningOfText.
    pub const BEGINNING_OF_TEXT: Self = Self(1);
    /// EndOfText.
    pub const END_OF_TEXT: Self = Self(2);
    /// PreserveDefaultIgnorables.
    pub const PRESERVE_DEFAULT_IGNORABLES: Self = Self(4);
    /// RemoveDefaultIgnorables.
    pub const REMOVE_DEFAULT_IGNORABLES: Self = Self(8);
    /// DoNotInsertDottedCircle.
    pub const DO_NOT_INSERT_DOTTED_CIRCLE: Self = Self(16);
    /// Verify.
    pub const VERIFY: Self = Self(32);
    /// ProduceUnsafeToConcat.
    pub const PRODUCE_UNSAFE_TO_CONCAT: Self = Self(64);
    /// ProduceSafeToInsertTatweel.
    pub const PRODUCE_SAFE_TO_INSERT_TATWEEL: Self = Self(128);
    /// All eight bits have original meaning; no combination is rewritten.
    pub const fn from_bits(value: u8) -> Self {
        Self(value)
    }
    /// Exact raw source flags.
    pub const fn bits(self) -> u8 {
        self.0
    }
    /// Test whether all selected bits are present.
    pub const fn contains(self, flags: Self) -> bool {
        self.0 & flags.0 == flags.0
    }
}
impl std::ops::BitOr for ManagedBufferFlags {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}
/// Owned immutable UTF-16 context view. Neighboring font runs share their actual
/// paragraph owner without copying its prefix and suffix for every partition.
#[derive(Clone, Debug, Default)]
pub struct ManagedTextContext {
    owner: Arc<[u16]>,
    range: std::ops::Range<usize>,
}
impl ManagedTextContext {
    /// Retain the exact original owner and validated range; no decoding or copy.
    pub fn view(owner: Arc<[u16]>, range: std::ops::Range<usize>) -> Result<Self, UnicodeError> {
        if range.start > range.end || range.end > owner.len() {
            return Err(UnicodeError::InvalidArgument);
        }
        Ok(Self { owner, range })
    }
    /// Actual immutable source owner, including units outside the selected view.
    pub fn owner(&self) -> &Arc<[u16]> {
        &self.owner
    }
    /// Original source-unit range within that owner.
    pub fn range(&self) -> std::ops::Range<usize> {
        self.range.clone()
    }
}
impl From<Arc<[u16]>> for ManagedTextContext {
    fn from(owner: Arc<[u16]>) -> Self {
        let range = 0..owner.len();
        Self { owner, range }
    }
}
impl From<Vec<u16>> for ManagedTextContext {
    fn from(value: Vec<u16>) -> Self {
        Self::from(Arc::<[u16]>::from(value))
    }
}
impl std::ops::Deref for ManagedTextContext {
    type Target = [u16];
    fn deref(&self) -> &Self::Target {
        &self.owner[self.range.clone()]
    }
}
impl AsRef<[u16]> for ManagedTextContext {
    fn as_ref(&self) -> &[u16] {
        self
    }
}
/// Immutable source policy and owned neighboring context. Profiles are explicit:
/// OS names, SDK versions and font scripts do not select their Unicode facts.
#[derive(Clone, Debug)]
pub struct ManagedPreparationOptions {
    /// Original cluster policy.
    pub cluster_level: ManagedClusterLevel,
    /// Original flags, consumed independently by later shaping stages.
    pub buffer_flags: ManagedBufferFlags,
    /// Witnessed source normalization facts.
    pub normalization: ManagedNormalizationProfile,
    /// Independently witnessed Rune mark facts.
    pub marks: ManagedMarkProfile,
    /// Independently witnessed first-element facts.
    pub graphemes: ManagedGraphemeProfile,
    /// Exact caller-provided preceding UTF-16; never synthesized from this run.
    pub pre_context: ManagedTextContext,
    /// Exact caller-provided following UTF-16.
    pub post_context: ManagedTextContext,
}
impl ManagedPreparationOptions {
    /// Original default clustering/flags and empty context, with explicit profiles.
    pub fn new(
        normalization: ManagedNormalizationProfile,
        marks: ManagedMarkProfile,
        graphemes: ManagedGraphemeProfile,
    ) -> Self {
        Self {
            normalization,
            marks,
            graphemes,
            cluster_level: ManagedClusterLevel::default(),
            buffer_flags: ManagedBufferFlags::default(),
            pre_context: ManagedTextContext::default(),
            post_context: ManagedTextContext::default(),
        }
    }
}
/// Complete original preparation identity retained by its mutable glyph buffer.
/// Cloned buffers share this immutable generation and own independent records.
#[derive(Clone, Debug)]
pub struct ManagedPreparationSource {
    font: ManagedFont,
    text: Arc<[u16]>,
    script: Arc<str>,
    options: ManagedPreparationOptions,
}
impl ManagedPreparationSource {
    /// Actual selected font instance, including variation/palette/cache identity.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
    /// Original UTF-16, including malformed units; normalization never rewrites it.
    pub fn text(&self) -> &[u16] {
        &self.text
    }
    /// Original script string, before separate OpenType tag selection.
    pub fn script(&self) -> &str {
        &self.script
    }
    /// Immutable original context, profiles and preparation policies.
    pub fn options(&self) -> &ManagedPreparationOptions {
        &self.options
    }
}
/// Failure before publication of a complete original preparation generation.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum PreparationError {
    /// The operation needs actual text/font/context preparation metadata.
    #[error("managed script action requires original preparation metadata")]
    MissingPreparation,
    /// Unknown public cluster policy.
    #[error("invalid managed cluster level")]
    InvalidClusterLevel,
    /// Input or decomposition exceeds the original one-million-record bound.
    #[error("managed shaping glyph limit exceeded")]
    GlyphLimit,
    /// Allocation cannot represent the complete candidate generation.
    #[error("managed preparation capacity exceeded")]
    Capacity,
    /// Original font mapping failure.
    #[error(transparent)]
    Font(#[from] FontError),
    /// Original normalization/encoding admission failure.
    #[error(transparent)]
    Unicode(#[from] UnicodeError),
}

impl ManagedGlyphBuffer {
    /// Port of original GlyphSubstitutionBuffer.Create. Publishes only a complete
    /// candidate and retains actual font/text/context owners. Later script stages,
    /// substitution, positioning, fallback and paragraph layout remain separate.
    pub fn from_text(
        text: impl Into<Arc<[u16]>>,
        font: ManagedFont,
        script: impl Into<Arc<str>>,
        options: ManagedPreparationOptions,
    ) -> Result<Self, PreparationError> {
        let text = text.into();
        if text.len() > MAX_MANAGED_SHAPED_GLYPHS {
            return Err(PreparationError::GlyphLimit);
        }
        let script = script.into();
        let mut glyphs = pool::rent(text.len())?;
        if text.iter().all(|&unit| (0x20..=0x7e).contains(&unit)) {
            for (index, &unit) in text.iter().enumerate() {
                append_mapped(
                    &mut glyphs,
                    &font,
                    char::from_u32(u32::from(unit)).unwrap(),
                    index as i32,
                )?;
            }
        } else {
            prepare_non_ascii(&text, &font, &script, &options, &mut glyphs)?;
        }
        let mut result = Self::with_mark_profile(font.source().clone(), glyphs, options.marks)?;
        result.preparation = Some(Arc::new(ManagedPreparationSource {
            font,
            text,
            script,
            options,
        }));
        Ok(result)
    }
}

#[derive(Default)]
pub(super) struct Normalizer {
    scalars: Vec<UnicodeScalar>,
    result: Vec<UnicodeScalar>,
}
fn resize<T: Clone>(values: &mut Vec<T>, length: usize, fill: T) -> Result<(), PreparationError> {
    if length > values.len() {
        values
            .try_reserve(length - values.len())
            .map_err(|_| PreparationError::Capacity)?;
    }
    values.resize(length, fill);
    Ok(())
}
impl Normalizer {
    pub(super) fn normalize(
        &mut self,
        input: &[u16],
        profile: ManagedNormalizationProfile,
        form: NormalizationForm,
        output: &mut Vec<u16>,
    ) -> Result<(), PreparationError> {
        resize(&mut self.scalars, input.len(), UnicodeScalar::default())?;
        let count = decode_utf16_into(input, &mut self.scalars)? as usize;
        let input = &self.scalars[..count];
        let capacity = managed_profile_capacity(input, profile)? as usize;
        resize(&mut self.result, capacity, UnicodeScalar::default())?;
        let count =
            normalize_managed_profile_into(input, profile, form, &mut self.result)? as usize;
        output.clear();
        output
            .try_reserve(count.saturating_mul(2))
            .map_err(|_| PreparationError::Capacity)?;
        for scalar in &self.result[..count] {
            let point = char::from_u32(scalar.code_point).expect("normalized scalar");
            output.extend_from_slice(point.encode_utf16(&mut [0; 2]));
        }
        Ok(())
    }
}
fn rune(input: &[u16]) -> (char, usize, bool) {
    match char::decode_utf16(input.iter().copied()).next() {
        Some(Ok(point)) => (point, point.len_utf16(), true),
        Some(Err(_)) => (char::REPLACEMENT_CHARACTER, 1, false),
        None => ('\0', 0, false),
    }
}
fn prepare_non_ascii(
    text: &[u16],
    font: &ManagedFont,
    script: &str,
    options: &ManagedPreparationOptions,
    glyphs: &mut Vec<ManagedShapingGlyph>,
) -> Result<(), PreparationError> {
    let mut normalizer = Normalizer::default();
    let mut normalized = Vec::new();
    let well_formed = char::decode_utf16(text.iter().copied()).all(|s| s.is_ok());
    let already_normalized = if well_formed {
        normalizer.normalize(
            text,
            options.normalization,
            NormalizationForm::CanonicalComposition,
            &mut normalized,
        )?;
        normalized == text
    } else {
        false
    };
    let use_order = resolve_managed_layout_script(font, script).use_mark_order;
    let preserve_ignorables = font.glyph_index(0x20)? != 0;
    let mut use_cluster = None;
    let mut decomposed = Vec::new();
    let mut grapheme_start = 0;
    while grapheme_start < text.len() {
        let length = managed_next_grapheme_length(&text[grapheme_start..], options.graphemes);
        let end = grapheme_start + length;
        let original = &text[grapheme_start..end];
        let retain_original = !well_formed
            || already_normalized
            || use_order
            || script == "hang"
            || script::indic(script)
                && char::decode_utf16(original.iter().copied())
                    .any(|r| matches!(r, Ok('\u{931}' | '\u{9dc}' | '\u{9dd}' | '\u{b94}')));
        let grapheme = if retain_original {
            original
        } else {
            normalizer.normalize(
                original,
                options.normalization,
                NormalizationForm::CanonicalComposition,
                &mut normalized,
            )?;
            &normalized
        };
        let separate = prepend(rune(grapheme).0 as u32);
        let mut indic_cluster = grapheme_start as i32;
        let mut previous = 0;
        let mut split_after_zwnj = false;
        let mut has_split_matra = false;
        let mut index = 0;
        while index < grapheme.len() {
            let appended_start = glyphs.len();
            let (point, consumed, _) = rune(&grapheme[index..]);
            let cp = point as u32;
            let (following, _, valid) = rune(&grapheme[index + consumed..]);
            let following = if valid { following as u32 } else { 0 };
            let position = (grapheme_start + index) as i32;
            if split_after_zwnj {
                indic_cluster = position;
                split_after_zwnj = false;
            }
            if script::indic(script)
                && (cp == 0x200c
                    || cp == 0x200d
                        && previous != script::virama(script)
                        && following != script::virama(script))
            {
                indic_cluster = position;
                split_after_zwnj =
                    cp == 0x200c && following != 0 && !options.marks.is_mark(following)?;
            } else if script == "khmr"
                && (matches!(cp, 0x200c | 0x200d) || previous == 0x17d2 && khmer_base(cp))
            {
                indic_cluster = position;
            }
            let characters = options.cluster_level.characters();
            let mut cluster = if characters || (separate || script == "hang") && retain_original {
                position
            } else if script::indic(script) || script == "khmr" {
                indic_cluster
            } else {
                grapheme_start as i32
            };
            if !characters && !use_order && cp == 0x200c {
                cluster = position;
            }
            if use_order {
                if preserve_ignorables && matches!(cp, 0x200c | 0x200d) {
                    cluster = position;
                }
                if options.marks.is_mark(cp)?
                    && let Some(previous) = use_cluster
                {
                    cluster = previous;
                } else {
                    use_cluster = Some(cluster);
                }
            }
            if cp == 0x200d
                && let Some(previous) = glyphs.last()
            {
                cluster = previous.cluster;
            }
            if matches!(cp, 0xfe00..=0xfe0f | 0xe0100..=0xe01ef)
                && let Some(previous) = glyphs.last_mut()
                && let Some(glyph) = font.variation_glyph(previous.code_point, cp)?
            {
                previous.glyph_index = glyph;
                index += consumed;
                continue;
            }
            if script == "khmr" && matches!(cp, 0x17be | 0x17bf | 0x17c0 | 0x17c4 | 0x17c5) {
                append_mapped(glyphs, font, '\u{17c1}', cluster)?;
            }
            let mut split = false;
            if script::indic(script) {
                let mut units = [0; 2];
                let scalar = point.encode_utf16(&mut units);
                normalizer.normalize(
                    scalar,
                    options.normalization,
                    NormalizationForm::CanonicalDecomposition,
                    &mut decomposed,
                )?;
                if decomposed != scalar && options.marks.is_mark(rune(&decomposed).0 as u32)? {
                    split = true;
                    let first = glyphs.len();
                    for component in char::decode_utf16(decomposed.iter().copied()) {
                        append_mapped(
                            glyphs,
                            font,
                            component.expect("normalized scalar"),
                            cluster,
                        )?;
                    }
                    for glyph in &mut glyphs[first..] {
                        glyph.character_cluster = cluster;
                        glyph.has_character_cluster = 1;
                    }
                }
            }
            if !split {
                append_normalized(
                    glyphs,
                    font,
                    point,
                    cluster,
                    options.normalization,
                    &mut normalizer,
                    &mut decomposed,
                )?;
            } else if !has_split_matra {
                for glyph in &mut glyphs[appended_start..] {
                    glyph.has_character_cluster = 0;
                }
                has_split_matra = true;
            }
            for glyph in &mut glyphs[appended_start..] {
                glyph.grapheme_cluster = grapheme_start as i32;
            }
            previous = cp;
            index += consumed;
        }
        grapheme_start = end;
    }
    if options.cluster_level.characters() {
        let mut start = 0;
        while start < glyphs.len() {
            let mut end = start + 1;
            while end < glyphs.len()
                && glyphs[end].grapheme_cluster == glyphs[start].grapheme_cluster
            {
                end += 1;
            }
            if end - start >= 2 {
                let minimum = glyphs[start..end].iter().map(|g| g.cluster).min().unwrap();
                for glyph in &mut glyphs[start..end] {
                    if glyph.cluster != minimum {
                        glyph.flags |= 3;
                    }
                }
            }
            start = end;
        }
    }
    Ok(())
}
fn append_normalized(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    mut point: char,
    cluster: i32,
    profile: ManagedNormalizationProfile,
    normalizer: &mut Normalizer,
    decomposed: &mut Vec<u16>,
) -> Result<(), PreparationError> {
    let mut glyph = font.glyph_index(point as u32)?;
    if glyph == 0 && point == '\u{2011}' {
        point = '\u{2010}';
        glyph = font.glyph_index(point as u32)?;
    }
    if glyph == 0 {
        let mut units = [0; 2];
        let scalar = point.encode_utf16(&mut units);
        normalizer.normalize(
            scalar,
            profile,
            NormalizationForm::CanonicalDecomposition,
            decomposed,
        )?;
        if decomposed != scalar {
            for component in char::decode_utf16(decomposed.iter().copied()) {
                append_mapped(glyphs, font, component.expect("normalized scalar"), cluster)?;
            }
            return Ok(());
        }
    }
    append_mapped(glyphs, font, point, cluster)
}
fn append_mapped(
    glyphs: &mut Vec<ManagedShapingGlyph>,
    font: &ManagedFont,
    point: char,
    cluster: i32,
) -> Result<(), PreparationError> {
    if glyphs.len() >= MAX_MANAGED_SHAPED_GLYPHS {
        return Err(PreparationError::GlyphLimit);
    }
    let mut glyph = font.glyph_index(point as u32)?;
    let mut fallback = if glyph == 0 {
        space_fallback(point as u32)
    } else {
        0
    };
    if fallback != 0 {
        let space = font.glyph_index(0x20)?;
        if space != 0 {
            glyph = space;
        } else {
            fallback = 0;
        }
    }
    glyphs
        .try_reserve(1)
        .map_err(|_| PreparationError::Capacity)?;
    glyphs.push(ManagedShapingGlyph {
        space_fallback: fallback,
        ..ManagedShapingGlyph::new(glyph, cluster, point as u32)
    });
    Ok(())
}
fn khmer_base(cp: u32) -> bool {
    let ranges = khmer_facts::KHMER_BASE_RANGES;
    let index = ranges.partition_point(|&(_, end)| end < cp);
    ranges.get(index).is_some_and(|&(start, _)| start <= cp)
}
fn prepend(cp: u32) -> bool {
    matches!(cp, 0x600..=0x605 | 0x6dd | 0x70f | 0x890..=0x891 | 0x8e2 | 0xd4e |
        0x110bd | 0x110cd | 0x111c2..=0x111c3 | 0x1193f | 0x11941 | 0x11a3a |
        0x11a84..=0x11a89 | 0x11d46)
}
fn space_fallback(cp: u32) -> u8 {
    match cp {
        0x20 | 0xa0 => 18,
        0x2000 | 0x2002 => 2,
        0x2001 | 0x2003 | 0x3000 => 1,
        0x2004 => 3,
        0x2005 => 4,
        0x2006 => 6,
        0x2007 => 19,
        0x2008 => 20,
        0x2009 => 5,
        0x200a => 16,
        0x202f => 21,
        0x205f => 17,
        _ => 0,
    }
}
