//! Original managed SfntFontSubsetter policy, separate from native admission.
//! Raw four-byte tags retain exact Latin-1 ordinal ordering. Caller inventories
//! are immutable slices, so no deferred glyph-enumeration callbacks are introduced.
use super::{FontError, native_subset};
pub use native_subset::{
    NativeSfntGlyphRemap as SfntGlyphRemap, NativeSfntSubset as SfntSubset,
    NativeSfntSubsetRequirements as SfntSubsetRequirements,
};

/// Glyph zero and transitive components retain source IDs. The managed tolerant
/// whole-glyf dependency scan and empty-range policy are independent of native.
pub fn glyph_id_preserving_subset(
    data: &[u8],
    directory_offset: i32,
    glyphs: &[u16],
) -> Result<SfntSubset, FontError> {
    native_subset::managed_subset(data, directory_offset, glyphs, false)
}
/// Compact ascending IDs and original metric/table remapping. Malformed-input
/// behavior retains managed bounds and source checked search-field arithmetic.
pub fn compact_subset(
    data: &[u8],
    directory_offset: i32,
    glyphs: &[u16],
) -> Result<SfntSubset, FontError> {
    native_subset::managed_subset(data, directory_offset, glyphs, true)
}
