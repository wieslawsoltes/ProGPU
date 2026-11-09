use super::*;
use crate::text::unicode::ManagedMarkProfile;
/// Original managed per-buffer lookup state, independent of the native packed ABI.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedGlyphState {
    /// Original manual-joiner and active syllable policy.
    pub traversal: ManagedContextState,
    /// Original exclusive context boundary, increased by successful nested records.
    pub context_match_end: i32,
    /// Original wrapping alternate generator, initially one.
    pub random_state: u32,
}
impl Default for ManagedGlyphState {
    fn default() -> Self {
        Self {
            traversal: ManagedContextState::default(),
            context_match_end: 0,
            random_state: 1,
        }
    }
}
/// Owned original managed records and actual font generation. GSUB mutation keeps
/// source, grapheme/character clusters and every independent private metadata byte.
/// This is reusable shaping state, not a published paragraph or GPU text object.
#[derive(Clone, Debug)]
pub struct ManagedGlyphBuffer {
    pub(crate) preparation:
        Option<std::sync::Arc<crate::text::preparation::ManagedPreparationSource>>,
    pub(crate) glyphs: Vec<ManagedShapingGlyph>,
    font: SfntFace,
    use_gdef: bool,
    mark_profile: ManagedMarkProfile,
    pub(super) state: ManagedGlyphState,
    pub(super) has_fraction_actions: bool,
    pub(super) mark_filtering_coverage: i32,
    pub(super) nested_depth: u32,
}
impl ManagedGlyphBuffer {
    /// Own already-mapped original records and the immutable source font. GDEF
    /// admission uses the original complete table-length blocklist; raw table
    /// contents retain the managed executor's independent weak validation.
    pub fn new(font: SfntFace, glyphs: Vec<ManagedShapingGlyph>) -> Result<Self, FontError> {
        Self::with_mark_profile(font, glyphs, ManagedMarkProfile::default())
    }
    /// Retain an explicitly witnessed Rune category repertoire independently of
    /// normalization. Invalid source scalars reject only when classification runs.
    pub fn with_mark_profile(
        font: SfntFace,
        glyphs: Vec<ManagedShapingGlyph>,
        mark_profile: ManagedMarkProfile,
    ) -> Result<Self, FontError> {
        if glyphs.len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        let len = |tag| font.table(tag).map_or(0, |t| t.bytes.len());
        let use_gdef = !is_gdef_blocklisted(
            len(Tag::new(*b"GDEF")),
            len(Tag::new(*b"GSUB")),
            len(Tag::new(*b"GPOS")),
        );
        Ok(Self {
            preparation: None,
            glyphs,
            font,
            use_gdef,
            mark_profile,
            state: ManagedGlyphState::default(),
            has_fraction_actions: false,
            mark_filtering_coverage: -1,
            nested_depth: 0,
        })
    }
    /// Original text/context/font generation when created through `from_text`.
    /// Already-mapped record constructors intentionally retain no fabricated text.
    pub fn preparation(&self) -> Option<&crate::text::preparation::ManagedPreparationSource> {
        self.preparation.as_deref()
    }
    /// Original independent fraction-action marker set by the preparation stage.
    /// This is not inferred from record bytes: retained source state may differ.
    pub fn has_fraction_actions(&self) -> bool {
        self.has_fraction_actions
    }
    /// Retain an explicit original preparation marker for already-prepared records.
    pub fn set_fraction_actions_present(&mut self, present: bool) {
        self.has_fraction_actions = present;
    }
    /// Exact retained selected source generation.
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    /// Complete live original record sequence, without native flag projection.
    pub fn glyphs(&self) -> &[ManagedShapingGlyph] {
        &self.glyphs
    }
    /// Mutate original records without changing the established live length.
    pub fn glyphs_mut(&mut self) -> &mut [ManagedShapingGlyph] {
        &mut self.glyphs
    }
    /// Original active lookup state.
    pub fn state(&self) -> ManagedGlyphState {
        self.state
    }
    /// Change explicitly selected lookup state; no script or feature policy is inferred.
    pub fn set_state(&mut self, state: ManagedGlyphState) {
        self.state = state;
    }
    /// Actual selected mark coverage offset, or the original -1 sentinel.
    pub fn mark_filtering_coverage(&self) -> i32 {
        self.mark_filtering_coverage
    }
    /// Active original nested lookup depth, restored on every normal/error return.
    pub fn nested_lookup_depth(&self) -> u32 {
        self.nested_depth
    }
    /// Consume the buffer and return its complete current records.
    pub fn into_glyphs(mut self) -> Vec<ManagedShapingGlyph> {
        // Explicit transfer owns these records; no empty replacement is pooled.
        self.preparation = None;
        std::mem::take(&mut self.glyphs)
    }
    pub(super) fn gdef(&self) -> &[u8] {
        if self.use_gdef {
            self.font.table(Tag::new(*b"GDEF")).map_or(&[], |t| t.bytes)
        } else {
            &[]
        }
    }
    pub(super) fn glyph_at(&self, i: usize) -> Result<u16, FontError> {
        let g = self.glyphs.get(i).ok_or(FontError::InvalidGlyph)?;
        Ok(if self.admitted(*g) {
            g.glyph_index
        } else {
            u16::MAX
        })
    }
    fn admitted(&self, g: ManagedShapingGlyph) -> bool {
        !self.state.traversal.restrict_to_syllable
            || g.use_syllable == self.state.traversal.lookup_syllable
    }
    fn visible(g: ManagedShapingGlyph) -> bool {
        g.substituted != 0 || g.script_shaper == 4 && g.script_feature_mask != 0
    }
    fn special(cp: u32) -> bool {
        matches!(cp,0x180b..=0x180e|0xe0000..=0xe007f)
    }
    /// Immutable source mark profile selected for this generation.
    pub fn mark_profile(&self) -> ManagedMarkProfile {
        self.mark_profile
    }
    /// Original raw GDEF classification with Rune fallback only for class zero.
    /// The original byte enum narrows GDEF classes before its zero fallback test.
    pub fn glyph_class(&self, i: usize) -> Result<u16, FontError> {
        let g = *self.glyphs.get(i).ok_or(FontError::InvalidArgument)?;
        let data = self.gdef();
        let class = if fits(data, 4, 2) && u16_at(data, 4) != 0 {
            u16::from(class_at(data, i64::from(u16_at(data, 4)), g.glyph_index) as u8)
        } else {
            0
        };
        if class != 0 {
            return Ok(class);
        }
        Ok(
            if self
                .mark_profile
                .is_mark(g.code_point)
                .map_err(|_| FontError::InvalidGlyph)?
            {
                3
            } else {
                1
            },
        )
    }
    fn class_ignored(&self, i: usize, flags: u16) -> Result<bool, FontError> {
        if flags & 0xff1e == 0 {
            return Ok(false);
        }
        let g = self.glyphs[i];
        let data = self.gdef();
        let class = self.glyph_class(i)?;
        if class == 3 {
            if flags & 16 != 0
                && (self.mark_filtering_coverage < 0
                    || coverage_at(data, i64::from(self.mark_filtering_coverage), g.glyph_index)?
                        .is_none())
            {
                return Ok(true);
            }
            let attachment = flags >> 8;
            if attachment != 0 {
                let actual = if fits(data, 10, 2) && u16_at(data, 10) != 0 {
                    class_at(data, i64::from(u16_at(data, 10)), g.glyph_index)
                } else {
                    0
                };
                if actual != attachment {
                    return Ok(true);
                }
            }
        }
        Ok(match class {
            1 => flags & 2 != 0,
            2 => flags & 4 != 0,
            3 => flags & 8 != 0,
            _ => false,
        })
    }
    fn ignored(&self, i: usize, flags: u16, context: bool) -> Result<bool, FontError> {
        let g = self.glyphs[i];
        if default_ignorable(g.code_point)
            && !Self::visible(g)
            && !Self::special(g.code_point)
            && if context {
                !(self.state.traversal.manual_joiners && g.code_point == 0x200c)
            } else {
                g.code_point != 0x200c
                    && !(self.state.traversal.manual_joiners && g.code_point == 0x200d)
            }
        {
            return Ok(true);
        }
        self.class_ignored(i, flags)
    }
    pub(super) fn next(
        &self,
        start: usize,
        flags: u16,
        context: bool,
    ) -> Result<Option<usize>, FontError> {
        for i in start..self.glyphs.len() {
            if !self.admitted(self.glyphs[i]) {
                break;
            }
            if !self.ignored(i, flags, context)? {
                return Ok(Some(i));
            }
        }
        Ok(None)
    }
    pub(super) fn previous(&self, before: usize, flags: u16) -> Result<Option<usize>, FontError> {
        if before > self.glyphs.len() {
            return Err(FontError::InvalidGlyph);
        }
        for i in (0..before).rev() {
            if !self.admitted(self.glyphs[i]) {
                break;
            }
            if !self.ignored(i, flags, true)? {
                return Ok(Some(i));
            }
        }
        Ok(None)
    }
    pub(crate) fn next_component(
        &self,
        start: usize,
        flags: u16,
        expected: u16,
    ) -> Result<Option<usize>, FontError> {
        for i in start..self.glyphs.len() {
            let g = self.glyphs[i];
            if !self.admitted(g) {
                break;
            }
            if default_ignorable(g.code_point) && !Self::visible(g) {
                if g.glyph_index == expected
                    || matches!(g.code_point, 0x34f | 0x200c)
                    || self.state.traversal.manual_joiners && g.code_point == 0x200d
                    || Self::special(g.code_point)
                {
                    return Ok(Some(i));
                }
                continue;
            }
            if !self.ignored(i, flags, false)? {
                return Ok(Some(i));
            }
        }
        Ok(None)
    }
    pub(super) fn select_mark_set(&mut self, index: u16) {
        self.mark_filtering_coverage = -1;
        if index == u16::MAX {
            return;
        }
        let data = self.gdef();
        if !fits(data, 12, 2) {
            return;
        }
        let sets = usize::from(u16_at(data, 12));
        if sets == 0 || !fits(data, sets, 4) || u16_at(data, sets) != 1 {
            return;
        }
        let p = sets + 4 + usize::from(index) * 4;
        if index >= u16_at(data, sets + 2) || !fits(data, p, 4) {
            return;
        }
        let relative = u32_at(data, p);
        let at = sets as i64 + i64::from(relative);
        if relative <= i32::MAX as u32 && fits_at(data, at, 4) {
            self.mark_filtering_coverage = at as i32;
        }
    }
    pub(super) fn dependency(&mut self, start: usize, end: usize) {
        let start = start.min(self.glyphs.len());
        let end = end.clamp(start, self.glyphs.len());
        if end - start < 2 {
            return;
        }
        let minimum = self.glyphs[start..end]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for g in &mut self.glyphs[start..end] {
            if g.cluster != minimum {
                g.flags |= 3;
            }
        }
    }
    pub(super) fn set_context_end(&mut self, end: i64) {
        self.state.context_match_end = self
            .state
            .context_match_end
            .max(end.clamp(0, self.glyphs.len() as i64) as i32);
    }
    pub(crate) fn replace(&mut self, index: usize, id: u16) -> Result<(), FontError> {
        let g = self.glyphs.get_mut(index).ok_or(FontError::InvalidGlyph)?;
        g.glyph_index = id;
        g.substituted = 1;
        Ok(())
    }
    pub(super) fn replace_multiple(
        &mut self,
        index: usize,
        replacements: &[u16],
    ) -> Result<(), FontError> {
        let new_count = self
            .glyphs
            .len()
            .checked_add(replacements.len())
            .and_then(|n| n.checked_sub(1))
            .ok_or(FontError::Capacity)?;
        if new_count > 1_048_576 {
            return Err(FontError::Capacity);
        }
        let record = *self.glyphs.get(index).ok_or(FontError::InvalidGlyph)?;
        self.glyphs
            .try_reserve(new_count.saturating_sub(self.glyphs.len()))
            .map_err(|_| FontError::Capacity)?;
        let delayed = record.ligated != 0 && replacements.len() > 1;
        let mut insertion = index;
        let mut first = 0;
        if delayed {
            insertion = index + 1;
            while insertion < self.glyphs.len()
                && self.glyphs[insertion].ligature_component != u8::MAX
            {
                insertion += 1;
            }
            let mut g = record;
            g.glyph_index = replacements[0];
            g.substituted = 1;
            g.multiple_substitution_component = 0;
            g.multiplied = 1;
            self.glyphs[index] = g;
            first = 1;
        } else {
            self.glyphs.remove(index);
        }
        let mut insert = |i: usize, id: u16, at: usize| {
            let mut g = record;
            g.glyph_index = id;
            g.substituted = 1;
            g.multiple_substitution_component = i.min(255) as u8;
            g.multiplied = 1;
            self.glyphs.insert(at, g);
        };
        if delayed {
            for (i, &id) in replacements.iter().enumerate().skip(first) {
                insert(i, id, insertion);
                insertion += 1;
            }
        } else {
            for (i, &id) in replacements.iter().enumerate().rev() {
                insert(i, id, index);
            }
        }
        if delayed {
            self.state.context_match_end = self.state.context_match_end.max(insertion as i32);
        }
        Ok(())
    }
    pub(crate) fn replace_ligature(&mut self, indices: &[usize], id: u16) -> Result<(), FontError> {
        let Some(&first) = indices.first() else {
            return Ok(());
        };
        let last = *indices.last().unwrap();
        let mut ligature = *self.glyphs.get(first).ok_or(FontError::InvalidGlyph)?;
        if last >= self.glyphs.len() {
            return Err(FontError::InvalidGlyph);
        }
        ligature.glyph_index = id;
        ligature.substituted = 1;
        ligature.ligated = 1;
        ligature.multiplied = 0;
        ligature.ligature_component_count = indices.len().min(255) as u8;
        for (component, pair) in indices.windows(2).enumerate() {
            for g in &mut self.glyphs[pair[0] + 1..pair[1]] {
                g.ligature_component = component.min(254) as u8;
            }
        }
        let first_cluster = self.glyphs[first].cluster;
        let last_cluster = self.glyphs[last].cluster;
        let mut start = first;
        let mut end = last + 1;
        while start > 0 && self.glyphs[start - 1].cluster == first_cluster {
            start -= 1;
        }
        while end < self.glyphs.len() && self.glyphs[end].cluster == last_cluster {
            end += 1;
        }
        let cluster = self.glyphs[start..end]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for g in &mut self.glyphs[start..end] {
            g.cluster = cluster;
        }
        ligature.cluster = cluster;
        self.glyphs[first] = ligature;
        for &i in indices[1..].iter().rev() {
            self.glyphs.remove(i);
        }
        Ok(())
    }
}
impl Drop for ManagedGlyphBuffer {
    fn drop(&mut self) {
        if self.preparation.is_some() {
            crate::text::preparation::pool::retire(std::mem::take(&mut self.glyphs));
        }
    }
}
