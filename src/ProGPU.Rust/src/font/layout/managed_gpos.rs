//! Original managed raw GPOS, independently preserving the source's weak reads,
//! complete records, ordered filtering and per-operation saturation.
use super::managed_context::{default_ignorable, raw_lookup};
use super::managed_gsub::{class_at, coverage_at, fits_at, word};
use super::{Arc, FontError, SfntFace, Storage, Tag, fits, is_gdef_blocklisted, u16_at, u32_at};
use crate::text::ManagedShapingGlyph;
mod buffer;
mod context;
mod primitive;
pub use buffer::{ManagedPositionBuffer, ManagedPositionDirection};
use context::Records;
type Applied = Result<bool, FontError>;
/// Explicit Rust bound for untrusted contextual call graphs. The original GPOS
/// executor has no recursion limit; an explicit stack preserves deep finite
/// source traversals, while this independent work bound rejects cyclic graphs.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedGposLimits {
    /// Lookup attempts per root call, including absent and ineligible lookups.
    pub max_lookup_calls: u32,
}
impl Default for ManagedGposLimits {
    fn default() -> Self {
        Self {
            max_lookup_calls: 65_536,
        }
    }
}
/// Immutable current managed raw GPOS generation. Raw lookup execution is separate
/// from feature dispatch, variation adjustments, preparation and attachment resolution.
#[derive(Clone, Debug)]
pub struct ManagedGposTable {
    storage: Storage,
}
impl ManagedGposTable {
    /// Retain exact raw bytes, preserving ordered source admission at execution.
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, FontError> {
        let storage = Storage::Bytes(bytes.into());
        if storage.bytes().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Self { storage })
    }
    /// Retain an actual selected font generation. Missing GPOS remains None.
    pub fn from_face(face: SfntFace) -> Result<Option<Self>, FontError> {
        let Some(storage) = Storage::from_face(face, Tag::new(*b"GPOS")) else {
            return Ok(None);
        };
        if storage.bytes().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        Ok(Some(Self { storage }))
    }
    /// Exact original bytes.
    pub fn bytes(&self) -> &[u8] {
        self.storage.bytes()
    }
    /// Actual selected source generation, when created from a font.
    pub fn source(&self) -> Option<&SfntFace> {
        self.storage.source().map(|(f, _)| f)
    }
    /// Apply all nine raw lookup types at one position. Out-of-range positions
    /// retain the original no-match behavior. Private mark state restores on all
    /// returns; earlier source writes and dependencies survive later errors.
    pub fn apply_lookup_at(
        &self,
        buffer: &mut ManagedPositionBuffer,
        index: u16,
        position: usize,
    ) -> Applied {
        self.apply_lookup_at_with_limits(buffer, index, position, ManagedGposLimits::default())
    }
    /// Execute with an independent per-root work bound and an explicit call stack.
    pub fn apply_lookup_at_with_limits(
        &self,
        buffer: &mut ManagedPositionBuffer,
        index: u16,
        position: usize,
        limits: ManagedGposLimits,
    ) -> Applied {
        let saved = buffer.mark_coverage;
        let result = Executor {
            data: self.bytes(),
            buffer,
            remaining: limits.max_lookup_calls,
        }
        .lookup(index, position);
        buffer.mark_coverage = saved;
        result
    }
}
struct Executor<'d, 'b> {
    data: &'d [u8],
    buffer: &'b mut ManagedPositionBuffer,
    remaining: u32,
}
struct Frame {
    at: i64,
    kind: u16,
    count: u16,
    next: u16,
    flags: u16,
    position: usize,
    previous: i32,
    records: Option<(Records, u16, bool)>,
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
            self.buffer.glyph(position),
        )
    }
    fn enter(&mut self, index: u16, position: usize) -> Result<Option<Frame>, FontError> {
        if self.remaining == 0 {
            return Err(FontError::Capacity);
        }
        self.remaining -= 1;
        if position >= self.buffer.glyphs.len() {
            return Ok(None);
        }
        let Some((at, kind, count)) = raw_lookup(self.data, index) else {
            return Ok(None);
        };
        let at = at as i64;
        let flags = self.read(at + 2)?;
        let pointer = at + 6 + i64::from(count) * 2;
        let set = if flags & 16 != 0 && self.fits(pointer, 2) {
            self.read(pointer)?
        } else {
            u16::MAX
        };
        let previous = self.buffer.mark_coverage;
        self.buffer.select_mark_set(set);
        if !self.buffer.eligible(position, flags)? {
            self.buffer.mark_coverage = previous;
            return Ok(None);
        }
        Ok(Some(Frame {
            at,
            kind,
            count,
            next: 0,
            flags,
            position,
            previous,
            records: None,
        }))
    }
    fn lookup(&mut self, index: u16, position: usize) -> Applied {
        let Some(mut frame) = self.enter(index, position)? else {
            return Ok(false);
        };
        // A leaf lookup keeps its one frame on the stack. Only contextual descent
        // needs dynamic storage; it is bounded by the explicit call budget.
        let mut parents: Vec<Frame> = Vec::new();
        loop {
            let mut done = None;
            if let Some((records, next, applied)) = &mut frame.records {
                if *next == records.count {
                    if *applied || records.count == 0 {
                        done = Some(true);
                    } else {
                        frame.records = None;
                    }
                } else {
                    let p = records.offset + i64::from(*next) * 4;
                    let sequence = self.read(p)?;
                    let lookup = self.read(p + 2)?;
                    *next += 1;
                    let mut target = Some(frame.position);
                    for _ in 0..sequence {
                        let Some(i) = target else {
                            break;
                        };
                        target = self.buffer.next(i + 1, frame.flags)?;
                    }
                    if let Some(target) = target
                        && let Some(child) = self.enter(lookup, target)?
                    {
                        parents.try_reserve(1).map_err(|_| FontError::Capacity)?;
                        parents.push(frame);
                        frame = child;
                    }
                    continue;
                }
            } else if frame.next >= frame.count {
                done = Some(false);
            } else {
                let pointer = frame.at + 6 + i64::from(frame.next) * 2;
                frame.next += 1;
                if !self.fits(pointer, 2) {
                    done = Some(false);
                } else {
                    let mut at = frame.at + i64::from(self.read(pointer)?);
                    let mut kind = frame.kind;
                    if kind == 9 {
                        if !self.fits(at, 8) || self.read(at)? != 1 {
                            continue;
                        }
                        kind = self.read(at + 2)?;
                        let relative = u32_at(self.data, at as usize + 4);
                        if relative > i32::MAX as u32 {
                            continue;
                        }
                        at += i64::from(relative);
                    }
                    if kind == 7 || kind == 8 {
                        if self.fits(at, if kind == 7 { 6 } else { 2 })
                            && let Some(records) = self.context(
                                at,
                                frame.position,
                                frame.flags,
                                kind == 8,
                                self.read(at)?,
                            )?
                        {
                            self.buffer.dependency(records.start, records.end);
                            frame.records = Some((records, 0, false));
                        }
                    } else if self.primitive(kind, at, frame.position, frame.flags)? {
                        done = Some(true);
                    }
                }
            }
            if let Some(applied) = done {
                self.buffer.mark_coverage = frame.previous;
                let Some(mut parent) = parents.pop() else {
                    return Ok(applied);
                };
                if let Some((_, _, matched)) = &mut parent.records {
                    *matched |= applied;
                }
                frame = parent;
            }
        }
    }
}
