//! Original platform-neutral font catalog, selected-source ownership and bounded
//! style/character matching. Host registration is also the browser provider path.
use super::{
    typeface::{FontSlant, FontStyle, FontStyleRequest, Typeface, TypefaceError},
    unicode::{GeneralCategory, general_category},
};
use crate::font::{
    FontError, ManagedFont, ManagedVariationSetting, normalize_managed_sfnt_container,
};
use std::{
    collections::{HashMap, HashSet, VecDeque},
    hash::Hash,
    path::PathBuf,
    sync::{Arc, Mutex},
};
mod fallback;
mod matching;
mod metadata;
mod provider;
mod source;
mod style_set;
pub use fallback::{GenericFontFamily, fallback_family_preferences};
pub use provider::{
    FileFontSource, FontCatalogProvider, FontPlatform, NativeFontCatalog, OwnedFontCatalog,
};
use source::SourceOwner;
pub use source::{CallbackFontSource, FontSource, FontSourceError, OwnedFontSource};
pub use style_set::FontStyleSet;

#[derive(Debug, thiserror::Error)]
pub enum FontManagerError {
    #[error(transparent)]
    Source(#[from] FontSourceError),
    #[error(transparent)]
    Font(#[from] FontError),
    #[error(transparent)]
    Typeface(#[from] TypefaceError),
    #[error("invalid font manager argument")]
    InvalidArgument,
    #[error("this font provider is unavailable on the selected target")]
    UnsupportedProvider,
}
pub type FontManagerResult<T> = Result<T, FontManagerError>;
/// Immutable catalog metadata plus actual selected provider identity. Multiple
/// records may share a source; metadata is never a substitute for a loaded font.
#[derive(Clone, Debug)]
pub struct FontCatalogFace {
    pub family: Arc<str>,
    pub name: Arc<str>,
    pub style: FontStyleRequest,
    pub file_path: Option<Arc<PathBuf>>,
    pub face_index: u32,
    owner: Arc<SourceOwner>,
    typeface_owner: Arc<SourceOwner>,
}
impl FontCatalogFace {
    pub fn new(
        family: impl Into<Arc<str>>,
        name: impl Into<Arc<str>>,
        style: FontStyleRequest,
        source: Arc<dyn FontSource>,
    ) -> Self {
        Self {
            family: family.into(),
            name: name.into(),
            style,
            file_path: None,
            face_index: 0,
            owner: SourceOwner::new(source.clone(), true),
            typeface_owner: SourceOwner::new(source, true),
        }
    }
    pub fn from_owned(font: ManagedFont) -> Self {
        let m = font.metadata();
        Self::new(
            m.family.clone(),
            m.full_name.clone(),
            FontStyleRequest::from_font(&font),
            Arc::new(OwnedFontSource(font)),
        )
    }
    pub(super) fn from_file_metadata(
        path: PathBuf,
        face_index: u32,
        family: Arc<str>,
        name: Arc<str>,
        style: FontStyleRequest,
    ) -> Self {
        let path = Arc::new(path);
        Self {
            family,
            name,
            style,
            file_path: Some(path.clone()),
            face_index,
            owner: SourceOwner::new(
                Arc::new(FileFontSource::new(path.clone(), face_index)),
                true,
            ),
            typeface_owner: SourceOwner::new(Arc::new(FileFontSource::new(path, face_index)), true),
        }
    }
    pub fn load(&self) -> Result<ManagedFont, FontSourceError> {
        self.owner.load()
    }
    pub fn metadata_glyph(&self, codepoint: u32) -> Result<u16, FontSourceError> {
        self.owner.source.metadata_glyph(codepoint)
    }
    pub fn same_source(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.owner, &other.owner)
    }
    fn visit_key(&self) -> SourceVisit {
        self.file_path
            .as_ref()
            .map_or(SourceVisit::Owner(self.owner.id), |path| {
                SourceVisit::File(path.clone(), self.face_index)
            })
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
enum SourceVisit {
    Owner(u64),
    File(Arc<PathBuf>, u32),
}
#[derive(Clone, Debug)]
struct RegisteredFace {
    family: Arc<str>,
    style: FontStyleRequest,
    fallback: bool,
    owner: Arc<SourceOwner>,
}
#[derive(Clone, Debug)]
pub struct CharacterMatch {
    pub font: ManagedFont,
    pub glyph_index: u16,
}
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
struct CharacterKey {
    family: Option<String>,
    style: FontStyleRequest,
    codepoint: i32,
    excluded: Option<ManagedFont>,
    version: u64,
}
struct Fifo<K, V> {
    values: HashMap<K, V>,
    order: VecDeque<K>,
}
impl<K, V> Default for Fifo<K, V> {
    fn default() -> Self {
        Self {
            values: HashMap::new(),
            order: VecDeque::new(),
        }
    }
}
impl<K: Eq + Hash + Clone, V: Clone> Fifo<K, V> {
    fn get(&self, key: &K) -> Option<V> {
        self.values.get(key).cloned()
    }
    fn insert(&mut self, key: K, value: V, capacity: usize) -> V {
        if let Some(previous) = self.values.get(&key) {
            return previous.clone();
        }
        self.values.insert(key.clone(), value.clone());
        self.order.push_back(key);
        while self.values.len() > capacity {
            if let Some(oldest) = self.order.pop_front() {
                self.values.remove(&oldest);
            }
        }
        value
    }
    fn clear(&mut self) {
        self.values.clear();
        self.order.clear();
    }
}
#[derive(Default)]
struct CatalogState {
    registered: Arc<[RegisteredFace]>,
    version: u64,
    platform_fallback: Option<ManagedFont>,
    default_typeface: Option<Typeface>,
    empty_typeface: Option<Typeface>,
    styles: Fifo<(ManagedFont, FontStyleRequest), ManagedFont>,
    variations: Fifo<(ManagedFont, FontStyleRequest), ManagedFont>,
    characters: Fifo<CharacterKey, Option<CharacterMatch>>,
}
/// Caller-owned catalog. Shared clones preserve registrations and all cache
/// identities; independent catalogs cannot accidentally select each other's default.
#[derive(Clone)]
pub struct FontManager {
    system: Arc<[FontCatalogFace]>,
    platform: FontPlatform,
    state: Arc<Mutex<CatalogState>>,
}
impl std::fmt::Debug for FontManager {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FontManager")
            .field("system_faces", &self.system.len())
            .field("platform", &self.platform)
            .finish_non_exhaustive()
    }
}
impl FontManager {
    pub fn new(platform: FontPlatform, faces: impl Into<Arc<[FontCatalogFace]>>) -> Self {
        Self {
            system: faces.into(),
            platform,
            state: Arc::new(Mutex::new(CatalogState::default())),
        }
    }
    pub fn from_provider(provider: &impl FontCatalogProvider) -> FontManagerResult<Self> {
        Ok(Self::new(provider.platform(), provider.faces()?))
    }
    pub fn browser() -> Self {
        Self::new(FontPlatform::Browser, Arc::<[FontCatalogFace]>::from([]))
    }
    pub fn system_faces(&self) -> &[FontCatalogFace] {
        &self.system
    }
    pub fn platform(&self) -> FontPlatform {
        self.platform
    }
    pub fn catalog_version(&self) -> u64 {
        self.state.lock().unwrap_or_else(|e| e.into_inner()).version
    }
    fn registered(&self) -> Arc<[RegisteredFace]> {
        self.state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .registered
            .clone()
    }
    pub fn register_font(&self, font: ManagedFont, fallback: bool) -> FontManagerResult<()> {
        let m = font.metadata();
        let family = if m.family.trim().is_empty() {
            m.full_name.clone()
        } else {
            m.family.clone()
        };
        let style = FontStyleRequest::from_font(&font);
        let owner = SourceOwner::new(Arc::new(OwnedFontSource(font)), false);
        owner.load()?;
        self.register(RegisteredFace {
            family,
            style,
            fallback,
            owner,
        });
        Ok(())
    }
    pub fn register_data(
        &self,
        data: impl Into<Arc<[u8]>>,
        face_index: u32,
        fallback: bool,
    ) -> FontManagerResult<ManagedFont> {
        let font = ManagedFont::new(normalize_managed_sfnt_container(data.into())?, face_index)?;
        self.register_font(font.clone(), fallback)?;
        Ok(font)
    }
    /// Anonymous lazy sources participate in fallback, but no family is invented
    /// before their real font is loaded. Loader identity prevents duplicates.
    pub fn register_source(&self, source: Arc<dyn FontSource>, fallback: bool) {
        self.register(RegisteredFace {
            family: Arc::from(""),
            style: FontStyleRequest::NORMAL,
            fallback,
            owner: SourceOwner::new(source, false),
        });
    }
    pub fn register_named_source(
        &self,
        family: &str,
        style: FontStyleRequest,
        source: Arc<dyn FontSource>,
        fallback: bool,
    ) -> FontManagerResult<()> {
        if family.trim().is_empty() {
            return Err(FontManagerError::InvalidArgument);
        }
        self.register(RegisteredFace {
            family: Arc::from(family.trim()),
            style: style.normalized(),
            fallback,
            owner: SourceOwner::new(source, false),
        });
        Ok(())
    }
    fn register(&self, face: RegisteredFace) {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let loaded = face.owner.loaded();
        if state.registered.iter().any(|current| {
            Arc::ptr_eq(&current.owner.source, &face.owner.source)
                || loaded
                    .as_ref()
                    .is_some_and(|value| current.owner.loaded().as_ref() == Some(value))
        }) {
            return;
        }
        let mut entries = state.registered.to_vec();
        entries.push(face);
        state.registered = entries.into();
        state.version = state.version.wrapping_add(1);
        state.styles.clear();
        state.variations.clear();
        state.characters.clear();
    }
    /// First host-provided platform fallback is preserved; all later sources
    /// still enter the original ordered fallback catalog.
    pub fn register_platform_fallback(&self, font: ManagedFont) -> FontManagerResult<()> {
        self.register_font(font.clone(), true)?;
        self.state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .platform_fallback
            .get_or_insert(font);
        Ok(())
    }
    pub fn platform_fallback(&self) -> Option<ManagedFont> {
        self.state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .platform_fallback
            .clone()
    }
    pub fn families(&self) -> Vec<Arc<str>> {
        let mut seen = HashSet::new();
        let mut output = Vec::new();
        for face in self.registered().iter() {
            if !face.family.trim().is_empty() && seen.insert(name_key(&face.family)) {
                output.push(face.family.clone());
            }
        }
        for face in self.system.iter() {
            if seen.insert(name_key(&face.family)) {
                output.push(face.family.clone());
            }
        }
        output.sort_by_cached_key(|name| name_key(name).encode_utf16().collect::<Vec<_>>());
        output
    }
    pub fn family_count(&self) -> usize {
        self.families().len()
    }
    pub fn match_family(
        &self,
        family: Option<&str>,
        style: FontStyleRequest,
    ) -> FontManagerResult<Option<ManagedFont>> {
        let Some(family) = family.filter(|value| !value.trim().is_empty()) else {
            return Ok(None);
        };
        let style = style.normalized();
        let registered = self.registered();
        if let Some(face) = registered
            .iter()
            .filter(|face| name_equal(&face.family, family))
            .min_by_key(|face| face.style.distance(style))
        {
            return self.apply_optional(face.owner.try_load()?, style);
        }
        let face = self.find_system(family, style, None)?;
        self.apply_optional(
            match face {
                Some(face) => face.owner.try_load()?,
                None => None,
            },
            style,
        )
    }
    fn find_system(
        &self,
        family: &str,
        style: FontStyleRequest,
        codepoint: Option<u32>,
    ) -> FontManagerResult<Option<&FontCatalogFace>> {
        let mut best = None;
        let mut distance = i64::MAX;
        for face in self.system.iter() {
            if !name_equal(&face.family, family) && !name_equal(&face.name, family) {
                continue;
            }
            if let Some(codepoint) = codepoint {
                if !metadata_coverage(face, codepoint)? {
                    continue;
                }
            }
            let next = face.style.distance(style);
            if next < distance {
                best = Some(face);
                distance = next;
            }
        }
        Ok(best)
    }
    fn apply_optional(
        &self,
        font: Option<ManagedFont>,
        style: FontStyleRequest,
    ) -> FontManagerResult<Option<ManagedFont>> {
        font.map(|font| self.apply_style(&font, style)).transpose()
    }
    fn apply_style(
        &self,
        font: &ManagedFont,
        style: FontStyleRequest,
    ) -> FontManagerResult<ManagedFont> {
        if font.variation_axes()?.is_empty() {
            return Ok(font.clone());
        }
        let key = (font.clone(), style);
        if let Some(value) = self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .variations
            .get(&key)
        {
            return Ok(value);
        }
        let value = apply_variations(font, style)?;
        Ok(self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .variations
            .insert(key, value, 256))
    }
    pub fn match_font(
        &self,
        font: &ManagedFont,
        style: FontStyleRequest,
    ) -> FontManagerResult<ManagedFont> {
        let style = style.normalized();
        let axes = font.variation_axes()?;
        if !axes.is_empty() {
            let mismatch = font.metadata().italic != (style.slant != FontSlant::Upright);
            let supported = axes
                .iter()
                .any(|axis| matches!(&axis.tag, b"ital" | b"slnt"));
            if !mismatch || supported {
                return self.apply_style(font, style);
            }
        } else if FontStyleRequest::from_font(font).distance(style) == 0 {
            return Ok(font.clone());
        }
        let key = (font.clone(), style);
        if let Some(value) = self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .styles
            .get(&key)
        {
            return Ok(value);
        }
        let matched = self.match_family(Some(&font.metadata().family), style)?;
        let value = if !axes.is_empty() {
            match matched {
                Some(value) if !value.same_instance(font) => value,
                _ => apply_variations(font, style)?,
            }
        } else {
            matched.unwrap_or_else(|| font.clone())
        };
        Ok(self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .styles
            .insert(key, value, 256))
    }
    pub fn match_typeface(&self, face: &Typeface, style: FontStyle) -> FontManagerResult<Typeface> {
        Ok(Typeface::with_requested_style(
            self.match_font(face.font(), style.request())?,
            style,
        ))
    }
}
fn apply_variations(font: &ManagedFont, style: FontStyleRequest) -> Result<ManagedFont, FontError> {
    let mut settings = Vec::with_capacity(4);
    for axis in font.variation_axes()? {
        let value = match &axis.tag {
            b"wght" => style.weight as f32,
            b"wdth" => match style.width {
                i32::MIN..=1 => 50.0,
                2 => 62.5,
                3 => 75.0,
                4 => 87.5,
                5 => 100.0,
                6 => 112.5,
                7 => 125.0,
                8 => 150.0,
                _ => 200.0,
            },
            b"ital" => {
                if style.slant == FontSlant::Upright {
                    axis.minimum
                } else {
                    axis.maximum
                }
            }
            b"slnt" => {
                if style.slant == FontSlant::Upright {
                    0.0f32.clamp(axis.minimum, axis.maximum)
                } else if axis.minimum.abs() >= axis.maximum.abs() {
                    axis.minimum
                } else {
                    axis.maximum
                }
            }
            b"opsz" if style.optical_size > 0.0 => style.optical_size,
            _ => continue,
        };
        settings.push(ManagedVariationSetting::new(
            Arc::<[u16]>::from(axis.tag.map(u16::from)),
            value,
        ));
    }
    if settings.is_empty() {
        Ok(font.clone())
    } else {
        font.with_variations(&settings)
    }
}
fn metadata_coverage(face: &FontCatalogFace, codepoint: u32) -> FontManagerResult<bool> {
    match face.metadata_glyph(codepoint) {
        Ok(glyph) => Ok(glyph != 0),
        Err(error @ FontSourceError::Font(FontError::Capacity)) => Err(error.into()),
        Err(_) => Ok(false),
    }
}
// Original ordinal matching uses simple case conversion (no linguistic expansion).
// Rust host Unicode tables remain an explicit cross-runtime qualification boundary.
fn upper_scalar(ch: char) -> char {
    // Ordinal comparison deliberately excludes the linguistic dotless-I fold.
    if ch == '\u{131}' {
        return ch;
    }
    let mut upper = ch.to_uppercase();
    let first = upper.next().unwrap_or(ch);
    if upper.next().is_none() { first } else { ch }
}
fn name_key(value: &str) -> String {
    value.chars().map(upper_scalar).collect()
}
fn name_equal(left: &str, right: &str) -> bool {
    left == right
        || left
            .chars()
            .map(upper_scalar)
            .eq(right.chars().map(upper_scalar))
}
