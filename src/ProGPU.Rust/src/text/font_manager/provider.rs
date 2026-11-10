//! Explicit filesystem discovery and portable host-owned catalog providers.
use super::*;
use crate::font::MetadataCharacterMap;
use std::{
    path::{Path, PathBuf},
    sync::OnceLock,
};
/// Selected host family policy. A policy value does not establish OS font access.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontPlatform {
    MacOs,
    Windows,
    Linux,
    Browser,
    Other,
}
impl FontPlatform {
    pub const fn current() -> Self {
        if cfg!(target_arch = "wasm32") {
            Self::Browser
        } else if cfg!(target_os = "macos") {
            Self::MacOs
        } else if cfg!(target_os = "windows") {
            Self::Windows
        } else if cfg!(target_os = "linux") {
            Self::Linux
        } else {
            Self::Other
        }
    }
}
/// Discovery produces a frozen metadata inventory, independently of font loading.
pub trait FontCatalogProvider {
    fn platform(&self) -> FontPlatform;
    fn faces(&self) -> FontManagerResult<Arc<[FontCatalogFace]>>;
}
#[derive(Clone, Debug)]
pub struct OwnedFontCatalog {
    platform: FontPlatform,
    faces: Arc<[FontCatalogFace]>,
}
impl OwnedFontCatalog {
    pub fn new(platform: FontPlatform, faces: impl Into<Arc<[FontCatalogFace]>>) -> Self {
        Self {
            platform,
            faces: faces.into(),
        }
    }
    /// Browser-owned bytes are loaded by the original portable font parser. No
    /// browser-installed/system-font permission is inferred by this adapter.
    pub fn browser(fonts: impl IntoIterator<Item = ManagedFont>) -> Self {
        Self::new(
            FontPlatform::Browser,
            fonts
                .into_iter()
                .map(FontCatalogFace::from_owned)
                .collect::<Vec<_>>(),
        )
    }
}
impl FontCatalogProvider for OwnedFontCatalog {
    fn platform(&self) -> FontPlatform {
        self.platform
    }
    fn faces(&self) -> FontManagerResult<Arc<[FontCatalogFace]>> {
        Ok(self.faces.clone())
    }
}
/// File source reads full immutable bytes only at actual font load. Compact cmap
/// reads use the original first-table, 64 MiB and collection/directory limits.
#[derive(Debug)]
pub struct FileFontSource {
    path: Arc<PathBuf>,
    face_index: u32,
    cmap: OnceLock<Result<Option<MetadataCharacterMap>, FontSourceError>>,
}
impl FileFontSource {
    pub fn new(path: impl Into<Arc<PathBuf>>, face_index: u32) -> Self {
        Self {
            path: path.into(),
            face_index,
            cmap: OnceLock::new(),
        }
    }
    pub fn path(&self) -> &Path {
        &self.path
    }
    pub fn face_index(&self) -> u32 {
        self.face_index
    }
}
impl FontSource for FileFontSource {
    fn load(&self) -> Result<ManagedFont, FontSourceError> {
        Ok(ManagedFont::new(
            normalize_managed_sfnt_container(std::fs::read(self.path.as_ref())?.into())?,
            self.face_index,
        )?)
    }
    fn metadata_glyph(&self, codepoint: u32) -> Result<u16, FontSourceError> {
        if codepoint > 0x10ffff {
            return Ok(0);
        }
        match self
            .cmap
            .get_or_init(|| metadata::character_map(&self.path, self.face_index))
        {
            Ok(Some(cmap)) => Ok(cmap.glyph_index(codepoint)?),
            Ok(None) => Ok(0),
            Err(error) => Err(error.clone()),
        }
    }
}
/// One immutable native scan. The caller supplies actual home/Windows-directory
/// information instead of fabricating process-global special-folder metadata.
#[derive(Clone, Debug)]
pub struct NativeFontCatalog {
    platform: FontPlatform,
    faces: Arc<[FontCatalogFace]>,
}
impl NativeFontCatalog {
    pub fn directories(
        platform: FontPlatform,
        home: Option<&Path>,
        windows: Option<&Path>,
    ) -> Vec<PathBuf> {
        let mut result = Vec::new();
        match platform {
            FontPlatform::MacOs => {
                result.extend(
                    [
                        "/System/Library/Fonts",
                        "/System/Library/Fonts/Supplemental",
                        "/Library/Fonts",
                    ]
                    .map(PathBuf::from),
                );
                if let Some(home) = home {
                    result.push(home.join("Library/Fonts"));
                }
            }
            FontPlatform::Windows => {
                if let Some(windows) = windows {
                    result.push(windows.join("Fonts"));
                }
            }
            FontPlatform::Linux => {
                result.extend(["/usr/share/fonts", "/usr/local/share/fonts"].map(PathBuf::from));
                if let Some(home) = home {
                    result.push(home.join(".fonts"));
                    result.push(home.join(".local/share/fonts"));
                }
            }
            FontPlatform::Browser | FontPlatform::Other => {}
        }
        result
    }
    pub fn discover(home: Option<&Path>, windows: Option<&Path>) -> FontManagerResult<Self> {
        let platform = FontPlatform::current();
        Self::scan(platform, &Self::directories(platform, home, windows))
    }
    pub fn scan(platform: FontPlatform, directories: &[PathBuf]) -> FontManagerResult<Self> {
        if cfg!(target_arch = "wasm32") || platform == FontPlatform::Browser {
            return Err(FontManagerError::UnsupportedProvider);
        }
        let mut files = HashSet::new();
        let mut faces = Vec::new();
        for root in directories {
            let mut pending = vec![(root.clone(), HashSet::new())];
            while let Some((directory, mut ancestors)) = pending.pop() {
                // A symlink may alias a directory but may not recurse into itself.
                // Sibling aliases retain their original file-path identities.
                if let Ok(canonical) = directory.canonicalize() {
                    if !ancestors.insert(canonical) {
                        continue;
                    }
                }
                let Ok(entries) = std::fs::read_dir(directory) else {
                    continue;
                };
                for entry in entries.flatten() {
                    let path = entry.path();
                    if path.is_dir() {
                        pending.push((path, ancestors.clone()));
                        continue;
                    }
                    let extension = path.extension().unwrap_or_default().to_string_lossy();
                    if !["ttf", "ttc", "otf"]
                        .iter()
                        .any(|value| extension.eq_ignore_ascii_case(value))
                    {
                        continue;
                    }
                    let text = path.to_string_lossy();
                    let key = if matches!(platform, FontPlatform::MacOs | FontPlatform::Windows) {
                        name_key(&text)
                    } else {
                        text.into_owned()
                    };
                    if !files.insert(key) {
                        continue;
                    }
                    faces.extend(Self::parse_file(&path)?);
                }
            }
        }
        let mut identities = HashSet::new();
        faces.retain(|face| {
            identities.insert((
                name_key(&face.name),
                name_key(&face.file_path.as_ref().unwrap().to_string_lossy()),
                face.face_index,
            ))
        });
        faces.sort_by_cached_key(|face| name_key(&face.name).encode_utf16().collect::<Vec<_>>());
        // Original per-catalog string pooling owns only strings reachable from
        // this immutable inventory, not a process-global intern table.
        let mut strings: HashMap<Arc<str>, Arc<str>> = HashMap::new();
        let mut paths: HashMap<PathBuf, Arc<PathBuf>> = HashMap::new();
        let mut owners: HashMap<(Arc<PathBuf>, u32), (Arc<SourceOwner>, Arc<SourceOwner>)> =
            HashMap::new();
        for face in &mut faces {
            for text in [&mut face.family, &mut face.name] {
                *text = strings
                    .entry(text.clone())
                    .or_insert_with(|| text.clone())
                    .clone();
            }
            if let Some(path) = face.file_path.as_mut() {
                *path = paths
                    .entry(path.as_ref().clone())
                    .or_insert_with(|| path.clone())
                    .clone();
                let pair = owners
                    .entry((path.clone(), face.face_index))
                    .or_insert_with(|| (face.owner.clone(), face.typeface_owner.clone()));
                face.owner = pair.0.clone();
                face.typeface_owner = pair.1.clone();
            }
        }
        Ok(Self {
            platform,
            faces: faces.into(),
        })
    }
    /// Original malformed/unreadable discovery fallback is one filename record;
    /// it does not claim that loading this record succeeds.
    pub fn parse_file(path: &Path) -> FontManagerResult<Vec<FontCatalogFace>> {
        match metadata::catalog(path) {
            Ok(faces) => Ok(faces),
            Err(FontSourceError::Font(FontError::Capacity)) => Err(FontError::Capacity.into()),
            Err(_) => {
                let name: Arc<str> = path
                    .file_stem()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .as_ref()
                    .into();
                Ok(vec![FontCatalogFace::from_file_metadata(
                    path.to_owned(),
                    0,
                    name.clone(),
                    name,
                    FontStyleRequest::NORMAL,
                )])
            }
        }
    }
}
impl FontCatalogProvider for NativeFontCatalog {
    fn platform(&self) -> FontPlatform {
        self.platform
    }
    fn faces(&self) -> FontManagerResult<Arc<[FontCatalogFace]>> {
        Ok(self.faces.clone())
    }
}
