//! Current original C# raw GSUB semantics. Native strict admission and packed
//! metadata are independent; malformed managed reads retain typed failure state.
use super::managed_context::{default_ignorable, raw_lookup};
use super::{
    Arc, FontError, ManagedContextState, SfntFace, Storage, Tag, fits, is_gdef_blocklisted, u16_at,
    u32_at,
};
use crate::text::ManagedShapingGlyph;
mod buffer;
mod context;
mod dispatch;
pub use buffer::{ManagedGlyphBuffer, ManagedGlyphState};
pub use dispatch::ManagedSubstitutionPlan;
type Applied = Result<bool, FontError>;

/// Per-operation work limit for untrusted managed lookup graphs. The original
/// 64-level depth rule remains independent: branching recursion can still require
/// exponential work. Exhaustion returns Capacity, retaining preceding writes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedGsubLimits {
    /// Maximum nested lookup attempts, including missing or depth-limited calls.
    pub max_lookup_calls: u32,
}
impl Default for ManagedGsubLimits {
    fn default() -> Self {
        Self {
            max_lookup_calls: 65_536,
        }
    }
}

/// Immutable original managed GSUB bytes or selected font generation. Construction
/// retains weak source admission: each operation makes its own ordered read checks.
#[derive(Clone, Debug)]
pub struct ManagedGsubTable {
    storage: Storage,
}
impl ManagedGsubTable {
    /// Retain exact raw bytes without applying the stricter native header parser.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, FontError> {
        let storage = Storage::Bytes(bytes.into());
        if storage.bytes().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Self { storage })
    }
    /// Retain an actual selected GSUB generation; missing data remains None.
    pub fn from_face(face: SfntFace) -> Result<Option<Self>, FontError> {
        let Some(storage) = Storage::from_face(face, Tag::new(*b"GSUB")) else {
            return Ok(None);
        };
        if storage.bytes().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Some(Self { storage }))
    }
    /// Exact raw original table bytes.
    pub fn bytes(&self) -> &[u8] {
        self.storage.bytes()
    }
    /// Actual selected font generation, if constructed from a face.
    pub fn source(&self) -> Option<&SfntFace> {
        self.storage.source().map(|(face, _)| face)
    }
    /// Execute the current managed nested-lookup contract at one live position.
    /// Type 3 selects its original first alternate; reverse and feature-value
    /// dispatch are separate source operations. Later source errors retain earlier
    /// record/state writes. Invalid source reads become InvalidFace; invalid live
    /// record accesses become InvalidGlyph. Private depth/mark state always restores.
    /// Illegal extension chains have a separate 64-hop safety bound.
    pub fn apply_lookup_at(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        index: u16,
        position: usize,
    ) -> Applied {
        self.apply_lookup_at_with_limits(buffer, index, position, ManagedGsubLimits::default())
    }
    /// Execute with an explicit nested-call budget. Budget errors restore private
    /// depth and mark coverage while retaining original earlier record writes.
    pub fn apply_lookup_at_with_limits(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        index: u16,
        position: usize,
        limits: ManagedGsubLimits,
    ) -> Applied {
        if position >= buffer.glyphs.len() {
            return Err(FontError::InvalidArgument);
        }
        Executor {
            data: self.bytes(),
            buffer,
            remaining_lookups: limits.max_lookup_calls,
        }
        .lookup(index, position)
    }
    /// Execute the original reverse-chaining subtable contract at a live position.
    /// The offset is into this exact table generation. A complete staged shaper
    /// selects lookup order, feature admission and active syllable separately.
    pub fn apply_reverse_at(
        &self,
        buffer: &mut ManagedGlyphBuffer,
        offset: usize,
        position: usize,
        flags: u16,
    ) -> Applied {
        if position >= buffer.glyphs.len() || offset > i32::MAX as usize {
            return Err(FontError::InvalidArgument);
        }
        Executor {
            data: self.bytes(),
            buffer,
            remaining_lookups: 0,
        }
        .reverse(offset as i64, position, flags)
    }
}
fn fits_at(data: &[u8], at: i64, n: usize) -> bool {
    at >= 0 && at <= i64::from(i32::MAX) && fits(data, at as usize, n)
}
fn word(data: &[u8], at: i64) -> Result<u16, FontError> {
    if fits_at(data, at, 2) {
        Ok(u16_at(data, at as usize))
    } else {
        Err(FontError::InvalidFace)
    }
}
fn coverage_at(data: &[u8], at: i64, glyph: u16) -> Result<Option<u32>, FontError> {
    if !fits_at(data, at, 4) {
        return Ok(None);
    }
    let format = word(data, at)?;
    let count = i32::from(word(data, at + 2)?);
    if format != 1 && format != 2 {
        return Ok(None);
    }
    if format == 2 && !fits_at(data, at + 4, count as usize * 6) {
        return Ok(None);
    }
    let (mut low, mut high) = (0, count - 1);
    while low <= high {
        let middle = (low + high) >> 1;
        let p = at + 4 + i64::from(middle) * if format == 1 { 2 } else { 6 };
        let start = word(data, p)?;
        let end = if format == 1 {
            start
        } else {
            word(data, p + 2)?
        };
        if glyph < start {
            high = middle - 1;
        } else if glyph > end {
            low = middle + 1;
        } else {
            return Ok(Some(if format == 1 {
                middle as u32
            } else {
                u32::from(word(data, p + 4)?) + u32::from(glyph) - u32::from(start)
            }));
        }
    }
    Ok(None)
}
fn class_at(data: &[u8], at: i64, glyph: u16) -> u16 {
    if !fits_at(data, at, 4) {
        return 0;
    }
    let format = word(data, at).unwrap();
    if format == 1 {
        if !fits_at(data, at, 6) {
            return 0;
        }
        let start = word(data, at + 2).unwrap();
        let count = word(data, at + 4).unwrap();
        let index = i32::from(glyph) - i32::from(start);
        let p = at + 6 + i64::from(index) * 2;
        return if index >= 0 && index < i32::from(count) && fits_at(data, p, 2) {
            word(data, p).unwrap()
        } else {
            0
        };
    }
    if format == 2 {
        let count = i32::from(word(data, at + 2).unwrap());
        if !fits_at(data, at + 4, count as usize * 6) {
            return 0;
        }
        let (mut low, mut high) = (0, count - 1);
        while low <= high {
            let mid = (low + high) >> 1;
            let p = at + 4 + i64::from(mid) * 6;
            let start = word(data, p).unwrap();
            let end = word(data, p + 2).unwrap();
            if glyph < start {
                high = mid - 1;
            } else if glyph > end {
                low = mid + 1;
            } else {
                return word(data, p + 4).unwrap();
            }
        }
    }
    0
}
struct Executor<'d, 'b> {
    data: &'d [u8],
    buffer: &'b mut ManagedGlyphBuffer,
    remaining_lookups: u32,
}
impl Executor<'_, '_> {
    fn read(&self, at: i64) -> Result<u16, FontError> {
        word(self.data, at)
    }
    fn fits(&self, at: i64, n: usize) -> bool {
        fits_at(self.data, at, n)
    }
    fn coverage(
        &self,
        parent: i64,
        pointer: i64,
        position: usize,
    ) -> Result<Option<u32>, FontError> {
        coverage_at(
            self.data,
            parent + i64::from(self.read(pointer)?),
            self.buffer.glyph_at(position)?,
        )
    }
    fn lookup(&mut self, index: u16, position: usize) -> Applied {
        if self.remaining_lookups == 0 {
            return Err(FontError::Capacity);
        }
        self.remaining_lookups -= 1;
        let Some((at, kind, count)) = raw_lookup(self.data, index) else {
            return Ok(false);
        };
        if self.buffer.nested_depth >= 64 {
            return Ok(false);
        }
        self.buffer.nested_depth += 1;
        let at = at as i64;
        let flags = self.read(at + 2)?;
        let set_at = at + 6 + i64::from(count) * 2;
        let set = if flags & 16 != 0 && self.fits(set_at, 2) {
            self.read(set_at)?
        } else {
            u16::MAX
        };
        let previous = self.buffer.mark_filtering_coverage;
        self.buffer.select_mark_set(set);
        let result = (|| {
            for i in 0..count {
                let sub = at + i64::from(self.read(at + 6 + i64::from(i) * 2)?);
                if self.subtable(kind, sub, position, flags)? {
                    return Ok(true);
                }
            }
            Ok(false)
        })();
        self.buffer.nested_depth -= 1;
        self.buffer.mark_filtering_coverage = previous;
        result
    }
    fn subtable(&mut self, mut kind: u16, mut offset: i64, position: usize, flags: u16) -> Applied {
        let mut extensions = 0;
        let format = loop {
            if !self.fits(offset, 6) {
                return Ok(false);
            }
            let format = self.read(offset)?;
            if kind != 7 {
                break format;
            }
            if format != 1 || !self.fits(offset, 8) {
                return Ok(false);
            }
            if extensions >= 64 {
                return Err(FontError::InvalidFace);
            }
            extensions += 1;
            kind = self.read(offset + 2)?;
            let relative = u32_at(self.data, offset as usize + 4);
            if relative > i32::MAX as u32 {
                return Ok(false);
            }
            offset += i64::from(relative);
        };
        if kind == 5 || kind == 6 {
            return self.context(offset, position, flags, kind == 6, format);
        }
        let Some(index) = self.coverage(offset, offset + 2, position)? else {
            return Ok(false);
        };
        if kind == 1 && format == 1 {
            let id = self
                .buffer
                .glyph_at(position)?
                .wrapping_add(self.read(offset + 4)?);
            self.buffer.replace(position, id)?;
            return Ok(true);
        }
        if kind == 1 && format == 2 {
            let count = self.read(offset + 4)?;
            let p = offset + 6 + i64::from(index) * 2;
            if index < u32::from(count) && self.fits(p, 2) {
                self.buffer.replace(position, self.read(p)?)?;
                return Ok(true);
            }
        }
        if (2..=4).contains(&kind) && format == 1 {
            let count = self.read(offset + 4)?;
            let p = offset + 6 + i64::from(index) * 2;
            if index >= u32::from(count) || !self.fits(p, 2) {
                return Ok(false);
            }
            let set = offset + i64::from(self.read(p)?);
            if kind == 2 {
                if !self.fits(set, 2) {
                    return Ok(false);
                }
                let n = usize::from(self.read(set)?);
                if !self.fits(set + 2, n * 2) {
                    return Ok(false);
                }
                let mut replacement = Vec::new();
                replacement
                    .try_reserve_exact(n)
                    .map_err(|_| FontError::Capacity)?;
                for i in 0..n {
                    replacement.push(self.read(set + 2 + i as i64 * 2)?);
                }
                self.buffer.replace_multiple(position, &replacement)?;
                return Ok(true);
            }
            if kind == 3 {
                if !self.fits(set, 4) || self.read(set)? == 0 {
                    return Ok(false);
                }
                self.buffer.replace(position, self.read(set + 2)?)?;
                return Ok(true);
            }
            if !self.fits(set, 2) {
                return Ok(false);
            }
            let n = self.read(set)?;
            for i in 0..n {
                let p = set + 2 + i64::from(i) * 2;
                if !self.fits(p, 2) {
                    break;
                }
                let ligature = set + i64::from(self.read(p)?);
                if !self.fits(ligature, 4) {
                    continue;
                }
                let id = self.read(ligature)?;
                let n = usize::from(self.read(ligature + 2)?);
                if n == 0 || !self.fits(ligature + 4, (n - 1) * 2) {
                    continue;
                }
                let mut inline = [0usize; 64];
                let mut heap = Vec::new();
                let indices = if n <= 64 {
                    &mut inline[..n]
                } else {
                    heap.try_reserve_exact(n).map_err(|_| FontError::Capacity)?;
                    heap.resize(n, 0);
                    heap.as_mut_slice()
                };
                indices[0] = position;
                let mut candidate = position;
                let mut matched = true;
                for (component, slot) in indices.iter_mut().enumerate().skip(1) {
                    let expected = self.read(ligature + 4 + (component - 1) as i64 * 2)?;
                    let Some(next) = self.buffer.next_component(candidate + 1, flags, expected)?
                    else {
                        matched = false;
                        break;
                    };
                    candidate = next;
                    if self.buffer.glyph_at(candidate)? != expected {
                        matched = false;
                        break;
                    }
                    *slot = candidate;
                }
                if !matched {
                    continue;
                }
                self.buffer.replace_ligature(indices, id)?;
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn reverse(&mut self, offset: i64, position: usize, flags: u16) -> Applied {
        if !self.fits(offset, 6) || self.read(offset)? != 1 {
            return Ok(false);
        }
        let Some(index) = self.coverage(offset, offset + 2, position)? else {
            return Ok(false);
        };
        let mut cursor = offset + 4;
        let back = self.read(cursor)?;
        cursor += 2;
        if !self.fits(cursor, usize::from(back) * 2) {
            return Ok(false);
        }
        let mut matched = position;
        let mut start = position;
        let mut end = position + 1;
        for i in 0..back {
            let Some(previous) = self.buffer.previous(matched, flags)? else {
                return Ok(false);
            };
            matched = previous;
            start = start.min(matched);
            if self
                .coverage(offset, cursor + i64::from(i) * 2, matched)?
                .is_none()
            {
                return Ok(false);
            }
        }
        cursor += i64::from(back) * 2;
        if !self.fits(cursor, 2) {
            return Ok(false);
        }
        let look = self.read(cursor)?;
        cursor += 2;
        if !self.fits(cursor, usize::from(look) * 2) {
            return Ok(false);
        }
        matched = position;
        for i in 0..look {
            let Some(next) = self.buffer.next(matched + 1, flags, true)? else {
                return Ok(false);
            };
            matched = next;
            end = end.max(matched + 1);
            if self
                .coverage(offset, cursor + i64::from(i) * 2, matched)?
                .is_none()
            {
                return Ok(false);
            }
        }
        cursor += i64::from(look) * 2;
        if !self.fits(cursor, 2) {
            return Ok(false);
        }
        let count = self.read(cursor)?;
        cursor += 2;
        if index >= u32::from(count) || !self.fits(cursor + i64::from(index) * 2, 2) {
            return Ok(false);
        }
        self.buffer.dependency(start, end);
        self.buffer
            .replace(position, self.read(cursor + i64::from(index) * 2)?)?;
        Ok(true)
    }
}
