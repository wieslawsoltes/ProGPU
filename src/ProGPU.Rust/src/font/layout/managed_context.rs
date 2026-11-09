//! Original C# format-3 preparation and negative-only ordered context filter.
use super::{
    Arc, ContextSubtable, FontError, ManagedGlyphSetDigest, SfntFace, Storage, Tag, fits, u16_at,
    u32_at,
};

/// Original glyph-record fields consumed by the managed context accelerator.
/// This is a read-only projection of shaping state, not a complete shaped glyph.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ManagedContextGlyph {
    /// Current original glyph ID.
    pub glyph_index: u16,
    /// Original code point, independent of the current glyph ID.
    pub code_point: u32,
    /// Raw source substitution byte; any nonzero value makes ignorables visible.
    pub substituted: u8,
    /// Raw source script-shaper identity (Hangul is 4).
    pub script_shaper: u8,
    /// Original script-feature mask.
    pub script_feature_mask: u8,
    /// Original USE syllable byte.
    pub use_syllable: u8,
}
/// Original active lookup state needed for context traversal.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ManagedContextState {
    /// Original manual-joiner policy; input and context rules differ.
    pub manual_joiners: bool,
    /// Stop traversing at a glyph outside the active source syllable.
    pub restrict_to_syllable: bool,
    /// Original active syllable identity.
    pub lookup_syllable: u8,
}
/// Immutable original glyph records, active state and reusable buffer digest.
#[derive(Clone, Copy, Debug)]
pub struct ManagedContextInput<'a> {
    glyphs: &'a [ManagedContextGlyph],
    state: ManagedContextState,
    digest: ManagedGlyphSetDigest,
}
impl<'a> ManagedContextInput<'a> {
    /// Build the original all-glyph digest once, in O(N) with no allocation.
    pub fn new(glyphs: &'a [ManagedContextGlyph], state: ManagedContextState) -> Self {
        let mut digest = ManagedGlyphSetDigest::default();
        for glyph in glyphs {
            digest.add(glyph.glyph_index);
        }
        Self {
            glyphs,
            state,
            digest,
        }
    }
    /// Retain an existing source buffer filter. It must cover every current glyph
    /// for sound negative rejection; an overapproximation remains permitted.
    pub fn with_digest(
        glyphs: &'a [ManagedContextGlyph],
        state: ManagedContextState,
        digest: ManagedGlyphSetDigest,
    ) -> Self {
        Self {
            glyphs,
            state,
            digest,
        }
    }
    /// Exact original records, borrowed for this immutable generation.
    pub fn glyphs(&self) -> &'a [ManagedContextGlyph] {
        self.glyphs
    }
    /// Frozen source traversal state.
    pub fn state(&self) -> ManagedContextState {
        self.state
    }
    /// Original negative-only buffer filter.
    pub fn digest(&self) -> ManagedGlyphSetDigest {
        self.digest
    }
    fn admitted(&self, glyph: ManagedContextGlyph) -> bool {
        !self.state.restrict_to_syllable || glyph.use_syllable == self.state.lookup_syllable
    }
    fn glyph_at(&self, index: usize) -> u16 {
        let glyph = self.glyphs[index];
        if self.admitted(glyph) {
            glyph.glyph_index
        } else {
            u16::MAX
        }
    }
    fn visible(&self, glyph: ManagedContextGlyph, context: bool) -> bool {
        !default_ignorable(glyph.code_point)
            || glyph.substituted != 0
            || glyph.script_shaper == 4 && glyph.script_feature_mask != 0
            || if context {
                self.state.manual_joiners && glyph.code_point == 0x200c
            } else {
                glyph.code_point == 0x200c
                    || self.state.manual_joiners && glyph.code_point == 0x200d
            }
            || matches!(glyph.code_point,0x180b..=0x180e | 0xe0000..=0xe007f)
    }
    fn next(&self, start: usize, context: bool) -> Option<usize> {
        for (i, &glyph) in self.glyphs.iter().enumerate().skip(start) {
            if !self.admitted(glyph) {
                return None;
            }
            if self.visible(glyph, context) {
                return Some(i);
            }
        }
        None
    }
    fn previous(&self, end: usize) -> Option<usize> {
        for (i, &glyph) in self.glyphs[..end].iter().enumerate().rev() {
            if !self.admitted(glyph) {
                return None;
            }
            if self.visible(glyph, true) {
                return Some(i);
            }
        }
        None
    }
}
pub(super) fn default_ignorable(c: u32) -> bool {
    matches!(c,0x00ad | 0x034f | 0x061c | 0x115f | 0x1160 | 0x17b4 | 0x17b5 |
        0x180b..=0x180f | 0x200b..=0x200f | 0x202a..=0x202e | 0x2060..=0x206f |
        0x3164 | 0xfeff | 0xffa0 | 0xfff0..=0xfff8 | 0xfe00..=0xfe0f |
        0x1bca0..=0x1bcaf | 0x1d173..=0x1d17a | 0xe0000..=0xe0fff)
}

#[derive(Clone, Copy, Debug)]
struct CoverageMeta {
    offset: usize,
    count: u16,
    ranges: bool,
    digest: ManagedGlyphSetDigest,
}
/// Original managed coverage matcher, retaining raw list/range order and indices.
/// This has weaker admission than exact native `Coverage` and must stay separate.
#[derive(Clone, Copy, Debug)]
pub struct ManagedCoverageMatcher<'a> {
    data: &'a [u8],
    meta: CoverageMeta,
}
impl ManagedCoverageMatcher<'_> {
    /// Original three-mask negative-only filter.
    pub fn digest(&self) -> ManagedGlyphSetDigest {
        self.meta.digest
    }
    /// Exact managed membership: linear through four items, inclusive binary
    /// search above four. Unsorted input retains its original query behavior.
    pub fn contains(&self, glyph: u16) -> bool {
        if !self.meta.digest.may_have(glyph) {
            return false;
        }
        let count = usize::from(self.meta.count);
        let range = |i| {
            let p = self.meta.offset + 4 + i * if self.meta.ranges { 6 } else { 2 };
            let start = u16_at(self.data, p);
            (
                start,
                if self.meta.ranges {
                    u16_at(self.data, p + 2)
                } else {
                    start
                },
            )
        };
        if count <= 4 {
            return (0..count).any(|i| {
                let (start, end) = range(i);
                glyph >= start && glyph <= end
            });
        }
        let mut low = 0i32;
        let mut high = count as i32 - 1;
        while low <= high {
            let mid = low + ((high - low) >> 1);
            let (start, end) = range(mid as usize);
            if glyph < start {
                high = mid - 1;
            } else if glyph > end {
                low = mid + 1;
            } else {
                return true;
            }
        }
        false
    }
}
#[derive(Clone, Debug)]
pub(super) struct ContextData {
    flags: u16,
    subtables: Vec<ContextSubtable>,
    coverages: Vec<CoverageMeta>,
}
impl ContextData {
    pub(super) fn view<'a>(&'a self, data: &'a [u8]) -> ManagedContextView<'a> {
        ManagedContextView {
            data,
            context: self,
        }
    }
}
/// Prepared original managed context inventory borrowing its immutable owner.
#[derive(Clone, Copy, Debug)]
pub struct ManagedContextView<'a> {
    data: &'a [u8],
    context: &'a ContextData,
}
impl<'a> ManagedContextView<'a> {
    /// Original lookup flags. GDEF-filtered contexts retain the source conservative path.
    pub fn flags(&self) -> u16 {
        self.context.flags
    }
    /// Original ordered subtable descriptors and coverage spans.
    pub fn subtables(&self) -> &'a [ContextSubtable] {
        &self.context.subtables
    }
    /// Number of original backtrack/input/lookahead coverage occurrences.
    pub fn coverage_count(&self) -> usize {
        self.context.coverages.len()
    }
    /// Borrow one privately validated original matcher without reparsing or copying.
    pub fn coverage(&self, index: usize) -> Option<ManagedCoverageMatcher<'a>> {
        self.context
            .coverages
            .get(index)
            .copied()
            .map(|meta| ManagedCoverageMatcher {
                data: self.data,
                meta,
            })
    }
    /// Original negative-only filter. True still requires the exact lookup
    /// executor. Flags requiring GDEF skip ordered traversal only after all
    /// coverage digests intersect, exactly as in the source accelerator.
    pub fn may_match(&self, input: ManagedContextInput<'_>) -> bool {
        for sub in &self.context.subtables {
            let start = sub.coverage_offset as usize;
            let count = sub.coverage_count as usize;
            if self.context.coverages[start..start + count]
                .iter()
                .any(|c| !c.digest.may_intersect(&input.digest))
            {
                continue;
            }
            if self.context.flags & 0xff1e != 0 {
                return true;
            }
            for position in 0..input.glyphs.len() {
                if self.matches_at(input, *sub, position) {
                    return true;
                }
            }
        }
        false
    }
    fn matches_at(
        &self,
        input: ManagedContextInput<'_>,
        sub: ContextSubtable,
        position: usize,
    ) -> bool {
        let mut at = position;
        let start = sub.coverage_offset as usize;
        let back = usize::from(sub.backtrack_count);
        let end = back + usize::from(sub.input_count);
        for i in 0..back {
            let Some(previous) = input.previous(at) else {
                return false;
            };
            at = previous;
            if !self
                .coverage(start + i)
                .unwrap()
                .contains(input.glyph_at(at))
            {
                return false;
            }
        }
        at = position;
        for i in back..end {
            if i != back {
                let Some(next) = input.next(at + 1, false) else {
                    return false;
                };
                at = next;
            }
            if !self
                .coverage(start + i)
                .unwrap()
                .contains(input.glyph_at(at))
            {
                return false;
            }
        }
        for i in end..sub.coverage_count as usize {
            let Some(next) = input.next(at + 1, true) else {
                return false;
            };
            at = next;
            if !self
                .coverage(start + i)
                .unwrap()
                .contains(input.glyph_at(at))
            {
                return false;
            }
        }
        true
    }
}
/// Standalone owned original managed context, retaining the exact font/table bytes.
#[derive(Clone, Debug)]
pub struct PreparedManagedContext {
    storage: Storage,
    context: ContextData,
}
impl PreparedManagedContext {
    /// Prepare raw managed data; malformed/unsupported optional contexts are absent.
    pub fn new(
        data: impl Into<Arc<[u8]>>,
        lookup: u16,
        extension: u16,
    ) -> Result<Option<Self>, FontError> {
        Self::build(Storage::Bytes(data.into()), lookup, extension)
    }
    /// Retain the selected original face, without copying its table generation.
    pub fn from_face(
        face: SfntFace,
        tag: Tag,
        lookup: u16,
        extension: u16,
    ) -> Result<Option<Self>, FontError> {
        match Storage::from_face(face, tag) {
            Some(storage) => Self::build(storage, lookup, extension),
            None => Ok(None),
        }
    }
    fn build(storage: Storage, lookup: u16, extension: u16) -> Result<Option<Self>, FontError> {
        Ok(prepare(storage.bytes(), lookup, extension)?.map(|context| Self { storage, context }))
    }
    /// Original immutable table bytes.
    pub fn bytes(&self) -> &[u8] {
        self.storage.bytes()
    }
    /// Exact retained selected face and table identity, if available.
    pub fn source(&self) -> Option<(&SfntFace, Tag)> {
        self.storage.source()
    }
    /// Borrow the prepared original inventory and its matching operations.
    pub fn view(&self) -> ManagedContextView<'_> {
        self.context.view(self.storage.bytes())
    }
}
pub(super) fn raw_lookup(data: &[u8], index: u16) -> Option<(usize, u16, u16)> {
    if !fits(data, 8, 2) {
        return None;
    }
    let list = usize::from(u16_at(data, 8));
    if !fits(data, list, 2)
        || index >= u16_at(data, list)
        || !fits(data, list + 2 + usize::from(index) * 2, 2)
    {
        return None;
    }
    let at = list + usize::from(u16_at(data, list + 2 + usize::from(index) * 2));
    if !fits(data, at, 6) {
        return None;
    }
    Some((at, u16_at(data, at), u16_at(data, at + 4)))
}
fn coverage(data: &[u8], at: usize) -> Option<CoverageMeta> {
    if !fits(data, at, 4) {
        return None;
    }
    let format = u16_at(data, at);
    let count = u16_at(data, at + 2);
    let stride = match format {
        1 => 2,
        2 => 6,
        _ => return None,
    };
    if !fits(data, at + 4, usize::from(count) * stride) {
        return None;
    }
    let mut digest = ManagedGlyphSetDigest::default();
    for i in 0..usize::from(count) {
        let p = at + 4 + i * stride;
        let first = u16_at(data, p);
        if format == 1 {
            digest.add(first);
        } else {
            let last = u16_at(data, p + 2);
            if last < first {
                return None;
            }
            digest.add_range(first, last);
        }
    }
    Some(CoverageMeta {
        offset: at,
        count,
        ranges: format == 2,
        digest,
    })
}
pub(super) fn prepare(
    data: &[u8],
    lookup: u16,
    extension: u16,
) -> Result<Option<ContextData>, FontError> {
    if data.len() > i32::MAX as usize {
        return Err(FontError::Capacity);
    }
    let Some((at, kind, count)) = raw_lookup(data, lookup) else {
        return Ok(None);
    };
    if count == 0 {
        return Ok(None);
    }
    let mut context = ContextData {
        flags: u16_at(data, at + 2),
        subtables: vec![],
        coverages: vec![],
    };
    context
        .subtables
        .try_reserve_exact(usize::from(count))
        .map_err(|_| FontError::Capacity)?;
    for i in 0..usize::from(count) {
        let p = at + 6 + i * 2;
        if !fits(data, p, 2) {
            return Ok(None);
        }
        let mut sub = at + usize::from(u16_at(data, p));
        let mut effective = kind;
        if kind == extension {
            if !fits(data, sub, 8) || u16_at(data, sub) != 1 {
                return Ok(None);
            }
            effective = u16_at(data, sub + 2);
            let offset = u32_at(data, sub + 4) as usize;
            if offset > i32::MAX as usize {
                return Ok(None);
            }
            sub += offset;
        }
        let is_context = effective == if extension == 7 { 5 } else { 7 };
        let is_chain = effective == if extension == 7 { 6 } else { 8 };
        if (!is_context && !is_chain) || !fits(data, sub, 6) || u16_at(data, sub) != 3 {
            return Ok(None);
        }
        let mut sections = [(0usize, 0u16); 3];
        let mut cursor = sub + 2;
        if is_context {
            let count = u16_at(data, cursor);
            cursor += 4;
            if count == 0 || !fits(data, cursor, usize::from(count) * 2) {
                return Ok(None);
            }
            sections[1] = (cursor, count);
        } else {
            for (i, section) in sections.iter_mut().enumerate() {
                if !fits(data, cursor, 2) {
                    return Ok(None);
                }
                let count = u16_at(data, cursor);
                cursor += 2;
                if i == 1 && count == 0 || !fits(data, cursor, usize::from(count) * 2) {
                    return Ok(None);
                }
                *section = (cursor, count);
                cursor += usize::from(count) * 2;
            }
        }
        let start = u32::try_from(context.coverages.len()).map_err(|_| FontError::Capacity)?;
        let total: usize = sections.iter().map(|&(_, n)| usize::from(n)).sum();
        let total_u32 = total as u32;
        start.checked_add(total_u32).ok_or(FontError::Capacity)?;
        context
            .coverages
            .try_reserve(total)
            .map_err(|_| FontError::Capacity)?;
        for (p, count) in sections {
            for j in 0..usize::from(count) {
                let coverage_offset = sub + usize::from(u16_at(data, p + j * 2));
                let Some(c) = coverage(data, coverage_offset) else {
                    return Ok(None);
                };
                context.coverages.push(c);
            }
        }
        context.subtables.push(ContextSubtable {
            coverage_offset: start,
            coverage_count: total_u32,
            backtrack_count: sections[0].1,
            input_count: sections[1].1,
        });
    }
    Ok(Some(context))
}
