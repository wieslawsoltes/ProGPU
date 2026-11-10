//! Ordered original family/style/language fallback and source-renderability gates.
use super::*;
impl FontManager {
    pub fn match_character(
        &self,
        family: Option<&str>,
        style: FontStyleRequest,
        languages: Option<&[&str]>,
        codepoint: i32,
        excluded: Option<&ManagedFont>,
    ) -> FontManagerResult<Option<CharacterMatch>> {
        if !(0..=0x10ffff).contains(&codepoint) {
            return Ok(None);
        }
        let style = style.normalized();
        let key = languages.is_none().then(|| CharacterKey {
            family: family.map(name_key),
            style,
            codepoint,
            excluded: excluded.cloned(),
            version: self.catalog_version(),
        });
        if let Some(key) = &key {
            if let Some(value) = self
                .state
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .characters
                .get(key)
            {
                return Ok(value);
            }
        }
        let value = self.match_character_core(family, style, languages, codepoint, excluded)?;
        if let Some(key) = key {
            return Ok(self
                .state
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .characters
                .insert(key, value, 4096));
        }
        Ok(value)
    }
    pub fn resolve_platform_fallback(
        &self,
        requested: &ManagedFont,
        style: FontStyleRequest,
        codepoint: i32,
    ) -> FontManagerResult<Option<CharacterMatch>> {
        self.match_character(
            Some(&requested.metadata().family),
            style,
            None,
            codepoint,
            Some(requested),
        )
    }
    fn match_character_core(
        &self,
        family: Option<&str>,
        style: FontStyleRequest,
        languages: Option<&[&str]>,
        codepoint: i32,
        excluded: Option<&ManagedFont>,
    ) -> FontManagerResult<Option<CharacterMatch>> {
        let mut visited_registered = HashSet::new();
        let mut visited_system = HashSet::new();
        if let Some(family) = family.filter(|name| !name.trim().is_empty()) {
            if let Some(result) = self.family_character(
                family,
                style,
                codepoint,
                excluded,
                &mut visited_registered,
                &mut visited_system,
            )? {
                return Ok(Some(result));
            }
        }
        for family in fallback_family_preferences(languages, codepoint) {
            if let Some(result) = self.family_character(
                family,
                style,
                codepoint,
                excluded,
                &mut visited_registered,
                &mut visited_system,
            )? {
                return Ok(Some(result));
            }
        }
        let registered = self.registered();
        for fallback_only in [true, false] {
            for face in registered.iter() {
                if (fallback_only && !face.fallback) || !visited_registered.insert(face.owner.id) {
                    continue;
                }
                if let Some(value) =
                    self.renderable(face.owner.try_load()?, style, codepoint, excluded)?
                {
                    return Ok(Some(value));
                }
            }
        }
        for exact_style in [true, false] {
            for face in self.system.iter() {
                if (exact_style && face.style.distance(style) != 0)
                    || !visited_system.insert(face.visit_key())
                    || !metadata_coverage(face, codepoint as u32)?
                {
                    continue;
                }
                if let Some(value) =
                    self.renderable(face.owner.try_load()?, style, codepoint, excluded)?
                {
                    return Ok(Some(value));
                }
            }
        }
        Ok(None)
    }
    fn family_character(
        &self,
        family: &str,
        style: FontStyleRequest,
        codepoint: i32,
        excluded: Option<&ManagedFont>,
        registered_visits: &mut HashSet<u64>,
        system_visits: &mut HashSet<SourceVisit>,
    ) -> FontManagerResult<Option<CharacterMatch>> {
        let registered = self.registered();
        let mut candidates: Vec<_> = registered
            .iter()
            .filter(|face| name_equal(&face.family, family))
            .collect();
        candidates.sort_by_key(|face| face.style.distance(style));
        for face in candidates {
            if registered_visits.insert(face.owner.id) {
                if let Some(value) =
                    self.renderable(face.owner.try_load()?, style, codepoint, excluded)?
                {
                    return Ok(Some(value));
                }
            }
        }
        if let Some(face) = self.find_system(family, style, Some(codepoint as u32))? {
            if system_visits.insert(face.visit_key()) {
                return self.renderable(face.owner.try_load()?, style, codepoint, excluded);
            }
        }
        Ok(None)
    }
    fn renderable(
        &self,
        font: Option<ManagedFont>,
        style: FontStyleRequest,
        codepoint: i32,
        excluded: Option<&ManagedFont>,
    ) -> FontManagerResult<Option<CharacterMatch>> {
        let Some(font) = self.apply_optional(font, style)? else {
            return Ok(None);
        };
        if excluded == Some(&font) {
            return Ok(None);
        }
        let glyph = font.glyph_index(codepoint as u32)?;
        if glyph == 0 {
            return Ok(None);
        }
        if font.glyph_outline(glyph)?.is_none()
            && !font.has_color_layers(glyph)?
            && font.bitmap_glyph(glyph, 64.0).is_none()
        {
            // Original ConvertFromUtf32 rejects surrogate values only after
            // the real outline/color/bitmap probes have all reported no ink.
            if (0xd800..=0xdfff).contains(&codepoint) {
                return Err(FontManagerError::InvalidArgument);
            }
            if !matches!(
                general_category(codepoint as u32),
                GeneralCategory::Control
                    | GeneralCategory::Format
                    | GeneralCategory::SpaceSeparator
                    | GeneralCategory::LineSeparator
                    | GeneralCategory::ParagraphSeparator
            ) {
                return Ok(None);
            }
        }
        Ok(Some(CharacterMatch {
            font,
            glyph_index: glyph,
        }))
    }
    fn intrinsic_system(
        &self,
        name: &str,
        style: FontStyle,
    ) -> FontManagerResult<Option<ManagedFont>> {
        let mut best = None;
        let mut distance = i64::MAX;
        for face in self.system.iter() {
            if !name_equal(&face.family, name) && !name_equal(&face.name, name) {
                continue;
            }
            let Some(font) = face.typeface_owner.try_load()? else {
                continue;
            };
            let candidate = FontStyleRequest::from_font(&font).distance(style.request());
            if candidate < distance {
                distance = candidate;
                best = Some(font);
                if candidate == 0 {
                    break;
                }
            }
        }
        Ok(best)
    }
    /// Original SKTypeface default resolution uses system generic preferences,
    /// then the first system record, then an actual host fallback owner.
    pub fn default_typeface(&self) -> FontManagerResult<Typeface> {
        if let Some(face) = self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .default_typeface
            .clone()
        {
            return Ok(face);
        }
        let candidate = self.resolve_default_typeface()?;
        Ok(self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .default_typeface
            .get_or_insert(candidate)
            .clone())
    }
    fn resolve_default_typeface(&self) -> FontManagerResult<Typeface> {
        for family in GenericFontFamily::SansSerif.preferences(self.platform) {
            if let Some(font) = self.intrinsic_system(family, FontStyle::NORMAL)? {
                return Ok(Typeface::from_managed(font));
            }
        }
        if let Some(face) = self.system.first() {
            return Ok(Typeface::from_managed(face.typeface_owner.load()?));
        }
        if let Some(font) = self.platform_fallback() {
            return Ok(Typeface::from_managed(font));
        }
        Err(TypefaceError::NoDefaultFont.into())
    }
    pub fn empty_typeface(&self) -> FontManagerResult<Typeface> {
        if let Some(face) = self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .empty_typeface
            .clone()
        {
            return Ok(face);
        }
        let candidate = Typeface::empty(self.default_typeface()?.font().clone());
        Ok(self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .empty_typeface
            .get_or_insert(candidate)
            .clone())
    }
    pub fn match_family_name(
        &self,
        family: Option<&str>,
        style: FontStyle,
    ) -> FontManagerResult<Option<Typeface>> {
        let Some(family) = family.filter(|name| !name.trim().is_empty()) else {
            return Ok(None);
        };
        if let Some(font) = self.match_family(Some(family), style.request())? {
            return Ok(Some(Typeface::with_requested_style(font, style)));
        }
        if self.platform == FontPlatform::Linux {
            if let Some(generic) = GenericFontFamily::parse(family) {
                for name in generic.preferences(self.platform) {
                    if let Some(font) = self.intrinsic_system(name, style)? {
                        return Ok(Some(Typeface::with_requested_style(font, style)));
                    }
                }
            }
        }
        Ok(self
            .intrinsic_system(family, style)?
            .map(|font| Typeface::with_requested_style(font, style)))
    }
    pub fn typeface_from_family(
        &self,
        family: Option<&str>,
        style: FontStyle,
    ) -> FontManagerResult<Typeface> {
        if let Some(face) = self.match_family_name(family, style)? {
            return Ok(face);
        }
        let default = self.default_typeface()?;
        if self.platform == FontPlatform::MacOs {
            return Ok(Typeface::from_managed(default.font().clone()));
        }
        Ok(Typeface::with_requested_style(
            default.font().clone(),
            style,
        ))
    }
    /// The raw integer overload preserves requested metadata even for the
    /// original macOS null/whitespace-family special case.
    pub fn typeface_from_family_values(
        &self,
        family: Option<&str>,
        weight: i32,
        width: i32,
        slant: FontSlant,
    ) -> FontManagerResult<Typeface> {
        let style = FontStyle::new(weight, width, slant);
        if family.is_none_or(|name| name.trim().is_empty()) {
            return Ok(Typeface::with_requested_style(
                self.default_typeface()?.font().clone(),
                style,
            ));
        }
        self.typeface_from_family(family, style)
    }
}
