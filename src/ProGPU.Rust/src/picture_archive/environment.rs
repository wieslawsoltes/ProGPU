//! Immutable source-host resource generations. Resolution is performed before
//! retained draws are published; encoding never calls a framework provider.
use super::{
    ArchiveBrush, ArchiveCommand, ArchiveError, ArchivePen, MAX_ARRAY_ELEMENTS, MAX_DEPTH, Result,
};
use std::{
    borrow::Cow,
    collections::{HashMap, HashSet},
    sync::Arc,
};

/// Original selected resource dictionary key, independent of platform family.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum ArchiveElementTheme {
    #[default]
    Default,
    Light,
    Dark,
}
/// Original dictionary selection includes the independent high-contrast override.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum ArchiveThemeDictionaryKind {
    #[default]
    Default,
    Light,
    Dark,
    HighContrast,
}
impl ArchiveThemeDictionaryKind {
    fn selected(theme: ArchiveElementTheme, high_contrast: bool) -> Self {
        if high_contrast {
            return Self::HighContrast;
        }
        match theme {
            ArchiveElementTheme::Default => Self::Default,
            ArchiveElementTheme::Light => Self::Light,
            ArchiveElementTheme::Dark => Self::Dark,
        }
    }
}
/// Actual source resource values relevant to brush lookup. A found non-brush or
/// null shadows later dictionaries, exactly as ResolveThemeBrush's checked cast.
#[derive(Clone, Debug)]
pub enum ArchiveThemeValue {
    Brush(Arc<ArchiveBrush>),
    Alias {
        key: Arc<str>,
        lookup_root: Option<Arc<ArchiveThemeLookupRoot>>,
    },
    NonBrush,
    Null,
}
/// Immutable original dictionary inventory, including declaration-order merged
/// dictionaries and independently selected theme/default dictionaries.
#[derive(Clone, Debug, Default)]
pub struct ArchiveResourceDictionary {
    entries: HashMap<Arc<str>, ArchiveThemeValue>,
    merged: Arc<[Arc<ArchiveResourceDictionary>]>,
    themes: HashMap<ArchiveThemeDictionaryKind, Arc<ArchiveResourceDictionary>>,
}
impl ArchiveResourceDictionary {
    /// Freeze an actual source dictionary. Later duplicate entries replace earlier
    /// values; merged lookup visits the last declared dictionary first.
    pub fn new(
        entries: impl IntoIterator<Item = (Arc<str>, ArchiveThemeValue)>,
        merged: Arc<[Arc<Self>]>,
        themes: impl IntoIterator<Item = (ArchiveThemeDictionaryKind, Arc<Self>)>,
    ) -> Result<Self> {
        let mut result = Self {
            merged,
            ..Self::default()
        };
        if result.merged.len() > MAX_ARRAY_ELEMENTS {
            return Err(ArchiveError::Capacity);
        }
        for (key, value) in entries {
            if result.entries.len() == MAX_ARRAY_ELEMENTS {
                return Err(ArchiveError::Capacity);
            }
            result
                .entries
                .try_reserve(1)
                .map_err(|_| ArchiveError::Capacity)?;
            result.entries.insert(key, value);
        }
        for (kind, value) in themes {
            result
                .themes
                .try_reserve(1)
                .map_err(|_| ArchiveError::Capacity)?;
            result.themes.insert(kind, value);
        }
        Ok(result)
    }
    /// Full original theme/default/local/reverse-merged traversal. No synthesized
    /// color or platform fallback is selected by a dictionary.
    pub fn lookup(
        &self,
        key: &str,
        theme: ArchiveElementTheme,
        high_contrast: bool,
    ) -> Result<Option<ArchiveThemeValue>> {
        self.lookup_core(
            key,
            ArchiveThemeDictionaryKind::selected(theme, high_contrast),
            &mut HashSet::new(),
            0,
        )
    }
    fn lookup_core(
        &self,
        key: &str,
        theme: ArchiveThemeDictionaryKind,
        active: &mut HashSet<usize>,
        depth: u32,
    ) -> Result<Option<ArchiveThemeValue>> {
        if depth > MAX_DEPTH {
            return Err(ArchiveError::Capacity);
        }
        let id = std::ptr::from_ref(self) as usize;
        active.try_reserve(1).map_err(|_| ArchiveError::Capacity)?;
        if !active.insert(id) {
            return Ok(None);
        }
        let result = (|| {
            if let Some(dictionary) = self.themes.get(&theme)
                && let Some(value) = dictionary.lookup_core(key, theme, active, depth + 1)?
            {
                return Ok(Some(value));
            }
            if theme != ArchiveThemeDictionaryKind::Default
                && let Some(dictionary) = self.themes.get(&ArchiveThemeDictionaryKind::Default)
                && let Some(value) = dictionary.lookup_core(
                    key,
                    ArchiveThemeDictionaryKind::Default,
                    active,
                    depth + 1,
                )?
            {
                return Ok(Some(value));
            }
            if let Some(value) = self.entries.get(key) {
                return Ok(Some(value.clone()));
            }
            for dictionary in self.merged.iter().rev() {
                if let Some(value) = dictionary.lookup_core(key, theme, active, depth + 1)? {
                    return Ok(Some(value));
                }
            }
            Ok(None)
        })();
        active.remove(&id);
        result
    }
}
/// One actual FrameworkElement resource chain: each element keeps its own
/// ActualTheme, and the original nearest-to-farthest parent ordering is retained.
#[derive(Clone, Debug)]
pub struct ArchiveThemeElement {
    pub theme: ArchiveElementTheme,
    pub resources: Option<Arc<ArchiveResourceDictionary>>,
    pub parent: Option<Arc<ArchiveThemeElement>>,
}
/// Typed lookup-root identity; a dictionary and a FrameworkElement have distinct
/// original lookup behavior, particularly application theme selection.
#[derive(Clone, Debug)]
pub enum ArchiveThemeLookupRoot {
    Dictionary(Arc<ArchiveResourceDictionary>),
    Element(Arc<ArchiveThemeElement>),
}
/// Frozen original lookup inputs. `platform` contains the actual results of the
/// source ThemeManager for its selected family/active-window/theme generation;
/// absence does not claim that a platform provider was available.
#[derive(Clone, Debug, Default)]
pub struct ArchiveMaterialEnvironment {
    pub theme: ArchiveElementTheme,
    pub high_contrast: bool,
    pub lookup_root: Option<Arc<ArchiveThemeLookupRoot>>,
    pub target: Option<Arc<ArchiveThemeElement>>,
    /// DependencyObject ancestors that are FrameworkElements, in original order.
    /// These remain distinct from a FrameworkElement's contiguous parent chain.
    pub ancestors: Arc<[Arc<ArchiveThemeElement>]>,
    pub application: Option<Arc<ArchiveResourceDictionary>>,
    pub platform: Option<Arc<ArchiveResourceDictionary>>,
}
/// Explicit source adapter seam. The caller captures a coherent immutable host
/// generation once; this provider is never stored in a prepared scene or encoded.
pub trait ArchiveMaterialProvider {
    fn snapshot(&self) -> Result<ArchiveMaterialEnvironment>;
}
impl ArchiveMaterialEnvironment {
    fn element_lookup(
        &self,
        element: &ArchiveThemeElement,
        key: &str,
    ) -> Result<Option<ArchiveThemeValue>> {
        let mut current = Some(element);
        let mut depth = 0;
        while let Some(element) = current {
            if depth > MAX_DEPTH {
                return Err(ArchiveError::Capacity);
            }
            if let Some(resources) = &element.resources
                && let Some(value) = resources.lookup(key, element.theme, self.high_contrast)?
            {
                return Ok(Some(value));
            }
            current = element.parent.as_deref();
            depth += 1;
        }
        match &self.application {
            Some(resources) => resources.lookup(key, element.theme, self.high_contrast),
            None => Ok(None),
        }
    }
    fn lookup(
        &self,
        root: Option<&ArchiveThemeLookupRoot>,
        key: &str,
    ) -> Result<Option<ArchiveThemeValue>> {
        if let Some(root) = root {
            let value = match root {
                ArchiveThemeLookupRoot::Dictionary(v) => {
                    v.lookup(key, self.theme, self.high_contrast)?
                }
                ArchiveThemeLookupRoot::Element(v) => self.element_lookup(v, key)?,
            };
            if value.is_some() {
                return Ok(value);
            }
        }
        if let Some(target) = &self.target
            && let Some(value) = self.element_lookup(target, key)?
        {
            return Ok(Some(value));
        }
        if self.ancestors.len() > MAX_ARRAY_ELEMENTS {
            return Err(ArchiveError::Capacity);
        }
        for ancestor in self.ancestors.iter() {
            if let Some(value) = self.element_lookup(ancestor, key)? {
                return Ok(Some(value));
            }
        }
        if let Some(resources) = &self.application
            && let Some(value) = resources.lookup(key, self.theme, self.high_contrast)?
        {
            return Ok(Some(value));
        }
        if key.is_empty() {
            return Ok(None);
        }
        match &self.platform {
            Some(resources) => resources.lookup(key, self.theme, self.high_contrast),
            None => Err(ArchiveError::Unsupported),
        }
    }
    /// Resolve the original string-key archive marker. Alias opacity is metadata
    /// only in XamlResourceResolver: it does not multiply the resulting brush.
    pub fn resolve(&self, brush: &Arc<ArchiveBrush>) -> Result<Option<Arc<ArchiveBrush>>> {
        let ArchiveBrush::ThemeResource { key, .. } = brush.as_ref() else {
            return Ok(Some(brush.clone()));
        };
        let mut key: Arc<str> = Arc::from(key.as_str());
        let mut root = self.lookup_root.clone();
        let mut visited = HashSet::new();
        loop {
            if visited.len() > MAX_ARRAY_ELEMENTS {
                return Err(ArchiveError::Capacity);
            }
            let value = self.lookup(root.as_deref(), &key)?;
            match value {
                Some(ArchiveThemeValue::Brush(brush)) => match brush.as_ref() {
                    ArchiveBrush::ThemeResource { key: next, .. } => {
                        visited.try_reserve(1).map_err(|_| ArchiveError::Capacity)?;
                        if !visited.insert(key) {
                            return Err(ArchiveError::InvalidData);
                        }
                        key = Arc::from(next.as_str());
                    }
                    _ => return Ok(Some(brush)),
                },
                Some(ArchiveThemeValue::Alias {
                    key: next,
                    lookup_root,
                }) => {
                    visited.try_reserve(1).map_err(|_| ArchiveError::Capacity)?;
                    if !visited.insert(key) {
                        return Err(ArchiveError::InvalidData);
                    }
                    if lookup_root.is_some() {
                        root = lookup_root;
                    }
                    key = next;
                }
                Some(ArchiveThemeValue::NonBrush | ArchiveThemeValue::Null) | None => {
                    return Ok(None);
                }
            }
        }
    }
    pub(super) fn command<'a>(
        &self,
        source: &'a ArchiveCommand,
    ) -> Result<Cow<'a, ArchiveCommand>> {
        use super::ArchiveCommandKind as K;
        // Inactive raw archive fields never cause resource callbacks or failures.
        let (uses_brush, uses_pen) = match source.kind {
            K::DrawRect | K::DrawRoundedRect | K::DrawEllipse | K::DrawCircle | K::DrawPath => {
                (true, true)
            }
            K::DrawText
            | K::DrawGlyphRun
            | K::FillTriangle
            | K::FillQuad
            | K::DrawPointBatch
            | K::DrawDotGrid
            | K::DrawDeviceDotGrid
            | K::DrawVertexMesh
            | K::DrawHatch
            | K::DrawGpuLineSeries
            | K::DrawGpuScatterSeries => (true, false),
            K::DrawLine
            | K::DrawBezier
            | K::DrawCubicBezier
            | K::DrawPolyline
            | K::DrawSpline
            | K::DrawLine3D
            | K::DrawAcisSolid => (false, true),
            K::PushOpacityMask if source.picture.is_some() => (false, false),
            K::PushOpacityMask if source.path.is_some() && source.pen.is_some() => (false, true),
            K::PushOpacityMask => (true, false),
            K::DrawExtension if matches!(source.extension_id, 1 | 2 | 4) => (false, true),
            K::DrawExtension if matches!(source.extension_id, 3 | 5 | 6) => (true, false),
            _ => (false, false),
        };
        let theme = |brush: &ArchiveBrush| matches!(brush, ArchiveBrush::ThemeResource { .. });
        if !(uses_brush && source.brush.as_deref().is_some_and(theme))
            && !(uses_pen && source.pen.as_ref().is_some_and(|pen| theme(&pen.brush)))
        {
            return Ok(Cow::Borrowed(source));
        }
        let mut result = source.clone();
        if uses_brush && let Some(brush) = &source.brush {
            result.brush = self.resolve(brush)?;
        }
        if uses_pen && let Some(pen) = &source.pen {
            result.pen = self.resolve(&pen.brush)?.map(|brush| {
                Arc::new(ArchivePen {
                    brush,
                    options: pen.options,
                    dashes: pen.dashes.clone(),
                })
            });
        }
        Ok(Cow::Owned(result))
    }
}
