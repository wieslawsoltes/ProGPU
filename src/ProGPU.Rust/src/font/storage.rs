//! Safe immutable counterparts of original owned, embedded and mapped font leases.
//! No pointer or OS mapping is fabricated. Native mapping requires a separately
//! supplied safe immutable owner; ordinary file loading exposes its owned copy.
use super::{FontError, normalize_managed_sfnt_container};
use std::{
    fmt,
    io::{Read, Seek, SeekFrom},
    ops::{Deref, Range},
    path::Path,
    sync::Arc,
};
/// Stable immutable source lease. Implementations must return the same complete
/// byte inventory for their lifetime. Shared handles keep the actual owner alive.
pub trait FontDataLease: Send + Sync {
    fn bytes(&self) -> &[u8];
}
impl<T: AsRef<[u8]> + Send + Sync> FontDataLease for T {
    fn bytes(&self) -> &[u8] {
        self.as_ref()
    }
}
#[derive(Clone)]
enum Backing {
    Owned(Arc<[u8]>),
    Static(&'static [u8]),
    Shared(Arc<dyn FontDataLease>),
}
/// Actual storage route, independent of OS/provider qualification.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontStorageKind {
    Owned,
    StaticEmbedded,
    SharedLease,
}
/// Original file preference plus an explicit fail-closed mapping requirement.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FontFileStorage {
    #[default]
    Automatic,
    OwnedCopy,
    RequireMapping,
}
#[derive(Debug, thiserror::Error)]
pub enum FontStorageError {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("no safe native file mapping provider is installed")]
    MappingUnavailable,
    #[error("native font files are unavailable on this target")]
    NativeFilesUnavailable,
    #[error("invalid embedded font slice")]
    InvalidSlice,
    #[error("invalid embedded font resource name")]
    InvalidResourceName,
    #[error("embedded font resource is missing: {0}")]
    MissingResource(Arc<str>),
    #[error("the requested resident font execution route is unavailable")]
    UnsupportedExecution,
}
/// Explicit native/browser embedded resource provider. A successful result owns
/// the selected immutable resource lease; no assembly reflection is inferred.
pub trait FontResourceProvider {
    fn font_resource(&self, name: &str) -> Result<Option<FontData>, FontStorageError>;
}
/// Zero-copy embedded resources, including browser/Wasm static assets.
#[derive(Clone, Copy, Debug)]
pub struct StaticFontResource {
    pub name: &'static str,
    pub bytes: &'static [u8],
}
#[derive(Clone, Copy, Debug)]
pub struct StaticFontResources(pub &'static [StaticFontResource]);
impl FontResourceProvider for StaticFontResources {
    fn font_resource(&self, name: &str) -> Result<Option<FontData>, FontStorageError> {
        Ok(self
            .0
            .iter()
            .find(|entry| entry.name == name)
            .map(|entry| FontData::embedded(entry.bytes)))
    }
}
/// Immutable bounded slice of the actual storage owner. Cloning or slicing never
/// copies a payload; compressed normalization intentionally creates a new owner.
#[derive(Clone)]
pub struct FontData {
    backing: Backing,
    range: Range<usize>,
}
impl fmt::Debug for FontData {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("FontData")
            .field("kind", &self.kind())
            .field("range", &self.range)
            .finish_non_exhaustive()
    }
}
impl FontData {
    pub fn owned(bytes: impl Into<Arc<[u8]>>) -> Self {
        let bytes = bytes.into();
        let length = bytes.len();
        Self {
            backing: Backing::Owned(bytes),
            range: 0..length,
        }
    }
    pub fn embedded(bytes: &'static [u8]) -> Self {
        Self {
            backing: Backing::Static(bytes),
            range: 0..bytes.len(),
        }
    }
    pub fn shared(owner: Arc<dyn FontDataLease>) -> Self {
        let length = owner.bytes().len();
        Self {
            backing: Backing::Shared(owner),
            range: 0..length,
        }
    }
    pub fn kind(&self) -> FontStorageKind {
        match &self.backing {
            Backing::Owned(_) => FontStorageKind::Owned,
            Backing::Static(_) => FontStorageKind::StaticEmbedded,
            Backing::Shared(_) => FontStorageKind::SharedLease,
        }
    }
    pub fn same_owner(&self, other: &Self) -> bool {
        match (&self.backing, &other.backing) {
            (Backing::Owned(a), Backing::Owned(b)) => Arc::ptr_eq(a, b),
            (Backing::Static(a), Backing::Static(b)) => std::ptr::eq(*a, *b),
            (Backing::Shared(a), Backing::Shared(b)) => Arc::ptr_eq(a, b),
            _ => false,
        }
    }
    pub fn owner_range(&self) -> Range<usize> {
        self.range.clone()
    }
    pub fn slice(&self, range: Range<usize>) -> Result<Self, FontStorageError> {
        if range.start > range.end || range.end > self.len() {
            return Err(FontStorageError::InvalidSlice);
        }
        Ok(Self {
            backing: self.backing.clone(),
            range: self.range.start + range.start..self.range.start + range.end,
        })
    }
    /// Original embedded-slice positive length and signed offset admission.
    pub fn embedded_slice(&self, offset: i32, length: i32) -> Result<Self, FontStorageError> {
        if offset < 0 || length <= 0 {
            return Err(FontStorageError::InvalidSlice);
        }
        self.slice(
            offset as usize
                ..(offset as usize)
                    .checked_add(length as usize)
                    .ok_or(FontStorageError::InvalidSlice)?,
        )?
        .normalize_managed()
        .map_err(Into::into)
    }
    pub fn to_owned_bytes(&self) -> Arc<[u8]> {
        match &self.backing {
            Backing::Owned(bytes) if self.range.start == 0 && self.range.end == bytes.len() => {
                bytes.clone()
            }
            _ => Arc::from(self.as_ref()),
        }
    }
    pub fn normalize_managed(self) -> Result<Self, FontError> {
        if self.len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        if self
            .get(..4)
            .is_some_and(|signature| signature == b"wOFF" || signature == b"wOF2")
        {
            let owned = self.to_owned_bytes();
            drop(self);
            Ok(Self::owned(normalize_managed_sfnt_container(owned)?))
        } else {
            Ok(self)
        }
    }
    /// Safe original fallback when a native mapping cannot be supplied. The
    /// diagnostic remains Owned; this API never labels a full copy as mapped.
    pub fn from_file(
        path: impl AsRef<Path>,
        preference: FontFileStorage,
    ) -> Result<Self, FontStorageError> {
        if cfg!(target_arch = "wasm32") {
            return Err(FontStorageError::NativeFilesUnavailable);
        }
        if preference == FontFileStorage::RequireMapping {
            return Err(FontStorageError::MappingUnavailable);
        }
        let mut file = std::fs::File::open(path)?;
        let mut signature = [0; 4];
        file.read_exact(&mut signature)?;
        file.seek(SeekFrom::Start(0))?;
        Self::from_reader(&mut file)
    }
    pub fn from_reader(reader: &mut impl Read) -> Result<Self, FontStorageError> {
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes)?;
        Ok(Self::owned(bytes).normalize_managed()?)
    }
    pub fn from_resource(
        provider: &impl FontResourceProvider,
        name: &str,
    ) -> Result<Self, FontStorageError> {
        if name.trim().is_empty() {
            return Err(FontStorageError::InvalidResourceName);
        }
        let data = provider
            .font_resource(name)?
            .ok_or_else(|| FontStorageError::MissingResource(name.into()))?;
        Ok(data.normalize_managed()?)
    }
    /// Consuming counterpart to the borrowed reader API. The selected reader is
    /// dropped exactly once on success or failure after ownership transfer.
    pub fn from_owned_reader_slice(
        mut reader: impl Read + Seek,
        offset: i32,
        length: i32,
    ) -> Result<Self, FontStorageError> {
        Self::from_reader_slice(&mut reader, offset, length)
    }
    /// Borrow the caller's seekable stream for an exact slice. The caller keeps
    /// the reader; returned data owns a complete independent immutable payload.
    pub fn from_reader_slice(
        reader: &mut (impl Read + Seek),
        offset: i32,
        length: i32,
    ) -> Result<Self, FontStorageError> {
        if offset < 0 || length <= 0 {
            return Err(FontStorageError::InvalidSlice);
        }
        let total = reader.seek(SeekFrom::End(0))?;
        if offset as u64 > total || length as u64 > total - offset as u64 {
            return Err(FontStorageError::InvalidSlice);
        }
        reader.seek(SeekFrom::Start(offset as u64))?;
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(length as usize)
            .map_err(|_| FontError::Capacity)?;
        bytes.resize(length as usize, 0);
        reader.read_exact(&mut bytes)?;
        Ok(Self::owned(bytes).normalize_managed()?)
    }
}
impl AsRef<[u8]> for FontData {
    fn as_ref(&self) -> &[u8] {
        let all = match &self.backing {
            Backing::Owned(bytes) => bytes.as_ref(),
            Backing::Static(bytes) => bytes,
            Backing::Shared(owner) => owner.bytes(),
        };
        &all[self.range.clone()]
    }
}
impl Deref for FontData {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        self.as_ref()
    }
}
impl From<Arc<[u8]>> for FontData {
    fn from(value: Arc<[u8]>) -> Self {
        Self::owned(value)
    }
}
impl From<Vec<u8>> for FontData {
    fn from(value: Vec<u8>) -> Self {
        Self::owned(value)
    }
}
impl From<Box<[u8]>> for FontData {
    fn from(value: Box<[u8]>) -> Self {
        Self::owned(value)
    }
}
impl From<&[u8]> for FontData {
    fn from(value: &[u8]) -> Self {
        Self::owned(value)
    }
}
impl<const N: usize> From<&[u8; N]> for FontData {
    fn from(value: &[u8; N]) -> Self {
        Self::owned(value.as_slice())
    }
}
