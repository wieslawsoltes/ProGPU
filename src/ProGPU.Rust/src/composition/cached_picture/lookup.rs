//! Weak lookup entries live only while actual source leases remain owned.
use super::*;
use std::{
    collections::{HashMap, HashSet},
    hash::Hash,
};
struct Lookup<K: Eq + Hash> {
    entries: HashMap<K, Weak<Entry<K>>>,
    creating: HashSet<K>,
    closed: bool,
}
struct Entry<K: Eq + Hash> {
    key: K,
    cache: Weak<RefCell<Lookup<K>>>,
    picture: CachedPicture,
}
// Remove only our exact dead registration; a later replacement remains live.
impl<K: Eq + Hash> Drop for Entry<K> {
    fn drop(&mut self) {
        if let Some(cache) = self.cache.upgrade() {
            if let Ok(mut state) = cache.try_borrow_mut() {
                let captured = self as *const Self;
                if state
                    .entries
                    .get(&self.key)
                    .is_some_and(|entry| entry.as_ptr() == captured)
                {
                    state.entries.remove(&self.key);
                }
            }
        }
        // Release source callbacks after the registry borrow has ended.
        self.picture.close();
    }
}
/// Shared rendering-thread lookup. Keys must include every capture context fact.
#[derive(Clone)]
pub struct CachedPictureSourceCache<K: Eq + Hash + Clone> {
    state: Rc<RefCell<Lookup<K>>>,
}
impl<K: Eq + Hash + Clone> Default for CachedPictureSourceCache<K> {
    fn default() -> Self {
        Self {
            state: Rc::new(RefCell::new(Lookup {
                entries: HashMap::new(),
                creating: HashSet::new(),
                closed: false,
            })),
        }
    }
}
trait LeaseOwner {
    fn picture(&self) -> &CachedPicture;
}
impl<K: Eq + Hash> LeaseOwner for Entry<K> {
    fn picture(&self) -> &CachedPicture {
        &self.picture
    }
}
/// One independently counted source lease. Clone before retaining in a recording.
#[derive(Clone)]
pub struct CachedPictureLease {
    owner: Rc<dyn LeaseOwner>,
}
impl fmt::Debug for CachedPictureLease {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_tuple("CachedPictureLease")
            .field(self.picture())
            .finish()
    }
}
impl CachedPictureLease {
    /// Borrow the source; retain/retire the lease instead of closing shared state.
    pub fn picture(&self) -> &CachedPicture {
        self.owner.picture()
    }
}
struct Creating<K: Eq + Hash + Clone> {
    state: Rc<RefCell<Lookup<K>>>,
    key: K,
}
impl<K: Eq + Hash + Clone> Drop for Creating<K> {
    fn drop(&mut self) {
        self.state.borrow_mut().creating.remove(&self.key);
    }
}
impl<K: Eq + Hash + Clone + 'static> CachedPictureSourceCache<K> {
    /// Count currently leased sources, removing dead weak registrations.
    pub fn len(&self) -> usize {
        let mut state = self.state.borrow_mut();
        state.entries.retain(|_, entry| entry.strong_count() != 0);
        state.entries.len()
    }
    /// Whether no actual source leases remain in this lookup.
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
    /// Acquire an existing source or run a typed factory once outside all lookup
    /// borrows. Recursive same-key and depth-256 acquisition fail atomically.
    pub fn acquire(
        &self,
        key: K,
        factory: impl FnOnce() -> Result<Rc<dyn CachedPictureSource>, Error>,
    ) -> Result<CachedPictureLease, Error> {
        {
            let mut state = self
                .state
                .try_borrow_mut()
                .map_err(|_| Error::ResourceDomain)?;
            if state.closed {
                return Err(Error::ResourceDomain);
            }
            if let Some(owner) = state.entries.get(&key).and_then(Weak::upgrade) {
                return Ok(CachedPictureLease { owner });
            }
            if state.creating.len() >= 256 || state.creating.contains(&key) {
                return Err(Error::Capacity);
            }
            state.creating.try_reserve(1).map_err(|_| Error::Capacity)?;
            state.creating.insert(key.clone());
        }
        let _guard = Creating {
            state: self.state.clone(),
            key: key.clone(),
        };
        let picture = CachedPicture::from_source(factory()?)?;
        let mut state = self
            .state
            .try_borrow_mut()
            .map_err(|_| Error::ResourceDomain)?;
        if state.closed {
            return Err(Error::ResourceDomain);
        }
        state.entries.try_reserve(1).map_err(|_| Error::Capacity)?;
        let owner = Rc::new(Entry {
            key: key.clone(),
            cache: Rc::downgrade(&self.state),
            picture,
        });
        state.entries.insert(key, Rc::downgrade(&owner));
        Ok(CachedPictureLease { owner })
    }
    /// Close the lookup without invalidating outstanding independently leased
    /// recordings or invoking their source callbacks under a registry borrow.
    pub fn close(&self) {
        let mut state = self.state.borrow_mut();
        state.closed = true;
        state.entries.clear();
    }
}
