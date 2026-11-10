//! Original native host/browser provider selection, separate from FontManager.
//! Providers retain immutable byte owners. Selection itself performs no discovery,
//! file IO or heap allocation; cache entries preserve original callback ordering.
use crate::font::{FontData, FontError, SfntFace};
use std::sync::Arc;
mod glyph;
pub use glyph::*;
mod svg;
pub use svg::*;
mod preferences;
pub use preferences::{
    NativeFallbackPreferences, fallback_family_preferences, fallback_family_preferences_bytes,
};
mod style;
pub use style::{NativeFontStyleRequest, NativeFontStyleVariation};

/// Original raw provider slant; Italic and Oblique remain separate cache keys.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum NativeFontProviderSlant {
    #[default]
    Normal = 0,
    Italic = 1,
    Oblique = 2,
}
impl TryFrom<u8> for NativeFontProviderSlant {
    type Error = FontError;
    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::Normal),
            1 => Ok(Self::Italic),
            2 => Ok(Self::Oblique),
            _ => Err(FontError::InvalidArgument),
        }
    }
}
/// One actual provider-owned container, with its original selected face and IDs.
/// The immutable storage lease survives all later callbacks and provider disposal.
#[derive(Clone, Debug)]
pub struct NativeFontProviderFace {
    pub data: FontData,
    pub identity: u64,
    pub family_identity: u64,
    pub face_index: u32,
    pub weight: u16,
    pub stretch: u8,
    pub slant: NativeFontProviderSlant,
    pub is_fallback: bool,
}
impl NativeFontProviderFace {
    /// Retain a selected face with the original default style (400,5,Normal).
    /// IDs belong to the provider; this constructor never hashes or invents them.
    pub fn new(data: FontData, face_index: u32, identity: u64, family_identity: u64) -> Self {
        Self {
            data,
            identity,
            family_identity,
            face_index,
            weight: 400,
            stretch: 5,
            slant: NativeFontProviderSlant::Normal,
            is_fallback: false,
        }
    }
}
/// Synchronous original count/face callbacks. Returning None has the original
/// false-callback behavior. Source ownership and generation changes are explicit.
/// No Send/Sync bound, host rendering callback or filesystem policy is imposed.
pub trait NativeFontProvider {
    fn face_count(&mut self) -> u32;
    fn face(&mut self, index: u32) -> Option<NativeFontProviderFace>;
}
/// A convenient immutable host/browser byte inventory, with no OS discovery.
#[derive(Clone, Debug)]
pub struct OwnedNativeFontProvider {
    faces: Arc<[NativeFontProviderFace]>,
}
impl OwnedNativeFontProvider {
    pub fn new(faces: impl Into<Arc<[NativeFontProviderFace]>>) -> Result<Self, FontError> {
        let faces = faces.into();
        u32::try_from(faces.len()).map_err(|_| FontError::Capacity)?;
        Ok(Self { faces })
    }
    pub fn faces(&self) -> &[NativeFontProviderFace] {
        &self.faces
    }
}
impl NativeFontProvider for OwnedNativeFontProvider {
    fn face_count(&mut self) -> u32 {
        self.faces.len() as u32
    }
    fn face(&mut self, index: u32) -> Option<NativeFontProviderFace> {
        self.faces.get(index as usize).cloned()
    }
}
/// Original provider request: zero family selects all families; raw weight is
/// not normalized or clamped to the separate catalog/style-variation range.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFontProviderRequest {
    pub family_identity: u64,
    pub weight: u16,
    pub stretch: u8,
    pub slant: NativeFontProviderSlant,
    pub code_point: u32,
}
impl NativeFontProviderRequest {
    fn validate(self) -> Result<(), FontError> {
        if !(1..=9).contains(&self.stretch) || self.code_point > 0x10ffff {
            Err(FontError::InvalidArgument)
        } else {
            Ok(())
        }
    }
}
/// Original caller-owned positive/negative cache slot. Empty slots and raw
/// provider index zero remain distinct; replacement never retains font bytes.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeFontProviderCacheEntry {
    pub generation: u64,
    pub family_identity: u64,
    pub code_point: u32,
    pub face_index: u32,
    pub weight: u16,
    pub stretch: u8,
    pub slant: NativeFontProviderSlant,
    pub found: bool,
    pub occupied: bool,
}
impl NativeFontProviderCacheEntry {
    fn matches(&self, generation: u64, request: NativeFontProviderRequest) -> bool {
        self.occupied
            && self.generation == generation
            && self.family_identity == request.family_identity
            && self.weight == request.weight
            && self.stretch == request.stretch
            && self.slant == request.slant
            && self.code_point == request.code_point
    }
}
/// One actual selected callback generation and its already-resolved glyph.
#[derive(Clone, Debug)]
pub struct NativeFontProviderSelection {
    pub face: NativeFontProviderFace,
    /// Parsed original native SFNT view sharing that same immutable byte lease.
    pub font: SfntFace,
    pub provider_index: u32,
    pub glyph_index: u16,
}
/// Frozen original generation number and an exclusive callback lease. Cache
/// identity comes from this exact number, not a platform or provider-name guess.
pub struct NativeFontProviderView<'a> {
    pub generation: u64,
    pub provider: &'a mut dyn NativeFontProvider,
}
impl NativeFontProviderView<'_> {
    /// Resolve one family/style/scalar request with the original O(C + F*T)
    /// callback order and caller-owned round-robin cache. A negative hit performs
    /// no callbacks. A failed positive hit restarts a full scan, not the next slot.
    pub fn resolve(
        &mut self,
        request: NativeFontProviderRequest,
        cache: &mut [NativeFontProviderCacheEntry],
        replacement_cursor: &mut u32,
    ) -> Result<Option<NativeFontProviderSelection>, FontError> {
        request.validate()?;
        let cache_len = u32::try_from(cache.len()).map_err(|_| FontError::Capacity)?;
        for entry in cache.iter() {
            if !entry.matches(self.generation, request) {
                continue;
            }
            if !entry.found {
                return Ok(None);
            }
            if let Some(selected) = self.candidate(
                entry.face_index,
                request.family_identity,
                0,
                request.code_point,
            ) {
                return Ok(Some(selected));
            }
            break;
        }
        let count = self.provider.face_count();
        let mut best = None;
        let mut best_score = u64::MAX;
        for index in 0..count {
            let Some(selected) =
                self.candidate(index, request.family_identity, 0, request.code_point)
            else {
                continue;
            };
            let score = style_score(&selected.face, request);
            if best.is_none() || score < best_score {
                best_score = score;
                best = Some(selected);
                if score == 0 {
                    break;
                }
            }
        }
        if cache_len != 0 {
            let slot = *replacement_cursor % cache_len;
            cache[slot as usize] = NativeFontProviderCacheEntry {
                generation: self.generation,
                family_identity: request.family_identity,
                code_point: request.code_point,
                face_index: best.as_ref().map_or(0, |v| v.provider_index),
                weight: request.weight,
                stretch: request.stretch,
                slant: request.slant,
                found: best.is_some(),
                occupied: true,
            };
            *replacement_cursor = (slot + 1) % cache_len;
        }
        Ok(best)
    }
    /// One original fallback pass: ordered nonzero family IDs, registered fallback
    /// faces, then all others. Family rank precedes style distance; ties retain
    /// the first source slot. The supplied request.family_identity is ignored.
    pub fn resolve_fallback(
        &mut self,
        ordered_families: &[u64],
        request: NativeFontProviderRequest,
        excluded_face_identity: u64,
    ) -> Result<Option<NativeFontProviderSelection>, FontError> {
        request.validate()?;
        let mut best = None;
        let mut best_tier = 3u8;
        let mut best_rank = ordered_families.len();
        let mut best_style = u64::MAX;
        let count = self.provider.face_count();
        for index in 0..count {
            let Some(selected) =
                self.candidate(index, 0, excluded_face_identity, request.code_point)
            else {
                continue;
            };
            let rank = ordered_families
                .iter()
                .position(|&family| family != 0 && family == selected.face.family_identity)
                .unwrap_or(ordered_families.len());
            let tier = if rank < ordered_families.len() {
                0
            } else if selected.face.is_fallback {
                1
            } else {
                2
            };
            let score = style_score(&selected.face, request);
            let better = tier < best_tier
                || (tier == best_tier && tier == 0 && rank < best_rank)
                || (tier == best_tier && (tier != 0 || rank == best_rank) && score < best_style);
            if !better {
                continue;
            }
            best_tier = tier;
            best_rank = rank;
            best_style = score;
            best = Some(selected);
        }
        Ok(best)
    }
    fn candidate(
        &mut self,
        index: u32,
        family: u64,
        excluded: u64,
        code_point: u32,
    ) -> Option<NativeFontProviderSelection> {
        let face = self.provider.face(index)?;
        if face.data.is_empty()
            || (family != 0 && face.family_identity != family)
            || (excluded != 0 && face.identity == excluded)
        {
            return None;
        }
        let font = SfntFace::from_storage(face.data.clone(), face.face_index).ok()?;
        let glyph = font.glyph_index(code_point).filter(|g| *g != 0)?;
        if !font.native_has_renderable_glyph(code_point, glyph) {
            return None;
        }
        Some(NativeFontProviderSelection {
            face,
            font,
            provider_index: index,
            glyph_index: glyph,
        })
    }
}
fn style_score(face: &NativeFontProviderFace, request: NativeFontProviderRequest) -> u64 {
    let face_italic = face.slant != NativeFontProviderSlant::Normal;
    let requested_italic = request.slant != NativeFontProviderSlant::Normal;
    u64::from(face_italic != requested_italic) * 10000
        + u64::from(face.stretch.abs_diff(request.stretch)) * 1000
        + u64::from(face.weight.abs_diff(request.weight))
}
