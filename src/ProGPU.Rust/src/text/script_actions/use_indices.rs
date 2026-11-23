//! Independent owned USE index lease. One empty allocation of at most 8,192
//! 32-bit indices per thread may be reused; no source owner or borrow escapes.
use crate::font::FontError;
use std::cell::RefCell;
thread_local! {
    static INDICES: RefCell<Vec<u32>> = const { RefCell::new(Vec::new()) };
}
pub(super) struct Indices(Vec<u32>);
impl Indices {
    pub(super) fn rent(capacity: usize) -> Result<Self, FontError> {
        let mut result = Self(
            INDICES
                .try_with(|cell| {
                    cell.try_borrow_mut()
                        .ok()
                        .map(|mut v| std::mem::take(&mut *v))
                })
                .ok()
                .flatten()
                .unwrap_or_default(),
        );
        result
            .0
            .try_reserve_exact(capacity)
            .map_err(|_| FontError::Capacity)?;
        Ok(result)
    }
    pub(super) fn records(&mut self) -> &mut Vec<u32> {
        &mut self.0
    }
}
impl Drop for Indices {
    fn drop(&mut self) {
        if self.0.capacity() > 8192 {
            return;
        }
        self.0.clear();
        let _ = INDICES.try_with(|cell| {
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
    fn independent_indices_reuse_empty_storage_and_bound_retention() {
        std::thread::spawn(|| {
            let mut outer = Indices::rent(1024).unwrap();
            outer.records().extend([0, 5, 7]);
            let pointer = outer.0.as_ptr();
            let mut inner = Indices::rent(512).unwrap();
            inner.records().push(99);
            assert_ne!(pointer, inner.0.as_ptr());
            drop(inner);
            assert_eq!(outer.records(), &[0, 5, 7]);
            drop(outer);
            INDICES.with(|cell| {
                let cached = cell.borrow();
                assert!(cached.is_empty());
                assert_eq!(cached.as_ptr(), pointer);
                drop(Indices::rent(64).unwrap());
            });
            drop(Indices::rent(8193).unwrap());
            INDICES.with(|cell| assert_eq!(cell.borrow().capacity(), 0));
        })
        .join()
        .unwrap();
    }
}
