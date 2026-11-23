//! Original native SFNT subset producers. Cold preparation owns one complete
//! candidate before copying to caller output; no font interpreter is invoked.
use super::{FontError, SfntFace, bytes};
use std::borrow::Cow;
mod compact;
mod glyphs;
mod tables;
type Result<T> = std::result::Result<T, FontError>;
const INVALID: FontError = FontError::InvalidFace;
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum Policy {
    Native,
    Managed,
}
const fn tag(value: &[u8; 4]) -> u32 {
    u32::from_be_bytes(*value)
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSfntSubsetRequirements {
    pub font_bytes: usize,
    pub glyph_map_count: usize,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSfntGlyphRemap {
    pub source_glyph_id: u16,
    pub subset_glyph_id: u16,
}
/// Complete immutable output, including the original ascending compact map.
/// Keeping this prepared value avoids rebuilding between requirements and copy.
#[derive(Debug)]
pub struct NativeSfntSubset {
    font: Vec<u8>,
    glyph_map: Vec<NativeSfntGlyphRemap>,
}
impl NativeSfntSubset {
    pub fn font_bytes(&self) -> &[u8] {
        &self.font
    }
    pub fn glyph_map(&self) -> &[NativeSfntGlyphRemap] {
        &self.glyph_map
    }
    pub fn requirements(&self) -> NativeSfntSubsetRequirements {
        NativeSfntSubsetRequirements {
            font_bytes: self.font.len(),
            glyph_map_count: self.glyph_map.len(),
        }
    }
    /// Both capacities are checked before either output changes. Tails survive.
    pub fn write_into(
        &self,
        output: &mut [u8],
        glyph_map: &mut [NativeSfntGlyphRemap],
    ) -> Result<NativeSfntSubsetRequirements> {
        if output.len() < self.font.len() || glyph_map.len() < self.glyph_map.len() {
            return Err(FontError::Capacity);
        }
        output[..self.font.len()].copy_from_slice(&self.font);
        glyph_map[..self.glyph_map.len()].copy_from_slice(&self.glyph_map);
        Ok(self.requirements())
    }
    pub fn into_parts(self) -> (Vec<u8>, Vec<NativeSfntGlyphRemap>) {
        (self.font, self.glyph_map)
    }
}
/// Keep original IDs, glyph zero and transitive composites. Missing TrueType
/// tables or zero glyph count use the original whole-font/DSIG-removal route.
/// directory_offset addresses an SFNT header in the actual complete container.
pub fn native_glyph_id_preserving_subset(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
) -> Result<NativeSfntSubset> {
    Ok(NativeSfntSubset {
        font: glyphs::preserving(data, directory_offset, glyphs, Policy::Native)?,
        glyph_map: Vec::new(),
    })
}
/// Compact original included glyphs in ascending source-ID order, remapping
/// composites and horizontal metrics. No cmap or shaping metadata is fabricated.
pub fn native_compact_sfnt_subset(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
) -> Result<NativeSfntSubset> {
    compact::build(data, directory_offset, glyphs, Policy::Native)
}
pub(super) fn managed_subset(
    data: &[u8],
    directory: i32,
    glyphs: &[u16],
    compact: bool,
) -> Result<NativeSfntSubset> {
    let directory = usize::try_from(directory).map_err(|_| FontError::InvalidArgument)?;
    if data.len() > i32::MAX as usize || slice(data, directory, 12).is_err() {
        return Err(FontError::InvalidArgument);
    }
    if compact {
        compact::build(data, directory, glyphs, Policy::Managed)
    } else {
        Ok(NativeSfntSubset {
            font: glyphs::preserving(data, directory, glyphs, Policy::Managed)?,
            glyph_map: Vec::new(),
        })
    }
}
pub fn native_glyph_id_preserving_subset_requirements(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
) -> Result<NativeSfntSubsetRequirements> {
    Ok(native_glyph_id_preserving_subset(data, directory_offset, glyphs)?.requirements())
}
pub fn native_glyph_id_preserving_subset_into(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
    output: &mut [u8],
) -> Result<NativeSfntSubsetRequirements> {
    native_glyph_id_preserving_subset(data, directory_offset, glyphs)?.write_into(output, &mut [])
}
pub fn native_compact_sfnt_subset_requirements(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
) -> Result<NativeSfntSubsetRequirements> {
    Ok(native_compact_sfnt_subset(data, directory_offset, glyphs)?.requirements())
}
pub fn native_compact_sfnt_subset_into(
    data: &[u8],
    directory_offset: usize,
    glyphs: &[u16],
    output: &mut [u8],
    glyph_map: &mut [NativeSfntGlyphRemap],
) -> Result<NativeSfntSubsetRequirements> {
    native_compact_sfnt_subset(data, directory_offset, glyphs)?.write_into(output, glyph_map)
}
impl SfntFace {
    /// Select this actual face header while preserving absolute table offsets.
    pub fn native_glyph_id_preserving_subset(&self, glyphs: &[u16]) -> Result<NativeSfntSubset> {
        native_glyph_id_preserving_subset(self.data(), self.face_offset() as usize, glyphs)
    }
    pub fn native_compact_subset(&self, glyphs: &[u16]) -> Result<NativeSfntSubset> {
        native_compact_sfnt_subset(self.data(), self.face_offset() as usize, glyphs)
    }
}

struct Table<'a> {
    tag: u32,
    bytes: Cow<'a, [u8]>,
}
impl Table<'_> {
    fn owned(tag: u32, bytes: Vec<u8>) -> Self {
        Self {
            tag,
            bytes: Cow::Owned(bytes),
        }
    }
}
struct Face<'a> {
    version: u32,
    tables: Vec<Table<'a>>,
    policy: Policy,
}
impl Face<'_> {
    fn find(&self, tag: u32) -> Option<&[u8]> {
        self.tables
            .iter()
            .rev()
            .find(|v| v.tag == tag)
            .map(|v| v.bytes.as_ref())
    }
}
fn slice(data: &[u8], offset: usize, length: usize) -> Result<&[u8]> {
    bytes::bytes(data, offset, length).ok_or(INVALID)
}
fn u16be(data: &[u8], offset: usize) -> Result<u16> {
    bytes::u16(data, offset).ok_or(INVALID)
}
fn i16be(data: &[u8], offset: usize) -> Result<i16> {
    bytes::i16(data, offset).ok_or(INVALID)
}
fn u32be(data: &[u8], offset: usize) -> Result<u32> {
    bytes::u32(data, offset).ok_or(INVALID)
}
fn put16(data: &mut [u8], offset: usize, value: u16) -> Result<()> {
    data.get_mut(offset..offset.checked_add(2).ok_or(INVALID)?)
        .ok_or(INVALID)?
        .copy_from_slice(&value.to_be_bytes());
    Ok(())
}
fn put32(data: &mut [u8], offset: usize, value: u32) -> Result<()> {
    data.get_mut(offset..offset.checked_add(4).ok_or(INVALID)?)
        .ok_or(INVALID)?
        .copy_from_slice(&value.to_be_bytes());
    Ok(())
}
fn align4(value: usize) -> Result<usize> {
    Ok(value.checked_add(3).ok_or(INVALID)? & !3)
}
fn reserved<T>(capacity: usize) -> Result<Vec<T>> {
    let mut result = Vec::new();
    result.try_reserve_exact(capacity).map_err(|_| INVALID)?;
    Ok(result)
}
fn filled<T: Clone>(length: usize, value: T) -> Result<Vec<T>> {
    let mut result = reserved(length)?;
    result.resize(length, value);
    Ok(result)
}
fn copy(data: &[u8]) -> Result<Vec<u8>> {
    let mut result = reserved(data.len())?;
    result.extend_from_slice(data);
    Ok(result)
}
