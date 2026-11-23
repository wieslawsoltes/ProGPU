//! Rendering-thread cached source generations and independently owned leases.
use crate::{Error, scene::Scene};
use std::{
    cell::{Cell, RefCell},
    fmt,
    rc::{Rc, Weak},
    sync::Arc,
};
mod lookup;
pub use lookup::{CachedPictureLease, CachedPictureSourceCache};

/// One owned immutable recording and exact original capture policy.
#[derive(Clone, Debug)]
pub struct CachedPictureSnapshot {
    /// Actual original command storage, shared without copying commands.
    pub scene: Arc<Scene>,
    /// Exact original XYWH capture rectangle, independently of final consumers.
    pub bounds: [f32; 4],
    /// Original finite nonnegative raster scale; zero paints nothing.
    pub render_scale: f32,
    /// Explicit cached text policy; false suppresses recorded ClearType only.
    pub enable_cleartype: bool,
}
impl CachedPictureSnapshot {
    /// Original generic defaults: scale one and recorded ClearType retained.
    pub fn new(scene: impl Into<Arc<Scene>>, bounds: [f32; 4]) -> Result<Self, Error> {
        let value = Self {
            scene: scene.into(),
            bounds,
            render_scale: 1.0,
            enable_cleartype: true,
        };
        value.validate()?;
        Ok(value)
    }
    /// Validate the complete source descriptor before changing a live generation.
    pub fn validate(&self) -> Result<(), Error> {
        let [x, y, w, h] = self.bounds;
        if ![
            x,
            y,
            w,
            h,
            x + w,
            y + h,
            w * self.render_scale,
            h * self.render_scale,
        ]
        .iter()
        .all(|v| v.is_finite())
            || w < 0.0
            || h < 0.0
            || !self.render_scale.is_finite()
            || self.render_scale < 0.0
        {
            return Err(Error::InvalidInput);
        }
        self.scene.validate_cached_picture_source()
    }
    fn same_content(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.scene, &other.scene)
            && self.bounds == other.bounds
            && self.render_scale == other.render_scale
            && self.enable_cleartype == other.enable_cleartype
    }
}

/// A source's subscription lease. Dropping it must detach its exact listener;
/// it must not flush, submit or poll a renderer. Implemented by the source owner.
pub trait CachedPictureSubscription {}
/// Typed live source. Capture returns a stable independent recording without GPU
/// submission. Subscribe retains one exact listener; multiple owners stay distinct.
pub trait CachedPictureSource {
    /// Capture current commands, bounds and raster policy as one generation.
    fn capture(&self) -> Result<CachedPictureSnapshot, Error>;
    /// Register a listener before the initial capture, returning its detach owner.
    fn subscribe(
        &self,
        invalidation: CachedPictureInvalidation,
    ) -> Result<Box<dyn CachedPictureSubscription>, Error>;
}
struct Signal {
    live: bool,
    disposed: Cell<bool>,
    dirty: Cell<bool>,
    source_version: Cell<u64>,
    revision: Cell<u64>,
}
impl Signal {
    fn advance(&self) {
        let next = self.revision.get().wrapping_add(1);
        self.revision.set(if next == 0 { 1 } else { next });
    }
}
/// Rendering-thread invalidation sender. It weakly refers to the exact source
/// owner, so an abandoned subscription cannot keep the cached picture alive.
#[derive(Clone)]
pub struct CachedPictureInvalidation(Weak<Signal>);
impl CachedPictureInvalidation {
    /// Mark source content dirty before the next preparation. Invalidation during
    /// capture rejects that candidate. Retired owners ignore late notifications.
    pub fn invalidate(&self) {
        if let Some(signal) = self.0.upgrade()
            && !signal.disposed.get()
        {
            signal
                .source_version
                .set(signal.source_version.get().wrapping_add(1));
            signal.dirty.set(signal.live);
            signal.advance();
        }
    }
}
struct State {
    snapshot: Option<CachedPictureSnapshot>,
    source: Option<Rc<dyn CachedPictureSource>>,
    subscription: Option<Box<dyn CachedPictureSubscription>>,
    refreshing: bool,
}
struct Owner {
    signal: Rc<Signal>,
    state: RefCell<State>,
}
impl Drop for Owner {
    fn drop(&mut self) {
        self.signal.disposed.set(true);
        let state = self.state.get_mut();
        // Detach the actual listener before releasing the source and commands.
        drop(state.subscription.take());
        drop(state.source.take());
        drop(state.snapshot.take());
    }
}
/// Shared rendering-thread source identity. Cloning is an independent handle;
/// explicit close empties all live handles while retained frozen captures survive.
#[derive(Clone)]
pub struct CachedPicture(Rc<Owner>);
impl fmt::Debug for CachedPicture {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("CachedPicture")
            .field("revision", &self.0.signal.revision.get())
            .field("disposed", &self.is_closed())
            .field("dirty", &self.is_source_dirty())
            .finish_non_exhaustive()
    }
}
impl CachedPicture {
    fn owner(
        snapshot: Option<CachedPictureSnapshot>,
        source: Option<Rc<dyn CachedPictureSource>>,
    ) -> Self {
        Self(Rc::new(Owner {
            signal: Rc::new(Signal {
                live: source.is_some(),
                disposed: Cell::new(false),
                dirty: Cell::new(source.is_some()),
                source_version: Cell::new(0),
                revision: Cell::new(1),
            }),
            state: RefCell::new(State {
                snapshot,
                source,
                subscription: None,
                refreshing: false,
            }),
        }))
    }
    /// Retain one independent recording lease; construction performs no GPU work.
    pub fn new(snapshot: CachedPictureSnapshot) -> Result<Self, Error> {
        snapshot.validate()?;
        Ok(Self::owner(Some(snapshot), None))
    }
    /// Subscribe before transactionally acquiring the original initial snapshot.
    /// Rust shared ownership replaces the source's borrowed/owned disposal flag:
    /// the last source owner releases it, after its exact subscription is detached.
    pub fn from_source(source: Rc<dyn CachedPictureSource>) -> Result<Self, Error> {
        let result = Self::owner(None, Some(source.clone()));
        let subscription = source.subscribe(result.invalidation())?;
        result.0.state.borrow_mut().subscription = Some(subscription);
        result.refresh()?;
        Ok(result)
    }
    /// Exact listener identity for source notifications or retained mutable input.
    pub fn invalidation(&self) -> CachedPictureInvalidation {
        CachedPictureInvalidation(Rc::downgrade(&self.0.signal))
    }
    /// Whether explicit retirement has emptied this shared owner.
    pub fn is_closed(&self) -> bool {
        self.0.signal.disposed.get()
    }
    /// Whether a live source needs recapture; immutable pictures remain clean.
    pub fn is_source_dirty(&self) -> bool {
        self.0.signal.dirty.get()
    }
    /// Preserve original unchanged-source O(1) no-op and reject recursive capture.
    /// The callback runs without a state borrow; invalidation/close remains usable.
    pub fn refresh(&self) -> Result<(), Error> {
        if self.is_closed() {
            return Err(Error::ResourceDomain);
        }
        if !self.is_source_dirty() {
            return Ok(());
        }
        let source = {
            let mut state = self
                .0
                .state
                .try_borrow_mut()
                .map_err(|_| Error::ResourceDomain)?;
            let Some(source) = state.source.clone() else {
                return Ok(());
            };
            if state.refreshing {
                return Err(Error::ResourceDomain);
            }
            state.refreshing = true;
            source
        };
        let guard = Refresh(self);
        let version = self.0.signal.source_version.get();
        let candidate = source.capture()?;
        candidate.validate()?;
        if self.is_closed() || version != self.0.signal.source_version.get() {
            return Err(Error::ResourceDomain);
        }
        self.publish(candidate)?;
        self.0.signal.dirty.set(false);
        drop(guard);
        Ok(())
    }
    fn publish(&self, candidate: CachedPictureSnapshot) -> Result<(), Error> {
        candidate
            .scene
            .validate_cached_picture_dependencies(self.identity(), 0)?;
        let previous = {
            let mut state = self
                .0
                .state
                .try_borrow_mut()
                .map_err(|_| Error::ResourceDomain)?;
            if state
                .snapshot
                .as_ref()
                .is_some_and(|old| old.same_content(&candidate))
            {
                return Ok(());
            }
            state.snapshot.replace(candidate)
        };
        self.0.signal.advance();
        drop(previous);
        Ok(())
    }
    /// Replace static content atomically. Live sources update through refresh.
    pub fn update(&self, candidate: CachedPictureSnapshot) -> Result<(), Error> {
        if self.is_closed()
            || self
                .0
                .state
                .try_borrow()
                .map_err(|_| Error::ResourceDomain)?
                .source
                .is_some()
        {
            return Err(Error::ResourceDomain);
        }
        candidate.validate()?;
        self.publish(candidate)
    }
    /// Invalidate original mutable source resources, preserving current ownership.
    pub fn invalidate(&self) -> Result<(), Error> {
        if self.is_closed() {
            return Err(Error::ResourceDomain);
        }
        if self
            .0
            .state
            .try_borrow()
            .map_err(|_| Error::ResourceDomain)?
            .source
            .is_some()
        {
            self.invalidation().invalidate();
        } else {
            self.0.signal.advance();
        }
        Ok(())
    }
    /// Refresh then retain immutable original source/policy/revision for one capture.
    pub fn freeze(&self) -> Result<CachedPictureGeneration, Error> {
        self.refresh()?;
        let snapshot = self
            .0
            .state
            .try_borrow()
            .map_err(|_| Error::ResourceDomain)?
            .snapshot
            .clone()
            .ok_or(Error::ResourceDomain)?;
        Ok(CachedPictureGeneration {
            snapshot,
            owner: Rc::downgrade(&self.0),
            revision: self.0.signal.revision.get(),
        })
    }
    /// Explicit retirement is idempotent and CPU-only; queued prepared captures
    /// retain their own source/texture owners independently.
    pub fn close(&self) {
        if self.0.signal.disposed.replace(true) {
            return;
        }
        self.0.signal.advance();
        let (subscription, source, snapshot) = {
            let mut state = self.0.state.borrow_mut();
            (
                state.subscription.take(),
                state.source.take(),
                state.snapshot.take(),
            )
        };
        drop(subscription);
        drop(source);
        drop(snapshot);
    }
    pub(crate) fn current_recording(&self) -> Result<Option<Arc<Scene>>, Error> {
        Ok(self
            .0
            .state
            .try_borrow()
            .map_err(|_| Error::ResourceDomain)?
            .snapshot
            .as_ref()
            .map(|s| s.scene.clone()))
    }
    pub(crate) fn identity(&self) -> usize {
        Rc::as_ptr(&self.0) as usize
    }
}
struct Refresh<'a>(&'a CachedPicture);
impl Drop for Refresh<'_> {
    fn drop(&mut self) {
        self.0.0.state.borrow_mut().refreshing = false;
    }
}
/// One frozen source generation. Its weak identity cannot extend a live callback
/// lifetime; the independent recording remains available after source retirement.
#[derive(Clone)]
pub struct CachedPictureGeneration {
    snapshot: CachedPictureSnapshot,
    owner: Weak<Owner>,
    revision: u64,
}
impl fmt::Debug for CachedPictureGeneration {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("CachedPictureGeneration")
            .field("snapshot", &self.snapshot)
            .field("revision", &self.revision)
            .finish_non_exhaustive()
    }
}
impl CachedPictureGeneration {
    /// Exact immutable original recording and source policy.
    pub fn snapshot(&self) -> &CachedPictureSnapshot {
        &self.snapshot
    }
    /// Nonzero local source revision, distinct from any device cache revision.
    pub fn revision(&self) -> u64 {
        self.revision
    }
    /// Whether the original owner still publishes this exact immutable generation.
    pub fn is_current(&self) -> bool {
        self.owner.upgrade().is_some_and(|o| {
            !o.signal.disposed.get()
                && !o.signal.dirty.get()
                && o.signal.revision.get() == self.revision
        })
    }
    /// Exact owner and revision; never an address-only cache identity.
    pub fn same_generation(&self, other: &Self) -> bool {
        Weak::ptr_eq(&self.owner, &other.owner) && self.revision == other.revision
    }
}
