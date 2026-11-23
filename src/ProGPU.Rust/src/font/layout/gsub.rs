//! Direct port of ProGPU's original native GSUB executor. All mutation order,
//! malformed-table admission and packed metadata come from the owned source.
use super::{
    ClassDefinition, ContextCoverage, ContextSubtable, Coverage, FontError, Gdef, GlyphClass,
    GlyphSetDigest, LayoutTable, Lookup, fits, u16_at, u32_at,
};
use crate::text::{
    ShapingGlyph,
    unicode::{GeneralCategory, general_category},
};
use std::ops::Range;
mod context;
type Applied = Result<bool, FontError>;

/// Explicit original native GSUB execution policy. This does not select scripts,
/// features, font policies or managed shaping defaults.
#[derive(Clone, Copy, Debug)]
pub struct GsubOptions<'a> {
    /// Optional validated GDEF. Missing classes use the original Unicode mark fallback.
    pub gdef: Option<Gdef<'a>>,
    /// One-based alternate choice, clamped to the set; zero disables alternates.
    pub alternate_value: u32,
    /// Required raw flags on top-level candidate glyphs only.
    pub required_glyph_flags: u32,
    /// Preserve original substituted/ligated/multiplied flag writes when enabled.
    pub mark_substituted: bool,
    /// Record original packed fallback ligature and skipped-mark components.
    pub track_fallback_mark_metadata: bool,
    /// Record original packed multiple-substitution stretch components.
    pub track_arabic_stretch_metadata: bool,
    /// Use caller random state for top-level alternates only, when present.
    pub random_alternate: bool,
    /// Optional original conservative filter; used by whole-lookup execution only.
    pub lookup_digest: Option<GlyphSetDigest>,
    /// Optional exact top-level coverage filter, explicitly selected by the caller.
    pub lookup_coverage: Option<Coverage<'a>>,
    /// Optional original prepared context descriptors. A mismatched count is ignored.
    pub lookup_context_subtables: &'a [ContextSubtable],
    /// Coverage generations corresponding to the context descriptors.
    pub lookup_context_coverages: &'a [ContextCoverage<'a>],
    /// Derive the restricted syllable from each top-level candidate's raw flags.
    pub restrict_to_syllable: bool,
    /// Explicit original restriction when automatic syllable selection is disabled.
    pub restricted_syllable: u8,
    /// Set advance_x to i32::MIN on substituted glyphs, independently of flags.
    pub track_substitution_provenance: bool,
}
impl Default for GsubOptions<'_> {
    fn default() -> Self {
        Self {
            gdef: None,
            alternate_value: 1,
            required_glyph_flags: 0,
            mark_substituted: false,
            track_fallback_mark_metadata: false,
            track_arabic_stretch_metadata: false,
            random_alternate: false,
            lookup_digest: None,
            lookup_coverage: None,
            lookup_context_subtables: &[],
            lookup_context_coverages: &[],
            restrict_to_syllable: false,
            restricted_syllable: 0,
            track_substitution_provenance: false,
        }
    }
}
/// Caller-owned mutation state. On error, glyph storage and this state retain the
/// original executor's partial writes; execution is deliberately not transactional.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GsubState {
    /// Live prefix of the supplied storage, updated by insertions and removals.
    pub glyph_count: u32,
    /// Reset on entry; original top-level success state, including on later errors.
    pub applied: bool,
    /// Optional caller context boundary, increased only after a successful application.
    pub context_match_end: Option<u32>,
    /// Optional original random generator state, updated with wrapping u32 multiplication.
    pub random_state: Option<u32>,
}
impl LayoutTable<'_> {
    /// Execute one original GSUB lookup, without allocating. `None` visits the
    /// live buffer in original lookup order; `Some(position)` applies at one slot.
    /// Storage length is the complete writable capacity and its unused tail is
    /// preserved exactly, including stale records after contractions.
    ///
    /// Invalid inputs fail before mutation. Malformed nested tables and capacity
    /// failures may retain earlier writes, even when `state.applied` is false.
    /// Prepared filters must describe this lookup in this exact table generation.
    /// Nesting retains the original 64-level bound. This is native GSUB execution,
    /// not a complete managed text shaper or a feature/normalization policy.
    pub fn apply_gsub(
        &self,
        lookup_index: u16,
        position: Option<u32>,
        storage: &mut [ShapingGlyph],
        options: GsubOptions<'_>,
        state: &mut GsubState,
    ) -> Result<(), FontError> {
        state.applied = false;
        if state.glyph_count as usize > storage.len()
            || position.is_some_and(|p| p >= state.glyph_count)
        {
            return Err(FontError::InvalidArgument);
        }
        let record_context = position.is_none() || state.context_match_end.is_some();
        let mut executor = Executor {
            table: *self,
            storage,
            state,
            options,
            context_end: 0,
            record_context,
        };
        if let Some(position) = position {
            let position = position as usize;
            if !executor.required(position) || !executor.covered(position) {
                return Ok(());
            }
            let before = executor.count();
            let syllable = executor.syllable(position);
            executor.state.applied = executor.lookup(lookup_index, position, syllable, 0)?;
            if executor.state.applied {
                executor.publish_context(before);
            }
            return Ok(());
        }
        let lookup = self.lookup(lookup_index)?;
        if !(1..=8).contains(&lookup.lookup_type()) {
            return Ok(());
        }
        let reverse = executor.is_reverse(lookup);
        let mut iteration = 0usize;
        while iteration < executor.count() {
            let position = if reverse {
                executor.count() - iteration - 1
            } else {
                iteration
            };
            iteration += 1;
            let glyph = executor.storage[position].glyph_id;
            if glyph <= u16::MAX as u32
                && executor
                    .options
                    .lookup_digest
                    .is_some_and(|d| !d.may_have(glyph as u16))
            {
                continue;
            }
            let frame = Frame::new(lookup, position, executor.syllable(position), 0);
            if !executor.covered(position)
                || !executor.required(position)
                || !executor.eligible(position, frame)
            {
                continue;
            }
            executor.context_end = 0;
            let before = executor.count();
            for i in 0..lookup.subtable_count() {
                if !executor.may_start(lookup, i, position) {
                    continue;
                }
                if executor.subtable(lookup.lookup_type(), lookup.subtable_offset(i)?, frame)? {
                    executor.state.applied = true;
                    let end = executor.adjusted_end(before);
                    if executor.count() > before {
                        iteration += executor.count() - before;
                    }
                    if !reverse && end > iteration {
                        iteration = end;
                    }
                    executor.publish_context(before);
                    break;
                }
            }
        }
        Ok(())
    }
}
#[derive(Clone, Copy)]
struct Frame {
    flags: u16,
    mark_set: u16,
    position: usize,
    syllable: u8,
    depth: u32,
}
impl Frame {
    fn new(lookup: Lookup<'_>, position: usize, syllable: u8, depth: u32) -> Self {
        Self {
            flags: lookup.flags(),
            mark_set: lookup.mark_filtering_set(),
            position,
            syllable,
            depth,
        }
    }
}
struct Executor<'t, 'o, 's> {
    table: LayoutTable<'t>,
    storage: &'s mut [ShapingGlyph],
    state: &'s mut GsubState,
    options: GsubOptions<'o>,
    context_end: usize,
    record_context: bool,
}
fn require(condition: bool) -> Result<(), FontError> {
    if condition {
        Ok(())
    } else {
        Err(FontError::InvalidFace)
    }
}
fn relative(parent: usize, offset: u32) -> Result<usize, FontError> {
    require(offset != 0)?;
    parent
        .checked_add(offset as usize)
        .ok_or(FontError::InvalidFace)
}
impl Executor<'_, '_, '_> {
    fn data(&self) -> &[u8] {
        self.table.bytes()
    }
    fn check(&self, offset: usize, length: usize) -> Result<(), FontError> {
        require(fits(self.data(), offset, length))
    }
    fn word(&self, offset: usize) -> u16 {
        u16_at(self.data(), offset)
    }
    fn count(&self) -> usize {
        self.state.glyph_count as usize
    }
    fn required(&self, p: usize) -> bool {
        self.storage[p].flags & self.options.required_glyph_flags
            == self.options.required_glyph_flags
    }
    fn covered(&self, p: usize) -> bool {
        let g = self.storage[p].glyph_id;
        g > 65535
            || self
                .options
                .lookup_coverage
                .is_none_or(|c| c.find(g as u16).is_some())
    }
    fn syllable(&self, p: usize) -> u8 {
        if self.options.restrict_to_syllable {
            ((self.storage[p].flags & 0x001fe000) >> 13) as u8
        } else {
            self.options.restricted_syllable
        }
    }
    fn eligible(&self, p: usize, frame: Frame) -> bool {
        let glyph = self.storage[p];
        if glyph.glyph_id > 65535 {
            return false;
        }
        if frame.flags & 0xff1e == 0 {
            return true;
        }
        let id = glyph.glyph_id as u16;
        let mut class = self
            .options
            .gdef
            .map_or(GlyphClass::Unclassified, |g| g.glyph_class(id));
        if class == GlyphClass::Unclassified {
            class = if matches!(
                general_category(glyph.code_point),
                GeneralCategory::NonspacingMark
                    | GeneralCategory::SpacingCombiningMark
                    | GeneralCategory::EnclosingMark
            ) {
                GlyphClass::Mark
            } else {
                GlyphClass::Base
            };
        }
        if class == GlyphClass::Base && frame.flags & 2 != 0
            || class == GlyphClass::Ligature && frame.flags & 4 != 0
        {
            return false;
        }
        if class != GlyphClass::Mark {
            return true;
        }
        if frame.flags & 8 != 0 {
            return false;
        }
        if frame.flags & 16 != 0 {
            return frame.mark_set != u16::MAX
                && self
                    .options
                    .gdef
                    .is_some_and(|g| g.is_in_mark_set(frame.mark_set, id));
        }
        let attachment = frame.flags >> 8;
        attachment == 0
            || self
                .options
                .gdef
                .is_some_and(|g| g.mark_attachment_class(id) == attachment)
    }
    fn same_syllable(&self, p: usize, frame: Frame) -> bool {
        frame.syllable == 0
            || (self.storage[p].flags & 0x001fe000) >> 13 == u32::from(frame.syllable)
    }
    fn next(&self, start: usize, frame: Frame) -> usize {
        for p in start..self.count() {
            if !self.same_syllable(p, frame) {
                break;
            }
            if self.eligible(p, frame) {
                return p;
            }
        }
        self.count()
    }
    fn previous(&self, before: usize, frame: Frame) -> usize {
        for p in (0..before).rev() {
            if !self.same_syllable(p, frame) {
                break;
            }
            if self.eligible(p, frame) {
                return p;
            }
        }
        self.count()
    }
    fn may_start(&self, lookup: Lookup<'_>, index: u16, p: usize) -> bool {
        if self.storage[p].glyph_id > 65535
            || self.options.lookup_context_subtables.len() != usize::from(lookup.subtable_count())
        {
            return true;
        }
        let sub = self.options.lookup_context_subtables[usize::from(index)];
        let first = u64::from(sub.coverage_offset) + u64::from(sub.backtrack_count);
        if sub.input_count == 0 || first >= self.options.lookup_context_coverages.len() as u64 {
            return true;
        }
        self.options.lookup_context_coverages[first as usize]
            .contains(self.storage[p].glyph_id as u16)
    }
    fn dependency(&mut self, first: usize, last: usize) {
        if first >= last || last >= self.storage.len() {
            return;
        }
        let min = self.storage[first..=last]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for glyph in &mut self.storage[first..=last] {
            if glyph.cluster != min {
                glyph.flags |= 3;
            }
        }
    }
    fn record_end(&mut self, end: usize) {
        if self.record_context {
            self.context_end = self.context_end.max(end);
        }
    }
    fn adjusted_end(&self, before: usize) -> usize {
        if self.context_end == 0 || before == self.count() {
            return self.context_end.min(self.count());
        }
        (self.context_end as i64 + self.count() as i64 - before as i64)
            .clamp(0, self.count() as i64) as usize
    }
    fn publish_context(&mut self, before: usize) {
        let adjusted = self.adjusted_end(before) as u32;
        if let Some(end) = &mut self.state.context_match_end {
            *end = (*end).max(adjusted);
        }
    }
    fn mark(&self, glyph: &mut ShapingGlyph, ligated: bool, multiplied: bool) {
        if self.options.track_substitution_provenance {
            glyph.advance_x = i32::MIN;
        }
        if self.options.mark_substituted {
            glyph.flags |= 0x80000000;
            if ligated {
                glyph.flags |= 0x40000000;
            }
            if multiplied {
                glyph.flags |= 0x20000000;
            }
        }
    }
    fn replace(&mut self, p: usize, id: u16) {
        let mut glyph = self.storage[p];
        glyph.glyph_id = u32::from(id);
        self.mark(&mut glyph, false, false);
        self.storage[p] = glyph;
    }
    fn multiple(&mut self, sequence: usize, p: usize) -> Applied {
        self.check(sequence, 2)?;
        let n = usize::from(self.word(sequence));
        self.check(sequence + 2, n * 2)?;
        let next = self
            .count()
            .checked_add(n)
            .and_then(|v| v.checked_sub(1))
            .ok_or(FontError::Capacity)?;
        if next > self.storage.len()
            || next > u32::MAX as usize
            || p.checked_add(n).is_none_or(|end| end > self.storage.len())
        {
            return Err(FontError::Capacity);
        }
        let original = self.storage[p];
        let count = self.count();
        if n != 1 && p + 1 < count {
            self.storage.copy_within(p + 1..count, p + n);
        }
        for i in 0..n {
            let mut glyph = original;
            glyph.glyph_id = u32::from(self.word(sequence + 2 + i * 2));
            self.mark(&mut glyph, false, true);
            if self.options.track_arabic_stretch_metadata {
                glyph.flags = (glyph.flags & !0x0ff80000) | 0x00080000 | (i.min(255) as u32) << 20;
            }
            self.storage[p + i] = glyph;
        }
        self.state.glyph_count = next as u32;
        Ok(true)
    }
    fn ligature(&mut self, offset: usize, frame: Frame) -> Applied {
        self.check(offset, 4)?;
        let id = self.word(offset);
        let n = usize::from(self.word(offset + 2));
        require(n != 0)?;
        self.check(offset + 4, (n - 1) * 2)?;
        let p = frame.position;
        let mut candidate = p;
        for component in 1..n {
            candidate = self.next(candidate + 1, frame);
            if candidate >= self.count()
                || self.storage[candidate].glyph_id
                    != u32::from(self.word(offset + 4 + (component - 1) * 2))
            {
                return Ok(false);
            }
        }
        let mut start = p;
        let mut end = candidate + 1;
        let first_cluster = self.storage[start].cluster;
        let last_cluster = self.storage[end - 1].cluster;
        while start > 0 && self.storage[start - 1].cluster == first_cluster {
            start -= 1;
        }
        while end < self.count() && self.storage[end].cluster == last_cluster {
            end += 1;
        }
        let cluster = self.storage[start..end]
            .iter()
            .map(|g| g.cluster)
            .min()
            .unwrap();
        for glyph in &mut self.storage[start..end] {
            glyph.cluster = cluster;
        }
        let mut first = self.storage[p];
        first.glyph_id = u32::from(id);
        self.mark(&mut first, n > 1, false);
        if self.options.track_fallback_mark_metadata {
            first.flags = (first.flags & !0x000007f8) | (n.min(255) as u32) << 3;
        }
        self.storage[p] = first;
        let mut write = p + 1;
        let mut component = 1;
        for read in p + 1..self.count() {
            if component < n
                && self.eligible(read, frame)
                && self.storage[read].glyph_id
                    == u32::from(self.word(offset + 4 + (component - 1) * 2))
            {
                component += 1;
            } else {
                if self.options.track_fallback_mark_metadata && component < n {
                    self.storage[read].flags = (self.storage[read].flags & !0x0007f800)
                        | (((component - 1).min(254) + 1) as u32) << 11;
                }
                self.storage[write] = self.storage[read];
                write += 1;
            }
        }
        self.state.glyph_count = write as u32;
        Ok(true)
    }
    fn strict_coverage(&self, parent: usize) -> Result<Coverage<'_>, FontError> {
        Coverage::parse(
            self.data(),
            relative(parent, u32::from(self.word(parent + 2)))?,
        )
    }
    fn is_reverse(&self, lookup: Lookup<'_>) -> bool {
        if lookup.lookup_type() == 8 {
            return true;
        }
        if lookup.lookup_type() != 7 || lookup.subtable_count() == 0 {
            return false;
        }
        let s = lookup.subtable_offset(0).unwrap();
        fits(self.data(), s, 4) && self.word(s) == 1 && self.word(s + 2) == 8
    }
    fn lookup(&mut self, index: u16, position: usize, syllable: u8, depth: u32) -> Applied {
        if depth >= 64 || position >= self.count() {
            return Ok(false);
        }
        let lookup = self
            .table
            .lookup(index)
            .map_err(|_| FontError::InvalidFace)?;
        let frame = Frame::new(lookup, position, syllable, depth);
        if !self.eligible(position, frame) {
            return Ok(false);
        }
        for i in 0..lookup.subtable_count() {
            if depth == 0 && !self.may_start(lookup, i, position) {
                continue;
            }
            if self.subtable(lookup.lookup_type(), lookup.subtable_offset(i)?, frame)? {
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn subtable(&mut self, mut kind: u16, mut s: usize, frame: Frame) -> Applied {
        if kind == 7 {
            self.check(s, 8)?;
            require(self.word(s) == 1)?;
            kind = self.word(s + 2);
            require(kind != 7)?;
            s = relative(s, u32_at(self.data(), s + 4))?;
            self.check(s, 2)?;
        }
        if kind == 5 || kind == 6 {
            self.check(s, 2)?;
            return match self.word(s) {
                1 | 2 => self.context_rules(s, kind == 6, self.word(s), frame),
                3 => self.context_coverages(s, kind == 6, frame),
                _ => Err(FontError::InvalidFace),
            };
        }
        if kind == 8 {
            return self.reverse(s, frame);
        }
        require((1..=4).contains(&kind))?;
        self.check(s, 6)?;
        let format = self.word(s);
        let p = frame.position;
        let Some(index) = self
            .strict_coverage(s)?
            .find(self.storage[p].glyph_id as u16)
        else {
            return Ok(false);
        };
        if kind == 1 && format == 1 {
            let id = (self.storage[p].glyph_id as u16).wrapping_add(self.word(s + 4));
            self.replace(p, id);
            return Ok(true);
        }
        if kind == 1 && format == 2 {
            let count = self.word(s + 4);
            require(index < count)?;
            self.check(s + 6, usize::from(count) * 2)?;
            self.replace(p, self.word(s + 6 + usize::from(index) * 2));
            return Ok(true);
        }
        require((2..=4).contains(&kind) && format == 1)?;
        let count = self.word(s + 4);
        require(index < count)?;
        self.check(s + 6, usize::from(count) * 2)?;
        let set = relative(s, u32::from(self.word(s + 6 + usize::from(index) * 2)))?;
        if kind == 2 {
            return self.multiple(set, p);
        }
        self.check(set, 2)?;
        let count = usize::from(self.word(set));
        self.check(set + 2, count * 2)?;
        if kind == 3 {
            if count == 0 || self.options.alternate_value == 0 {
                return Ok(false);
            }
            let mut selected = (self.options.alternate_value.min(count as u32) - 1) as usize;
            if frame.depth == 0
                && self.options.random_alternate
                && self.state.random_state.is_some()
            {
                self.dependency(0, self.count().wrapping_sub(1));
                let random = self.state.random_state.as_mut().unwrap();
                *random = random.wrapping_mul(48271) % 2147483647;
                selected = *random as usize % count;
            }
            self.replace(p, self.word(set + 2 + selected * 2));
            return Ok(true);
        }
        for i in 0..count {
            let ligature = relative(set, u32::from(self.word(set + 2 + i * 2)))?;
            if self.ligature(ligature, frame)? {
                return Ok(true);
            }
        }
        Ok(false)
    }
}
