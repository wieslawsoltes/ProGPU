use super::*;
use crate::font::layout::{
    ManagedFeatureOptions, ManagedGlyphSetDigest, ManagedLayoutVariations, ManagedLookupSelection,
    ManagedSelectionRequest,
};
/// Owned complete current managed raw GPOS plan for an already-normalized source
/// instance and already-prepared position records. Feature/variation dispatch is
/// included; metric preparation, attachment resolution and fallback are separate.
#[derive(Clone, Debug)]
pub struct ManagedPositioningPlan {
    selection: ManagedLookupSelection,
    options: ManagedFeatureOptions,
    table: ManagedGposTable,
    variations: ManagedLayoutVariations,
    has_kerning: bool,
}
impl ManagedPositioningPlan {
    /// Retain the selected font and its original normalized coordinates, lookup
    /// order and per-cluster feature policy. Missing GPOS remains None. Coordinates
    /// describe an already-normalized source instance; this is not fvar/avar
    /// setting selection or a claim that invalid source instance creation succeeds.
    pub fn from_face(
        face: SfntFace,
        options: ManagedFeatureOptions,
        script: &[u16],
        language: Option<&[u16]>,
        coordinates: &[i16],
    ) -> Result<Option<Self>, FontError> {
        let Some(selection) = ManagedLookupSelection::from_face(
            face.clone(),
            Tag::new(*b"GPOS"),
            ManagedSelectionRequest {
                script,
                language,
                settings: options.features(),
                coordinates,
                extension_lookup_type: 9,
            },
        )?
        else {
            return Ok(None);
        };
        let variations = ManagedLayoutVariations::from_normalized(face, coordinates)?;
        Ok(Some(Self::selected(selection, options, variations)))
    }
    /// Build directly from original managed variation-data selection. Default
    /// instances retain the original root's complete zero coordinate inventory
    /// for FeatureVariations; active instances retain the already-parsed store.
    /// TtfFont's full metadata/root/cache wrapper remains a separate consumer.
    pub fn from_variation_instance(
        instance: &crate::font::ManagedVariationInstance,
        options: ManagedFeatureOptions,
        script: &[u16],
        language: Option<&[u16]>,
    ) -> Result<Option<Self>, FontError> {
        let coordinates = instance.coordinates();
        let Some(selection) = ManagedLookupSelection::from_face(
            instance.data().source().clone(),
            Tag::new(*b"GPOS"),
            ManagedSelectionRequest {
                script,
                language,
                settings: options.features(),
                coordinates,
                extension_lookup_type: 9,
            },
        )?
        else {
            return Ok(None);
        };
        Ok(Some(Self::selected(
            selection,
            options,
            instance.layout_variations(),
        )))
    }
    fn selected(
        selection: ManagedLookupSelection,
        options: ManagedFeatureOptions,
        variations: ManagedLayoutVariations,
    ) -> Self {
        let has_kerning = selection
            .lookups()
            .iter()
            .any(|v| v.tag == *b"kern" || v.tag == *b"dist");
        let table = ManagedGposTable {
            storage: selection.retained_storage(),
        };
        Self {
            selection,
            options,
            table,
            variations,
            has_kerning,
        }
    }
    /// Exact retained original lookup selection and digests.
    pub fn selection(&self) -> &ManagedLookupSelection {
        &self.selection
    }
    /// Frozen original global, baseline and ranged feature policy.
    pub fn options(&self) -> &ManagedFeatureOptions {
        &self.options
    }
    /// Actual source generation and normalized layout variation state.
    pub fn variations(&self) -> &ManagedLayoutVariations {
        &self.variations
    }
    /// Original plan-level presence of enabled kern/dist, independent of matches.
    pub fn has_kerning(&self) -> bool {
        self.has_kerning
    }
    /// Apply raw positioning then original variation adjustments per selected
    /// lookup. Return original kerning presence, not whether a glyph changed.
    /// Earlier writes survive source failures; private mark state restores.
    pub fn apply(&self, buffer: &mut ManagedPositionBuffer) -> Applied {
        self.apply_with_limits(buffer, ManagedGposLimits::default())
    }
    /// Apply with an independent recursive-work budget for each root invocation.
    pub fn apply_with_limits(
        &self,
        buffer: &mut ManagedPositionBuffer,
        limits: ManagedGposLimits,
    ) -> Applied {
        let mut digest = ManagedGlyphSetDigest::default();
        for g in buffer.glyphs() {
            digest.add(g.glyph_index);
        }
        for enabled in self.selection.lookups() {
            if enabled.digest.is_some_and(|d| !d.may_intersect(&digest)) {
                continue;
            }
            if raw_lookup(self.table.bytes(), enabled.index).is_some() {
                for i in 0..buffer.glyphs.len() {
                    if enabled.digest.is_some_and(|d| !d.may_have(buffer.glyph(i)))
                        || !enabled.required
                            && self
                                .options
                                .value_at(enabled.tag.map(u16::from), buffer.glyphs[i].cluster)
                                == 0
                    {
                        continue;
                    }
                    self.table
                        .apply_lookup_at_with_limits(buffer, enabled.index, i, limits)?;
                }
            }
            if self.variations.is_active() {
                let saved = buffer.mark_coverage;
                let result = Executor {
                    data: self.table.bytes(),
                    buffer,
                    remaining: 0,
                }
                .variations(enabled, &self.options, &self.variations);
                buffer.mark_coverage = saved;
                result?;
            }
        }
        Ok(self.has_kerning)
    }
}
