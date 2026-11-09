//! Original owned TtfFont construction, metadata and variation-instance identity.
use super::{
    FontError, ManagedCharacterMap, ManagedVariationAxis, ManagedVariationData,
    ManagedVariationInstance, ManagedVariationSetting, SfntFace, Tag,
};
use std::sync::atomic::{AtomicU32, Ordering};
use std::{
    collections::{HashMap, VecDeque},
    hash::{Hash, Hasher},
    sync::{Arc, Mutex, OnceLock},
};
mod bitmap;
mod color;
mod metadata;
mod metrics;
mod outline;
mod svg;
pub use bitmap::{DecodedBitmapGlyph, ManagedBitmapGlyph};
pub use color::ColrGlyphLayer;
use metadata::Raw;
pub use outline::{ManagedGlyphOutline, ManagedOutlineFigure};

/// Original immutable font properties in design units. Capability flags describe
/// original table presence, not qualification of every outline or bitmap consumer.
#[derive(Clone, Debug)]
pub struct ManagedFontMetadata {
    /// Preferred family, then legacy family, then empty.
    pub family: Arc<str>,
    /// Preferred subfamily, then legacy subfamily, then empty.
    pub subfamily: Arc<str>,
    /// Full name, falling back to the selected family.
    pub full_name: Arc<str>,
    /// Original PostScript name or empty.
    pub postscript_name: Arc<str>,
    /// Original OS/2 class, or variation-adjusted wght.
    pub weight_class: u16,
    /// Original OS/2 width class, unchanged by wdth selection.
    pub width_class: u16,
    /// Original selection/head policy and explicit ital/slnt adjustment.
    pub italic: bool,
    /// Both original loca and glyf tables are present.
    pub has_true_type_outlines: bool,
    /// Original CFF1 table is present; its decoder remains lazy.
    pub has_cff_outlines: bool,
    /// Original owned-font bitmap table presence.
    pub has_bitmap_glyphs: bool,
    /// Original COLR/CPAL or SVG table presence.
    pub has_color_glyphs: bool,
    /// Original post fixed-pitch flag.
    pub fixed_pitch: bool,
    /// Original head/bhed units per em, including zero.
    pub units_per_em: u16,
    /// Original signed head minX/minY/maxX/maxY.
    pub bounds: [i16; 4],
    /// Original or variation-adjusted ascent.
    pub ascender: i16,
    /// Original or variation-adjusted descent.
    pub descender: i16,
    /// Original or variation-adjusted line gap.
    pub line_gap: i16,
    /// Nullable original post underline position.
    pub underline_position: Option<i16>,
    /// Nullable original post underline thickness.
    pub underline_thickness: Option<i16>,
    /// Nullable original OS/2 strikeout position.
    pub strikeout_position: Option<i16>,
    /// Nullable original OS/2 strikeout thickness.
    pub strikeout_thickness: Option<i16>,
    /// Nullable original OS/2 x-height.
    pub x_height: Option<i16>,
    /// Nullable original OS/2 cap height.
    pub cap_height: Option<i16>,
    /// Original whole-font maxp glyph count, absent defaults zero.
    pub glyph_count: u16,
    /// Selected original palette as straight RGBA, without color-layer execution.
    pub palette: Arc<[[f32; 4]]>,
}
#[derive(Debug)]
struct State {
    metadata: ManagedFontMetadata,
    instance: Option<ManagedVariationInstance>,
    ascii: OnceLock<Box<[AtomicU32; 128]>>,
    outlines: Mutex<outline::OutlineCache>,
    flipped: Mutex<HashMap<u16, Option<Arc<ManagedGlyphOutline>>>>,
}
#[derive(Debug, Default)]
struct Cache {
    instances: HashMap<Arc<[i16]>, Arc<State>>,
    order: VecDeque<Arc<[i16]>>,
    item_counts: Vec<usize>,
}
#[derive(Debug)]
struct Root {
    cmap: ManagedCharacterMap,
    raw: Raw,
    base: Arc<State>,
    data: OnceLock<Option<ManagedVariationData>>,
    cff: OnceLock<Result<Option<crate::font::cff::ManagedCff>, FontError>>,
    cache: Mutex<Cache>,
}
/// Owned current managed font data and immutable selected instance. Original
/// construction, variation caching, identity and metadata are included; complete
/// outline/hinting/color/shaping consumers are separate ports. Clones share the
/// exact instance, while separately constructed roots keep distinct identities.
/// Cache entries own snapshots without a back-reference to the root, avoiding
/// reference cycles while retaining the original strong 32-entry FIFO cache.
#[derive(Clone, Debug)]
pub struct ManagedFont {
    root: Arc<Root>,
    state: Arc<State>,
}
impl ManagedFont {
    /// Select an owned uncompressed SFNT/TTC font with original managed error and
    /// whole-collection admission. WOFF normalization is a separate pending port.
    pub fn new(data: impl Into<Arc<[u8]>>, face_index: u32) -> Result<Self, FontError> {
        let face = SfntFace::new(data, face_index).map_err(|e| match e {
            FontError::InvalidArgument | FontError::UnsupportedContainer | FontError::Capacity => e,
            _ => FontError::InvalidFace,
        })?;
        Self::from_face(face)
    }
    /// Execute original owned uncompressed SFNT/TTC constructor admission and
    /// metadata reads. Compressed/mapped/resident-font factories remain separate.
    pub fn from_face(source: SfntFace) -> Result<Self, FontError> {
        if source.data().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        // SfntFontFace.Load constructs every collection face before selecting
        // one. An invalid unselected directory therefore rejects the font too.
        for index in 0..SfntFace::face_count(source.data())? {
            if index != source.face_index() {
                SfntFace::new(source.data.clone(), index).map_err(|_| FontError::InvalidFace)?;
            }
        }
        let (metadata, raw) = metadata::parse(&source)?;
        let state = Arc::new(State {
            metadata,
            instance: None,
            ascii: OnceLock::new(),
            outlines: Mutex::new(outline::OutlineCache::default()),
            flipped: Mutex::new(HashMap::new()),
        });
        Ok(Self {
            root: Arc::new(Root {
                cmap: ManagedCharacterMap::new(source)?,
                raw,
                base: state.clone(),
                data: OnceLock::new(),
                cff: OnceLock::new(),
                cache: Mutex::new(Cache::default()),
            }),
            state,
        })
    }
    /// Actual retained immutable selected SFNT face.
    pub fn source(&self) -> &SfntFace {
        self.root.cmap.source()
    }
    /// Original selected character map, independent of font size and variation
    /// instance metadata. This immutable mapping retains the actual source.
    pub fn character_map(&self) -> &ManagedCharacterMap {
        &self.root.cmap
    }
    /// Original nominal glyph with a lazy per-instance 128-entry ASCII cache.
    /// Cached zero remains distinct from an uninitialized slot. Failed original
    /// queries publish no entry; non-ASCII queries retain the original direct path.
    pub fn glyph_index(&self, code: u32) -> Result<u16, FontError> {
        if code >= 128 {
            return Ok(self.root.cmap.glyph_index(code)?.unwrap_or(0));
        }
        let cache = self
            .state
            .ascii
            .get_or_init(|| Box::new(std::array::from_fn(|_| AtomicU32::new(0))));
        let slot = &cache[code as usize];
        let value = slot.load(Ordering::Relaxed);
        if value != 0 {
            return Ok(value as u16);
        }
        let value = self.root.cmap.glyph_index(code)?.unwrap_or(0);
        // One atomic word owns both publication and value; the source is immutable.
        // Concurrent identical computations do not require a second publication flag.
        slot.store(0x1_0000 | u32::from(value), Ordering::Relaxed);
        Ok(value)
    }
    /// Original nonzero nominal-coverage predicate; no fallback or shaping.
    pub fn has_glyph(&self, code: u32) -> Result<bool, FontError> {
        Ok(self.glyph_index(code)? != 0)
    }
    /// Original variation-selector query, preserving explicit glyph zero and the
    /// instance's ASCII cache when a default sequence selects nominal coverage.
    pub fn variation_glyph(&self, code: u32, selector: u32) -> Result<Option<u16>, FontError> {
        self.root
            .cmap
            .variation_with(code, selector, || self.glyph_index(code))
    }
    /// Complete immutable original metadata for this selected instance.
    pub fn metadata(&self) -> &ManagedFontMetadata {
        &self.state.metadata
    }
    /// Original root identity, including after the caller drops its root handle.
    pub fn root(&self) -> Self {
        Self {
            root: self.root.clone(),
            state: self.root.base.clone(),
        }
    }
    /// Reference identity of the original instance, distinct from normalized-key
    /// equality after an entry is evicted and recreated.
    pub fn same_instance(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.root, &other.root) && Arc::ptr_eq(&self.state, &other.state)
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn instance_identity(&self) -> (usize, usize) {
        (
            Arc::as_ptr(&self.root) as usize,
            Arc::as_ptr(&self.state) as usize,
        )
    }
    /// Original active variation predicate. Default selection reuses the root.
    pub fn has_active_variations(&self) -> bool {
        self.state.instance.is_some()
    }
    fn data(&self) -> Result<Option<&ManagedVariationData>, FontError> {
        if let Some(data) = self.root.data.get() {
            return Ok(data.as_ref());
        }
        let _lock = self.root.cache.lock().unwrap_or_else(|e| e.into_inner());
        if self.root.data.get().is_none() {
            let data = ManagedVariationData::from_named_face(self.source().clone())?;
            let _ = self.root.data.set(data);
        }
        Ok(self.root.data.get().unwrap().as_ref())
    }
    /// Original lazily captured full axis inventory; invalid data stays absent.
    pub fn variation_axes(&self) -> Result<&[ManagedVariationAxis], FontError> {
        Ok(self.data()?.map_or(&[], |d| d.axes()))
    }
    /// Original first-cached public settings; the default root exposes none.
    pub fn variation_settings(&self) -> &[ManagedVariationSetting] {
        self.state.instance.as_ref().map_or(&[], |i| i.settings())
    }
    /// Original cached instance selection, including default-root reuse and
    /// last-setting normalization before every cache lookup. Metadata failures
    /// publish neither an instance nor a FIFO entry.
    pub fn with_variations(&self, settings: &[ManagedVariationSetting]) -> Result<Self, FontError> {
        let Some(data) = self.data()? else {
            return Ok(self.root());
        };
        let instance = data.create_instance(settings)?;
        if instance.is_default() {
            return Ok(self.root());
        }
        let mut cache = self.root.cache.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(state) = cache.instances.get(instance.coordinates()) {
            return Ok(Self {
                root: self.root.clone(),
                state: state.clone(),
            });
        }
        let metadata = metadata::varied(&self.root.base.metadata, &instance)?;
        let key: Arc<[i16]> = instance.coordinates().into();
        let state = Arc::new(State {
            metadata,
            instance: Some(instance),
            ascii: OnceLock::new(),
            outlines: Mutex::new(outline::OutlineCache::default()),
            flipped: Mutex::new(HashMap::new()),
        });
        cache.instances.insert(key.clone(), state.clone());
        cache.order.push_back(key);
        while cache.order.len() > 32 {
            let key = cache.order.pop_front().unwrap();
            cache.instances.remove(&key);
        }
        Ok(Self {
            root: self.root.clone(),
            state,
        })
    }
    /// Original selected coordinate. Default roots expose zero for every actual
    /// axis even if avar would map a newly created default instance differently.
    pub fn normalized_coordinate(&self, axis: i32) -> Result<Option<i16>, FontError> {
        let Some(data) = self.data()? else {
            return Ok(None);
        };
        if axis < 0 || axis as usize >= data.axes().len() {
            return Ok(None);
        }
        Ok(Some(
            self.state
                .instance
                .as_ref()
                .map_or(0, |i| i.coordinates()[axis as usize]),
        ))
    }
    /// Complete original normalized coordinate inventory for plan preparation.
    pub fn normalized_coordinates(&self) -> Result<Vec<i16>, FontError> {
        Ok(if let Some(i) = &self.state.instance {
            i.coordinates().to_vec()
        } else {
            vec![0; self.variation_axes()?.len()]
        })
    }
    /// Original root layout delta is zero without forcing lazy variation parsing.
    pub fn layout_delta(&self, outer: u16, inner: u16) -> Result<f32, FontError> {
        self.state
            .instance
            .as_ref()
            .map_or(Ok(0.0), |i| i.layout_delta(outer, inner))
    }
    /// Apply the original selected simple-glyph deltas; default roots leave caller
    /// coordinates untouched without executing fonts. Full outline writing is separate.
    pub fn apply_simple_variations(
        &self,
        glyph: u16,
        points: &mut [[f32; 2]],
        ends: &[u16],
    ) -> Result<(), FontError> {
        self.state
            .instance
            .as_ref()
            .map_or(Ok(()), |i| i.apply_simple_deltas(glyph, points, ends))
    }
    /// Original selected component offsets, or empty for a default root.
    pub fn component_variations(
        &self,
        glyph: u16,
        count: usize,
    ) -> Result<Vec<[f32; 2]>, FontError> {
        self.state
            .instance
            .as_ref()
            .map_or(Ok(Vec::new()), |i| i.composite_deltas(glyph, count))
    }
    /// Original independent palette clone, retaining selected variation settings
    /// and applying packed ARGB overrides after palette selection. No-op policies
    /// preserve exact instance identity; a real clone owns a new source/root.
    pub fn with_color_palette(
        &self,
        index: i32,
        overrides: &[(u16, u32)],
    ) -> Result<Self, FontError> {
        let raw = &self.root.raw;
        if raw.palettes == 0 || raw.palette_entries == 0 {
            return Ok(self.clone());
        }
        let selected = if (index as u32) < u32::from(raw.palettes) {
            index as u32
        } else {
            0
        };
        if selected == 0 && overrides.is_empty() {
            return Ok(self.clone());
        }
        let mut clone = Self::from_face(SfntFace::new(
            Arc::<[u8]>::from(self.source().data()),
            self.source().face_index(),
        )?)?;
        if !self.variation_settings().is_empty() {
            clone = clone.with_variations(self.variation_settings())?;
        }
        let raw = &clone.root.raw;
        let first = u32::from(metadata::word(
            clone.source(),
            raw.cpal.wrapping_add(12 + selected * 2),
        )?);
        let mut palette = vec![[0.0; 4]; usize::from(raw.palette_entries)];
        for (i, color) in palette.iter_mut().enumerate() {
            let record = first + i as u32;
            if record >= u32::from(raw.colors) {
                break;
            }
            *color = metadata::color(clone.source(), raw.color_records.wrapping_add(record * 4))?;
        }
        for &(i, color) in overrides {
            if let Some(slot) = palette.get_mut(usize::from(i)) {
                *slot = [
                    ((color >> 16) & 255) as f32 / 255.0,
                    ((color >> 8) & 255) as f32 / 255.0,
                    (color & 255) as f32 / 255.0,
                    ((color >> 24) & 255) as f32 / 255.0,
                ];
            }
        }
        let mut metadata = clone.state.metadata.clone();
        metadata.palette = palette.into();
        let state = Arc::new(State {
            metadata,
            instance: clone.state.instance.clone(),
            ascii: OnceLock::new(),
            outlines: Mutex::new(outline::OutlineCache::default()),
            flipped: Mutex::new(HashMap::new()),
        });
        // This root is still private to the candidate. Replace the same snapshot
        // in its cache before publication, matching the source's private mutation.
        let root = Arc::get_mut(&mut clone.root).expect("new palette root is unpublished");
        if let Some(i) = &state.instance {
            root.cache
                .get_mut()
                .unwrap_or_else(|e| e.into_inner())
                .instances
                .insert(i.coordinates().into(), state.clone());
        } else {
            root.base = state.clone();
        }
        clone.state = state;
        Ok(clone)
    }
    pub(crate) fn layout_variations(
        &self,
    ) -> Result<super::layout::ManagedLayoutVariations, FontError> {
        if let Some(i) = &self.state.instance {
            return Ok(i.layout_variations());
        }
        Ok(super::layout::ManagedLayoutVariations::from_store(
            self.source().clone(),
            vec![0; self.variation_axes()?.len()].into(),
            None,
        ))
    }
}
impl PartialEq for ManagedFont {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.root, &other.root)
            && match (&self.state.instance, &other.state.instance) {
                (None, None) => true,
                (Some(a), Some(b)) => a.coordinates() == b.coordinates(),
                _ => false,
            }
    }
}
impl Eq for ManagedFont {}
impl Hash for ManagedFont {
    fn hash<H: Hasher>(&self, state: &mut H) {
        Arc::as_ptr(&self.root).hash(state);
        self.state
            .instance
            .as_ref()
            .map(|i| i.coordinates())
            .hash(state);
    }
}
