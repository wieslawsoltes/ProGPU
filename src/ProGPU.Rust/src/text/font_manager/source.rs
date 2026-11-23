//! Explicit source callbacks and their real once-loaded owner generations.
use super::FontManagerError;
use crate::font::{FontError, ManagedFont};
use std::{
    cell::RefCell,
    collections::HashSet,
    fmt,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
};

/// Source failures are cloneable so concurrent lazy readers see the same actual
/// result. No provider error is relabeled as missing native execution support.
#[derive(Clone, Debug, thiserror::Error)]
pub enum FontSourceError {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error("font source IO ({kind:?}): {message}")]
    Io {
        kind: std::io::ErrorKind,
        message: Arc<str>,
    },
    #[error("font source provider: {0}")]
    Provider(Arc<str>),
    #[error("recursive font source load")]
    RecursiveLoad,
    #[error("this lazy source has no standalone metadata coverage query")]
    MetadataUnavailable,
}
impl From<std::io::Error> for FontSourceError {
    fn from(error: std::io::Error) -> Self {
        Self::Io {
            kind: error.kind(),
            message: error.to_string().into(),
        }
    }
}
/// An actual selected face source, independent of raster/hinting providers.
/// Coverage performs metadata-only nominal lookup; it must not shape or load
/// outlines. Browser hosts can implement this over their immutable owned bytes.
pub trait FontSource: Send + Sync {
    fn load(&self) -> Result<ManagedFont, FontSourceError>;
    fn metadata_glyph(&self, codepoint: u32) -> Result<u16, FontSourceError>;
}
/// An already materialized original face, retaining its exact instance identity.
#[derive(Clone, Debug)]
pub struct OwnedFontSource(pub ManagedFont);
impl FontSource for OwnedFontSource {
    fn load(&self) -> Result<ManagedFont, FontSourceError> {
        Ok(self.0.clone())
    }
    fn metadata_glyph(&self, codepoint: u32) -> Result<u16, FontSourceError> {
        Ok(self.0.glyph_index(codepoint)?)
    }
}
/// Typed loader and independent metadata callback, retained once for every
/// registration generation. Neither callback runs during draw encoding.
pub struct CallbackFontSource {
    load: Arc<dyn Fn() -> Result<ManagedFont, FontSourceError> + Send + Sync>,
    glyph: Arc<dyn Fn(u32) -> Result<u16, FontSourceError> + Send + Sync>,
}
impl CallbackFontSource {
    /// Original lazy-registration adapter: family fallback loads the real font;
    /// no nominal coverage is fabricated for a system-metadata query.
    pub fn loader(
        load: impl Fn() -> Result<ManagedFont, FontSourceError> + Send + Sync + 'static,
    ) -> Self {
        Self::new(load, |_| Err(FontSourceError::MetadataUnavailable))
    }
    pub fn new(
        load: impl Fn() -> Result<ManagedFont, FontSourceError> + Send + Sync + 'static,
        metadata_glyph: impl Fn(u32) -> Result<u16, FontSourceError> + Send + Sync + 'static,
    ) -> Self {
        Self {
            load: Arc::new(load),
            glyph: Arc::new(metadata_glyph),
        }
    }
}
impl FontSource for CallbackFontSource {
    fn load(&self) -> Result<ManagedFont, FontSourceError> {
        (self.load)()
    }
    fn metadata_glyph(&self, codepoint: u32) -> Result<u16, FontSourceError> {
        (self.glyph)(codepoint)
    }
}
static NEXT_SOURCE: AtomicU64 = AtomicU64::new(1);
thread_local! { static LOADING: RefCell<HashSet<u64>> = RefCell::new(HashSet::new()); }
struct LoadGuard(u64);
impl Drop for LoadGuard {
    fn drop(&mut self) {
        LOADING.with(|set| {
            set.borrow_mut().remove(&self.0);
        });
    }
}
type Attempt = OnceLock<Result<ManagedFont, FontSourceError>>;
/// Shared original lazy identity. Registrations remember a failed attempt;
/// system files clear only that failed attempt, so later loads can retry.
pub(super) struct SourceOwner {
    pub id: u64,
    pub source: Arc<dyn FontSource>,
    attempt: Mutex<Option<Arc<Attempt>>>,
    retry_failure: bool,
}
impl fmt::Debug for SourceOwner {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SourceOwner")
            .field("id", &self.id)
            .field("retry_failure", &self.retry_failure)
            .finish_non_exhaustive()
    }
}
impl SourceOwner {
    pub fn new(source: Arc<dyn FontSource>, retry_failure: bool) -> Arc<Self> {
        Arc::new(Self {
            id: NEXT_SOURCE.fetch_add(1, Ordering::Relaxed),
            source,
            attempt: Mutex::new(None),
            retry_failure,
        })
    }
    pub fn loaded(&self) -> Option<ManagedFont> {
        self.attempt
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_ref()?
            .get()?
            .as_ref()
            .ok()
            .cloned()
    }
    pub fn load(&self) -> Result<ManagedFont, FontSourceError> {
        if !LOADING.with(|set| set.borrow_mut().insert(self.id)) {
            return Err(FontSourceError::RecursiveLoad);
        }
        let _guard = LoadGuard(self.id);
        let attempt = self
            .attempt
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .get_or_insert_with(|| Arc::new(OnceLock::new()))
            .clone();
        let result = attempt.get_or_init(|| self.source.load()).clone();
        if result.is_err() && self.retry_failure {
            let mut current = self.attempt.lock().unwrap_or_else(|e| e.into_inner());
            if current
                .as_ref()
                .is_some_and(|value| Arc::ptr_eq(value, &attempt))
            {
                *current = None;
            }
        }
        result
    }
    pub fn try_load(&self) -> Result<Option<ManagedFont>, FontManagerError> {
        match self.load() {
            Ok(font) => Ok(Some(font)),
            Err(error @ FontSourceError::Font(FontError::Capacity)) => Err(error.into()),
            Err(_) => Ok(None),
        }
    }
}
