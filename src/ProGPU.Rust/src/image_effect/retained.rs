//! Actual encode completion admission for mutable render-thread recycling.
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicU64, Ordering},
};

/// Original producer content and view generations, captured with its actual
/// texture owner. A caller must advance content after a write and view after a
/// view replacement; these values never assert portable/provider qualification.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct ImageEffectTextureGeneration {
    pub content: u32,
    pub view: u32,
}

#[derive(Debug, Default)]
pub(crate) struct ResourceUse {
    encoded: AtomicU64,
    submitted: AtomicU64,
    completed: AtomicU64,
    ambiguous: AtomicBool,
}
impl ResourceUse {
    pub fn encode(&self) {
        // More than one unsubmitted recording cannot be assigned to a queue
        // submission without a command-buffer ticket. Retain those resources
        // permanently outside mutable reuse; never guess which buffer submitted.
        let prior = self
            .encoded
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |value| {
                value.checked_add(1)
            });
        if prior.is_err()
            || prior.is_ok_and(|value| value != self.submitted.load(Ordering::Acquire))
        {
            self.ambiguous.store(true, Ordering::Release);
        }
    }
    pub fn reusable(&self) -> bool {
        !self.ambiguous.load(Ordering::Acquire)
            && self.encoded.load(Ordering::Acquire) == self.completed.load(Ordering::Acquire)
    }
    pub fn submitted(owner: Arc<Self>, queue: &wgpu::Queue) {
        let encoded = owner.encoded.load(Ordering::Acquire);
        let prior = owner.submitted.swap(encoded, Ordering::AcqRel);
        if encoded == prior {
            return;
        }
        if prior.checked_add(1) != Some(encoded) {
            owner.ambiguous.store(true, Ordering::Release);
            return;
        }
        if !owner.ambiguous.load(Ordering::Acquire) {
            queue.on_submitted_work_done(move || {
                owner.completed.fetch_max(encoded, Ordering::AcqRel);
            });
        }
    }
}
