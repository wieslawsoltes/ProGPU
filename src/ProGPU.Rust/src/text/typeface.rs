//! Original SKTypeface facade over the actual immutable managed font instance.
//! Factories never invent a system default or replace a missing platform source.
use super::canvas_font::{CanvasFont, CanvasFontError, TextEncoding};
use crate::font::{FontError, ManagedFont, Tag, normalize_managed_sfnt_container};
use std::{
    io::{Cursor, Read},
    path::Path,
    sync::{Arc, Mutex, OnceLock},
};
mod style;
mod tables;
mod variations;
pub use style::{FontSlant, FontStyle, FontStyleRequest, FontWeight, FontWidth};
pub use variations::{
    FontArguments, FontPaletteOverride, FontVariationAxis, FontVariationCoordinate,
};

/// Factory and selected-source errors. Malformed supported fonts use `None` in
/// nullable factories; direct container APIs retain their typed unsupported error.
#[derive(Debug, thiserror::Error)]
pub enum TypefaceError {
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Text(#[from] CanvasFontError),
    #[error("no default font is available from the selected catalog")]
    NoDefaultFont,
    #[error("invalid font facade argument")]
    InvalidArgument,
}
pub type TypefaceResult<T> = Result<T, TypefaceError>;
/// Independent original constructor metadata. Requested fields remain optional
/// individually; intrinsic selected-font properties fill only absent fields.
#[derive(Clone, Debug, Default)]
pub struct TypefaceOptions {
    pub family: Arc<str>,
    pub bold: bool,
    pub italic: bool,
    pub requested_weight: Option<i32>,
    pub requested_width: Option<i32>,
    pub requested_slant: Option<FontSlant>,
    pub empty: bool,
}
#[derive(Debug)]
struct State {
    font: ManagedFont,
    family: Arc<str>,
    bold: bool,
    italic: bool,
    requested_weight: Option<i32>,
    requested_width: Option<i32>,
    requested_slant: Option<FontSlant>,
    empty: bool,
    tables: OnceLock<Arc<[Tag]>>,
    variation: Mutex<Option<(Arc<[FontVariationCoordinate]>, ManagedFont)>>,
}
/// Cloneable immutable face identity. Cloning this handle retains the same
/// facade; variation/palette cloning selects a new immutable source instance.
#[derive(Clone, Debug)]
pub struct Typeface(Arc<State>);
impl Typeface {
    pub fn from_managed(font: ManagedFont) -> Self {
        let family = font.metadata().family.clone();
        Self::create(font, family, false, false, None, false)
    }
    /// The original empty facade retains an actual backing default font, while
    /// every public glyph/metadata query still observes the empty contract.
    pub fn empty(backing: ManagedFont) -> Self {
        Self::create(backing, Arc::from(""), false, false, None, true)
    }
    pub(crate) fn create(
        font: ManagedFont,
        family: Arc<str>,
        bold: bool,
        italic: bool,
        requested: Option<FontStyle>,
        empty: bool,
    ) -> Self {
        Self::with_options(
            font,
            TypefaceOptions {
                family,
                bold,
                italic,
                empty,
                requested_weight: requested.map(|s| s.weight),
                requested_width: requested.map(|s| s.width),
                requested_slant: requested.map(|s| s.slant),
            },
        )
    }
    pub fn with_options(font: ManagedFont, options: TypefaceOptions) -> Self {
        Self(Arc::new(State {
            font,
            family: options.family,
            bold: options.bold,
            italic: options.italic,
            requested_weight: options.requested_weight,
            requested_width: options.requested_width,
            requested_slant: options.requested_slant,
            empty: options.empty,
            tables: OnceLock::new(),
            variation: Mutex::new(None),
        }))
    }
    pub fn options(&self) -> TypefaceOptions {
        TypefaceOptions {
            family: self.0.family.clone(),
            bold: self.0.bold,
            italic: self.0.italic,
            requested_weight: self.0.requested_weight,
            requested_width: self.0.requested_width,
            requested_slant: self.0.requested_slant,
            empty: self.0.empty,
        }
    }
    pub fn with_requested_style(font: ManagedFont, style: FontStyle) -> Self {
        let family = font.metadata().family.clone();
        Self::create(
            font,
            family,
            style.weight >= 600,
            style.slant != FontSlant::Upright,
            Some(style),
            false,
        )
    }
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    pub fn font(&self) -> &ManagedFont {
        &self.0.font
    }
    pub fn family_name(&self) -> &str {
        &self.0.family
    }
    pub fn is_empty(&self) -> bool {
        self.0.empty
    }
    pub fn is_bold(&self) -> bool {
        self.0.bold
    }
    pub fn is_italic(&self) -> bool {
        self.0.italic
    }
    pub fn font_weight(&self) -> i32 {
        self.0.requested_weight.unwrap_or_else(|| {
            let value = self.font().metadata().weight_class;
            if value == 0 { 400 } else { i32::from(value) }
        })
    }
    pub fn font_width(&self) -> i32 {
        self.0.requested_width.unwrap_or_else(|| {
            let value = self.font().metadata().width_class;
            if value == 0 { 5 } else { i32::from(value) }
        })
    }
    pub fn font_slant(&self) -> FontSlant {
        self.0.requested_slant.unwrap_or_else(|| {
            if self.0.italic || self.font().metadata().italic {
                FontSlant::Italic
            } else {
                FontSlant::Upright
            }
        })
    }
    pub fn font_style(&self) -> FontStyle {
        FontStyle::new(
            self.font_weight(),
            self.font_width().clamp(1, 9),
            self.font_slant(),
        )
    }
    pub fn units_per_em(&self) -> u16 {
        if self.is_empty() {
            0
        } else {
            self.font().metadata().units_per_em
        }
    }
    pub fn glyph_count(&self) -> u16 {
        if self.is_empty() {
            0
        } else {
            self.font().metadata().glyph_count
        }
    }
    pub fn is_fixed_pitch(&self) -> bool {
        !self.is_empty() && self.font().metadata().fixed_pitch
    }
    pub fn postscript_name(&self) -> &str {
        if self.is_empty() {
            ""
        } else {
            &self.font().metadata().postscript_name
        }
    }
    pub fn to_font(&self, size: f32) -> CanvasFont {
        if self.is_empty() {
            CanvasFont::empty(size)
        } else {
            CanvasFont::new(self.font().clone(), size)
        }
    }
    pub fn to_font_scaled(&self, size: f32, scale_x: f32, skew_x: f32) -> CanvasFont {
        let mut font = self.to_font(size);
        font.scale_x = scale_x;
        font.skew_x = skew_x;
        font
    }
    pub fn glyph(&self, codepoint: i32) -> TypefaceResult<u16> {
        Ok(self.to_font(12.0).glyph(codepoint)?)
    }
    pub fn glyphs(&self, text: &str) -> TypefaceResult<Vec<u16>> {
        Ok(self.to_font(12.0).glyphs(text)?)
    }
    pub fn glyphs_from_codepoints(&self, codepoints: &[i32]) -> TypefaceResult<Vec<u16>> {
        Ok(self.to_font(12.0).glyphs_from_codepoints(codepoints)?)
    }
    pub fn encoded_glyphs(&self, bytes: &[u8], encoding: TextEncoding) -> TypefaceResult<Vec<u16>> {
        Ok(self.to_font(12.0).encoded_glyphs(bytes, encoding)?)
    }
    pub fn write_encoded_glyphs(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        output: &mut [u16],
    ) -> TypefaceResult<usize> {
        Ok(self
            .to_font(12.0)
            .write_encoded_glyphs(bytes, encoding, output)?)
    }
    pub fn count_glyphs(&self, bytes: &[u8], encoding: TextEncoding) -> usize {
        self.to_font(12.0).count_glyphs(bytes, encoding)
    }
    pub fn contains_glyph(&self, codepoint: i32) -> TypefaceResult<bool> {
        Ok(self.to_font(12.0).contains_glyph(codepoint)?)
    }
    pub fn contains_encoded_glyphs(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
    ) -> TypefaceResult<bool> {
        Ok(self
            .to_font(12.0)
            .contains_encoded_glyphs(bytes, encoding)?)
    }
    pub fn contains_codepoints(&self, codepoints: &[i32]) -> TypefaceResult<bool> {
        Ok(self.to_font(12.0).contains_codepoints(codepoints)?)
    }
    pub const fn has_kerning_pair_adjustments(&self) -> bool {
        false
    }
    pub fn kerning_pair_adjustments(&self, glyphs: &[u16]) -> Vec<i32> {
        vec![0; glyphs.len()]
    }
    pub fn write_kerning_pair_adjustments(
        &self,
        glyphs: &[u16],
        output: &mut [i32],
    ) -> TypefaceResult<bool> {
        let count = glyphs.len().saturating_sub(1);
        let prefix = output
            .get_mut(..count)
            .ok_or(TypefaceError::InvalidArgument)?;
        prefix.fill(0);
        Ok(false)
    }
    /// Copy complete source bytes, retaining the selected collection index.
    pub fn open_stream(&self) -> Option<(Cursor<Arc<[u8]>>, u32)> {
        (!self.is_empty()).then(|| {
            (
                Cursor::new(Arc::from(self.font().source().data())),
                self.font().source().face_index(),
            )
        })
    }
    /// Owned data factory. All collection directories are admitted by ManagedFont.
    pub fn from_data(data: impl Into<Arc<[u8]>>, face_index: i32) -> TypefaceResult<Option<Self>> {
        let Ok(index) = u32::try_from(face_index) else {
            return Ok(None);
        };
        let data = match normalize_managed_sfnt_container(data.into()) {
            Ok(data) => data,
            Err(error @ FontError::Capacity) => {
                return Err(error.into());
            }
            Err(_) => return Ok(None),
        };
        match ManagedFont::new(data, index) {
            Ok(font) => Ok(Some(Self::from_managed(font))),
            Err(error @ FontError::Capacity) => Err(error.into()),
            Err(_) => Ok(None),
        }
    }
    /// Read the remaining stream into owned bytes without taking ownership of
    /// the caller's reader. IO errors remain distinct from invalid font data.
    pub fn from_reader(reader: &mut impl Read, face_index: i32) -> TypefaceResult<Option<Self>> {
        let mut bytes = Vec::new();
        reader.read_to_end(&mut bytes)?;
        Self::from_data(bytes, face_index)
    }
    pub fn from_file(path: impl AsRef<Path>, face_index: i32) -> TypefaceResult<Option<Self>> {
        let path = path.as_ref();
        let mut file = match std::fs::File::open(path) {
            Ok(file) => file,
            Err(error)
                if error.kind() == std::io::ErrorKind::NotFound
                    && path
                        .parent()
                        .filter(|parent| !parent.as_os_str().is_empty())
                        .unwrap_or_else(|| Path::new("."))
                        .is_dir() =>
            {
                return Ok(None);
            }
            Err(error) if error.kind() == std::io::ErrorKind::InvalidInput => return Ok(None),
            Err(error) => return Err(error.into()),
        };
        Self::from_reader(&mut file, face_index)
    }
}
