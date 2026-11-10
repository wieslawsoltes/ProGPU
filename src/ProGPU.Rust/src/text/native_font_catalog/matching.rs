//! Native nominal cmap lookup, ordered family preference and bounded FIFO cache.
use super::*;
use crate::text::native_font_provider::fallback_family_preferences;
fn normalized(mut request: NativeFontStyleRequest) -> NativeFontStyleRequest {
    if request.weight <= 0 {
        request.weight = 400;
    }
    request.weight = request.weight.clamp(1, 1000);
    if request.width <= 0 {
        request.width = 5;
    }
    request.width = request.width.clamp(1, 9);
    request
}
fn distance(actual: &NativeCatalogFaceInfo, request: NativeFontStyleRequest) -> u32 {
    let slant = if (actual.slant != NativeFontProviderSlant::Normal)
        == (request.slant != NativeFontProviderSlant::Normal)
    {
        0
    } else {
        10000
    };
    slant
        + (i32::from(actual.width) - request.width).unsigned_abs() * 1000
        + (i32::from(actual.weight) - request.weight).unsigned_abs()
}
fn family_matches(info: &NativeCatalogFaceInfo, family: &[u8]) -> bool {
    equal(&info.family_name, family) || equal(&info.full_name, family)
}
impl NativeSystemFontCatalog {
    pub fn match_family(
        &self,
        family: &str,
        style: NativeFontStyleRequest,
    ) -> Result<Option<u32>, NativeCatalogError> {
        self.match_family_bytes(family.as_bytes(), style)
    }
    /// Original bytewise ASCII-insensitive full-name/family match. Slanted styles
    /// share the original italic penalty; equal distances retain catalog order.
    pub fn match_family_bytes(
        &self,
        family: &[u8],
        style: NativeFontStyleRequest,
    ) -> Result<Option<u32>, NativeCatalogError> {
        if family.is_empty() {
            return Err(NativeCatalogError::InvalidArgument);
        }
        let style = normalized(style);
        let mut best = None;
        let mut best_distance = u32::MAX;
        for (index, entry) in self.entries.iter().enumerate() {
            if !family_matches(&entry.info, family) {
                continue;
            }
            let score = distance(&entry.info, style);
            if best.is_none() || score < best_distance {
                best = Some(index as u32);
                best_distance = score;
            }
        }
        Ok(best)
    }
    fn glyph(&self, index: usize, code_point: u32) -> Result<Option<u16>, NativeCatalogError> {
        let mut cache = self.cache();
        let cmap = &mut cache.cmaps[index];
        if !cmap.attempted {
            cmap.attempted = true;
            let result = (|| {
                let entry = &self.entries[index].info;
                let mut reader = reader::Reader::open(&entry.file_path, None)?;
                let offsets = reader.face_offsets()?;
                let offset = *offsets
                    .get(entry.face_index as usize)
                    .ok_or(NativeCatalogError::InvalidFont)?;
                reader.compact(offset, &[Tag::new(*b"cmap")])
            })();
            match result {
                Ok(face) => cmap.face = Some(face),
                Err(NativeCatalogError::OutOfMemory) => {
                    return Err(NativeCatalogError::OutOfMemory);
                }
                Err(_) => {}
            }
        }
        Ok(cmap
            .face
            .as_ref()
            .and_then(|font| font.glyph_index(code_point))
            .filter(|&glyph| glyph != 0))
    }
    fn character_family(
        &self,
        family: &[u8],
        style: NativeFontStyleRequest,
        code_point: u32,
        excluded: u64,
        visited: &mut [u8],
    ) -> Result<Option<NativeCatalogCharacter>, NativeCatalogError> {
        let mut best = None;
        let mut best_distance = u32::MAX;
        let mut failed_allocation = false;
        for (index, entry) in self.entries.iter().enumerate() {
            if visited[index] != 0
                || entry.info.identity == excluded
                || !family_matches(&entry.info, family)
            {
                continue;
            }
            let glyph = match self.glyph(index, code_point) {
                Ok(Some(glyph)) => glyph,
                Err(NativeCatalogError::OutOfMemory) => {
                    failed_allocation = true;
                    continue;
                }
                _ => continue,
            };
            let score = distance(&entry.info, style);
            if best.is_none() || score < best_distance {
                best = Some(NativeCatalogCharacter {
                    catalog_index: index as u32,
                    glyph_index: glyph,
                });
                best_distance = score;
            }
        }
        if let Some(value) = best {
            visited[value.catalog_index as usize] = 1;
        }
        if failed_allocation {
            Err(NativeCatalogError::OutOfMemory)
        } else {
            Ok(best)
        }
    }
    pub fn match_character(
        &self,
        family: &str,
        style: NativeFontStyleRequest,
        language_tags: &[&str],
        code_point: u32,
        excluded_identity: u64,
    ) -> Result<Option<NativeCatalogCharacter>, NativeCatalogError> {
        self.match_character_bytes(
            family.as_bytes(),
            style,
            language_tags,
            code_point,
            excluded_identity,
        )
    }
    /// Requested family, original language/scalar preference families, then exact
    /// style and unrestricted inventory passes. Language requests bypass caching;
    /// positive and negative no-language results share the original 4,096 FIFO.
    pub fn match_character_bytes(
        &self,
        family: &[u8],
        style: NativeFontStyleRequest,
        language_tags: &[&str],
        code_point: u32,
        excluded_identity: u64,
    ) -> Result<Option<NativeCatalogCharacter>, NativeCatalogError> {
        if code_point > 0x10ffff {
            return Err(NativeCatalogError::InvalidArgument);
        }
        let style = normalized(style);
        let key = CharacterKey {
            family: if family.is_empty() {
                0
            } else {
                hash_normalized(family)
            },
            excluded: excluded_identity,
            code_point,
            weight: style.weight as u16,
            width: style.width as u8,
            slant: style.slant as u8,
        };
        if language_tags.is_empty() {
            if let Some(&cached) = self.cache().characters.get(&key) {
                return Ok(cached);
            }
        }
        let mut visited = zeros(self.entries.len())?;
        let mut result = if family.is_empty() {
            None
        } else {
            self.character_family(family, style, code_point, excluded_identity, &mut visited)?
        };
        if result.is_none() {
            let preferences =
                fallback_family_preferences(language_tags, code_point).map_err(map_font_error)?;
            for family in preferences.as_slice() {
                result = self.character_family(
                    family.as_bytes(),
                    style,
                    code_point,
                    excluded_identity,
                    &mut visited,
                )?;
                if result.is_some() {
                    break;
                }
            }
        }
        for pass in 0..2 {
            if result.is_some() {
                break;
            }
            for (index, entry) in self.entries.iter().enumerate() {
                if visited[index] != 0
                    || entry.info.identity == excluded_identity
                    || (pass == 0 && distance(&entry.info, style) != 0)
                {
                    continue;
                }
                visited[index] = 1;
                if let Some(glyph) = self.glyph(index, code_point)? {
                    result = Some(NativeCatalogCharacter {
                        catalog_index: index as u32,
                        glyph_index: glyph,
                    });
                    break;
                }
            }
        }
        if language_tags.is_empty() {
            let mut cache = self.cache();
            // Cache allocation is optional and cannot turn a genuine match into
            // failure. Reserve both containers before publishing a new FIFO key.
            if !cache.characters.contains_key(&key)
                && cache.characters.try_reserve(1).is_ok()
                && cache.character_order.try_reserve(1).is_ok()
            {
                cache.characters.insert(key, result);
                cache.character_order.push_back(key);
            }
            while cache.characters.len() > 4096 {
                let Some(old) = cache.character_order.pop_front() else {
                    break;
                };
                cache.characters.remove(&old);
            }
        }
        Ok(result)
    }
}
