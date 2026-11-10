//! Original native color/bitmap font queries; independent of managed probes.
//! All encoded data views borrow the selected SfntFace's actual immutable bytes.
use crate::font::{FontError, SfntFace, Tag};
mod bitmap;
mod color;
mod svg;
pub use bitmap::NativeBitmapGlyphData;
pub use color::{
    NativeColorGlyphLayer, NativeColorGlyphLayers, NativeColorPalette, NativeColorPaletteOverride,
    NativeColorRgba8, NativePaletteColor,
};
pub use svg::NativeSvgGlyphDocument;

type Result<T> = std::result::Result<T, FontError>;
fn u16be(data: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_be_bytes(
        data.get(offset..offset.checked_add(2)?)?.try_into().ok()?,
    ))
}
fn u32be(data: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_be_bytes(
        data.get(offset..offset.checked_add(4)?)?.try_into().ok()?,
    ))
}
fn slice(data: &[u8], offset: usize, length: usize) -> Option<&[u8]> {
    data.get(offset..offset.checked_add(length)?)
}
fn glyph_count(face: &SfntFace, glyph: u16) -> Result<u16> {
    face.glyph_count()
        .filter(|count| glyph < *count)
        .ok_or(FontError::InvalidArgument)
}
/// Original default-coordinate outline source; this query does not invent a
/// varied TrueType generation or pad the empty CFF2 coordinate inventory.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeGlyphOutlineSource {
    #[default]
    None,
    TrueTypeStatic,
    Cff1,
    Cff2,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeGlyphOutlineRequirements {
    pub source: NativeGlyphOutlineSource,
    pub path_segment_count: u32,
}
impl SfntFace {
    /// Exact source selection for the native fallback candidate's empty
    /// normalized-coordinate request. glyf+loca wins even for missing/empty ink;
    /// a present CFF1 table never falls through to CFF2 after a decoding error.
    pub fn native_default_outline_requirements(
        &self,
        glyph: u16,
    ) -> Result<NativeGlyphOutlineRequirements> {
        let count = self
            .glyph_count()
            .filter(|count| glyph < *count)
            .ok_or(FontError::InvalidGlyph)?;
        if self.table(Tag::new(*b"glyf")).is_some() && self.table(Tag::new(*b"loca")).is_some() {
            return Ok(NativeGlyphOutlineRequirements {
                source: NativeGlyphOutlineSource::TrueTypeStatic,
                path_segment_count: 0,
            });
        }
        if self.table(Tag::new(*b"CFF ")).is_some() {
            return Ok(NativeGlyphOutlineRequirements {
                source: NativeGlyphOutlineSource::Cff1,
                path_segment_count: self.cff1(count)?.outline_segment_count(u32::from(glyph))?,
            });
        }
        if self.table(Tag::new(*b"CFF2")).is_some() {
            return Ok(NativeGlyphOutlineRequirements {
                source: NativeGlyphOutlineSource::Cff2,
                path_segment_count: self
                    .cff2(count)?
                    .outline_segment_count(u32::from(glyph), &[])?,
            });
        }
        Ok(NativeGlyphOutlineRequirements::default())
    }
    /// Original try_open_candidate renderability order after cmap selection.
    /// A color/bitmap/SVG lookup is capability, not pixel or decoder qualification.
    /// The caller retains the independently selected nonzero cmap glyph slot.
    pub fn native_has_renderable_glyph(&self, code_point: u32, glyph: u16) -> bool {
        if glyph == 0 {
            return false;
        }
        if self
            .glyph_data(glyph)
            .is_some_and(|value| !value.bytes.is_empty())
        {
            return true;
        }
        if self
            .native_default_outline_requirements(glyph)
            .is_ok_and(|value| {
                matches!(
                    value.source,
                    NativeGlyphOutlineSource::Cff1 | NativeGlyphOutlineSource::Cff2
                ) && value.path_segment_count != 0
            })
        {
            return true;
        }
        if self
            .native_colr_layer_count(glyph)
            .is_ok_and(|count| count != 0)
        {
            return true;
        }
        if self
            .native_svg_glyph_document(glyph)
            .is_ok_and(|document| !document.bytes.is_empty())
        {
            return true;
        }
        if self.native_sbix_glyph(glyph, 64.0).is_ok()
            || self.native_cbdt_glyph(glyph, 64.0).is_ok()
        {
            return true;
        }
        use crate::text::unicode::{GeneralCategory as C, general_category};
        matches!(
            general_category(code_point),
            C::Control | C::Format | C::SpaceSeparator | C::LineSeparator | C::ParagraphSeparator
        )
    }
}
