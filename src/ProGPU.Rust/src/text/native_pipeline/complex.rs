//! Original native unified shaping stages and owned design-unit run writer.
//! Native packed glyph metadata, capacity admission, substitution probes and
//! stage ordering remain independent of managed glyph-buffer implementations.
use super::*;
use crate::text::unicode::{self, GeneralCategory, ManagedIndicProperties, UnicodeScalar};
use unicode::grapheme::{self, GraphemeCluster, GraphemePolicy};
use unicode::normalization::{NormalizationData, NormalizationForm, NormalizationPlan};
mod arabic_fallback;
mod fallback_marks;
pub use fallback_marks::NativeFallbackBounds;
mod indic;
mod input;
mod joining;
mod khmer;
mod myanmar;
mod ordinary_input;
mod route;
mod run;
mod spaces;
mod stages;
mod stretch;
mod syllables;
mod use_shaper;
pub use route::{NativeShapingRoute, resolve_native_language_tag, resolve_native_shaping_route};
pub use run::{
    NativeRun, NativeShapeRequest, NativeShapeRequirements, NativeShapeScratch, NativeShaper,
};
/// Compatibility name for the original complex request API.
pub type NativeComplexRequest<'a> = NativeShapeRequest<'a>;
/// Compatibility name for original complex-run capacities.
pub type NativeComplexRequirements = NativeShapeRequirements;
/// Compatibility name for original complex-run scratch.
pub type NativeComplexScratch = NativeShapeScratch;
/// Compatibility name for the now-unified native run owner.
pub type NativeComplexShaper = NativeShaper;
/// Compatibility name for published complex runs.
pub type NativeComplexRun = NativeRun;

/// Original native complex-script selector; `None` remains a distinct route.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeComplexKind {
    /// Ordinary/Arabic/Hangul shaping without a complex-script syllable machine.
    #[default]
    None = 0,
    /// Original Indic stage order and reordering.
    Indic = 1,
    /// Original Universal Shaping Engine stage order and reordering.
    Use = 2,
    /// Original Myanmar stage order and reordering.
    Myanmar = 3,
    /// Original Khmer stage order and reordering.
    Khmer = 4,
}
/// Original source-cluster policy, independent of grapheme segmentation version.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum NativeClusterLevel {
    /// Merge original cluster dependencies monotonically.
    #[default]
    MonotoneGraphemes = 0,
    /// Original per-character cluster behavior where explicitly selected.
    MonotoneCharacters = 1,
    /// Preserve original character-level assignments.
    Characters = 2,
    /// Original grapheme-level nonmonotone policy.
    Graphemes = 3,
}
/// Original raw native buffer flags. Unknown bits remain unchanged; conflicting
/// preserve/remove defaults are rejected at the run boundary.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeBufferFlags(pub u8);
impl NativeBufferFlags {
    /// Beginning of the source text, permitting initial dotted-circle recovery.
    pub const BEGINNING_OF_TEXT: Self = Self(1);
    /// End of the source text.
    pub const END_OF_TEXT: Self = Self(2);
    /// Keep original default-ignorable glyphs/advances.
    pub const PRESERVE_DEFAULT_IGNORABLES: Self = Self(4);
    /// Remove original default-ignorable glyphs.
    pub const REMOVE_DEFAULT_IGNORABLES: Self = Self(8);
    /// Suppress beginning and broken-cluster dotted-circle insertion.
    pub const DO_NOT_INSERT_DOTTED_CIRCLE: Self = Self(16);
    /// Original fragment verification; requires the independent provider route.
    pub const VERIFY: Self = Self(32);
    /// Original request to retain unsafe-to-concatenate output dependencies.
    pub const PRODUCE_UNSAFE_TO_CONCAT: Self = Self(64);
    /// Original request for safe Arabic tatweel-insertion dependencies.
    pub const PRODUCE_SAFE_TO_INSERT_TATWEEL: Self = Self(128);
    /// Preserve the original bitwise admission.
    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 != 0
    }
}

const CATEGORY: u32 = 0x1f8;
const POSITION: u32 = 0x1e00;
const SYLLABLE: u32 = 0x1fe000;
const FEATURE: u32 = 0xfe00000;
const MULTIPLIED: u32 = 0x20000000;
const LIGATED: u32 = 0x40000000;
const SUBSTITUTED: u32 = 0x80000000;
const METADATA: u32 = CATEGORY | POSITION | SYLLABLE | FEATURE | MULTIPLIED | LIGATED | SUBSTITUTED;
fn category(g: ShapingGlyph) -> u8 {
    ((g.flags & CATEGORY) >> 3) as u8
}
fn position(g: ShapingGlyph) -> u8 {
    ((g.flags & POSITION) >> 9) as u8
}
fn syllable(g: ShapingGlyph) -> u8 {
    ((g.flags & SYLLABLE) >> 13) as u8
}
fn feature(g: ShapingGlyph) -> u8 {
    ((g.flags & FEATURE) >> 21) as u8
}
fn set_category(g: &mut ShapingGlyph, v: u8) {
    g.flags = (g.flags & !CATEGORY) | ((u32::from(v) << 3) & CATEGORY);
}
fn set_position(g: &mut ShapingGlyph, v: u8) {
    g.flags = (g.flags & !POSITION) | ((u32::from(v) << 9) & POSITION);
}
fn set_syllable(g: &mut ShapingGlyph, v: u8) {
    g.flags = (g.flags & !SYLLABLE) | ((u32::from(v) << 13) & SYLLABLE);
}
fn add_feature(g: &mut ShapingGlyph, v: u8) {
    g.flags |= (u32::from(v) << 21) & FEATURE;
}
fn substituted(g: ShapingGlyph) -> bool {
    g.flags & SUBSTITUTED != 0
}
fn ligated(g: ShapingGlyph) -> bool {
    g.flags & LIGATED != 0
}
fn multiplied(g: ShapingGlyph) -> bool {
    g.flags & MULTIPLIED != 0
}
fn clear_substituted(glyphs: &mut [ShapingGlyph]) {
    for g in glyphs {
        g.flags &= !SUBSTITUTED;
    }
}
fn clear_metadata(glyphs: &mut [ShapingGlyph]) {
    for g in glyphs {
        g.flags &= !METADATA;
    }
}
fn end_of_syllable(glyphs: &[ShapingGlyph], start: usize) -> usize {
    let mut end = start + 1;
    while end < glyphs.len() && syllable(glyphs[end]) == syllable(glyphs[start]) {
        end += 1;
    }
    end
}
fn merge_cluster(glyphs: &mut [ShapingGlyph], mut start: usize, mut end: usize) {
    if start >= end {
        return;
    }
    let first = glyphs[start].cluster;
    let last = glyphs[end - 1].cluster;
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
}
fn mark_dependency(glyphs: &mut [ShapingGlyph], start: usize, end: usize) {
    if end - start < 2 {
        return;
    }
    let minimum = glyphs[start..end].iter().map(|g| g.cluster).min().unwrap();
    for g in &mut glyphs[start..end] {
        if g.cluster != minimum {
            g.flags |= 3;
        }
    }
}
fn mark_syllables_unsafe(glyphs: &mut [ShapingGlyph]) {
    let mut start = 0;
    while start < glyphs.len() {
        let end = end_of_syllable(glyphs, start);
        mark_dependency(glyphs, start, end);
        start = end;
    }
}
fn stable_sort_positions(glyphs: &mut [ShapingGlyph], start: usize, end: usize) {
    for index in start + 1..end {
        let glyph = glyphs[index];
        let mut target = index;
        while target > start && position(glyphs[target - 1]) > position(glyph) {
            glyphs[target] = glyphs[target - 1];
            target -= 1;
        }
        glyphs[target] = glyph;
    }
}
struct Buffer<'a> {
    storage: &'a mut [ShapingGlyph],
    count: usize,
}
impl Buffer<'_> {
    fn glyphs(&self) -> &[ShapingGlyph] {
        &self.storage[..self.count]
    }
    fn glyphs_mut(&mut self) -> &mut [ShapingGlyph] {
        &mut self.storage[..self.count]
    }
    fn reserve(&self, additional: usize) -> Result<(), FontError> {
        if additional > self.storage.len() - self.count {
            Err(FontError::Capacity)
        } else {
            Ok(())
        }
    }
    fn insert(&mut self, index: usize, glyph: ShapingGlyph) -> Result<(), FontError> {
        self.reserve(1)?;
        self.storage.copy_within(index..self.count, index + 1);
        self.storage[index] = glyph;
        self.count += 1;
        Ok(())
    }
}
fn glyph(font: &SfntFace, code_point: u32, cluster: i32) -> Result<ShapingGlyph, FontError> {
    Ok(ShapingGlyph {
        glyph_id: u32::from(font.glyph_index(code_point).ok_or(FontError::InvalidFace)?),
        code_point,
        cluster,
        ..Default::default()
    })
}
fn unicode_mark(cp: u32) -> bool {
    matches!(
        unicode::general_category(cp),
        GeneralCategory::NonspacingMark
            | GeneralCategory::SpacingCombiningMark
            | GeneralCategory::EnclosingMark
    )
}

// All four originals use this exact broken-syllable insertion preflight; USE
// and Indic retain their independent within-syllable insertion positions.
fn broken_count(glyphs: &[ShapingGlyph], broken: u8) -> usize {
    let mut count = 0;
    let mut previous = 0;
    for g in glyphs {
        let current = syllable(*g);
        if current != previous && current & 15 == broken {
            count += 1;
        }
        previous = current;
    }
    count
}
