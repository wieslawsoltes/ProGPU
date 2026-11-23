//! Original native uniform-run Arabic/Hangul and ordinary feature orchestration.
use super::*;
const STRETCH: Tag = Tag::new(*b"stch");
const FORMS: [(Tag, u8); 7] = [
    (Tag::new(*b"isol"), 0),
    (Tag::new(*b"fina"), 1),
    (Tag::new(*b"fin2"), 2),
    (Tag::new(*b"fin3"), 3),
    (Tag::new(*b"medi"), 4),
    (Tag::new(*b"med2"), 5),
    (Tag::new(*b"init"), 6),
];
#[derive(Clone, Copy)]
enum ArabicStage {
    Directional,
    Preprocessing,
    Required,
    Contextual,
    PostRequired,
    PostContextual,
}
fn admitted(tag: Tag, stage: ArabicStage) -> bool {
    let direction = DIRECTIONAL.contains(&tag);
    let preprocessing = PREPROCESSING.contains(&tag);
    let form = FORMS.iter().any(|&(f, _)| f == tag);
    let required = tag == Tag::new(*b"rlig");
    let contextual = [Tag::new(*b"calt"), Tag::new(*b"rclt")].contains(&tag);
    match stage {
        ArabicStage::Directional => direction,
        ArabicStage::Preprocessing => preprocessing && !FRACTIONS.contains(&tag),
        ArabicStage::Required => required,
        ArabicStage::Contextual => contextual,
        ArabicStage::PostRequired => {
            !direction && tag != STRETCH && !preprocessing && !form && !required
        }
        ArabicStage::PostContextual => {
            !direction && tag != STRETCH && !preprocessing && !form && !required && !contextual
        }
    }
}
impl Context<'_> {
    pub(crate) fn fallback_marks(&self) -> bool {
        self.zero_mark_advances
            && self.route.complex == NativeComplexKind::None
            && ![*b"thai", *b"lao ", *b"mymr", *b"qaag"]
                .contains(&self.route.unicode_script.bytes())
    }
    fn options(&self) -> GsubOptions<'_> {
        GsubOptions {
            gdef: self.gdef,
            alternate_value: self.alternate,
            track_fallback_mark_metadata: self.fallback_marks(),
            track_substitution_provenance: true,
            random_alternate: true,
            ..Default::default()
        }
    }
    fn selected(&self, scratch: &mut [u16], excluded: &[Tag]) -> Result<usize, FontError> {
        Ok(self
            .table
            .ok_or(FontError::InvalidArgument)?
            .select_lookups_excluding_into(self.selection, excluded, scratch)? as usize)
    }
    fn resolved(&self, lookup: u16, b: &mut Buffer<'_>) -> Result<(), FontError> {
        let resolution = Resolution::new(
            self.table.ok_or(FontError::InvalidArgument)?,
            self.selection,
            lookup,
        )?;
        self.apply_lookup(
            lookup,
            if resolution.required {
                None
            } else {
                resolution.feature
            },
            self.options(),
            b,
        )
    }
    fn arabic_stage(
        &self,
        stage: ArabicStage,
        scratch: &mut [u16],
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        let n = self.selected(scratch, &FRACTIONS)?;
        for &lookup in &scratch[..n] {
            let r = Resolution::new(
                self.table.ok_or(FontError::InvalidArgument)?,
                self.selection,
                lookup,
            )?;
            if let Some(tag) = r.feature
                && admitted(tag, stage)
            {
                self.resolved(lookup, b)?;
            }
        }
        Ok(())
    }
    fn targeted(
        &self,
        tag: Tag,
        action: u8,
        hangul: bool,
        scratch: &mut [u16],
        b: &mut Buffer<'_>,
    ) -> Result<(), FontError> {
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let n = table.select_feature_into(
            self.selection.script,
            self.selection.language,
            tag,
            self.selection.coordinates,
            scratch,
        )? as usize;
        for &lookup in &scratch[..n] {
            let mut position = 0usize;
            while position < b.count {
                let value = self.plan.value(tag, b.storage[position].cluster);
                let actual = if hangul {
                    ((b.storage[position].flags & 0x30000000) >> 28) as u8
                } else {
                    joining::action(b.storage[position])
                };
                if value == 0 || actual != action {
                    position += 1;
                    continue;
                }
                let before = b.count;
                let mut state = GsubState {
                    glyph_count: b.count as u32,
                    ..Default::default()
                };
                let result = table.apply_gsub(
                    lookup,
                    Some(position as u32),
                    b.storage,
                    GsubOptions {
                        alternate_value: value,
                        random_alternate: false,
                        ..self.options()
                    },
                    &mut state,
                );
                b.count = state.glyph_count as usize;
                result?;
                position += 1 + b.count.saturating_sub(before);
            }
        }
        Ok(())
    }
    fn stretch_feature(&self, scratch: &mut [u16], b: &mut Buffer<'_>) -> Result<(), FontError> {
        if !self.plan.run_enabled(STRETCH) {
            return Ok(());
        }
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let n = table.select_feature_into(
            self.selection.script,
            self.selection.language,
            STRETCH,
            self.selection.coordinates,
            scratch,
        )? as usize;
        for &lookup in &scratch[..n] {
            let mut position = 0usize;
            while position < b.count {
                let value = self.plan.value(STRETCH, b.storage[position].cluster);
                if value == 0 {
                    position += 1;
                    continue;
                }
                let before = b.count;
                let mut state = GsubState {
                    glyph_count: b.count as u32,
                    context_match_end: Some(0),
                    ..Default::default()
                };
                let result = table.apply_gsub(
                    lookup,
                    Some(position as u32),
                    b.storage,
                    GsubOptions {
                        alternate_value: value,
                        track_arabic_stretch_metadata: true,
                        random_alternate: false,
                        ..self.options()
                    },
                    &mut state,
                );
                b.count = state.glyph_count as usize;
                result?;
                position = (position + b.count.saturating_sub(before) + 1)
                    .max(state.context_match_end.unwrap_or(0) as usize);
            }
        }
        for g in b.glyphs_mut() {
            if g.flags & 0x80000 != 0 {
                let action = if (g.flags >> 20) & 1 != 0 { 9 } else { 8 };
                joining::set_action(g, action);
            }
            g.flags &= !0x0ff80000;
        }
        Ok(())
    }
    fn fractions(&self, input: &[UnicodeScalar], b: &mut Buffer<'_>) -> Result<(), FontError> {
        let table = self.table.ok_or(FontError::InvalidArgument)?;
        let numeric = |cp| {
            unicode::line_break::break_class(cp) == unicode::line_break::LineBreakClass::Numeric
        };
        let actions = (1..input.len().saturating_sub(1)).any(|i| {
            input[i].code_point == 0x2044
                && numeric(input[i - 1].code_point)
                && numeric(input[i + 1].code_point)
        });
        if !actions
            && !FRACTIONS
                .iter()
                .any(|tag| self.plan.explicit().contains(tag) || self.plan.has_settings(*tag))
        {
            return Ok(());
        }
        for lookup in 0..table.lookup_count() {
            let mut feature = None;
            for tag in FRACTIONS {
                if self.plan.requested().contains(&tag)
                    && table.feature_contains_lookup(
                        self.selection.script,
                        self.selection.language,
                        tag,
                        lookup,
                        self.selection.coordinates,
                    )?
                {
                    feature = Some(tag);
                }
            }
            if let Some(tag) = feature {
                self.fraction(input, lookup, tag, b)?;
            }
        }
        Ok(())
    }
    pub(crate) fn apply_run(
        &self,
        input: &[UnicodeScalar],
        b: &mut Buffer<'_>,
        scratch: &mut [u16],
        indices: &mut [u32],
    ) -> Result<(), FontError> {
        let arabic = self.route.arabic_joining;
        let actual_arabic = self.route.unicode_script == Tag::new(*b"arab");
        let hangul = self.route.unicode_script == Tag::new(*b"hang");
        let has_table = self.table.is_some_and(|t| t.lookup_count() != 0);
        let mut has_forms = false;
        if actual_arabic && has_table {
            let table = self.table.unwrap();
            for (tag, _) in FORMS {
                if table
                    .select_feature_into(
                        self.selection.script,
                        self.selection.language,
                        tag,
                        self.selection.coordinates,
                        scratch,
                    )
                    .map_err(|error| {
                        joining::clear(b.glyphs_mut());
                        error
                    })?
                    != 0
                {
                    has_forms = true;
                    break;
                }
            }
        }
        let mut fallback_completed = false;
        let mut clear_error = arabic;
        let result = (|| {
            if has_table {
                if arabic {
                    self.arabic_stage(ArabicStage::Directional, scratch, b)?;
                    self.stretch_feature(scratch, b)?;
                }
                if self.route.complex != NativeComplexKind::None {
                    clear_error = false;
                    self.apply(input, b, scratch, indices)?;
                    clear_error = arabic;
                } else if arabic {
                    self.arabic_stage(ArabicStage::Preprocessing, scratch, b)?;
                    self.fractions(input, b)?;
                } else {
                    let mut excluded = [Tag(0); 3];
                    let mut count = 0;
                    for tag in self.plan.excluded_fractions() {
                        excluded[count] = tag;
                        count += 1;
                    }
                    let n = self.selected(scratch, &excluded[..count])?;
                    for &lookup in &scratch[..n] {
                        self.resolved(lookup, b)?;
                    }
                    self.fractions(input, b)?;
                }
                if arabic {
                    for (tag, action) in FORMS {
                        self.targeted(tag, action, false, scratch, b)?;
                    }
                    let table = self.table.unwrap();
                    let required_context = table.select_feature_into(
                        self.selection.script,
                        self.selection.language,
                        Tag::new(*b"rclt"),
                        self.selection.coordinates,
                        scratch,
                    )? != 0;
                    self.arabic_stage(ArabicStage::Required, scratch, b)?;
                    if actual_arabic {
                        if !has_forms {
                            arabic_fallback::apply(
                                self.font,
                                b,
                                self.gdef,
                                self.plan,
                                self.fallback_marks(),
                            )?;
                        }
                        fallback_completed = true;
                    }
                    let reverse = self.route.direction == GposDirection::LeftToRight && has_forms;
                    if reverse {
                        b.glyphs_mut().reverse();
                    }
                    let result = if required_context {
                        self.arabic_stage(ArabicStage::PostRequired, scratch, b)
                    } else {
                        self.arabic_stage(ArabicStage::Contextual, scratch, b)
                            .and_then(|()| {
                                self.arabic_stage(ArabicStage::PostContextual, scratch, b)
                            })
                    };
                    if reverse {
                        b.glyphs_mut().reverse();
                    }
                    result?;
                }
                if hangul {
                    for (tag, action) in [
                        (Tag::new(*b"ljmo"), 1),
                        (Tag::new(*b"vjmo"), 2),
                        (Tag::new(*b"tjmo"), 3),
                    ] {
                        self.targeted(tag, action, true, scratch, b)?;
                    }
                }
            }
            if actual_arabic && !fallback_completed && !has_forms {
                arabic_fallback::apply(self.font, b, self.gdef, self.plan, self.fallback_marks())?;
            }
            if !has_table && self.route.complex != NativeComplexKind::None {
                clear_error = false;
                self.apply(input, b, scratch, indices)?;
            }
            Ok(())
        })();
        if hangul {
            for g in b.glyphs_mut() {
                g.flags &= !0x30000000;
            }
        }
        if result.is_err() && clear_error {
            joining::clear(b.glyphs_mut());
        }
        result
    }
}
