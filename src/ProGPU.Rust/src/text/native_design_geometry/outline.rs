//! Original native TrueType/gvar/CFF1/CFF2 route selection and owned decoding.
use super::*;
use crate::font::{GlyphOutline, VariedGlyphOutline, cff};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDesignOutlineKind {
    TrueType,
    VariedTrueType,
    Cff1,
    Cff2,
}
#[derive(Debug)]
enum Owner {
    TrueType(GlyphOutline),
    Varied(VariedGlyphOutline),
    Cff1(cff::Outline),
    Cff2(cff::Outline2),
}
#[derive(Debug)]
pub struct NativeDesignOutline {
    font: SfntFace,
    glyph: u16,
    coordinates: Vec<i16>,
    owner: Owner,
}
impl NativeDesignOutline {
    pub(super) fn new(font: SfntFace, glyph: u16, coordinates: &[i16]) -> Result<Self> {
        let count = font.glyph_count().ok_or(FontError::InvalidFace)?;
        if glyph >= count {
            return Err(FontError::InvalidGlyph.into());
        }
        let owner = if font.table(Tag::new(*b"glyf")).is_some()
            && font.table(Tag::new(*b"loca")).is_some()
        {
            if coordinates.iter().any(|&c| c != 0) && font.table(Tag::new(*b"gvar")).is_some() {
                Owner::Varied(font.varied_glyph_outline(glyph, coordinates)?)
            } else {
                Owner::TrueType(font.glyph_outline(glyph)?)
            }
        } else if font.table(Tag::new(*b"CFF ")).is_some() {
            Owner::Cff1(font.cff1(count)?.outline(u32::from(glyph))?)
        } else if font.table(Tag::new(*b"CFF2")).is_some() {
            Owner::Cff2(font.cff2(count)?.outline(u32::from(glyph), coordinates)?)
        } else {
            return Err(FontError::UnsupportedOutline.into());
        };
        let mut frozen = reserved(coordinates.len())?;
        frozen.extend_from_slice(coordinates);
        let result = Self {
            font,
            glyph,
            coordinates: frozen,
            owner,
        };
        for segment in result.segments() {
            if segment.kind > 2 {
                return Err(FontError::InvalidGlyph.into());
            }
            segment.validate()?;
        }
        Ok(result)
    }
    pub fn font(&self) -> &SfntFace {
        &self.font
    }
    pub fn glyph_id(&self) -> u16 {
        self.glyph
    }
    pub fn normalized_coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    pub fn kind(&self) -> NativeDesignOutlineKind {
        match self.owner {
            Owner::TrueType(_) => NativeDesignOutlineKind::TrueType,
            Owner::Varied(_) => NativeDesignOutlineKind::VariedTrueType,
            Owner::Cff1(_) => NativeDesignOutlineKind::Cff1,
            Owner::Cff2(_) => NativeDesignOutlineKind::Cff2,
        }
    }
    pub fn segments(&self) -> &[Segment] {
        match &self.owner {
            Owner::TrueType(v) => v.segments(),
            Owner::Varied(v) => v.segments(),
            Owner::Cff1(v) => v.segments(),
            Owner::Cff2(v) => v.segments(),
        }
    }
    pub fn has_ink(&self) -> bool {
        !self.segments().is_empty()
    }
    pub fn copy_segments_into(&self, output: &mut [Segment]) -> Result<usize> {
        let source = self.segments();
        if output.len() < source.len() {
            return Err(FontError::Capacity.into());
        }
        output[..source.len()].copy_from_slice(source);
        Ok(source.len())
    }
}
