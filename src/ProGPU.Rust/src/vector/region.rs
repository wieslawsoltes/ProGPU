//! Original CPU integer regions: lazy canonical bands, exact Boolean inventories
//! and bounded retained scratch. Source path scan conversion is independent of GPU paint.
use super::{PathFigure, PathGeometry, PathSegment, PathSegmentKind};
use crate::Error;
use std::{
    cell::{Ref, RefCell},
    collections::HashMap,
};
mod scan;
pub use scan::{RegionExecution, RegionScanMetrics};

/// Original left/top/right/bottom integer rectangle. Width/height use the source's
/// explicit wrapping arithmetic; validity is separate from constructor admission.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct RegionRect(pub [i32; 4]);
impl RegionRect {
    /// Original wrapping extent.
    pub fn width(self) -> i32 {
        self.0[2].wrapping_sub(self.0[0])
    }
    /// Original wrapping extent.
    pub fn height(self) -> i32 {
        self.0[3].wrapping_sub(self.0[1])
    }
    /// Original positive extent policy.
    pub fn is_valid(self) -> bool {
        self.width() > 0 && self.height() > 0
    }
    /// Original half-open integer point admission.
    pub fn contains(self, x: i32, y: i32) -> bool {
        x >= self.0[0] && x < self.0[2] && y >= self.0[1] && y < self.0[3]
    }
    fn intersection(self, other: Self) -> Self {
        let a = self.0;
        let b = other.0;
        Self([
            a[0].max(b[0]),
            a[1].max(b[1]),
            a[2].min(b[2]),
            a[3].min(b[3]),
        ])
    }
    fn covers(self, other: Self) -> bool {
        let a = self.0;
        let b = other.0;
        other.is_valid() && a[0] <= b[0] && a[1] <= b[1] && a[2] >= b[2] && a[3] >= b[3]
    }
    fn translated(self, x: i32, y: i32) -> Self {
        use wide::i32x4;
        Self((i32x4::from(self.0) + i32x4::from([x, y, x, y])).to_array())
    }
}
/// Original six region operations, independent of path Boolean wire enums.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RegionOperation {
    /// Source minus operand.
    Difference,
    /// Common covered pixels.
    Intersect,
    /// Covered by either operand.
    Union,
    /// Covered by exactly one operand.
    Xor,
    /// Operand minus source.
    ReverseDifference,
    /// Replace the original source inventory.
    Replace,
}
#[derive(Default)]
struct Storage {
    ys: Vec<i32>,
    intervals: Vec<[i32; 2]>,
    merged: Vec<[i32; 2]>,
    rects: Vec<RegionRect>,
    previous: HashMap<[i32; 2], usize>,
    current: HashMap<[i32; 2], usize>,
}
impl Storage {
    fn clear(&mut self) {
        self.ys.clear();
        self.intervals.clear();
        self.merged.clear();
        self.rects.clear();
        self.previous.clear();
        self.current.clear();
    }
    fn retainable(&self) -> bool {
        self.ys.capacity() <= 512
            && self.intervals.capacity() <= 1024
            && self.merged.capacity() <= 1024
            && self.rects.capacity() <= 1024
            && self.previous.capacity() <= 2048
            && self.current.capacity() <= 2048
    }
}
thread_local! {
    static RECT_POOL: RefCell<Option<Vec<RegionRect>>> = const { RefCell::new(None) };
    static STORAGE_POOL: RefCell<Option<Storage>> = const { RefCell::new(None) };
}
struct State {
    rects: Vec<RegionRect>,
    bounds: RegionRect,
    normalized: bool,
    storage: Option<Storage>,
}
impl Default for State {
    fn default() -> Self {
        Self {
            rects: RECT_POOL
                .try_with(|pool| pool.borrow_mut().take())
                .ok()
                .flatten()
                .unwrap_or_default(),
            bounds: RegionRect::default(),
            normalized: true,
            storage: None,
        }
    }
}
impl Drop for State {
    fn drop(&mut self) {
        self.rects.clear();
        if self.rects.capacity() <= 1024 {
            let _ = RECT_POOL.try_with(|pool| {
                if let Ok(mut pool) = pool.try_borrow_mut()
                    && pool.is_none()
                {
                    *pool = Some(std::mem::take(&mut self.rects));
                }
            });
        }
        if let Some(mut storage) = self.storage.take() {
            storage.clear();
            if storage.retainable() {
                let _ = STORAGE_POOL.try_with(|pool| {
                    if let Ok(mut pool) = pool.try_borrow_mut()
                        && pool.is_none()
                    {
                        *pool = Some(storage);
                    }
                });
            }
        }
    }
}
impl State {
    fn update_bounds(&mut self) {
        let Some(first) = self.rects.first().copied() else {
            self.bounds = RegionRect::default();
            return;
        };
        self.bounds = first;
        for index in 1..self.rects.len() {
            self.union_bounds(self.rects[index]);
        }
    }
    fn union_bounds(&mut self, rect: RegionRect) {
        if self.rects.len() == 1 {
            self.bounds = rect;
            return;
        }
        let a = self.bounds.0;
        let b = rect.0;
        self.bounds = RegionRect([
            a[0].min(b[0]),
            a[1].min(b[1]),
            a[2].max(b[2]),
            a[3].max(b[3]),
        ]);
    }
    fn normalize(&mut self) -> Result<(), Error> {
        if self.normalized {
            return Ok(());
        }
        if self.rects.len() <= 1 {
            self.normalized = true;
            return Ok(());
        }
        let mut storage = self.storage.take().unwrap_or_else(|| {
            STORAGE_POOL
                .try_with(|pool| pool.borrow_mut().take())
                .ok()
                .flatten()
                .unwrap_or_default()
        });
        let result = normalize(&self.rects, &mut storage).and_then(|()| {
            self.rects
                .try_reserve(storage.rects.len().saturating_sub(self.rects.len()))
                .map_err(|_| Error::Capacity)?;
            self.rects.clear();
            self.rects.extend_from_slice(&storage.rects);
            self.normalized = true;
            Ok(())
        });
        self.storage = Some(storage);
        result
    }
}
/// Owned original region. Read queries normalize a private cache; iterator
/// snapshots stay independent of later mutations, source retirement and pooling.
#[derive(Default)]
pub struct Region {
    state: RefCell<State>,
}
impl std::fmt::Debug for Region {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let s = self.state.borrow();
        f.debug_struct("Region")
            .field("bounds", &s.bounds)
            .field("recorded_rectangles", &s.rects.len())
            .field("normalized", &s.normalized)
            .finish()
    }
}
impl Region {
    /// Original empty inventory, borrowing bounded per-thread storage when present.
    pub fn new() -> Self {
        Self::default()
    }
    /// Original positive rectangle constructor.
    pub fn from_rect(rect: RegionRect) -> Result<Self, Error> {
        let mut r = Self::new();
        r.set_rect(rect)?;
        Ok(r)
    }
    /// Copy complete canonical source bands, preserving their exact order.
    pub fn try_clone(&self) -> Result<Self, Error> {
        let state = self.normalized()?;
        let mut out = Self::new();
        let target = out.state.get_mut();
        target
            .rects
            .try_reserve_exact(state.rects.len())
            .map_err(|_| Error::Capacity)?;
        target.rects.extend_from_slice(&state.rects);
        target.bounds = state.bounds;
        Ok(out)
    }
    fn normalized(&self) -> Result<Ref<'_, State>, Error> {
        self.state.borrow_mut().normalize()?;
        Ok(self.state.borrow())
    }
    /// Actual empty source inventory (no normalization allocation).
    pub fn is_empty(&self) -> bool {
        self.state.borrow().rects.is_empty()
    }
    /// Exact current cached source bounds.
    pub fn bounds(&self) -> RegionRect {
        self.state.borrow().bounds
    }
    /// Whether canonical coverage is one rectangle.
    pub fn is_rect(&self) -> Result<bool, Error> {
        Ok(self.normalized()?.rects.len() == 1)
    }
    /// Whether canonical coverage has multiple rectangles.
    pub fn is_complex(&self) -> Result<bool, Error> {
        Ok(self.normalized()?.rects.len() > 1)
    }
    /// Clear state while retaining the original reusable storage.
    pub fn clear(&mut self) {
        let s = self.state.get_mut();
        s.rects.clear();
        s.bounds = RegionRect::default();
        s.normalized = true;
    }
    /// Original single-rectangle assignment and nonempty result.
    pub fn set_rect(&mut self, rect: RegionRect) -> Result<bool, Error> {
        let s = self.state.get_mut();
        if rect.is_valid() {
            s.rects.try_reserve(1).map_err(|_| Error::Capacity)?;
        }
        s.rects.clear();
        if rect.is_valid() {
            s.rects.push(rect);
        }
        s.normalized = true;
        s.update_bounds();
        Ok(!s.rects.is_empty())
    }
    /// Whole bulk assignment, normalized once before publication.
    pub fn set_rects(&mut self, rects: &[RegionRect]) -> Result<bool, Error> {
        let mut candidate = State::default();
        candidate
            .rects
            .try_reserve_exact(rects.len())
            .map_err(|_| Error::Capacity)?;
        candidate
            .rects
            .extend(rects.iter().copied().filter(|r| r.is_valid()));
        candidate.normalized = false;
        candidate.normalize()?;
        candidate.update_bounds();
        let nonempty = !candidate.rects.is_empty();
        *self.state.get_mut() = candidate;
        Ok(nonempty)
    }
    /// Replace with a complete independent canonical source snapshot.
    pub fn set_region(&mut self, source: &Self) -> Result<bool, Error> {
        let mut copy = source.try_clone()?;
        let result = !copy.is_empty();
        std::mem::swap(self, &mut copy);
        Ok(result)
    }
    /// Original wrapping translation; no point rounding or extent normalization.
    pub fn translate(&mut self, x: i32, y: i32) {
        let s = self.state.get_mut();
        for r in &mut s.rects {
            *r = r.translated(x, y);
        }
        if !s.rects.is_empty() {
            s.bounds = s.bounds.translated(x, y);
        }
    }
    /// Original half-open integer point query.
    pub fn contains(&self, x: i32, y: i32) -> Result<bool, Error> {
        if !self.bounds().contains(x, y) {
            return Ok(false);
        }
        Ok(self.normalized()?.rects.iter().any(|r| r.contains(x, y)))
    }
    /// Original whole-rectangle difference proof; empty operands are false.
    pub fn contains_rect(&self, rect: RegionRect) -> Result<bool, Error> {
        if !rect.is_valid() {
            return Ok(false);
        }
        let mut remainder = Self::from_rect(rect)?;
        remainder.op_region(self, RegionOperation::Difference)?;
        Ok(remainder.is_empty())
    }
    /// Original whole-region difference proof; empty operands are false.
    pub fn contains_region(&self, other: &Self) -> Result<bool, Error> {
        if other.is_empty() {
            return Ok(false);
        }
        let mut remainder = other.try_clone()?;
        remainder.op_region(self, RegionOperation::Difference)?;
        Ok(remainder.is_empty())
    }
    /// Original quick-contains contract retains the complete coverage proof.
    pub fn quick_contains(&self, rect: RegionRect) -> Result<bool, Error> {
        self.contains_rect(rect)
    }
    /// Original quick rectangle rejection uses actual covered rectangles.
    pub fn quick_reject_rect(&self, rect: RegionRect) -> Result<bool, Error> {
        Ok(!self.intersects_rect(rect)?)
    }
    /// Original quick region rejection, preserving full source coverage.
    pub fn quick_reject_region(&self, other: &Self) -> Result<bool, Error> {
        Ok(!self.intersects_region(other)?)
    }
    /// Original bounds-first intersection with actual normalized coverage.
    pub fn intersects_rect(&self, rect: RegionRect) -> Result<bool, Error> {
        if !self.bounds().intersection(rect).is_valid() {
            return Ok(false);
        }
        Ok(self
            .normalized()?
            .rects
            .iter()
            .any(|r| r.intersection(rect).is_valid()))
    }
    /// Original actual region intersection, preserving canonical inventories.
    pub fn intersects_region(&self, other: &Self) -> Result<bool, Error> {
        // Snapshot before querying self so a caller can compare a region to itself.
        let snapshot = other.rectangles()?;
        for rect in snapshot {
            if self.intersects_rect(rect)? {
                return Ok(true);
            }
        }
        Ok(false)
    }
    /// Original rectangle Boolean path. Union retains deferred canonicalization
    /// and its containment fast path; dependent operations normalize first.
    pub fn op_rect(&mut self, rect: RegionRect, operation: RegionOperation) -> Result<bool, Error> {
        use RegionOperation as O;
        if operation == O::Replace {
            return self.set_rect(rect);
        }
        if operation == O::Union {
            let s = self.state.get_mut();
            if !rect.is_valid() {
                return Ok(!s.rects.is_empty());
            }
            if s.rects.iter().any(|r| r.covers(rect)) {
                return Ok(true);
            }
            s.rects.try_reserve(1).map_err(|_| Error::Capacity)?;
            s.rects.push(rect);
            s.normalized = false;
            s.union_bounds(rect);
            return Ok(true);
        }
        let source = self.normalized()?;
        let mut result = Vec::new();
        match operation {
            O::Intersect => {
                for r in &source.rects {
                    push_valid(&mut result, r.intersection(rect))?;
                }
            }
            O::Difference => {
                for r in &source.rects {
                    difference(&mut result, *r, rect)?;
                }
            }
            O::ReverseDifference => {
                push_valid(&mut result, rect)?;
                for cutter in &source.rects {
                    subtract_reverse(&mut result, *cutter)?;
                }
            }
            O::Xor => {
                for r in &source.rects {
                    difference(&mut result, *r, rect)?;
                }
                let mut right = Vec::new();
                push_valid(&mut right, rect)?;
                for cutter in &source.rects {
                    subtract_reverse(&mut right, *cutter)?;
                }
                append(&mut result, &right)?;
            }
            _ => unreachable!(),
        }
        drop(source);
        self.publish(result)
    }
    /// Original full canonical region Boolean operation and nonempty result.
    pub fn op_region(&mut self, other: &Self, operation: RegionOperation) -> Result<bool, Error> {
        use RegionOperation as O;
        let left = self.normalized()?;
        let right = other.normalized()?;
        let mut result = Vec::new();
        match operation {
            O::Replace => append(&mut result, &right.rects)?,
            O::Intersect => {
                for l in &left.rects {
                    for r in &right.rects {
                        push_valid(&mut result, l.intersection(*r))?;
                    }
                }
            }
            O::Union => {
                append(&mut result, &left.rects)?;
                append(&mut result, &right.rects)?;
            }
            O::Difference => {
                append(&mut result, &left.rects)?;
                for r in &right.rects {
                    subtract(&mut result, *r)?;
                }
            }
            O::ReverseDifference => {
                append(&mut result, &right.rects)?;
                for l in &left.rects {
                    subtract(&mut result, *l)?;
                }
            }
            O::Xor => {
                append(&mut result, &left.rects)?;
                for r in &right.rects {
                    subtract(&mut result, *r)?;
                }
                // The original recursively normalizes each difference before union.
                let mut a = State::default();
                a.rects = std::mem::take(&mut result);
                a.normalized = false;
                a.normalize()?;
                let mut b = State::default();
                append(&mut b.rects, &right.rects)?;
                for l in &left.rects {
                    subtract(&mut b.rects, *l)?;
                }
                b.normalized = false;
                b.normalize()?;
                append(&mut result, &a.rects)?;
                append(&mut result, &b.rects)?;
            }
        }
        drop(left);
        drop(right);
        self.publish(result)
    }
    fn publish(&mut self, rects: Vec<RegionRect>) -> Result<bool, Error> {
        let mut candidate = State::default();
        candidate.rects = rects;
        candidate.normalized = false;
        candidate.normalize()?;
        candidate.update_bounds();
        let result = !candidate.rects.is_empty();
        *self.state.get_mut() = candidate;
        Ok(result)
    }
    /// Original separate rectangular contours in canonical order, connected to
    /// the ordinary vector raster/stroke/clip consumers without CPU pixel uploads.
    pub fn boundary_path(&self) -> Result<PathGeometry, Error> {
        let state = self.normalized()?;
        let mut path = PathGeometry::default();
        path.figures
            .try_reserve_exact(state.rects.len())
            .map_err(|_| Error::Capacity)?;
        for rect in &state.rects {
            let [l, t, r, b] = rect.0.map(|v| v as f32);
            let mut figure = PathFigure::new([l, t], true);
            figure
                .segments
                .try_reserve_exact(3)
                .map_err(|_| Error::Capacity)?;
            for p in [[r, t], [r, b], [l, b]] {
                figure
                    .segments
                    .push(PathSegment::new(PathSegmentKind::Line(p)));
            }
            path.figures.push(figure);
        }
        Ok(path)
    }
    /// Owned canonical rectangle iterator, independent of future region mutation.
    pub fn rectangles(&self) -> Result<RegionRectIterator, Error> {
        let s = self.normalized()?;
        let mut rects = Vec::new();
        append(&mut rects, &s.rects)?;
        Ok(RegionRectIterator(rects.into_iter()))
    }
    /// Original clip iterator culls disjoint rectangles, retaining complete source
    /// rectangles rather than cropping them to the clip.
    pub fn clipped_rectangles(&self, clip: RegionRect) -> Result<RegionRectIterator, Error> {
        let s = self.normalized()?;
        let mut rects = Vec::new();
        rects
            .try_reserve_exact(s.rects.len())
            .map_err(|_| Error::Capacity)?;
        rects.extend(
            s.rects
                .iter()
                .copied()
                .filter(|r| r.intersection(clip).is_valid()),
        );
        Ok(RegionRectIterator(rects.into_iter()))
    }
    /// Original sorted and merged exclusive horizontal runs for an integer row.
    pub fn spans(&self, y: i32, left: i32, right: i32) -> Result<RegionSpanIterator, Error> {
        let s = self.normalized()?;
        let mut spans = Vec::new();
        if right > left {
            for r in &s.rects {
                if y >= r.0[1] && y < r.0[3] {
                    let a = left.max(r.0[0]);
                    let b = right.min(r.0[2]);
                    if b > a {
                        push(&mut spans, [a, b])?;
                    }
                }
            }
        }
        spans.sort_unstable_by_key(|p| p[0]);
        merge(&mut spans);
        Ok(RegionSpanIterator(spans.into_iter()))
    }
}
/// Snapshot of complete canonical rectangles.
pub struct RegionRectIterator(std::vec::IntoIter<RegionRect>);
impl Iterator for RegionRectIterator {
    type Item = RegionRect;
    fn next(&mut self) -> Option<Self::Item> {
        self.0.next()
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        self.0.size_hint()
    }
}
impl ExactSizeIterator for RegionRectIterator {}
impl std::iter::FusedIterator for RegionRectIterator {}
/// Snapshot of merged half-open integer X spans.
pub struct RegionSpanIterator(std::vec::IntoIter<[i32; 2]>);
impl Iterator for RegionSpanIterator {
    type Item = [i32; 2];
    fn next(&mut self) -> Option<Self::Item> {
        self.0.next()
    }
    fn size_hint(&self) -> (usize, Option<usize>) {
        self.0.size_hint()
    }
}
impl ExactSizeIterator for RegionSpanIterator {}
impl std::iter::FusedIterator for RegionSpanIterator {}
fn push<T>(out: &mut Vec<T>, value: T) -> Result<(), Error> {
    out.try_reserve(1).map_err(|_| Error::Capacity)?;
    out.push(value);
    Ok(())
}
fn append(out: &mut Vec<RegionRect>, values: &[RegionRect]) -> Result<(), Error> {
    out.try_reserve(values.len()).map_err(|_| Error::Capacity)?;
    out.extend_from_slice(values);
    Ok(())
}
fn push_valid(out: &mut Vec<RegionRect>, value: RegionRect) -> Result<(), Error> {
    if value.is_valid() {
        push(out, value)?;
    }
    Ok(())
}
fn difference(
    out: &mut Vec<RegionRect>,
    source: RegionRect,
    cutter: RegionRect,
) -> Result<(), Error> {
    if !source.is_valid() {
        return Ok(());
    }
    let overlap = source.intersection(cutter);
    if !overlap.is_valid() {
        return push(out, source);
    }
    let s = source.0;
    let o = overlap.0;
    for r in [
        [s[0], s[1], s[2], o[1]],
        [s[0], o[3], s[2], s[3]],
        [s[0], o[1], o[0], o[3]],
        [o[2], o[1], s[2], o[3]],
    ] {
        push_valid(out, RegionRect(r))?;
    }
    Ok(())
}
fn subtract(rects: &mut Vec<RegionRect>, cutter: RegionRect) -> Result<(), Error> {
    if !cutter.is_valid() || rects.is_empty() {
        return Ok(());
    }
    let mut out = Vec::new();
    out.try_reserve(rects.len()).map_err(|_| Error::Capacity)?;
    for r in rects.iter() {
        difference(&mut out, *r, cutter)?;
    }
    *rects = out;
    Ok(())
}
fn subtract_reverse(rects: &mut Vec<RegionRect>, cutter: RegionRect) -> Result<(), Error> {
    for index in (0..rects.len()).rev() {
        let current = rects.remove(index);
        difference(rects, current, cutter)?;
    }
    Ok(())
}
fn merge(values: &mut Vec<[i32; 2]>) {
    if values.len() < 2 {
        return;
    }
    let mut write = 0;
    for read in 1..values.len() {
        let next = values[read];
        if next[0] <= values[write][1] {
            values[write][1] = values[write][1].max(next[1]);
        } else {
            write += 1;
            values[write] = next;
        }
    }
    values.truncate(write + 1);
}
fn normalize(rects: &[RegionRect], s: &mut Storage) -> Result<(), Error> {
    s.clear();
    s.ys.try_reserve(rects.len().checked_mul(2).ok_or(Error::Capacity)?)
        .map_err(|_| Error::Capacity)?;
    for r in rects {
        if r.is_valid() {
            s.ys.push(r.0[1]);
            s.ys.push(r.0[3]);
        }
    }
    s.ys.sort_unstable();
    s.ys.dedup();
    for pair in s.ys.windows(2) {
        let top = pair[0];
        let bottom = pair[1];
        if bottom <= top {
            continue;
        }
        s.intervals.clear();
        for r in rects {
            if r.is_valid() && r.0[1] <= top && r.0[3] >= bottom {
                push(&mut s.intervals, [r.0[0], r.0[2]])?;
            }
        }
        if s.intervals.is_empty() {
            s.previous.clear();
            continue;
        }
        s.intervals.sort_unstable();
        s.merged.clear();
        s.merged
            .try_reserve(s.intervals.len())
            .map_err(|_| Error::Capacity)?;
        s.merged.extend_from_slice(&s.intervals);
        merge(&mut s.merged);
        s.current.clear();
        s.current
            .try_reserve(s.merged.len())
            .map_err(|_| Error::Capacity)?;
        for interval in &s.merged {
            let old = s
                .previous
                .get(interval)
                .copied()
                .filter(|i| s.rects[*i].0[3] == top);
            let index = if let Some(index) = old {
                s.rects[index].0[3] = bottom;
                index
            } else {
                let index = s.rects.len();
                push(
                    &mut s.rects,
                    RegionRect([interval[0], top, interval[1], bottom]),
                )?;
                index
            };
            s.current.insert(*interval, index);
        }
        std::mem::swap(&mut s.previous, &mut s.current);
    }
    Ok(())
}
