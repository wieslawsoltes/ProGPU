//! Owned sort scratch replacing the source ArrayPool lease. At most one empty
//! allocation of 4096 records per thread survives a call. No source owners enter
//! this cache, and no RefCell borrow is held during shaping or font callbacks.
use super::{FontError, ManagedShapingGlyph};
use std::cell::RefCell;
thread_local! {
    static SORT: RefCell<Vec<ManagedShapingGlyph>> = const { RefCell::new(Vec::new()) };
}
pub(super) struct Scratch(Vec<ManagedShapingGlyph>);
impl Scratch {
    pub(super) fn new() -> Self {
        Self(
            SORT.try_with(|cell| {
                cell.try_borrow_mut()
                    .ok()
                    .map(|mut v| std::mem::take(&mut *v))
            })
            .ok()
            .flatten()
            .unwrap_or_default(),
        )
    }
    pub(super) fn records(
        &mut self,
        length: usize,
    ) -> Result<&mut [ManagedShapingGlyph], FontError> {
        if length > self.0.len() {
            self.0
                .try_reserve(length - self.0.len())
                .map_err(|_| FontError::Capacity)?;
            self.0.resize(length, ManagedShapingGlyph::default());
        }
        Ok(&mut self.0[..length])
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        if self.0.capacity() > 4096 {
            return;
        }
        self.0.clear();
        let _ = SORT.try_with(|cell| {
            if let Ok(mut cached) = cell.try_borrow_mut()
                && self.0.capacity() > cached.capacity()
            {
                std::mem::swap(&mut *cached, &mut self.0);
            }
        });
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn nested_leases_are_independent_and_retention_is_bounded() {
        std::thread::spawn(|| {
            let mut outer = Scratch::new();
            outer.records(256).unwrap()[0] = ManagedShapingGlyph::new(7, 9, 65);
            let pointer = outer.0.as_ptr();
            let mut inner = Scratch::new();
            inner.records(128).unwrap()[0] = ManagedShapingGlyph::new(3, 4, 66);
            assert_ne!(pointer, inner.0.as_ptr());
            drop(inner);
            assert_eq!(outer.0[0].glyph_index, 7);
            drop(outer);
            SORT.with(|cell| {
                let cached = cell.borrow();
                assert!(cached.is_empty());
                assert_eq!(cached.as_ptr(), pointer);
                // Reentrant borrow falls back to unpooled storage and ordinary drop.
                let mut blocked = Scratch::new();
                blocked.records(64).unwrap();
                drop(blocked);
            });
            let mut oversized = Scratch::new();
            assert_eq!(oversized.0.as_ptr(), pointer);
            oversized.records(4097).unwrap();
            drop(oversized);
            SORT.with(|cell| assert_eq!(cell.borrow().capacity(), 0));
        })
        .join()
        .unwrap();
    }
}
