use super::*;
use crate::font::layout::{
    ManagedContextInput, ManagedFeatureOptions, ManagedGlyphSetDigest, ManagedLookupSelection,
    ManagedSelectionRequest, ManagedSubstitutionStage,
};
/// Owned current managed raw substitution plan: frozen selection, feature policy,
/// exact GSUB bytes and optional source font. Applying a stage consumes prepared
/// script metadata; it does not assign categories, reorder scripts or run GPOS.
#[derive(Clone, Debug)]
pub struct ManagedSubstitutionPlan {
    selection: ManagedLookupSelection,
    options: ManagedFeatureOptions,
    table: ManagedGsubTable,
}
impl ManagedSubstitutionPlan {
    // Original Indic probe: one mutable buffer survives all matching lookups.
    // Feature values, digests and per-cluster stage admission do not apply here.
    pub(crate) fn indic_would_substitute(
        &self,
        font: &crate::font::ManagedFont,
        marks: crate::text::unicode::ManagedMarkProfile,
        tag: [u8; 4],
        glyphs: &[u16],
    ) -> Result<bool, FontError> {
        if self.table.bytes().is_empty() {
            return Ok(false);
        }
        let mut records = Vec::new();
        records
            .try_reserve_exact(glyphs.len())
            .map_err(|_| FontError::Capacity)?;
        records.extend(
            glyphs
                .iter()
                .enumerate()
                .map(|(i, &g)| ManagedShapingGlyph::new(g, i as i32, 0)),
        );
        let mut probe =
            ManagedGlyphBuffer::with_mark_profile(font.source().clone(), records, marks)?;
        for lookup in self.selection.lookups() {
            if lookup.tag != tag {
                continue;
            }
            let before_count = probe.glyphs.len();
            let before_glyph = probe.glyph_at(0)?;
            if self.table.apply_lookup_at(&mut probe, lookup.index, 0)?
                && (probe.glyphs.len() != before_count || probe.glyph_at(0)? != before_glyph)
            {
                return Ok(true);
            }
        }
        Ok(false)
    }
    /// Build original selection and optional context filters from raw owned bytes.
    pub fn new(
        bytes: impl Into<Arc<[u8]>>,
        options: ManagedFeatureOptions,
        script: &[u16],
        language: Option<&[u16]>,
        coordinates: &[i16],
    ) -> Result<Self, FontError> {
        let selection = ManagedLookupSelection::new(
            bytes,
            ManagedSelectionRequest {
                script,
                language,
                settings: options.features(),
                coordinates,
                extension_lookup_type: 7,
            },
        )?;
        Ok(Self::from_selection(selection, options))
    }
    /// Retain the actual selected GSUB generation; missing GSUB remains None.
    pub fn from_face(
        face: SfntFace,
        options: ManagedFeatureOptions,
        script: &[u16],
        language: Option<&[u16]>,
        coordinates: &[i16],
    ) -> Result<Option<Self>, FontError> {
        let selected = ManagedLookupSelection::from_face(
            face,
            Tag::new(*b"GSUB"),
            ManagedSelectionRequest {
                script,
                language,
                settings: options.features(),
                coordinates,
                extension_lookup_type: 7,
            },
        )?;
        Ok(selected.map(|selection| Self::from_selection(selection, options)))
    }
    fn from_selection(selection: ManagedLookupSelection, options: ManagedFeatureOptions) -> Self {
        let table = ManagedGsubTable {
            storage: selection.retained_storage(),
        };
        Self {
            selection,
            options,
            table,
        }
    }
    /// Immutable original selection, retained request, optional digest and contexts.
    pub fn selection(&self) -> &ManagedLookupSelection {
        &self.selection
    }
    /// Frozen original per-cluster policy.
    pub fn options(&self) -> &ManagedFeatureOptions {
        &self.options
    }
    /// Apply one original raw stage, preserving lookup order, filter timing and
    /// partial mutation/error state. Script preparation and stage ordering are
    /// separate. The extra Rust work budget applies independently to each root
    /// lookup, so an ordinary large glyph inventory does not exhaust it.
    pub fn apply_stage(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        stage: ManagedSubstitutionStage,
    ) -> Result<(), FontError> {
        self.apply_stage_with_limits(buffer, stage, ManagedGsubLimits::default())
    }
    /// Apply a stage with an explicit per-root nested-lookup call budget. Original
    /// traversal/mark state remains observable on top-level source failures.
    pub fn apply_stage_with_limits(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        stage: ManagedSubstitutionStage,
        limits: ManagedGsubLimits,
    ) -> Result<(), FontError> {
        let mut executor = Executor {
            data: self.table.bytes(),
            buffer,
            remaining_lookups: limits.max_lookup_calls,
        };
        let mut digest = ManagedGlyphSetDigest::default();
        add_glyphs(&mut digest, executor.buffer);
        for (selected_index, enabled) in self.selection.lookups().iter().enumerate() {
            let tag = enabled.tag.map(u16::from);
            if !stage.enables(tag)
                || !enabled.required
                    && !self
                        .options
                        .may_enable(tag, executor.buffer.has_fraction_actions)
            {
                continue;
            }
            if enabled.digest.is_some_and(|d| !d.may_intersect(&digest)) {
                continue;
            }
            if self
                .selection
                .context(selected_index)
                .is_some_and(|context| {
                    !context.may_match(ManagedContextInput::with_digest(
                        executor.buffer.glyphs(),
                        executor.buffer.state.traversal,
                        digest,
                    ))
                })
            {
                continue;
            }
            executor.buffer.state.traversal.manual_joiners = stage.manual_joiners();
            let restrict = stage.restricts_syllable(tag);
            let alternate = is_type(executor.data, enabled.index, 3);
            if is_type(executor.data, enabled.index, 8) {
                executor.reverse_lookup(
                    enabled.index,
                    restrict,
                    tag,
                    enabled.required,
                    &self.options,
                )?;
                add_glyphs(&mut digest, executor.buffer);
                continue;
            }
            if !alternate {
                let mut position = 0;
                while position < executor.buffer.glyphs.len() {
                    set_syllable(executor.buffer, position, restrict);
                    if enabled
                        .digest
                        .is_none_or(|d| d.may_have(executor.buffer.glyph_at(position).unwrap()))
                        && (enabled.required
                            || self.options.enabled(executor.buffer.glyphs[position], tag))
                    {
                        executor.buffer.state.context_match_end = 0;
                        let before = executor.buffer.glyphs.len();
                        executor.remaining_lookups = limits.max_lookup_calls;
                        executor.lookup(enabled.index, position)?;
                        let inserted = executor.buffer.glyphs.len().saturating_sub(before);
                        position += inserted;
                        if i64::from(executor.buffer.state.context_match_end) > position as i64 + 1
                        {
                            position = executor.buffer.state.context_match_end as usize - 1;
                        }
                    }
                    position += 1;
                }
            }
            clear_syllable(executor.buffer);
            executor.buffer.state.traversal.manual_joiners = false;
            executor.alternate_lookup(
                enabled.index,
                enabled.value,
                tag,
                enabled.required,
                &self.options,
            )?;
            add_glyphs(&mut digest, executor.buffer);
        }
        executor.buffer.state.traversal.manual_joiners = false;
        Ok(())
    }
}
fn add_glyphs(digest: &mut ManagedGlyphSetDigest, buffer: &ManagedGlyphBuffer) {
    for g in &buffer.glyphs {
        digest.add(g.glyph_index);
    }
}
fn set_syllable(buffer: &mut ManagedGlyphBuffer, position: usize, restrict: bool) {
    buffer.state.traversal.restrict_to_syllable = restrict;
    buffer.state.traversal.lookup_syllable = if restrict {
        buffer.glyphs[position].use_syllable
    } else {
        0
    };
}
fn clear_syllable(buffer: &mut ManagedGlyphBuffer) {
    buffer.state.traversal.restrict_to_syllable = false;
    buffer.state.traversal.lookup_syllable = 0;
}
fn is_type(data: &[u8], index: u16, expected: u16) -> bool {
    let Some((at, kind, count)) = raw_lookup(data, index) else {
        return false;
    };
    if kind == expected {
        return true;
    }
    if kind != 7 || count == 0 || !fits(data, at + 6, 2) {
        return false;
    }
    let extension = at + usize::from(u16_at(data, at + 6));
    fits(data, extension, 4)
        && u16_at(data, extension) == 1
        && u16_at(data, extension + 2) == expected
}
impl Executor<'_, '_> {
    fn top_subtable(
        &self,
        kind: u16,
        at: i64,
        pointer: i64,
    ) -> Result<Option<(u16, i64)>, FontError> {
        let mut sub = at + i64::from(self.read(pointer)?);
        let mut kind = kind;
        if kind == 7 {
            if !self.fits(sub, 8) || self.read(sub)? != 1 {
                return Ok(None);
            }
            kind = self.read(sub + 2)?;
            let relative = u32_at(self.data, sub as usize + 4);
            if relative > i32::MAX as u32 {
                return Ok(None);
            }
            sub += i64::from(relative);
        }
        Ok(Some((kind, sub)))
    }
    fn reverse_lookup(
        &mut self,
        index: u16,
        restrict: bool,
        tag: [u16; 4],
        required: bool,
        options: &ManagedFeatureOptions,
    ) -> Result<(), FontError> {
        let Some((at, kind, count)) = raw_lookup(self.data, index) else {
            return Ok(());
        };
        let at = at as i64;
        let flags = self.read(at + 2)?;
        let set = at + 6 + i64::from(count) * 2;
        let set = if flags & 16 != 0 && self.fits(set, 2) {
            self.read(set)?
        } else {
            u16::MAX
        };
        let previous = self.buffer.mark_filtering_coverage;
        self.buffer.select_mark_set(set);
        for position in (0..self.buffer.glyphs.len()).rev() {
            set_syllable(self.buffer, position, restrict);
            if !required && !options.enabled(self.buffer.glyphs[position], tag) {
                continue;
            }
            for sub_index in 0..count {
                let pointer = at + 6 + i64::from(sub_index) * 2;
                if !self.fits(pointer, 2) {
                    break;
                }
                let Some((effective, sub)) = self.top_subtable(kind, at, pointer)? else {
                    continue;
                };
                if effective == 8 && self.reverse(sub, position, flags)? {
                    break;
                }
            }
        }
        // The original top-level source has no finally here: errors above retain
        // the selected mark coverage and last syllable state, unlike nested calls.
        self.buffer.mark_filtering_coverage = previous;
        clear_syllable(self.buffer);
        Ok(())
    }
    fn alternate_lookup(
        &mut self,
        index: u16,
        value: i32,
        tag: [u16; 4],
        required: bool,
        options: &ManagedFeatureOptions,
    ) -> Result<(), FontError> {
        if value <= 0 {
            return Ok(());
        }
        let Some((at, kind, count)) = raw_lookup(self.data, index) else {
            return Ok(());
        };
        let at = at as i64;
        let random = tag == b"rand".map(u16::from) && value == 65535;
        if random {
            self.buffer.dependency(0, self.buffer.glyphs.len());
        }
        for sub_index in 0..count {
            let pointer = at + 6 + i64::from(sub_index) * 2;
            if !self.fits(pointer, 2) {
                break;
            }
            let Some((effective, sub)) = self.top_subtable(kind, at, pointer)? else {
                continue;
            };
            if effective != 3 || !self.fits(sub, 6) || self.read(sub)? != 1 {
                continue;
            }
            let coverage = sub + i64::from(self.read(sub + 2)?);
            let sets = self.read(sub + 4)?;
            for position in 0..self.buffer.glyphs.len() {
                let position_value = if required {
                    value
                } else {
                    options.value_at(tag, self.buffer.glyphs[position].cluster)
                };
                if position_value <= 0
                    || !required && !options.enabled(self.buffer.glyphs[position], tag)
                {
                    continue;
                }
                let Some(covered) =
                    coverage_at(self.data, coverage, self.buffer.glyph_at(position)?)?
                else {
                    continue;
                };
                let pointer = sub + 6 + i64::from(covered) * 2;
                if covered >= u32::from(sets) || !self.fits(pointer, 2) {
                    continue;
                }
                let set = sub + i64::from(self.read(pointer)?);
                if !self.fits(set, 2) {
                    continue;
                }
                let count = self.read(set)?;
                if count == 0 {
                    continue;
                }
                let alternate = if random && position_value == 65535 {
                    self.buffer.state.random_state =
                        self.buffer.state.random_state.wrapping_mul(48271) % 2147483647;
                    self.buffer.state.random_state % u32::from(count)
                } else {
                    position_value.clamp(1, i32::from(count)) as u32 - 1
                };
                let pointer = set + 2 + i64::from(alternate) * 2;
                if self.fits(pointer, 2) {
                    self.buffer.replace(position, self.read(pointer)?)?;
                }
            }
        }
        Ok(())
    }
}
