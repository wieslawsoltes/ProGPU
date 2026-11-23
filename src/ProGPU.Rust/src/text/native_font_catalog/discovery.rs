//! Original all-or-nothing metadata discovery and platform directory inventory.
use super::*;
use std::{collections::HashSet, ffi::OsStr, fs, io, path::Component};
/// Exact host conversion used by original filesystem::path::string(). Native
/// Unix paths use their original bytes. Windows narrow names depend on the host
/// code page; a caller can supply that actual producer without UTF-8 inference.
pub trait NativeCatalogPathEncoder {
    fn encode_native_path(&self, path: &Path) -> Result<Vec<u8>, NativeCatalogError>;
}
struct DefaultPathEncoder;
impl NativeCatalogPathEncoder for DefaultPathEncoder {
    fn encode_native_path(&self, path: &Path) -> Result<Vec<u8>, NativeCatalogError> {
        let bytes = path.as_os_str().as_encoded_bytes();
        if cfg!(windows) && !bytes.is_ascii() {
            return Err(NativeCatalogError::Filesystem);
        }
        let mut result = reserved(bytes.len())?;
        result.extend_from_slice(bytes);
        Ok(result)
    }
}
fn absolute(path: &Path) -> Result<PathBuf, NativeCatalogError> {
    let path = std::path::absolute(path).map_err(|_| NativeCatalogError::Filesystem)?;
    let mut result = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                result.pop();
            }
            other => result.push(other),
        }
    }
    Ok(result)
}
fn font_extension(path: &Path) -> bool {
    path.extension().is_some_and(|extension| {
        let value = extension.as_encoded_bytes();
        equal(value, b"ttf") || equal(value, b"ttc") || equal(value, b"otf")
    })
}
fn decoded(font: &SfntFace, name: u16) -> Result<Option<Vec<u8>>, NativeCatalogError> {
    match font.native_name(name) {
        Ok(value) => Ok(value.map(String::into_bytes)),
        Err(FontError::Capacity) => Err(NativeCatalogError::OutOfMemory),
        Err(_) => Ok(None),
    }
}
fn read_entries(
    path: &Path,
    encoder: &impl NativeCatalogPathEncoder,
    measured: &mut NativeCatalogScanMetrics,
) -> Result<Vec<Entry>, NativeCatalogError> {
    let path = absolute(path)?;
    let source_path = encoder.encode_native_path(&path)?;
    let fallback = encoder.encode_native_path(Path::new(
        path.file_stem().unwrap_or_else(|| OsStr::new("")),
    ))?;
    let mut reader = reader::Reader::open(&path, Some(&mut measured.bytes_read))?;
    let offsets = reader.face_offsets()?;
    let mut entries = reserved(offsets.len())?;
    for (index, offset) in offsets.into_iter().enumerate() {
        let font = reader.compact(
            offset,
            &[Tag::new(*b"name"), Tag::new(*b"OS/2"), Tag::new(*b"head")],
        )?;
        let family = if let Some(name) = decoded(&font, 16)? {
            name
        } else if let Some(name) = decoded(&font, 1)? {
            name
        } else {
            let mut name = reserved(fallback.len())?;
            name.extend_from_slice(&fallback);
            name
        };
        let full = if let Some(name) = decoded(&font, 4)? {
            name
        } else {
            let mut name = reserved(family.len())?;
            name.extend_from_slice(&family);
            name
        };
        let style = font.native_face_style();
        entries.push(Entry {
            info: NativeCatalogFaceInfo {
                full_name: full.into(),
                family_identity: hash_normalized(&family),
                family_name: family.into(),
                source_path: Arc::from(source_path.as_slice()),
                file_path: path.clone(),
                identity: face_identity(&source_path, index as u32),
                face_index: index as u32,
                weight: style.weight,
                width: style.width as u8,
                slant: if style.italic {
                    NativeFontProviderSlant::Italic
                } else {
                    NativeFontProviderSlant::Normal
                },
            },
        });
    }
    Ok(entries)
}
impl NativeSystemFontCatalog {
    /// Original ordered directories, including per-user Windows and Android
    /// roots. No host filesystem scan occurs until explicit discovery is called.
    pub fn system_font_directories() -> Vec<PathBuf> {
        let mut paths = Vec::new();
        let mut add = |path: PathBuf| {
            if !path.as_os_str().is_empty() && !paths.contains(&path) {
                paths.push(path);
            }
        };
        let environment = |name: &str, suffix: &str| {
            std::env::var_os(name)
                .filter(|v| !v.is_empty())
                .map(|v| PathBuf::from(v).join(suffix))
        };
        if cfg!(target_vendor = "apple") {
            for path in [
                "/System/Library/Fonts",
                "/System/Library/Fonts/Supplemental",
                "/Library/Fonts",
            ] {
                add(path.into());
            }
            if let Some(path) = environment("HOME", "Library/Fonts") {
                add(path);
            }
        } else if cfg!(windows) {
            if let Some(path) = environment("WINDIR", "Fonts") {
                add(path);
            }
            if let Some(path) = environment("LOCALAPPDATA", "Microsoft/Windows/Fonts") {
                add(path);
            }
        } else if cfg!(target_os = "android") {
            for path in ["/system/fonts", "/product/fonts", "/vendor/fonts"] {
                add(path.into());
            }
        } else if cfg!(target_os = "linux") {
            add("/usr/share/fonts".into());
            add("/usr/local/share/fonts".into());
            if let Some(path) = environment("HOME", ".fonts") {
                add(path);
            }
            if let Some(path) = environment("HOME", ".local/share/fonts") {
                add(path);
            }
            if let Some(path) = environment("XDG_DATA_HOME", "fonts") {
                add(path);
            }
        }
        paths
    }
    pub fn discover_system_fonts(
        &mut self,
    ) -> Result<NativeCatalogScanMetrics, NativeCatalogScanError> {
        let paths = Self::system_font_directories();
        let mut views = reserved(paths.len()).map_err(|error| NativeCatalogScanError {
            error,
            metrics: NativeCatalogScanMetrics::default(),
        })?;
        views.extend(paths.iter().map(PathBuf::as_path));
        self.discover_fonts(&views)
    }
    /// Publish a complete replacement only after every discovered file and final
    /// sort. Unreadable/malformed files are skipped; OOM retains the old catalog.
    /// Recursive iteration follows directory entries but never directory symlinks.
    pub fn discover_fonts(
        &mut self,
        directories: &[&Path],
    ) -> Result<NativeCatalogScanMetrics, NativeCatalogScanError> {
        self.discover_fonts_with_path_encoder(directories, &DefaultPathEncoder)
    }
    pub fn discover_fonts_with_path_encoder(
        &mut self,
        directories: &[&Path],
        encoder: &impl NativeCatalogPathEncoder,
    ) -> Result<NativeCatalogScanMetrics, NativeCatalogScanError> {
        let mut metrics = NativeCatalogScanMetrics::default();
        let result = discover(directories, encoder, &mut metrics).and_then(|entries| {
            if entries.len() > u32::MAX as usize {
                return Err(NativeCatalogError::OutOfMemory);
            }
            let mut cmaps = reserved(entries.len())?;
            cmaps.resize_with(entries.len(), CharacterMap::default);
            metrics.face_count = entries.len() as u32;
            self.entries = entries;
            *self
                .cache
                .get_mut()
                .unwrap_or_else(std::sync::PoisonError::into_inner) = Cache {
                cmaps,
                ..Cache::default()
            };
            Ok(())
        });
        result
            .map(|()| metrics)
            .map_err(|error| NativeCatalogScanError { error, metrics })
    }
}
fn discover(
    directories: &[&Path],
    encoder: &impl NativeCatalogPathEncoder,
    metrics: &mut NativeCatalogScanMetrics,
) -> Result<Vec<Entry>, NativeCatalogError> {
    let mut discovered = Vec::new();
    let mut visited = HashSet::new();
    for &root in directories {
        if root.as_os_str().is_empty() || !root.is_dir() {
            continue;
        }
        metrics.directory_count = metrics.directory_count.wrapping_add(1);
        let Ok(directory) = fs::read_dir(root) else {
            continue;
        };
        let mut stack = reserved(1)?;
        stack.push(directory);
        while let Some(iterator) = stack.last_mut() {
            let Some(next) = iterator.next() else {
                stack.pop();
                continue;
            };
            let entry = match next {
                Ok(entry) => entry,
                Err(error) if error.kind() == io::ErrorKind::PermissionDenied => continue,
                Err(_) => break,
            };
            let path = entry.path();
            if entry.metadata().is_ok_and(|metadata| metadata.is_file()) && font_extension(&path) {
                let mut key = match absolute(&path).and_then(|p| encoder.encode_native_path(&p)) {
                    Ok(path) => path,
                    Err(NativeCatalogError::OutOfMemory) => {
                        return Err(NativeCatalogError::OutOfMemory);
                    }
                    Err(_) => {
                        metrics.skipped_file_count = metrics.skipped_file_count.wrapping_add(1);
                        continue;
                    }
                };
                if cfg!(target_vendor = "apple") || cfg!(windows) {
                    key.iter_mut().for_each(|byte| *byte = folded(*byte));
                }
                visited
                    .try_reserve(1)
                    .map_err(|_| NativeCatalogError::OutOfMemory)?;
                if visited.insert(key) {
                    metrics.file_count = metrics.file_count.wrapping_add(1);
                    match read_entries(&path, encoder, metrics) {
                        Ok(mut entries) => {
                            discovered
                                .try_reserve(entries.len())
                                .map_err(|_| NativeCatalogError::OutOfMemory)?;
                            discovered.append(&mut entries);
                        }
                        Err(NativeCatalogError::OutOfMemory) => {
                            return Err(NativeCatalogError::OutOfMemory);
                        }
                        Err(_) => {
                            metrics.skipped_file_count = metrics.skipped_file_count.wrapping_add(1)
                        }
                    }
                }
            }
            if entry.file_type().is_ok_and(|kind| kind.is_dir()) {
                match fs::read_dir(path) {
                    Ok(child) => {
                        stack
                            .try_reserve(1)
                            .map_err(|_| NativeCatalogError::OutOfMemory)?;
                        stack.push(child);
                    }
                    Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {}
                    Err(_) => break,
                }
            }
        }
    }
    discovered.sort_unstable_by(|left, right| {
        let (left, right) = (&left.info, &right.info);
        left.full_name
            .iter()
            .copied()
            .map(folded)
            .cmp(right.full_name.iter().copied().map(folded))
            .then_with(|| left.source_path.cmp(&right.source_path))
            .then_with(|| left.face_index.cmp(&right.face_index))
    });
    Ok(discovered)
}
