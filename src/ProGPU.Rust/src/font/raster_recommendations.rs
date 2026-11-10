//! Original exact static gasp metadata. These independent recommendations do
//! not select a rasterizer, execute hinting or change glyph placement.
use super::{FontError, ManagedFont, bytes};

/// Original independent OpenType gasp bits, never aliases for source GDI/GDI+
/// rendering modes or evidence of an executed rasterization policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FontRasterizationRecommendations(u16);
impl FontRasterizationRecommendations {
    /// Explicit original zero recommendation; distinct from missing coverage.
    pub const NONE: Self = Self(0);
    /// Original grid-fitting bit.
    pub const GRID_FIT: Self = Self(1);
    /// Original grayscale bit.
    pub const GRAYSCALE: Self = Self(2);
    /// Version-one symmetric grid-fitting bit.
    pub const SYMMETRIC_GRID_FIT: Self = Self(4);
    /// Version-one symmetric smoothing bit, independent of grayscale.
    pub const SYMMETRIC_SMOOTHING: Self = Self(8);
    /// Retain a valid independent-bit combination; unknown bits reject.
    pub fn from_bits(bits: u16) -> Result<Self, FontError> {
        if bits & !15 != 0 {
            return Err(FontError::InvalidArgument);
        }
        Ok(Self(bits))
    }
    /// Exact original flag bits.
    pub fn bits(self) -> u16 {
        self.0
    }
    /// Test all selected bits without interpreting rendering policy.
    pub fn contains(self, value: Self) -> bool {
        self.0 & value.0 == value.0
    }
}
impl std::ops::BitOr for FontRasterizationRecommendations {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}
/// Original selected inclusive size range, retaining queried size separately.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FontRasterizationRange {
    /// Original gasp version, zero or one.
    pub version: u16,
    /// Exact positive query size.
    pub pixels_per_em: u16,
    /// Original inclusive range maximum.
    pub maximum_pixels_per_em: u16,
    /// Original independent recommendation bits, including explicit zero.
    pub recommendations: FontRasterizationRecommendations,
}
/// Validate every original range before selecting the first applicable one.
/// Empty inventory and uncovered size return `Ok(None)`. Malformed later ranges
/// reject an earlier match. O(R) time, O(1) scratch with ordered range dependency.
pub fn read_rasterization_recommendations(
    table: &[u8],
    pixels_per_em: u16,
) -> Result<Option<FontRasterizationRange>, FontError> {
    if pixels_per_em == 0 {
        return Err(FontError::InvalidArgument);
    }
    let version = bytes::u16(table, 0).ok_or(FontError::InvalidFace)?;
    let count = usize::from(bytes::u16(table, 2).ok_or(FontError::InvalidFace)?);
    if version > 1 || table.len() != 4 + count * 4 {
        return Err(FontError::InvalidFace);
    }
    let allowed = if version == 0 { 3 } else { 15 };
    let mut previous: i32 = -1;
    let mut candidate = None;
    for index in 0..count {
        let offset = 4 + index * 4;
        let maximum = bytes::u16(table, offset).ok_or(FontError::InvalidFace)?;
        let flags = bytes::u16(table, offset + 2).ok_or(FontError::InvalidFace)?;
        if i32::from(maximum) <= previous || flags & !allowed != 0 {
            return Err(FontError::InvalidFace);
        }
        if candidate.is_none() && pixels_per_em <= maximum {
            candidate = Some(FontRasterizationRange {
                version,
                pixels_per_em,
                maximum_pixels_per_em: maximum,
                recommendations: FontRasterizationRecommendations(flags),
            });
        }
        previous = i32::from(maximum);
    }
    Ok(candidate)
}
impl ManagedFont {
    /// Exact static TrueType gasp query. Raw fvar presence rejects unadjusted
    /// instance policy, even if ordinary table lookup ignored that fvar record.
    /// Duplicate/out-of-range gasp and malformed full inventories fail first.
    pub fn static_rasterization_recommendations(
        &self,
        pixels_per_em: u16,
    ) -> Result<Option<FontRasterizationRange>, FontError> {
        if pixels_per_em == 0 {
            return Err(FontError::InvalidArgument);
        }
        let data = self.source().data();
        let directory = self.source().face_offset() as usize + 12;
        let count = bytes::u16(data, directory - 8).ok_or(FontError::InvalidFace)?;
        let mut table = None;
        let mut variable = false;
        for index in 0..usize::from(count) {
            let entry =
                bytes::bytes(data, directory + index * 16, 16).ok_or(FontError::InvalidFace)?;
            variable |= &entry[..4] == b"fvar";
            if &entry[..4] != b"gasp" {
                continue;
            }
            if table.is_some() {
                return Err(FontError::InvalidFace);
            }
            let offset = bytes::u32(entry, 8).ok_or(FontError::InvalidFace)? as usize;
            let length = bytes::u32(entry, 12).ok_or(FontError::InvalidFace)? as usize;
            table = Some(bytes::bytes(data, offset, length).ok_or(FontError::InvalidFace)?);
        }
        let Some(table) = table else {
            return Ok(None);
        };
        let candidate = read_rasterization_recommendations(table, pixels_per_em)?;
        if variable || !self.metadata().has_true_type_outlines {
            return Ok(None);
        }
        Ok(candidate)
    }
}
