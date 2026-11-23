//! Original native complex feature scheduler and bounded Indic GSUB probes.
use super::*;
#[path = "ordinary_stages.rs"]
mod ordinary;
const DIRECTIONAL: &[Tag] = &[
    Tag::new(*b"ltra"),
    Tag::new(*b"ltrm"),
    Tag::new(*b"rtla"),
    Tag::new(*b"rtlm"),
];
const PREPROCESSING: &[Tag] = &[
    Tag::new(*b"rvrn"),
    Tag::new(*b"frac"),
    Tag::new(*b"numr"),
    Tag::new(*b"dnom"),
    Tag::new(*b"locl"),
    Tag::new(*b"ccmp"),
];
const USE_EXTRA: &[Tag] = &[Tag::new(*b"nukt"), Tag::new(*b"akhn")];
const KHMER: &[Tag] = &[
    Tag::new(*b"pref"),
    Tag::new(*b"blwf"),
    Tag::new(*b"abvf"),
    Tag::new(*b"pstf"),
    Tag::new(*b"cfar"),
];
const MYANMAR: &[Tag] = &[
    Tag::new(*b"rphf"),
    Tag::new(*b"pref"),
    Tag::new(*b"blwf"),
    Tag::new(*b"pstf"),
];
const INDIC: &[Tag] = &[
    Tag::new(*b"nukt"),
    Tag::new(*b"akhn"),
    Tag::new(*b"rphf"),
    Tag::new(*b"rkrf"),
    Tag::new(*b"pref"),
    Tag::new(*b"blwf"),
    Tag::new(*b"abvf"),
    Tag::new(*b"half"),
    Tag::new(*b"pstf"),
    Tag::new(*b"vatu"),
    Tag::new(*b"cjct"),
];
const USE_BASIC: &[Tag] = &[
    Tag::new(*b"rkrf"),
    Tag::new(*b"abvf"),
    Tag::new(*b"blwf"),
    Tag::new(*b"half"),
    Tag::new(*b"pstf"),
    Tag::new(*b"vatu"),
    Tag::new(*b"cjct"),
];
const USE_TOPO: &[Tag] = &[
    Tag::new(*b"isol"),
    Tag::new(*b"init"),
    Tag::new(*b"medi"),
    Tag::new(*b"fina"),
];
pub(super) struct Context<'a> {
    pub font: &'a SfntFace,
    pub table: Option<LayoutTable<'a>>,
    pub selection: SelectionRequest<'a>,
    pub plan: &'a NativeFeaturePlan,
    pub route: NativeShapingRoute,
    pub alternate: u32,
    pub flags: NativeBufferFlags,
    pub gdef: Option<Gdef<'a>>,
    pub random: std::cell::Cell<u32>,
    pub zero_mark_advances: bool,
}
fn per_syllable(kind: NativeComplexKind, tag: Tag, presentation: bool) -> bool {
    let bytes = tag.bytes();
    [
        *b"locl", *b"ccmp", *b"nukt", *b"akhn", *b"rphf", *b"pref", *b"rkrf", *b"abvf", *b"blwf",
        *b"half", *b"pstf", *b"vatu", *b"cjct",
    ]
    .contains(&bytes)
        || kind == NativeComplexKind::Khmer && bytes == *b"cfar"
        || presentation
            && kind == NativeComplexKind::Indic
            && [*b"init", *b"pres", *b"abvs", *b"blws", *b"psts", *b"haln"].contains(&bytes)
}
fn mask(kind: NativeComplexKind, tag: Tag) -> u32 {
    match (kind, &tag.bytes()) {
        (NativeComplexKind::Khmer, b"pref") => 1,
        (NativeComplexKind::Khmer, b"blwf" | b"abvf" | b"pstf") => 2,
        (NativeComplexKind::Khmer, b"cfar") => 4,
        (NativeComplexKind::Indic | NativeComplexKind::Use, b"rphf") => 1,
        (NativeComplexKind::Indic, b"pref") => 2,
        (NativeComplexKind::Indic, b"blwf") => 4,
        (NativeComplexKind::Indic, b"abvf") => 8,
        (NativeComplexKind::Indic, b"half") => 16,
        (NativeComplexKind::Indic, b"pstf") => 32,
        (NativeComplexKind::Indic, b"init") => 64,
        _ => 0,
    }
}
impl Context<'_> {
    fn apply_lookup(
        &self,
        lookup: u16,
        feature: Option<Tag>,
        options: GsubOptions<'_>,
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let feature = feature.filter(|&f| self.plan.has_settings(f));
        let mut state = GsubState {
            glyph_count: b.count as u32,
            random_state: Some(self.random.get()),
            ..Default::default()
        };
        let result = (|| {
            if let Some(feature) = feature {
                let reverse = table.lookup(lookup)?.lookup_type() == 8;
                let mut iteration = if reverse { state.glyph_count } else { 0 };
                while if reverse {
                    iteration != 0
                } else {
                    iteration < state.glyph_count
                } {
                    let position = if reverse {
                        iteration -= 1;
                        iteration
                    } else {
                        iteration
                    };
                    let value = self
                        .plan
                        .value(feature, b.storage[position as usize].cluster);
                    if value == 0 {
                        if !reverse {
                            iteration += 1;
                        }
                        continue;
                    }
                    let before = state.glyph_count;
                    state.context_match_end = Some(0);
                    table.apply_gsub(
                        lookup,
                        Some(position),
                        b.storage,
                        GsubOptions {
                            alternate_value: value,
                            random_alternate: options.random_alternate
                                && feature == RAND
                                && value == u32::from(u16::MAX),
                            ..options
                        },
                        &mut state,
                    )?;
                    if !reverse {
                        iteration += state.glyph_count.saturating_sub(before);
                        iteration = (iteration + 1).max(state.context_match_end.unwrap_or(0));
                    }
                }
            } else {
                table.apply_gsub(
                    lookup,
                    None,
                    b.storage,
                    GsubOptions {
                        random_alternate: false,
                        ..options
                    },
                    &mut state,
                )?;
            }
            Ok(())
        })();
        b.count = state.glyph_count as usize;
        self.random
            .set(state.random_state.unwrap_or(self.random.get()));
        result
    }
    fn feature(
        &self,
        tag: Tag,
        presentation: bool,
        scratch: &mut [u16],
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        if !self.plan.requested().contains(&tag) {
            return Ok(());
        }
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let count = table.select_feature_into(
            self.selection.script,
            self.selection.language,
            tag,
            self.selection.coordinates,
            scratch,
        )? as usize;
        let options = GsubOptions {
            gdef: self.gdef,
            alternate_value: self.alternate,
            required_glyph_flags: if presentation {
                0
            } else {
                mask(self.route.complex, tag) << 21
            },
            mark_substituted: true,
            track_substitution_provenance: true,
            restrict_to_syllable: per_syllable(self.route.complex, tag, presentation),
            ..Default::default()
        };
        for &lookup in &scratch[..count] {
            self.apply_lookup(lookup, Some(tag), options, b)?;
        }
        Ok(())
    }
    fn group(
        &self,
        group: &[Tag],
        scratch: &mut [u16],
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        for &tag in group {
            self.feature(tag, false, scratch, b)?;
        }
        Ok(())
    }
    fn fraction_enabled(&self, input: &[UnicodeScalar], tag: Tag, cluster: i32) -> bool {
        if cluster < 0 || self.plan.value(tag, cluster) == 0 {
            return false;
        }
        let index = cluster as u32;
        if self
            .plan
            .settings()
            .iter()
            .any(|f| f.tag == tag && f.start <= index && index < f.end)
            || self.plan.explicit().contains(&tag) && !self.plan.has_settings(tag)
        {
            return true;
        }
        let numeric = |cp| {
            unicode::line_break::break_class(cp) == unicode::line_break::LineBreakClass::Numeric
        };
        for slash in 1..input.len().saturating_sub(1) {
            if input[slash].code_point != 0x2044
                || !numeric(input[slash - 1].code_point)
                || !numeric(input[slash + 1].code_point)
            {
                continue;
            }
            let mut numerator = slash;
            while numerator > 0 && numeric(input[numerator - 1].code_point) {
                numerator -= 1;
            }
            let mut denominator = slash + 1;
            while denominator < input.len() && numeric(input[denominator].code_point) {
                denominator += 1;
            }
            for i in numerator..denominator {
                if input[i].input_index == index {
                    return tag == FRACTIONS[0]
                        || tag == FRACTIONS[1] && i < slash
                        || tag == FRACTIONS[2] && i > slash;
                }
            }
        }
        false
    }
    fn fraction(
        &self,
        input: &[UnicodeScalar],
        lookup: u16,
        tag: Tag,
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let mut state = GsubState {
            glyph_count: b.count as u32,
            ..Default::default()
        };
        let mut position = 0;
        while position < state.glyph_count {
            let cluster = b.storage[position as usize].cluster;
            if !self.fraction_enabled(input, tag, cluster) {
                position += 1;
                continue;
            }
            let before = state.glyph_count;
            state.context_match_end = Some(0);
            let result = table.apply_gsub(
                lookup,
                Some(position),
                b.storage,
                GsubOptions {
                    gdef: self.gdef,
                    alternate_value: self.plan.value(tag, cluster),
                    track_fallback_mark_metadata: self.fallback_marks(),
                    track_substitution_provenance: true,
                    ..Default::default()
                },
                &mut state,
            );
            b.count = state.glyph_count as usize;
            result?;
            position += state.glyph_count.saturating_sub(before);
            position = (position + 1).max(state.context_match_end.unwrap_or(0));
        }
        Ok(())
    }
    fn probe(&self, tag: Tag, ids: &[u16], scratch: &mut [u16]) -> Result<bool, FontError> {
        if ids.is_empty() || ids.len() > 3 || !self.plan.requested().contains(&tag) {
            return Ok(false);
        }
        let Some(table) = self.table else {
            return Ok(false);
        };
        let count = table.select_feature_into(
            self.selection.script,
            self.selection.language,
            tag,
            self.selection.coordinates,
            scratch,
        )? as usize;
        let mut storage = [ShapingGlyph::default(); 64];
        for (i, &id) in ids.iter().enumerate() {
            storage[i] = ShapingGlyph {
                glyph_id: u32::from(id),
                cluster: i as i32,
                ..Default::default()
            };
        }
        let mut state = GsubState {
            glyph_count: ids.len() as u32,
            ..Default::default()
        };
        for &lookup in &scratch[..count] {
            let before = state.glyph_count;
            let first = storage[0].glyph_id;
            table.apply_gsub(
                lookup,
                Some(0),
                &mut storage,
                GsubOptions {
                    gdef: self.gdef,
                    alternate_value: self.alternate,
                    ..Default::default()
                },
                &mut state,
            )?;
            if state.applied && (state.glyph_count != before || storage[0].glyph_id != first) {
                return Ok(true);
            }
        }
        Ok(false)
    }
    pub(super) fn apply(
        &self,
        input: &[UnicodeScalar],
        b: &mut Buffer<'_>,
        lookup_scratch: &mut [u16],
        indices: &mut [u32],
    ) -> Result<(), FontError> {
        let Some(table) = self.table.filter(|t| t.lookup_count() != 0) else {
            match self.route.complex {
                NativeComplexKind::Use => use_shaper::reorder(self.font, self.flags, b)?,
                NativeComplexKind::Myanmar => myanmar::reorder(self.font, self.flags, b)?,
                NativeComplexKind::Indic => {
                    indic::initial(
                        self.font,
                        self.route.unicode_script,
                        self.route.layout_script,
                        self.flags,
                        b,
                        indices,
                        &mut |_, _| false,
                    )?;
                    indic::final_reorder(self.font, self.route.unicode_script, b.glyphs_mut());
                }
                _ => (),
            }
            return Ok(());
        };
        let required = table.select_lookups_into(
            SelectionRequest {
                features: &[],
                ..self.selection
            },
            lookup_scratch,
        )? as usize;
        for &lookup in &lookup_scratch[..required] {
            self.apply_lookup(
                lookup,
                None,
                GsubOptions {
                    gdef: self.gdef,
                    alternate_value: self.alternate,
                    mark_substituted: true,
                    track_substitution_provenance: true,
                    ..Default::default()
                },
                b,
            )?;
        }
        self.group(DIRECTIONAL, lookup_scratch, b)?;
        for lookup in 0..table.lookup_count() {
            let resolution = Resolution::new(table, self.selection, lookup)?;
            let Some(feature) = resolution.feature else {
                continue;
            };
            if resolution.required
                || !(PREPROCESSING.contains(&feature)
                    || self.route.complex == NativeComplexKind::Use && USE_EXTRA.contains(&feature))
            {
                continue;
            }
            if FRACTIONS.contains(&feature) {
                self.fraction(input, lookup, feature, b)?;
            } else {
                self.apply_lookup(
                    lookup,
                    Some(feature),
                    GsubOptions {
                        gdef: self.gdef,
                        alternate_value: self.alternate,
                        track_substitution_provenance: true,
                        restrict_to_syllable: per_syllable(self.route.complex, feature, false),
                        ..Default::default()
                    },
                    b,
                )?;
            }
        }
        match self.route.complex {
            NativeComplexKind::Khmer => self.group(KHMER, lookup_scratch, b)?,
            NativeComplexKind::Myanmar => {
                myanmar::reorder(self.font, self.flags, b)?;
                self.group(MYANMAR, lookup_scratch, b)?;
            }
            NativeComplexKind::Indic => {
                let mut error = None;
                let mut probe = |tag, ids: &[u16]| {
                    if error.is_some() {
                        return false;
                    }
                    match self.probe(tag, ids, lookup_scratch) {
                        Ok(v) => v,
                        Err(e) => {
                            error = Some(e);
                            false
                        }
                    }
                };
                indic::initial(
                    self.font,
                    self.route.unicode_script,
                    self.route.layout_script,
                    self.flags,
                    b,
                    indices,
                    &mut probe,
                )?;
                if let Some(error) = error {
                    return Err(error);
                }
                self.group(INDIC, lookup_scratch, b)?;
                indic::final_reorder(self.font, self.route.unicode_script, b.glyphs_mut());
            }
            NativeComplexKind::Use => {
                clear_substituted(b.glyphs_mut());
                self.group(&[Tag::new(*b"rphf")], lookup_scratch, b)?;
                use_shaper::record_repha(b.glyphs_mut());
                clear_substituted(b.glyphs_mut());
                self.group(&[Tag::new(*b"pref")], lookup_scratch, b)?;
                use_shaper::record_prebase(b.glyphs_mut());
                self.group(USE_BASIC, lookup_scratch, b)?;
                use_shaper::reorder(self.font, self.flags, b)?;
                self.group(USE_TOPO, lookup_scratch, b)?;
            }
            NativeComplexKind::None => return Err(FontError::InvalidArgument),
        }
        for &feature in self.plan.requested() {
            let applied = DIRECTIONAL.contains(&feature)
                || PREPROCESSING.contains(&feature)
                || match self.route.complex {
                    NativeComplexKind::Khmer => KHMER.contains(&feature),
                    NativeComplexKind::Myanmar => MYANMAR.contains(&feature),
                    NativeComplexKind::Indic => INDIC.contains(&feature),
                    _ => {
                        USE_EXTRA.contains(&feature)
                            || [Tag::new(*b"rphf"), Tag::new(*b"pref")].contains(&feature)
                            || USE_BASIC.contains(&feature)
                            || USE_TOPO.contains(&feature)
                    }
                };
            if !applied {
                self.feature(feature, true, lookup_scratch, b)?;
            }
        }
        Ok(())
    }
}
