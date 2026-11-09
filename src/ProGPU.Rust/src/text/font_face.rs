//! Original TtfShapingFontFace boundary, independent of graphics initialization.
use crate::font::{FontError, ManagedFont, Tag};

/// Original design-unit glyph envelope; height is signed in font coordinates.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ShapingGlyphExtents {
    /// Original minimum X.
    pub x_bearing: i32,
    /// Original maximum Y.
    pub y_bearing: i32,
    /// Original maximum-minus-minimum X.
    pub width: i32,
    /// Original minimum-minus-maximum Y.
    pub height: i32,
}
/// Typed immutable font boundary for reusable CPU shaping consumers. Borrowed
/// table bytes cannot outlive the owning face; providers retain their own source
/// admission, variation and metric policy. No GPU resources cross this interface.
pub trait ShapingFontFace {
    /// Selected source collection face.
    fn face_index(&self) -> u32;
    /// Original head/bhed design-unit scale.
    fn units_per_em(&self) -> u16;
    /// Original maxp glyph inventory.
    fn glyph_count(&self) -> u32;
    /// Actual selected face's complete variation-axis count.
    fn variation_axis_count(&self) -> Result<u32, FontError>;
    /// Whether this exact instance has non-default selected variations.
    fn has_active_variations(&self) -> bool;
    /// Borrow an immutable selected table with the actual owner's lifetime.
    fn table(&self, tag: Tag) -> Option<&[u8]>;
    /// Original nominal map; missing coverage is glyph zero.
    fn nominal_glyph(&self, point: u32) -> Result<u32, FontError>;
    /// Original variation-sequence admission; absence remains distinct from zero.
    fn variation_glyph(&self, point: u32, selector: u32) -> Result<Option<u32>, FontError>;
    /// Original rounded horizontal design advance.
    fn horizontal_advance(&self, glyph: u32) -> Result<i32, FontError>;
    /// Original rounded vertical design advance.
    fn vertical_advance(&self, glyph: u32) -> Result<i32, FontError>;
    /// Original rounded horizontal advance divided by two, toward zero.
    fn horizontal_origin(&self, glyph: u32) -> Result<i32, FontError>;
    /// Original rounded vertical design origin.
    fn vertical_origin(&self, glyph: u32) -> Result<i32, FontError>;
    /// Original source outline bounds, without tight-curve substitution.
    fn glyph_extents(&self, glyph: u32) -> Result<Option<ShapingGlyphExtents>, FontError>;
    /// Actual source normalized coordinate, with out-of-range axes absent.
    fn normalized_coordinate(&self, axis: u32) -> Result<Option<i16>, FontError>;
    /// Selected font's original layout variation-store delta.
    fn layout_delta(&self, outer: u16, inner: u16) -> Result<f32, FontError>;
}
/// Owned adapter to the original managed font implementation and selected instance.
#[derive(Clone, Debug)]
pub struct ManagedShapingFontFace {
    font: ManagedFont,
}
impl ManagedShapingFontFace {
    /// Retain the actual selected font; no table data or outline is eagerly copied.
    pub fn new(font: ManagedFont) -> Self {
        Self { font }
    }
    /// Actual selected font generation.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
}
impl ShapingFontFace for ManagedShapingFontFace {
    fn face_index(&self) -> u32 {
        self.font.source().face_index()
    }
    fn units_per_em(&self) -> u16 {
        self.font.metadata().units_per_em
    }
    fn glyph_count(&self) -> u32 {
        u32::from(self.font.metadata().glyph_count)
    }
    fn variation_axis_count(&self) -> Result<u32, FontError> {
        u32::try_from(self.font.variation_axes()?.len()).map_err(|_| FontError::Capacity)
    }
    fn has_active_variations(&self) -> bool {
        self.font.has_active_variations()
    }
    fn table(&self, tag: Tag) -> Option<&[u8]> {
        self.font.source().table(tag).map(|t| t.bytes)
    }
    fn nominal_glyph(&self, point: u32) -> Result<u32, FontError> {
        self.font.glyph_index(point).map(u32::from)
    }
    fn variation_glyph(&self, point: u32, selector: u32) -> Result<Option<u32>, FontError> {
        self.font
            .variation_glyph(point, selector)
            .map(|v| v.map(u32::from))
    }
    fn horizontal_advance(&self, glyph: u32) -> Result<i32, FontError> {
        if glyph > u32::from(u16::MAX) {
            return Ok(0);
        }
        checked_integer(
            self.font
                .advance_width(glyph as u16, f32::from(self.units_per_em()))?
                .round(),
        )
    }
    fn vertical_advance(&self, glyph: u32) -> Result<i32, FontError> {
        if glyph > u32::from(u16::MAX) {
            return Ok(0);
        }
        checked_integer(
            self.font
                .advance_height(glyph as u16, f32::from(self.units_per_em()))?
                .round_ties_even(),
        )
    }
    fn horizontal_origin(&self, glyph: u32) -> Result<i32, FontError> {
        Ok(self.horizontal_advance(glyph)? / 2)
    }
    fn vertical_origin(&self, glyph: u32) -> Result<i32, FontError> {
        if glyph > u32::from(u16::MAX) {
            return Ok(0);
        }
        checked_integer(
            self.font
                .vertical_origin_y(glyph as u16, f32::from(self.units_per_em()))?
                .round_ties_even(),
        )
    }
    fn glyph_extents(&self, glyph: u32) -> Result<Option<ShapingGlyphExtents>, FontError> {
        if glyph > u32::from(u16::MAX) {
            return Ok(None);
        }
        Ok(self
            .font
            .glyph_bounds(glyph as u16)?
            .map(|b| ShapingGlyphExtents {
                x_bearing: i32::from(b[0]),
                y_bearing: i32::from(b[3]),
                width: i32::from(b[2]) - i32::from(b[0]),
                height: i32::from(b[1]) - i32::from(b[3]),
            }))
    }
    fn normalized_coordinate(&self, axis: u32) -> Result<Option<i16>, FontError> {
        if axis > u32::from(u16::MAX) {
            return Ok(None);
        }
        self.font.normalized_coordinate(axis as i32)
    }
    fn layout_delta(&self, outer: u16, inner: u16) -> Result<f32, FontError> {
        self.font.layout_delta(outer, inner)
    }
}
fn checked_integer(value: f32) -> Result<i32, FontError> {
    if !value.is_finite()
        || f64::from(value) < f64::from(i32::MIN)
        || f64::from(value) > f64::from(i32::MAX)
    {
        return Err(FontError::InvalidGlyph);
    }
    Ok(value as i32)
}
