//! Current managed process-neutral variation data and immutable selected instances.
//! Ported from OpenTypeVariationData.cs; native variation policy stays independent.
use super::{
    FontError, SfntFace, Tag,
    layout::{ManagedLayoutVariations, ManagedVariationStore},
};
use std::{
    collections::HashMap,
    sync::{Arc, Mutex},
};
mod glyph;
mod parse;
use glyph::{GlyphCache, GlyphMetadata};
use parse::{Axis, IndexMap};
/// One original managed axis, including the exact source-resolved UTF-16 name.
#[derive(Clone, Debug)]
pub struct ManagedVariationAxis {
    /// ASCII-decoded source tag; each high byte retains the original '?' replacement.
    pub tag: [u8; 4],
    /// Owned original callback result, or the decoded tag when no name is available.
    pub name: Arc<[u16]>,
    /// Original single-precision minimum.
    pub minimum: f32,
    /// Original single-precision default.
    pub default: f32,
    /// Original single-precision maximum.
    pub maximum: f32,
    /// Original low flag bit.
    pub hidden: bool,
}
/// Exact ordinal UTF-16 tag and original floating user coordinate.
#[derive(Clone, Debug)]
pub struct ManagedVariationSetting {
    /// Exact tag; unknown, empty and unpaired-surrogate tags stay unmatched.
    pub tag: Arc<[u16]>,
    /// Original value, including nonfinite inputs handled by source normalization.
    pub value: f32,
}
impl ManagedVariationSetting {
    /// Retain an exact original UTF-16 setting without normalization.
    pub fn new(tag: impl Into<Arc<[u16]>>, value: f32) -> Self {
        Self {
            tag: tag.into(),
            value,
        }
    }
    /// Convenience constructor for a Rust string tag.
    pub fn from_str(tag: &str, value: f32) -> Self {
        Self::new(tag.encode_utf16().collect::<Vec<_>>(), value)
    }
}
#[derive(Debug)]
struct Data {
    source: SfntFace,
    axes: Vec<Axis>,
    public_axes: Vec<ManagedVariationAxis>,
    advance: Option<Arc<ManagedVariationStore>>,
    layout: Option<Arc<ManagedVariationStore>>,
    metrics: Option<Arc<ManagedVariationStore>>,
    advance_map: Option<IndexMap>,
    metric_records: HashMap<[u8; 4], (u16, u16)>,
    glyphs: Option<GlyphMetadata>,
    glyph_cache: Mutex<Vec<GlyphCache>>,
}
/// Owned original managed variation generation. Parsing follows all original
/// fvar/avar/gvar/HVAR/GDEF/MVAR admission before publication. Glyph tuple parsing
/// remains lazy and shares its original first-query state across instances.
#[derive(Clone, Debug)]
pub struct ManagedVariationData(Arc<Data>);
/// One immutable original CreateInstance result, retaining its complete owner.
/// This is process-neutral variation state; TtfFont's root/cache/metadata wrapper
/// and complete varied-outline construction are separate consumers.
#[derive(Clone, Debug)]
pub struct ManagedVariationInstance {
    data: ManagedVariationData,
    coordinates: Arc<[i16]>,
    settings: Arc<[ManagedVariationSetting]>,
    scalars: Arc<[f32]>,
    is_default: bool,
}
fn allocate<T: Clone>(len: usize, value: T) -> Result<Vec<T>, FontError> {
    let mut out = Vec::new();
    out.try_reserve_exact(len)
        .map_err(|_| FontError::Capacity)?;
    out.resize(len, value);
    Ok(out)
}
impl ManagedVariationData {
    /// Execute original TryCreate admission. Missing/invalid source metadata
    /// yields None; allocation/capacity failures remain errors, like uncaught OOM.
    /// The original name-resolver dependency is explicit and called once per
    /// axis in source order; provide the selected face's source name policy.
    pub fn from_face(
        source: SfntFace,
        resolve_name: impl FnMut(u16) -> Option<Arc<[u16]>>,
    ) -> Result<Option<Self>, FontError> {
        if source.table(Tag::new(*b"fvar")).is_none() {
            return Ok(None);
        }
        if source.data().len() > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        match parse::parse(source, resolve_name) {
            Ok(data) if data.axes.is_empty() => Ok(None),
            Ok(data) => Ok(Some(Self(Arc::new(data)))),
            Err(FontError::Capacity) => Err(FontError::Capacity),
            Err(_) => Ok(None),
        }
    }
    /// Actual retained immutable source face.
    pub fn source(&self) -> &SfntFace {
        &self.0.source
    }
    /// Original complete axis inventory in source order.
    pub fn axes(&self) -> &[ManagedVariationAxis] {
        &self.0.public_axes
    }
    /// Original HVAR-over-gvar phantom selection, independent of a glyph query.
    pub fn uses_glyph_phantom_advance(&self) -> bool {
        self.0.advance.is_none() && self.0.glyphs.is_some()
    }
    /// Freeze original last-matching settings, clamped public settings, normalized
    /// cache key and HVAR region scalars. Invalid axis ordering fails at the
    /// original instance boundary, not eagerly during axis inventory parsing.
    pub fn create_instance(
        &self,
        settings: &[ManagedVariationSetting],
    ) -> Result<ManagedVariationInstance, FontError> {
        let mut selected = Vec::new();
        selected
            .try_reserve_exact(self.axes().len())
            .map_err(|_| FontError::Capacity)?;
        selected.extend(self.axes().iter().map(|a| a.default));
        for setting in settings {
            for (i, a) in self.axes().iter().enumerate() {
                if setting.tag.as_ref() == a.tag.map(u16::from) {
                    selected[i] = setting.value;
                }
            }
        }
        let mut coordinates = Vec::new();
        coordinates
            .try_reserve_exact(selected.len())
            .map_err(|_| FontError::Capacity)?;
        let mut output = Vec::new();
        output
            .try_reserve_exact(selected.len())
            .map_err(|_| FontError::Capacity)?;
        for (i, &value) in selected.iter().enumerate() {
            let axis = &self.0.axes[i];
            let public = &self.0.public_axes[i];
            if public.minimum > public.maximum {
                return Err(FontError::InvalidArgument);
            }
            // Math.Clamp leaves NaN unchanged; FloatToFixed then maps it to zero.
            let value = if value < public.minimum {
                public.minimum
            } else if value > public.maximum {
                public.maximum
            } else {
                value
            };
            let fixed = if value.is_finite() {
                (f64::from(value) * 65536.0)
                    .round()
                    .clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32
            } else {
                0
            };
            coordinates.push(axis.normalize(fixed)?);
            output.push(ManagedVariationSetting::new(
                public.tag.map(u16::from).to_vec(),
                fixed as f32 / 65536.0,
            ));
        }
        let count = self
            .0
            .advance
            .as_ref()
            .map_or(0, |s| s.region_count())
            .max(self.0.metrics.as_ref().map_or(0, |s| s.region_count()))
            .max(self.0.layout.as_ref().map_or(0, |s| s.region_count()));
        let mut scalars = allocate(usize::from(count), 0.0)?;
        if let Some(store) = &self.0.advance {
            for i in 0..store.region_count() {
                scalars[usize::from(i)] = store.scalar(&coordinates, i);
            }
        }
        let is_default = coordinates.iter().all(|&v| v == 0);
        Ok(ManagedVariationInstance {
            data: self.clone(),
            coordinates: coordinates.into(),
            settings: output.into(),
            scalars: scalars.into(),
            is_default,
        })
    }
}
impl ManagedVariationInstance {
    /// Actual immutable variation generation.
    pub fn data(&self) -> &ManagedVariationData {
        &self.data
    }
    /// Exact original normalized coordinates; their u16 bit patterns are the
    /// original ordinal cache key, including the complete axis inventory.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Original clamped/requantized user settings in complete source axis order.
    pub fn settings(&self) -> &[ManagedVariationSetting] {
        &self.settings
    }
    /// Exact original region-scalar inventory, including unused zero entries.
    pub fn region_scalars(&self) -> &[f32] {
        &self.scalars
    }
    /// Original default-instance predicate after avar mapping.
    pub fn is_default(&self) -> bool {
        self.is_default
    }
    /// First matching original user coordinate; no tag case conversion.
    pub fn user_coordinate(&self, tag: &[u16]) -> Option<f32> {
        self.settings
            .iter()
            .find(|s| s.tag.as_ref() == tag)
            .map(|s| s.value)
    }
    /// Original HVAR delta with cached source region scalars and lazy row reads.
    pub fn advance_delta(&self, glyph: u16) -> Result<f32, FontError> {
        let Some(store) = &self.data.0.advance else {
            return Ok(0.0);
        };
        let (outer, inner) = self
            .data
            .0
            .advance_map
            .as_ref()
            .map_or((0, glyph), |map| map.get(glyph));
        store.delta(&self.coordinates, Some(&self.scalars), outer, inner)
    }
    /// Original last-record-wins MVAR lookup with its independent store.
    pub fn metric_delta(&self, tag: &[u16]) -> Result<f32, FontError> {
        let Some(store) = &self.data.0.metrics else {
            return Ok(0.0);
        };
        if tag.len() != 4 || tag.iter().any(|&v| v > 127) {
            return Ok(0.0);
        }
        let key = [tag[0] as u8, tag[1] as u8, tag[2] as u8, tag[3] as u8];
        let Some(&(outer, inner)) = self.data.0.metric_records.get(&key) else {
            return Ok(0.0);
        };
        store.delta(&self.coordinates, None, outer, inner)
    }
    /// Original layout delta, independent of HVAR's cached scalar inventory.
    pub fn layout_delta(&self, outer: u16, inner: u16) -> Result<f32, FontError> {
        self.data
            .0
            .layout
            .as_ref()
            .map_or(Ok(0.0), |s| s.delta(&self.coordinates, None, outer, inner))
    }
    /// Retained layout state for a consumer with original active-instance policy.
    pub fn layout_variations(&self) -> ManagedLayoutVariations {
        ManagedLayoutVariations::from_store(
            self.data.source().clone(),
            self.coordinates.clone(),
            self.data.0.layout.clone(),
        )
    }
}
