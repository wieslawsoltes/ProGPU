//! Original glyph-resident SFNT/SBIX extraction and demand-loaded bitmap owners.
//! Record order, source checksums and selected glyph IDs remain unchanged.
use super::{FontData, FontError, FontStorageError};
use crate::path::ExecutionPreference;
use std::io::{Read, Seek};
mod extract;
mod source;
pub(crate) use source::GlyphBitmapSource;
/// CPU container production policy, independent from GPU glyph rendering.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResidentFontExecution {
    CpuSimd,
    ScalarReference,
}
impl ResidentFontExecution {
    pub fn from_preference(preference: ExecutionPreference) -> Result<Self, FontStorageError> {
        let available = cfg!(any(
            target_feature = "sse2",
            target_feature = "neon",
            target_feature = "simd128"
        ));
        match preference {
            ExecutionPreference::Automatic => Ok(if available {
                Self::CpuSimd
            } else {
                Self::ScalarReference
            }),
            ExecutionPreference::CpuSimd if available => Ok(Self::CpuSimd),
            ExecutionPreference::ScalarReference => Ok(Self::ScalarReference),
            _ => Err(FontStorageError::UnsupportedExecution),
        }
    }
}
/// Actual authored extraction work; this does not claim performance qualification.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ResidentFontMetrics {
    pub execution: ResidentFontExecution,
    pub source_bytes_read: u64,
    pub source_tables: u32,
    pub source_strikes: u32,
    pub output_bytes: u64,
    pub checksum_bytes: u64,
}
impl ResidentFontMetrics {
    fn new(execution: ResidentFontExecution) -> Self {
        Self {
            execution,
            source_bytes_read: 0,
            source_tables: 0,
            source_strikes: 0,
            output_bytes: 0,
            checksum_bytes: 0,
        }
    }
}
/// The compact font retains the original glyph inventory and every table; only
/// the first sbix payload is replaced by its selected-glyph strike records.
#[derive(Clone, Debug)]
pub struct GlyphResidentFontData {
    pub data: FontData,
    pub source_face_index: u32,
    pub glyph_index: u16,
    pub glyph_count: u16,
    pub metrics: ResidentFontMetrics,
}
#[derive(Clone, Debug)]
pub struct GlyphResidentSbix {
    pub data: FontData,
    pub glyph_count: u16,
    pub metrics: ResidentFontMetrics,
}
/// Original TryCreateGlyphResidentFont nullable admission over a caller-owned
/// reader. Invalid/truncated/unreadable inventories return None atomically;
/// allocation/capacity failures and unavailable forced routes remain explicit.
pub fn extract_glyph_resident_font(
    reader: &mut (impl Read + Seek),
    face_index: u32,
    glyph: u16,
    preference: ExecutionPreference,
) -> Result<Option<GlyphResidentFontData>, FontStorageError> {
    let execution = ResidentFontExecution::from_preference(preference)?;
    nullable(extract::font(reader, face_index, glyph, execution))
}
/// Original per-glyph sbix extraction, preserving all strike headers and empty
/// records. A missing glyph bitmap does not remove its descriptor/offset slots.
pub fn extract_glyph_sbix(
    reader: &mut (impl Read + Seek),
    face_index: u32,
    glyph: u16,
    preference: ExecutionPreference,
) -> Result<Option<GlyphResidentSbix>, FontStorageError> {
    let execution = ResidentFontExecution::from_preference(preference)?;
    nullable(extract::sbix(reader, face_index, glyph, execution))
}
fn nullable<T>(value: Result<Option<T>, FontStorageError>) -> Result<Option<T>, FontStorageError> {
    match value {
        Err(
            error @ (FontStorageError::Font(FontError::Capacity)
            | FontStorageError::UnsupportedExecution),
        ) => Err(error),
        Err(_) => Ok(None),
        other => other,
    }
}
#[derive(Debug)]
pub(crate) struct ResidentFontState {
    pub glyph: u16,
    pub source_face_index: u32,
    pub source: std::sync::Arc<GlyphBitmapSource>,
    pub metrics: ResidentFontMetrics,
}
