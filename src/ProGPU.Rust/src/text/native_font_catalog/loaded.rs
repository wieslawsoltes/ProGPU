//! Actual immutable file leases and original weak whole-file cache publication.
use super::*;
use crate::font::FontDataLease;
/// An actual read-only mapped-file owner supplied by an embedding host. The
/// returned bytes must be the same complete immutable mapping until retirement.
/// This producer contract does not assert that the built-in owned fallback maps.
pub trait NativeMappedFontLease: FontDataLease + std::fmt::Debug {}
/// Optional native mapping provider. Failure/absence selects the original owned
/// file-read fallback. Implementations may not return temporary/borrowed views.
pub trait NativeFontFileMapper {
    fn try_map_font(&self, path: &Path) -> Option<Arc<dyn NativeMappedFontLease>>;
}
struct MappingOwner(Arc<dyn NativeMappedFontLease>);
impl FontDataLease for MappingOwner {
    fn bytes(&self) -> &[u8] {
        self.0.bytes()
    }
}
impl FontDataLease for FileStorage {
    fn bytes(&self) -> &[u8] {
        self.data.as_ref()
    }
}
/// Complete selected face plus actual source owner. Clones and downstream native
/// SfntFace clones retain the same FileStorage lease, so every hinted/unhinted
/// shaping/atlas user keeps the original weak cache generation live.
#[derive(Clone, Debug)]
pub struct NativeLoadedFontFace {
    storage: Arc<FileStorage>,
    font: SfntFace,
    catalog_index: u32,
    identity: u64,
}
impl NativeLoadedFontFace {
    fn new(
        storage: Arc<FileStorage>,
        face: u32,
        catalog_index: u32,
        identity: u64,
    ) -> Result<Self, NativeCatalogError> {
        // The SfntFace contains a storage lease, never the reverse: no owner cycle.
        let font = SfntFace::from_storage(FontData::shared(storage.clone()), face)
            .map_err(|_| NativeCatalogError::InvalidFont)?;
        Ok(Self {
            storage,
            font,
            catalog_index,
            identity,
        })
    }
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    pub fn data(&self) -> &[u8] {
        self.storage.data.as_ref()
    }
    pub fn is_memory_mapped(&self) -> bool {
        self.storage.mapped
    }
    pub fn catalog_index(&self) -> u32 {
        self.catalog_index
    }
    pub fn identity(&self) -> u64 {
        self.identity
    }
    /// Original one-glyph resident source keeps catalog identity/index while
    /// producing its actual new face-zero immutable container. No source glyph
    /// remapping, string shaping, missing slots or raw tags are synthesized.
    pub fn create_glyph_resident_face(
        &self,
        glyph: u16,
    ) -> Result<(Self, NativeGlyphResidentRequirements), NativeCatalogError> {
        let requirements = self
            .font
            .native_glyph_resident_requirements(glyph)
            .map_err(|_| NativeCatalogError::InvalidFont)?;
        let mut output = zeros(requirements.font_bytes)?;
        let requirements = self
            .font
            .native_glyph_resident_font_into(glyph, &mut output)
            .map_err(|_| NativeCatalogError::InvalidFont)?;
        let storage = Arc::new(FileStorage {
            data: FontData::owned(output),
            mapped: false,
        });
        Ok((
            Self::new(storage, 0, self.catalog_index, self.identity)?,
            requirements,
        ))
    }
}
impl NativeSystemFontCatalog {
    /// Built-in safe full-file fallback. No ambient mapper is assumed or reported.
    /// Complete font parsing occurs only after selecting and owning this file.
    pub fn load_face(
        &self,
        catalog_index: u32,
    ) -> Result<NativeLoadedFontFace, NativeCatalogError> {
        self.load(catalog_index, None)
    }
    pub fn load_face_with_mapper(
        &self,
        catalog_index: u32,
        mapper: &dyn NativeFontFileMapper,
    ) -> Result<NativeLoadedFontFace, NativeCatalogError> {
        self.load(catalog_index, Some(mapper))
    }
    fn load(
        &self,
        catalog_index: u32,
        mapper: Option<&dyn NativeFontFileMapper>,
    ) -> Result<NativeLoadedFontFace, NativeCatalogError> {
        let entry = &self
            .entries
            .get(catalog_index as usize)
            .ok_or(NativeCatalogError::InvalidArgument)?
            .info;
        let cached = self
            .cache()
            .loaded_files
            .get(&entry.source_path)
            .and_then(Weak::upgrade);
        let storage = if let Some(storage) = cached {
            storage
        } else {
            // Call a real host mapper outside the catalog lock. After it returns,
            // recheck this path's weak slot and keep an already-published owner.
            let mapping = mapper.and_then(|mapper| mapper.try_map_font(&entry.file_path));
            let candidate = if let Some(mapping) = mapping {
                Arc::new(FileStorage {
                    data: FontData::shared(Arc::new(MappingOwner(mapping))),
                    mapped: true,
                })
            } else {
                let mut reader = reader::Reader::open(&entry.file_path, None)?;
                let length =
                    usize::try_from(reader.length).map_err(|_| NativeCatalogError::Filesystem)?;
                let mut bytes = zeros(length)?;
                reader
                    .read_into(0, &mut bytes)
                    .map_err(|_| NativeCatalogError::Filesystem)?;
                Arc::new(FileStorage {
                    data: FontData::owned(bytes),
                    mapped: false,
                })
            };
            let mut cache = self.cache();
            if let Some(concurrent) = cache
                .loaded_files
                .get(&entry.source_path)
                .and_then(Weak::upgrade)
            {
                concurrent
            } else {
                cache
                    .loaded_files
                    .try_reserve(1)
                    .map_err(|_| NativeCatalogError::OutOfMemory)?;
                cache
                    .loaded_files
                    .insert(entry.source_path.clone(), Arc::downgrade(&candidate));
                candidate
            }
        };
        NativeLoadedFontFace::new(storage, entry.face_index, catalog_index, entry.identity)
    }
}
