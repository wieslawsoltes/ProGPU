//! Original native system catalog: metadata-only discovery, lazy native cmap
//! selection and shared immutable selected-file ownership. This API preserves
//! the C++ catalog policy independently of the managed FontManager facade.
use super::native_font_provider::{NativeFontProviderSlant, NativeFontStyleRequest};
use crate::font::{FontData, FontError, SfntFace, Tag};
use std::{
    collections::{HashMap, VecDeque},
    path::{Path, PathBuf},
    sync::{Arc, Mutex, Weak},
};
mod discovery;
mod loaded;
mod matching;
mod metadata;
mod names;
mod names_data;
mod reader;
mod resident;
pub use discovery::NativeCatalogPathEncoder;
pub use loaded::{NativeFontFileMapper, NativeLoadedFontFace, NativeMappedFontLease};
pub use metadata::NativeFaceStyle;
pub use names::NativeNameRequirements;
pub use resident::NativeGlyphResidentRequirements;

/// Original catalog failure classes. Optional absent/unreadable cmap data stays
/// a missing character; only actual allocation failure escapes that lazy query.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
pub enum NativeCatalogError {
    #[error("invalid native font catalog argument")]
    InvalidArgument,
    #[error("native font catalog filesystem failure")]
    Filesystem,
    #[error("invalid selected native font")]
    InvalidFont,
    #[error("native font catalog allocation failed")]
    OutOfMemory,
}
/// Counts from the actual bounded discovery reads, including work before failure.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeCatalogScanMetrics {
    pub bytes_read: u64,
    pub directory_count: u32,
    pub file_count: u32,
    pub face_count: u32,
    pub skipped_file_count: u32,
}
/// Failed discovery retains its complete prior catalog and reports actual work.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[error("native font discovery: {error}")]
pub struct NativeCatalogScanError {
    pub error: NativeCatalogError,
    pub metrics: NativeCatalogScanMetrics,
}
/// Exact native metadata. Strings and paths borrow one immutable catalog
/// generation; successful discovery requires an exclusive mutable catalog.
#[derive(Clone, Debug)]
pub struct NativeCatalogFaceInfo {
    pub full_name: Arc<[u8]>,
    pub family_name: Arc<[u8]>,
    /// Original native narrow path bytes, retained independently of OS path storage.
    pub source_path: Arc<[u8]>,
    pub file_path: PathBuf,
    pub identity: u64,
    pub family_identity: u64,
    pub face_index: u32,
    pub weight: u16,
    pub width: u8,
    pub slant: NativeFontProviderSlant,
}
impl NativeCatalogFaceInfo {
    pub fn full_name_utf8(&self) -> Option<&str> {
        std::str::from_utf8(&self.full_name).ok()
    }
    pub fn family_name_utf8(&self) -> Option<&str> {
        std::str::from_utf8(&self.family_name).ok()
    }
}
#[derive(Debug)]
struct Entry {
    info: NativeCatalogFaceInfo,
}
#[derive(Debug, Default)]
struct CharacterMap {
    attempted: bool,
    face: Option<SfntFace>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
struct CharacterKey {
    family: u64,
    excluded: u64,
    code_point: u32,
    weight: u16,
    width: u8,
    slant: u8,
}
/// A genuine nominal cmap match in the current catalog, not a renderability or
/// glyph-ink guarantee. Catalog matches deliberately differ from provider matches.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeCatalogCharacter {
    pub catalog_index: u32,
    pub glyph_index: u16,
}
#[derive(Debug)]
struct FileStorage {
    data: FontData,
    mapped: bool,
}
#[derive(Debug, Default)]
struct Cache {
    cmaps: Vec<CharacterMap>,
    loaded_files: HashMap<Arc<[u8]>, Weak<FileStorage>>,
    characters: HashMap<CharacterKey, Option<NativeCatalogCharacter>>,
    character_order: VecDeque<CharacterKey>,
}
/// Concurrent matching/loading follows completed exclusive discovery. Metadata
/// and cached cmap inventories remain separate from full selected font storage;
/// loaded file owners outlive catalog replacement and are shared by path.
#[derive(Debug, Default)]
pub struct NativeSystemFontCatalog {
    entries: Vec<Entry>,
    cache: Mutex<Cache>,
}
impl NativeSystemFontCatalog {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn face_count(&self) -> u32 {
        self.entries.len() as u32
    }
    pub fn face_info(&self, index: u32) -> Option<&NativeCatalogFaceInfo> {
        self.entries.get(index as usize).map(|entry| &entry.info)
    }
    fn cache(&self) -> std::sync::MutexGuard<'_, Cache> {
        self.cache
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}
fn reserved<T>(count: usize) -> Result<Vec<T>, NativeCatalogError> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(count)
        .map_err(|_| NativeCatalogError::OutOfMemory)?;
    Ok(values)
}
fn zeros(count: usize) -> Result<Vec<u8>, NativeCatalogError> {
    let mut values = reserved(count)?;
    values.resize(count, 0);
    Ok(values)
}
fn word(data: &[u8], offset: usize) -> u16 {
    u16::from_be_bytes([data[offset], data[offset + 1]])
}
fn dword(data: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes(data[offset..offset + 4].try_into().unwrap())
}
fn put16(data: &mut [u8], offset: usize, value: u16) {
    data[offset..offset + 2].copy_from_slice(&value.to_be_bytes());
}
fn put32(data: &mut [u8], offset: usize, value: u32) {
    data[offset..offset + 4].copy_from_slice(&value.to_be_bytes());
}
fn folded(value: u8) -> u8 {
    if value.is_ascii_uppercase() {
        value + (b'a' - b'A')
    } else {
        value
    }
}
fn equal(left: &[u8], right: &[u8]) -> bool {
    left.len() == right.len()
        && left
            .iter()
            .copied()
            .map(folded)
            .eq(right.iter().copied().map(folded))
}
fn hash_normalized(value: &[u8]) -> u64 {
    let mut hash = 1_469_598_103_934_665_603u64;
    for byte in value {
        hash = (hash ^ u64::from(folded(*byte))).wrapping_mul(1_099_511_628_211);
    }
    if hash == 0 { 1 } else { hash }
}
fn face_identity(path: &[u8], face: u32) -> u64 {
    let mut hash = hash_normalized(path);
    for byte in face.to_le_bytes() {
        hash = (hash ^ u64::from(byte)).wrapping_mul(1_099_511_628_211);
    }
    if hash == 0 { 1 } else { hash }
}
fn map_font_error(error: FontError) -> NativeCatalogError {
    if error == FontError::Capacity {
        NativeCatalogError::OutOfMemory
    } else {
        NativeCatalogError::InvalidFont
    }
}
