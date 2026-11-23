//! Original storage factories and resident-source generation attachment.
use super::*;
use crate::font::{
    FontData, FontFileStorage, FontStorageError, FontStorageKind,
    resident::{self, ResidentFontMetrics, ResidentFontState},
};
use crate::path::ExecutionPreference;
use std::{
    io::{BufReader, Cursor, Read, Seek},
    path::Path,
};
impl ManagedFont {
    /// Retain a safe complete source lease. Uncompressed data preserves the
    /// actual owner and range; compressed data selects a normalized owned root.
    pub fn from_storage(data: FontData, face_index: u32) -> Result<Self, FontError> {
        Self::from_face(SfntFace::from_storage(
            data.normalize_managed()?,
            face_index,
        )?)
    }
    pub fn from_embedded(data: &'static [u8], face_index: u32) -> Result<Self, FontError> {
        Self::from_storage(FontData::embedded(data), face_index)
    }
    pub fn from_embedded_slice(
        data: FontData,
        offset: i32,
        length: i32,
        face_index: u32,
    ) -> Result<Self, FontStorageError> {
        Ok(Self::from_storage(
            data.embedded_slice(offset, length)?,
            face_index,
        )?)
    }
    pub fn from_resource(
        provider: &impl crate::font::FontResourceProvider,
        name: &str,
        face_index: u32,
    ) -> Result<Self, FontStorageError> {
        Ok(Self::from_storage(
            FontData::from_resource(provider, name)?,
            face_index,
        )?)
    }
    pub fn from_resource_slice(
        provider: &impl crate::font::FontResourceProvider,
        name: &str,
        offset: i32,
        length: i32,
        face_index: u32,
    ) -> Result<Self, FontStorageError> {
        if name.trim().is_empty() {
            return Err(FontStorageError::InvalidResourceName);
        }
        if offset < 0 || length <= 0 {
            return Err(FontStorageError::InvalidSlice);
        }
        let data = provider
            .font_resource(name)?
            .ok_or_else(|| FontStorageError::MissingResource(name.into()))?;
        Self::from_embedded_slice(data, offset, length, face_index)
    }
    pub fn from_reader_slice(
        reader: &mut (impl Read + Seek),
        offset: i32,
        length: i32,
        face_index: u32,
    ) -> Result<Self, FontStorageError> {
        Ok(Self::from_storage(
            FontData::from_reader_slice(reader, offset, length)?,
            face_index,
        )?)
    }
    pub fn from_file(
        path: impl AsRef<Path>,
        face_index: u32,
        preference: FontFileStorage,
    ) -> Result<Self, FontStorageError> {
        Ok(Self::from_storage(
            FontData::from_file(path, preference)?,
            face_index,
        )?)
    }
    pub fn storage_kind(&self) -> FontStorageKind {
        self.source().storage().kind()
    }
    pub fn uses_external_storage(&self) -> bool {
        self.storage_kind() != FontStorageKind::Owned
    }
    /// Shared leases do not assert that their host owner is an OS file mapping.
    pub fn uses_mapped_file_storage(&self) -> bool {
        false
    }
    pub fn resident_glyph_index(&self) -> Option<u16> {
        self.root.resident.as_ref().map(|value| value.glyph)
    }
    pub fn resident_source_face_index(&self) -> Option<u32> {
        self.root
            .resident
            .as_ref()
            .map(|value| value.source_face_index)
    }
    pub fn resident_metrics(&self) -> Option<ResidentFontMetrics> {
        self.root.resident.as_ref().map(|value| value.metrics)
    }
    pub fn glyph_resident_bitmap_cache_bytes(&self) -> u64 {
        self.root
            .resident
            .as_ref()
            .map_or(0, |value| value.source.cached_bytes())
    }
    pub fn load_glyph_resident_file(
        path: impl AsRef<Path>,
        face_index: u32,
        glyph: u16,
        preference: ExecutionPreference,
    ) -> Result<Self, FontStorageError> {
        if cfg!(target_arch = "wasm32") {
            return Err(FontStorageError::NativeFilesUnavailable);
        }
        resident::ResidentFontExecution::from_preference(preference)?;
        let path = Arc::new(path.as_ref().to_owned());
        let selected = match std::fs::File::open(path.as_ref()) {
            Ok(file) => resident::extract_glyph_resident_font(
                &mut BufReader::with_capacity(4096, file),
                face_index,
                glyph,
                preference,
            )?,
            Err(_) => None,
        };
        let Some(selected) = selected else {
            return Self::from_file(path.as_ref(), face_index, FontFileStorage::Automatic);
        };
        let source = resident::GlyphBitmapSource::file(path, face_index);
        Self::attach_resident(selected, source)
    }
    /// Portable/browser producer over actual immutable owned or shared source.
    /// The complete backing stays retained for later zero-copy sbix selections.
    pub fn load_glyph_resident_data(
        data: FontData,
        face_index: u32,
        glyph: u16,
        preference: ExecutionPreference,
    ) -> Result<Self, FontStorageError> {
        let selected = resident::extract_glyph_resident_font(
            &mut Cursor::new(data.clone()),
            face_index,
            glyph,
            preference,
        )?;
        let Some(selected) = selected else {
            return Ok(Self::from_storage(data, face_index)?);
        };
        let source = resident::GlyphBitmapSource::retained(data, face_index);
        Self::attach_resident(selected, source)
    }
    fn attach_resident(
        selected: resident::GlyphResidentFontData,
        source: Arc<resident::GlyphBitmapSource>,
    ) -> Result<Self, FontStorageError> {
        let mut font = Self::from_storage(selected.data, 0)?;
        Arc::get_mut(&mut font.root)
            .expect("unpublished resident root")
            .resident = Some(Arc::new(ResidentFontState {
            glyph: selected.glyph_index,
            source_face_index: selected.source_face_index,
            source,
            metrics: selected.metrics,
        }));
        Ok(font)
    }
}
