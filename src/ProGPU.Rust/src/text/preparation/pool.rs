//! Original bounded per-thread glyph-list reuse: four lists, 4096 capacity each.
//! Only published prepared buffers return records. Font/text/context owners never
//! enter the pool; oversized storage and failed candidates retire normally.
use super::*;
use std::cell::RefCell;
struct Pool {
    slots: [Option<Vec<ManagedShapingGlyph>>; 4],
    count: usize,
}
thread_local! {
    static GLYPHS: RefCell<Pool> = const { RefCell::new(Pool { slots: [None,None,None,None], count: 0 }) };
}
pub(super) fn rent(capacity: usize) -> Result<Vec<ManagedShapingGlyph>, PreparationError> {
    let mut glyphs = GLYPHS
        .try_with(|cell| {
            let mut pool = cell.try_borrow_mut().ok()?;
            if pool.count == 0 {
                return None;
            }
            pool.count -= 1;
            let index = pool.count;
            pool.slots[index].take()
        })
        .ok()
        .flatten()
        .unwrap_or_default();
    glyphs
        .try_reserve(capacity)
        .map_err(|_| PreparationError::Capacity)?;
    Ok(glyphs)
}
pub(crate) fn retire(mut glyphs: Vec<ManagedShapingGlyph>) {
    if glyphs.capacity() > 4096 {
        return;
    }
    glyphs.clear();
    // Thread-local teardown or reentrant borrowing falls back to normal Vec
    // retirement. No lock, source callback or owner resurrection is involved.
    let _ = GLYPHS.try_with(|cell| {
        if let Ok(mut pool) = cell.try_borrow_mut()
            && pool.count < 4
        {
            let index = pool.count;
            pool.slots[index] = Some(glyphs);
            pool.count += 1;
        }
    });
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pool_is_lifo_bounded_and_retains_only_empty_record_storage() {
        std::thread::spawn(|| {
            let mut arrays: Vec<_> = (0..6).map(|_| Vec::with_capacity(128)).collect();
            let expected = arrays[3].as_ptr();
            for mut records in arrays.drain(..) {
                records.push(ManagedShapingGlyph::new(7, 9, 65));
                retire(records);
            }
            GLYPHS.with(|cell| {
                let pool = cell.borrow();
                assert_eq!(pool.count, 4);
                assert!(
                    pool.slots
                        .iter()
                        .all(|s| s.as_ref().is_some_and(Vec::is_empty))
                );
            });
            let records = rent(64).unwrap();
            assert!(records.is_empty());
            assert_eq!(records.as_ptr(), expected);
            retire(Vec::with_capacity(4097));
            GLYPHS.with(|cell| assert_eq!(cell.borrow().count, 3));
            drop(records);
        })
        .join()
        .unwrap();
    }
}
