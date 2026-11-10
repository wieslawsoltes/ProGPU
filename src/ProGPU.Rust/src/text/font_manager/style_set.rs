//! Original style sets retain loader owners and defer font materialization.
use super::*;
#[derive(Clone, Debug)]
struct Entry {
    style: FontStyle,
    name: Arc<str>,
    owner: Arc<SourceOwner>,
}
#[derive(Clone, Debug, Default)]
pub struct FontStyleSet {
    entries: Arc<[Entry]>,
}
impl FontStyleSet {
    pub fn len(&self) -> usize {
        self.entries.len()
    }
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
    pub fn style(&self, index: usize) -> Option<FontStyle> {
        self.entries.get(index).map(|entry| entry.style)
    }
    pub fn style_name(&self, index: usize) -> Option<&str> {
        self.entries.get(index).map(|entry| entry.name.as_ref())
    }
    pub fn styles(&self) -> impl ExactSizeIterator<Item = FontStyle> + '_ {
        self.entries.iter().map(|entry| entry.style)
    }
    pub fn create_typeface(&self, index: usize) -> FontManagerResult<Option<Typeface>> {
        let entry = self
            .entries
            .get(index)
            .ok_or(FontManagerError::InvalidArgument)?;
        Ok(entry.owner.try_load()?.map(Typeface::from_managed))
    }
    /// Style sets compare all three slants exactly, unlike catalog family matching.
    pub fn match_style(&self, style: FontStyle) -> FontManagerResult<Option<Typeface>> {
        let Some((index, _)) = self.entries.iter().enumerate().min_by_key(|(_, entry)| {
            let slant = if style.slant == entry.style.slant {
                0
            } else {
                10_000
            };
            slant
                + (i64::from(style.width) - i64::from(entry.style.width)).abs() * 1_000
                + (i64::from(style.weight) - i64::from(entry.style.weight)).abs()
        }) else {
            return Ok(None);
        };
        self.create_typeface(index)
    }
}
impl FontManager {
    pub fn get_family_name(&self, index: usize) -> FontManagerResult<Arc<str>> {
        self.families()
            .get(index)
            .cloned()
            .ok_or(FontManagerError::InvalidArgument)
    }
    pub fn styles_at(&self, index: usize) -> FontStyleSet {
        self.families()
            .get(index)
            .map_or_else(FontStyleSet::default, |family| {
                self.font_styles(Some(family))
            })
    }
    pub fn font_styles(&self, family: Option<&str>) -> FontStyleSet {
        let Some(family) = family else {
            return FontStyleSet::default();
        };
        let mut entries = Vec::new();
        let mut styles = HashSet::new();
        if !family.trim().is_empty() {
            for face in self
                .registered()
                .iter()
                .filter(|face| name_equal(&face.family, family))
            {
                entries.push(Entry {
                    style: face.style.style(),
                    name: style_name(face.style).into(),
                    owner: face.owner.clone(),
                });
                styles.insert(face.style);
            }
            for face in self
                .system
                .iter()
                .filter(|face| name_equal(&face.family, family))
            {
                if styles.insert(face.style) {
                    entries.push(Entry {
                        style: face.style.style(),
                        name: suffix(&face.name, &face.family).unwrap_or_else(|| face.name.clone()),
                        owner: face.owner.clone(),
                    });
                }
            }
        }
        if !entries.is_empty() {
            entries.sort_by_key(|entry| (entry.style.slant, entry.style.width, entry.style.weight));
            return FontStyleSet {
                entries: entries.into(),
            };
        }
        let mut selected: Vec<_> = self
            .system
            .iter()
            .filter(|face| name_equal(&face.family, family))
            .collect();
        if selected.is_empty() {
            let generic = GenericFontFamily::parse(family).or_else(|| {
                matches!(
                    family.trim().to_ascii_lowercase().as_str(),
                    "cursive" | "fantasy"
                )
                .then_some(GenericFontFamily::SansSerif)
            });
            if let Some(generic) = generic {
                for name in generic.preferences(self.platform) {
                    selected = self
                        .system
                        .iter()
                        .filter(|face| name_equal(&face.family, name))
                        .collect();
                    if !selected.is_empty() {
                        break;
                    }
                }
            }
        }
        for face in selected {
            let style = parse_style(&face.name);
            let name = suffix(&face.name, &face.family).unwrap_or_else(|| {
                if style == FontStyle::NORMAL {
                    Arc::from("Regular")
                } else {
                    face.name.clone()
                }
            });
            entries.push(Entry {
                style,
                name,
                owner: face.typeface_owner.clone(),
            });
        }
        FontStyleSet {
            entries: entries.into(),
        }
    }
    pub fn match_character_typeface(
        &self,
        family: Option<&str>,
        style: FontStyle,
        languages: Option<&[&str]>,
        codepoint: i32,
    ) -> FontManagerResult<Option<Typeface>> {
        Ok(self
            .match_character(family, style.request(), languages, codepoint, None)?
            .map(|result| Typeface::with_requested_style(result.font, style)))
    }
}
fn suffix(name: &str, family: &str) -> Option<Arc<str>> {
    let end = name
        .char_indices()
        .nth(family.chars().count())
        .map_or(name.len(), |(index, _)| index);
    if !name_equal(&name[..end], family) {
        return None;
    }
    let suffix = name[end..].trim_matches([' ', '-', '_']);
    (!suffix.is_empty()).then(|| Arc::from(suffix))
}
fn style_name(style: FontStyleRequest) -> String {
    let weight = match style.weight {
        i32::MIN..=100 => "Thin",
        101..=200 => "Extra Light",
        201..=300 => "Light",
        301..=400 => "Regular",
        401..=500 => "Medium",
        501..=600 => "Semi Bold",
        601..=700 => "Bold",
        701..=800 => "Extra Bold",
        _ => "Black",
    };
    if style.slant == FontSlant::Upright {
        weight.to_owned()
    } else {
        format!("{weight} Italic")
    }
}
fn parse_style(name: &str) -> FontStyle {
    let name = name.replace(' ', "").to_lowercase();
    let weight = if name.contains("extralight") || name.contains("ultralight") {
        200
    } else if name.contains("semibold") || name.contains("demibold") {
        600
    } else if name.contains("extrabold") || name.contains("ultrabold") {
        800
    } else if name.contains("thin") {
        100
    } else if name.contains("light") {
        300
    } else if name.contains("medium") {
        500
    } else if name.contains("black") || name.contains("heavy") {
        900
    } else if name.contains("bold") {
        700
    } else {
        400
    };
    let width = if name.contains("ultracondensed") {
        1
    } else if name.contains("extracondensed") {
        2
    } else if name.contains("semicondensed") {
        4
    } else if name.contains("condensed") {
        3
    } else if name.contains("ultraexpanded") {
        9
    } else if name.contains("extraexpanded") {
        8
    } else if name.contains("semiexpanded") {
        6
    } else if name.contains("expanded") {
        7
    } else {
        5
    };
    let slant = if name.contains("oblique") {
        FontSlant::Oblique
    } else if name.contains("italic") {
        FontSlant::Italic
    } else {
        FontSlant::Upright
    };
    FontStyle::new(weight, width, slant)
}
