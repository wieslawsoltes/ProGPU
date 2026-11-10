//! Original raw SKFontStyle values and separately normalized catalog requests.
use crate::font::ManagedFont;
use std::hash::{Hash, Hasher};

/// Original three distinct slants. Catalog matching groups italic and oblique;
/// style-set matching retains their distinction.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum FontSlant {
    #[default]
    Upright,
    Italic,
    Oblique,
}
/// Original named weight values; raw styles may retain other integer values.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(i32)]
pub enum FontWeight {
    Invisible = 0,
    Thin = 100,
    ExtraLight = 200,
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800,
    Black = 900,
    ExtraBlack = 1000,
}
/// Original named width classes.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(i32)]
pub enum FontWidth {
    UltraCondensed = 1,
    ExtraCondensed = 2,
    Condensed = 3,
    SemiCondensed = 4,
    Normal = 5,
    SemiExpanded = 6,
    Expanded = 7,
    ExtraExpanded = 8,
    UltraExpanded = 9,
}
/// Immutable raw public style; construction does not silently normalize values.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct FontStyle {
    pub weight: i32,
    pub width: i32,
    pub slant: FontSlant,
}
impl FontStyle {
    pub const NORMAL: Self = Self::new(400, 5, FontSlant::Upright);
    pub const BOLD: Self = Self::new(700, 5, FontSlant::Upright);
    pub const ITALIC: Self = Self::new(400, 5, FontSlant::Italic);
    pub const BOLD_ITALIC: Self = Self::new(700, 5, FontSlant::Italic);
    pub const fn new(weight: i32, width: i32, slant: FontSlant) -> Self {
        Self {
            weight,
            width,
            slant,
        }
    }
    pub fn request(self) -> FontStyleRequest {
        FontStyleRequest {
            weight: self.weight,
            width: self.width,
            slant: self.slant,
            optical_size: 0.0,
        }
    }
}
impl Default for FontStyle {
    fn default() -> Self {
        Self::NORMAL
    }
}
/// Original catalog request. Zero/default base selects normal at the matching
/// boundary; positive finite optical size selects the original opsz axis.
#[derive(Clone, Copy, Debug, Default)]
pub struct FontStyleRequest {
    pub weight: i32,
    pub width: i32,
    pub slant: FontSlant,
    pub optical_size: f32,
}
impl FontStyleRequest {
    pub const NORMAL: Self = Self {
        weight: 400,
        width: 5,
        slant: FontSlant::Upright,
        optical_size: 0.0,
    };
    pub fn normalized(self) -> Self {
        let unspecified = self.weight == 0 && self.width == 0 && self.slant == FontSlant::Upright;
        Self {
            weight: if unspecified {
                400
            } else {
                self.weight.clamp(1, 1000)
            },
            width: if unspecified {
                5
            } else {
                self.width.clamp(1, 9)
            },
            slant: self.slant,
            optical_size: if self.optical_size.is_finite() && self.optical_size > 0.0 {
                self.optical_size
            } else {
                0.0
            },
        }
    }
    pub fn from_font(font: &ManagedFont) -> Self {
        let m = font.metadata();
        let mut result = Self {
            weight: if m.weight_class == 0 {
                400
            } else {
                i32::from(m.weight_class)
            },
            width: if m.width_class == 0 {
                5
            } else {
                i32::from(m.width_class)
            },
            slant: if m.italic {
                FontSlant::Italic
            } else {
                FontSlant::Upright
            },
            optical_size: 0.0,
        };
        for setting in font.variation_settings() {
            if setting.tag.as_ref() == [111, 112, 115, 122]
                && setting.value.is_finite()
                && setting.value > 0.0
            {
                result.optical_size = setting.value;
                break;
            }
        }
        result
    }
    pub fn style(self) -> FontStyle {
        FontStyle::new(self.weight, self.width, self.slant)
    }
    pub(crate) fn distance(self, other: Self) -> i64 {
        let slant = if (self.slant == FontSlant::Upright) == (other.slant == FontSlant::Upright) {
            0
        } else {
            10_000
        };
        slant
            + (i64::from(self.width) - i64::from(other.width)).abs() * 1_000
            + (i64::from(self.weight) - i64::from(other.weight)).abs()
    }
}
pub(super) fn float_key(value: f32) -> u32 {
    if value == 0.0 {
        0
    } else if value.is_nan() {
        f32::NAN.to_bits()
    } else {
        value.to_bits()
    }
}
impl PartialEq for FontStyleRequest {
    fn eq(&self, other: &Self) -> bool {
        self.weight == other.weight
            && self.width == other.width
            && self.slant == other.slant
            && float_key(self.optical_size) == float_key(other.optical_size)
    }
}
impl Eq for FontStyleRequest {}
impl Hash for FontStyleRequest {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.weight.hash(state);
        self.width.hash(state);
        self.slant.hash(state);
        float_key(self.optical_size).hash(state);
    }
}
