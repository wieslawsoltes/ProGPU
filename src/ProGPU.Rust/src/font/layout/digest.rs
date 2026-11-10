//! Original ProGPU negative-only lookup preparation, separate from execution.
use super::{Coverage, FontError, LayoutTable, Lookup, OwnedLayoutTable, fits, u16_at, u32_at};

fn range_mask(first: u16, last: u16, shift: u32) -> u64 {
    let start = u32::from(first) >> shift;
    let end = u32::from(last) >> shift;
    if end - start >= 63 {
        return u64::MAX;
    }
    let first = 1u64 << (start & 63);
    let last = 1u64 << (end & 63);
    last.wrapping_add(last.wrapping_sub(first))
        .wrapping_sub(u64::from(last < first))
}

macro_rules! digest {
    ($name:ident, $n:literal, $shifts:expr, $doc:literal) => {
        #[doc = $doc]
        #[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
        pub struct $name {
            masks: [u64; $n],
        }
        impl $name {
            /// Add one original glyph ID without allocation.
            pub fn add(&mut self, glyph: u16) {
                for (mask, shift) in self.masks.iter_mut().zip($shifts) {
                    *mask |= 1u64 << ((glyph >> shift) & 63);
                }
            }
            /// Add the inclusive original range in constant time. Reversed ranges
            /// are ignored; wrapping bit arithmetic is intentional.
            pub fn add_range(&mut self, first: u16, last: u16) {
                if last >= first {
                    for (mask, shift) in self.masks.iter_mut().zip($shifts) {
                        *mask |= range_mask(first, last, shift);
                    }
                }
            }
            /// False proves absence. True still requires exact coverage evaluation.
            pub fn may_have(&self, glyph: u16) -> bool {
                self.masks
                    .iter()
                    .zip($shifts)
                    .all(|(&mask, shift)| mask & (1u64 << ((glyph >> shift) & 63)) != 0)
            }
            /// False proves disjointness. True is an approximate result only.
            pub fn may_intersect(&self, other: &Self) -> bool {
                self.masks.iter().zip(other.masks).all(|(&a, b)| a & b != 0)
            }
            /// Original mask order, retained for immutable preparation/cache identity.
            pub fn masks(&self) -> &[u64; $n] {
                &self.masks
            }
        }
    };
}
digest!(
    GlyphSetDigest,
    5,
    [2, 4, 0, 6, 10],
    "Original native five-mask glyph-set filter. Positive results are not exact matches."
);
digest!(
    ManagedGlyphSetDigest,
    3,
    [4, 0, 6],
    "Original managed three-mask filter, kept distinct from the stronger native filter."
);
impl GlyphSetDigest {
    /// Preserve the exact original managed masks; native's extra masks can reject
    /// additional false positives, so their query results are not interchangeable.
    pub fn managed(&self) -> ManagedGlyphSetDigest {
        ManagedGlyphSetDigest {
            masks: [self.masks[1], self.masks[2], self.masks[3]],
        }
    }
}

// Digest admission deliberately differs from exact Coverage::parse: unordered
// values and noncontiguous/overlapping range indices remain an approximate set.
fn collect(data: &[u8], at: usize, digest: &mut GlyphSetDigest) -> Option<()> {
    if !fits(data, at, 4) {
        return None;
    }
    let count = usize::from(u16_at(data, at + 2));
    let stride = match u16_at(data, at) {
        1 => 2,
        2 => 6,
        _ => return None,
    };
    if !fits(data, at + 4, count * stride) {
        return None;
    }
    for index in 0..count {
        let p = at + 4 + index * stride;
        let first = u16_at(data, p);
        if stride == 2 {
            digest.add(first);
        } else {
            let last = u16_at(data, p + 2);
            if last < first {
                return None;
            }
            digest.add_range(first, last);
        }
    }
    Some(())
}
fn resolve(lookup: Lookup<'_>, index: u16, extension: u16) -> Option<(usize, u16)> {
    let data = lookup.bytes();
    let mut at = lookup.subtable_offset(index).ok()?;
    let mut kind = lookup.lookup_type();
    if !fits(data, at, 4) {
        return None;
    }
    if kind == extension {
        if !fits(data, at, 8) || u16_at(data, at) != 1 {
            return None;
        }
        kind = u16_at(data, at + 2);
        at = at.checked_add(u32_at(data, at + 4) as usize)?;
        if !fits(data, at, 4) {
            return None;
        }
    }
    Some((at, kind))
}

/// Exact capacities of a wholly validated format-3 context inventory.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ContextRequirements {
    /// One descriptor per original subtable.
    pub subtables: u32,
    /// Every backtrack, input and lookahead coverage, in original order.
    pub coverages: u32,
}
/// Original ordered span of context coverage requirements.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ContextSubtable {
    /// Starting index in the prepared coverage inventory.
    pub coverage_offset: u32,
    /// Complete backtrack/input/lookahead coverage count.
    pub coverage_count: u32,
    /// First section length, retaining source backtrack order.
    pub backtrack_count: u16,
    /// Middle section length; nonzero for every supported subtable.
    pub input_count: u16,
}
/// Validated exact coverage and its original negative-only filter.
#[derive(Clone, Copy, Debug)]
pub struct ContextCoverage<'a> {
    coverage: Coverage<'a>,
    digest: GlyphSetDigest,
}
impl Default for ContextCoverage<'_> {
    fn default() -> Self {
        Self {
            coverage: Coverage::validated(&[0, 1, 0, 0], 0),
            digest: GlyphSetDigest::default(),
        }
    }
}
impl<'a> ContextCoverage<'a> {
    /// Original immutable coverage view, with no copied glyph array.
    pub fn coverage(&self) -> Coverage<'a> {
        self.coverage
    }
    /// Prepared native filter. Use `managed()` for the distinct C# masks.
    pub fn digest(&self) -> GlyphSetDigest {
        self.digest
    }
    /// Exact membership, using the filter only to reject impossible matches.
    pub fn contains(&self, glyph: u16) -> bool {
        self.digest.may_have(glyph) && self.coverage.find(glyph).is_some()
    }
}
#[derive(Clone, Copy, Debug)]
struct StoredCoverage {
    offset: usize,
    digest: GlyphSetDigest,
}
impl StoredCoverage {
    fn view(self, data: &[u8]) -> ContextCoverage<'_> {
        ContextCoverage {
            coverage: Coverage::validated(data, self.offset),
            digest: self.digest,
        }
    }
}
fn context(
    lookup: Lookup<'_>,
    index: u16,
    extension: u16,
    mut emit: impl FnMut(StoredCoverage),
) -> Option<ContextSubtable> {
    let data = lookup.bytes();
    let (at, kind) = resolve(lookup, index, extension)?;
    let ordinary = if extension == 7 { 5 } else { 7 };
    if (kind != ordinary && kind != ordinary + 1) || !fits(data, at, 6) || u16_at(data, at) != 3 {
        return None;
    }
    let mut result = ContextSubtable::default();
    let mut add = |record: usize| -> Option<()> {
        if !fits(data, record, 2) {
            return None;
        }
        let relative = usize::from(u16_at(data, record));
        if relative == 0 {
            return None;
        }
        let offset = at.checked_add(relative)?;
        Coverage::parse(data, offset).ok()?;
        let mut digest = GlyphSetDigest::default();
        collect(data, offset, &mut digest)?;
        emit(StoredCoverage { offset, digest });
        result.coverage_count += 1;
        Some(())
    };
    if kind == ordinary {
        let count = u16_at(data, at + 2);
        let records = usize::from(u16_at(data, at + 4));
        let size = usize::from(count) * 2;
        if count == 0 || !fits(data, at + 6, size) || !fits(data, at + 6 + size, records * 4) {
            return None;
        }
        for i in 0..usize::from(count) {
            add(at + 6 + i * 2)?;
        }
        result.input_count = count;
    } else {
        let mut cursor = at + 2;
        let mut counts = [0; 3];
        for (section, count) in counts.iter_mut().enumerate() {
            if !fits(data, cursor, 2) {
                return None;
            }
            *count = u16_at(data, cursor);
            cursor += 2;
            if (section == 1 && *count == 0) || !fits(data, cursor, usize::from(*count) * 2) {
                return None;
            }
            for i in 0..usize::from(*count) {
                add(cursor + i * 2)?;
            }
            cursor += usize::from(*count) * 2;
        }
        if !fits(data, cursor, 2) || !fits(data, cursor + 2, usize::from(u16_at(data, cursor)) * 4)
        {
            return None;
        }
        result.backtrack_count = counts[0];
        result.input_count = counts[1];
    }
    Some(result)
}
impl<'a> LayoutTable<'a> {
    /// Collect the original leading-coverage digest, resolving one extension.
    /// This retains the raw offset-2 interpretation, not a semantic check that
    /// the lookup has a leading coverage. Context and executor eligibility must
    /// be established independently before using this as a rejection filter.
    /// Invalid lookup headers are errors. Unsupported/malformed payloads disable
    /// the optional accelerator (`None`) without approving lookup execution.
    pub fn lookup_digest(
        &self,
        index: u16,
        extension: u16,
    ) -> Result<Option<GlyphSetDigest>, FontError> {
        let lookup = self.lookup(index)?;
        if lookup.subtable_count() == 0 {
            return Ok(None);
        }
        let mut digest = GlyphSetDigest::default();
        for i in 0..lookup.subtable_count() {
            let Some((at, _)) = resolve(lookup, i, extension) else {
                return Ok(None);
            };
            let Some(coverage) = at.checked_add(usize::from(u16_at(self.bytes(), at + 2))) else {
                return Ok(None);
            };
            if collect(self.bytes(), coverage, &mut digest).is_none() {
                return Ok(None);
            }
        }
        Ok(Some(digest))
    }
    /// Retain an exact leading coverage only for the original supported single
    /// subtable shapes. Format-3 contexts use their independent inventory instead.
    /// The validated lookup is returned even when no exact coverage is available.
    pub fn single_subtable_coverage(
        &self,
        index: u16,
        extension: u16,
    ) -> Result<(Lookup<'a>, Option<Coverage<'a>>), FontError> {
        let lookup = self.lookup(index)?;
        let find = || {
            if lookup.subtable_count() != 1 {
                return None;
            }
            let (at, kind) = resolve(lookup, 0, extension)?;
            if !matches!(extension, 7 | 9) || kind == 0 || kind > 8 || kind == extension {
                return None;
            }
            let is_context = if extension == 7 {
                matches!(kind, 5 | 6)
            } else {
                matches!(kind, 7 | 8)
            };
            if is_context && u16_at(self.bytes(), at) == 3 {
                return None;
            }
            let relative = usize::from(u16_at(self.bytes(), at + 2));
            if relative == 0 {
                return None;
            }
            Coverage::parse(self.bytes(), at.checked_add(relative)?).ok()
        };
        Ok((lookup, find()))
    }
    /// Validate every context coverage and lookup-record extent before reporting
    /// capacities. `None` retains the original unsupported-accelerator route.
    pub fn context_requirements(
        &self,
        index: u16,
        extension: u16,
    ) -> Result<Option<ContextRequirements>, FontError> {
        let lookup = self.lookup(index)?;
        if lookup.subtable_count() == 0 {
            return Ok(None);
        }
        let mut coverages = 0u32;
        for i in 0..lookup.subtable_count() {
            let Some(s) = context(lookup, i, extension, |_| {}) else {
                return Ok(None);
            };
            let Some(count) = coverages.checked_add(s.coverage_count) else {
                return Ok(None);
            };
            coverages = count;
        }
        Ok(Some(ContextRequirements {
            subtables: u32::from(lookup.subtable_count()),
            coverages,
        }))
    }
    /// Build original descriptors in caller storage without allocation. Whole-
    /// inventory validation and both capacities precede mutation. Unused tails
    /// remain untouched. Success returns the exact raw lookup flags.
    pub fn build_context_into(
        &self,
        index: u16,
        extension: u16,
        subtables: &mut [ContextSubtable],
        coverages: &mut [ContextCoverage<'a>],
    ) -> Result<Option<u16>, FontError> {
        let Some(r) = self.context_requirements(index, extension)? else {
            return Ok(None);
        };
        if subtables.len() < r.subtables as usize || coverages.len() < r.coverages as usize {
            return Err(FontError::Capacity);
        }
        let lookup = self.lookup(index)?;
        let mut cursor = 0usize;
        for (index, slot) in subtables[..r.subtables as usize].iter_mut().enumerate() {
            let start = cursor;
            let mut s = context(lookup, index as u16, extension, |v| {
                coverages[cursor] = v.view(self.bytes());
                cursor += 1;
            })
            .expect("immutable context inventory passed preflight");
            s.coverage_offset = start as u32;
            *slot = s;
        }
        Ok(Some(lookup.flags()))
    }
}
/// Owned immutable preparation retaining the exact original layout/font owner.
/// Coverage queries never reparse or copy font bytes. Ordered glyph traversal,
/// GDEF filtering and actual lookup execution remain separate consumers.
#[derive(Clone, Debug)]
pub struct PreparedContext {
    table: OwnedLayoutTable,
    subtables: Vec<ContextSubtable>,
    coverages: Vec<StoredCoverage>,
    flags: u16,
}
impl PreparedContext {
    /// Build once, retaining the selected source generation after caller disposal.
    pub fn new(
        table: OwnedLayoutTable,
        index: u16,
        extension: u16,
    ) -> Result<Option<Self>, FontError> {
        let view = table.view();
        let Some(r) = view.context_requirements(index, extension)? else {
            return Ok(None);
        };
        let mut subtables = Vec::new();
        let mut coverages = Vec::new();
        subtables
            .try_reserve_exact(r.subtables as usize)
            .map_err(|_| FontError::Capacity)?;
        coverages
            .try_reserve_exact(r.coverages as usize)
            .map_err(|_| FontError::Capacity)?;
        let lookup = view.lookup(index)?;
        for index in 0..lookup.subtable_count() {
            let start = coverages.len();
            let mut s = context(lookup, index, extension, |v| coverages.push(v))
                .expect("immutable context inventory passed preflight");
            s.coverage_offset = start as u32;
            subtables.push(s);
        }
        let flags = lookup.flags();
        Ok(Some(Self {
            table,
            subtables,
            coverages,
            flags,
        }))
    }
    /// Exact captured source owner and its optional selected font generation.
    pub fn table(&self) -> &OwnedLayoutTable {
        &self.table
    }
    /// Original ordered context spans.
    pub fn subtables(&self) -> &[ContextSubtable] {
        &self.subtables
    }
    /// Number of prepared coverage views.
    pub fn coverage_count(&self) -> usize {
        self.coverages.len()
    }
    /// Borrow one previously validated coverage in O(1).
    pub fn coverage(&self, index: usize) -> Option<ContextCoverage<'_>> {
        self.coverages
            .get(index)
            .map(|v| v.view(self.table.view().bytes()))
    }
    /// Raw source lookup flags; no GDEF filtering is inferred here.
    pub fn lookup_flags(&self) -> u16 {
        self.flags
    }
}
