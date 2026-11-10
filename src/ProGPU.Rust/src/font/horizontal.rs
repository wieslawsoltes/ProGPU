//! Original legacy HVAR advances, strict bearing preparation and gvar fallback.
use super::{
    FontError, GlyphKind, SfntFace, Span, Tag, bytes, gvar,
    item_variation::{OwnedMap, OwnedStore},
};

const HVAR: Tag = Tag::new(*b"HVAR");
#[derive(Clone, Debug)]
struct Views {
    store: OwnedStore,
    advance_map: Option<OwnedMap>,
}

// Capture the original base-selection decision once for a prepared immutable
// face. Invalid base metadata remains a query error, never a preparation error.
#[derive(Clone, Copy, Debug)]
enum BaseAdvance {
    Fallback(Result<f32, FontError>),
    Metrics { table: Span, count: u16 },
}
impl BaseAdvance {
    fn new(face: &SfntFace) -> Self {
        let header = face.horizontal_header_metrics();
        let table = face.table_span(Tag::new(*b"hmtx"));
        if let (Some(header), Some((_, table))) = (header, table)
            && header.number_of_horizontal_metrics != 0
        {
            return Self::Metrics {
                table,
                count: header.number_of_horizontal_metrics,
            };
        }
        Self::Fallback(
            face.header_metrics()
                .filter(|h| h.units_per_em != 0)
                .map(|h| f32::from(h.units_per_em) * 0.5)
                .ok_or(FontError::InvalidFace),
        )
    }
    fn width(self, face: &SfntFace, glyph: u16) -> Result<f32, FontError> {
        match self {
            Self::Fallback(value) => value,
            Self::Metrics { table, count } => bytes::u16(
                table.get(face.data()),
                usize::from(glyph.min(count - 1)) * 4,
            )
            .map(f32::from)
            .ok_or(FontError::InvalidFace),
        }
    }
}

/// Immutable original HVAR instance with cached region scalars and owned source.
/// Missing/disabled HVAR remains distinct from an active zero delta. Coordinates
/// retain their exact request, including unused extra axes. No shaping occurs.
#[derive(Clone, Debug)]
pub struct HorizontalAdvanceInstance {
    source: SfntFace,
    coordinates: Vec<i16>,
    views: Option<Views>,
    scalars: Vec<f32>,
    base: BaseAdvance,
}
impl HorizontalAdvanceInstance {
    /// Exact immutable selected face, retained after caller retirement.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original supplied normalized request, without clamping or truncation.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Whether the original advance-only parser selected an active HVAR store.
    pub fn uses_hvar(&self) -> bool {
        self.views.is_some()
    }
    /// Original cached scalars in full source region order.
    pub fn region_scalars(&self) -> &[f32] {
        &self.scalars
    }
    /// HVAR delta for an original glyph slot. Native map repetition, implicit
    /// indices and missing-item zero remain unchanged; glyph count is not inferred.
    pub fn advance_variation(&self, glyph: u16) -> Result<Option<f32>, FontError> {
        let Some(v) = &self.views else {
            return Ok(None);
        };
        let (outer, inner) = indices(v.advance_map.as_ref(), glyph);
        v.store
            .view()
            .delta_with_scalars(&self.scalars, outer, inner)
            .map(Some)
    }
    /// Original hmtx/fallback base plus HVAR only. An empty coordinate request
    /// retains the base and does not evaluate variations.
    pub fn design_advance_width_hvar(&self, glyph: u16) -> Result<f32, FontError> {
        let base = self.base.width(&self.source, glyph)?;
        if self.coordinates.is_empty() {
            return Ok(base);
        }
        Ok(base + self.advance_variation(glyph)?.unwrap_or(0.0))
    }
    /// Original full advance route: HVAR takes precedence; otherwise selected
    /// gvar horizontal phantoms provide right-minus-left, including empty glyphs.
    pub fn design_advance_width(&self, glyph: u16) -> Result<f32, FontError> {
        let base = self.base.width(&self.source, glyph)?;
        if self.coordinates.is_empty() {
            return Ok(base);
        }
        let delta = match self.advance_variation(glyph)? {
            Some(value) => value,
            None => phantom_advance(&self.source, glyph, &self.coordinates)?,
        };
        Ok(base + delta)
    }
}
/// Original stricter HVAR 1.0 preparation for explicit left-side-bearing deltas.
/// This does not change the legacy advance-only header admission.
#[derive(Clone, Debug)]
pub struct HorizontalMetricsInstance {
    advance: HorizontalAdvanceInstance,
    left_bearing_map: Option<OwnedMap>,
}
impl HorizontalMetricsInstance {
    /// Same source/request generation and cached advance-region scalars.
    pub fn advance(&self) -> &HorizontalAdvanceInstance {
        &self.advance
    }
    /// Explicit left-side-bearing mapping, with absence distinct from zero.
    /// Finite-result rejection follows the original source-bearing boundary.
    pub fn left_side_bearing_variation(&self, glyph: u16) -> Result<Option<f32>, FontError> {
        let Some(map) = &self.left_bearing_map else {
            return Ok(None);
        };
        let v = self
            .advance
            .views
            .as_ref()
            .ok_or(FontError::InvalidArgument)?;
        let (outer, inner) = map.view().get(u32::from(glyph));
        let delta = v
            .store
            .view()
            .delta_with_scalars(&self.advance.scalars, outer, inner)?;
        if !delta.is_finite() {
            return Err(FontError::InvalidFace);
        }
        Ok(Some(delta))
    }
}

impl SfntFace {
    /// Original legacy advance-only HVAR delta. Absent table or zero store offset
    /// returns `None`; an active store returning zero remains `Some(0.0)`.
    pub fn horizontal_advance_variation(
        &self,
        glyph: u16,
        coordinates: &[i16],
    ) -> Result<Option<f32>, FontError> {
        let Some(v) = views(self, coordinates)? else {
            return Ok(None);
        };
        let (outer, inner) = indices(v.advance_map.as_ref(), glyph);
        v.store.view().delta(coordinates, outer, inner).map(Some)
    }
    /// Validate the entire original advance store/map before reporting its region
    /// inventory. Missing/disabled HVAR remains `None` even for empty coordinates.
    pub fn horizontal_advance_variation_region_count(
        &self,
        coordinates: &[i16],
    ) -> Result<Option<u16>, FontError> {
        Ok(views(self, coordinates)?.map(|v| v.store.view().region_count()))
    }
    /// Own the original advance instance and calculate its complete scalar prefix
    /// once. Encoded store/map metadata is retained without reparsing each query.
    pub fn prepare_horizontal_advance_variation(
        &self,
        coordinates: &[i16],
    ) -> Result<HorizontalAdvanceInstance, FontError> {
        let views = views(self, coordinates)?;
        let mut scalars = Vec::new();
        if let Some(v) = &views {
            let store = v.store.view();
            scalars
                .try_reserve_exact(usize::from(store.region_count()))
                .map_err(|_| FontError::Capacity)?;
            for region in 0..store.region_count() {
                scalars.push(store.region_scalar(coordinates, region)?);
            }
        }
        let mut owned_coordinates = Vec::new();
        owned_coordinates
            .try_reserve_exact(coordinates.len())
            .map_err(|_| FontError::Capacity)?;
        owned_coordinates.extend_from_slice(coordinates);
        Ok(HorizontalAdvanceInstance {
            source: self.clone(),
            coordinates: owned_coordinates,
            views,
            scalars,
            base: BaseAdvance::new(self),
        })
    }
    /// Prepare the independent source-bearing contract. Present HVAR requires
    /// its complete version-1 header and nonzero store; the LSB map validates
    /// before the legacy advance store, preserving original error ordering.
    pub fn prepare_horizontal_metrics_variation(
        &self,
        coordinates: &[i16],
    ) -> Result<HorizontalMetricsInstance, FontError> {
        let left_bearing_map = if let Some((_, span)) = self.table_span(HVAR) {
            let data = span.get(self.data());
            if data.len() < 20
                || bytes::u32(data, 0) != Some(0x10000)
                || bytes::u32(data, 4) == Some(0)
            {
                return Err(FontError::InvalidFace);
            }
            let offset = bytes::u32(data, 12).unwrap() as usize;
            if offset == 0 {
                None
            } else {
                Some(OwnedMap::parse(self, span, offset)?)
            }
        } else {
            None
        };
        let advance = self.prepare_horizontal_advance_variation(coordinates)?;
        Ok(HorizontalMetricsInstance {
            advance,
            left_bearing_map,
        })
    }
    /// Original base advance plus HVAR only; the native no-phantom-scratch route.
    pub fn design_advance_width_hvar(
        &self,
        glyph: u16,
        coordinates: &[i16],
    ) -> Result<f32, FontError> {
        let base = base_advance(self, glyph)?;
        if coordinates.is_empty() {
            return Ok(base);
        }
        Ok(base
            + self
                .horizontal_advance_variation(glyph, coordinates)?
                .unwrap_or(0.0))
    }
    /// Original full unscaled advance, with HVAR precedence over gvar phantoms.
    /// Empty requests return the base before variation-table validation. This
    /// does not position glyphs, apply hints or select a raster policy.
    pub fn design_advance_width(&self, glyph: u16, coordinates: &[i16]) -> Result<f32, FontError> {
        let base = base_advance(self, glyph)?;
        if coordinates.is_empty() {
            return Ok(base);
        }
        let delta = match self.horizontal_advance_variation(glyph, coordinates)? {
            Some(value) => value,
            None => phantom_advance(self, glyph, coordinates)?,
        };
        Ok(base + delta)
    }
    /// Original variation item inventory: simple points or component descriptors,
    /// followed by four phantom slots. Missing glyf/loca retains four slots.
    pub fn glyph_variation_item_count(&self, glyph: u16) -> Result<u32, FontError> {
        if self.glyph_count().is_none_or(|count| glyph >= count) {
            return Err(FontError::InvalidGlyph);
        }
        if self.table(Tag::new(*b"glyf")).is_none() || self.table(Tag::new(*b"loca")).is_none() {
            return Ok(4);
        }
        let r = self.glyph_requirements(glyph)?;
        let count = match r.kind {
            GlyphKind::Empty => 0,
            GlyphKind::Simple => r.point_count,
            GlyphKind::Composite => self.composite_requirements(glyph)?.component_count,
        };
        count.checked_add(4).ok_or(FontError::InvalidGlyph)
    }
}
fn views(face: &SfntFace, coordinates: &[i16]) -> Result<Option<Views>, FontError> {
    let Some((_, span)) = face.table_span(HVAR) else {
        return Ok(None);
    };
    let data = span.get(face.data());
    bytes::bytes(data, 0, 12).ok_or(FontError::InvalidFace)?;
    let offset = bytes::u32(data, 4).unwrap() as usize;
    if offset == 0 {
        return Ok(None);
    }
    let axes = face.variation_axis_count()?;
    if coordinates.len() < usize::from(axes) {
        return Err(FontError::Capacity);
    }
    let store = OwnedStore::parse(face, span, offset, axes)?;
    let offset = bytes::u32(data, 8).unwrap() as usize;
    let advance_map = if offset == 0 {
        None
    } else {
        Some(OwnedMap::parse(face, span, offset)?)
    };
    Ok(Some(Views { store, advance_map }))
}
fn indices(map: Option<&OwnedMap>, glyph: u16) -> (u16, u16) {
    map.map_or((0, glyph), |map| map.view().get(u32::from(glyph)))
}
fn base_advance(face: &SfntFace, glyph: u16) -> Result<f32, FontError> {
    BaseAdvance::new(face).width(face, glyph)
}
fn phantom_advance(face: &SfntFace, glyph: u16, coordinates: &[i16]) -> Result<f32, FontError> {
    if face.table(Tag::new(*b"gvar")).is_none() {
        return Ok(0.0);
    }
    let count = face.glyph_variation_item_count(glyph)?;
    let pair = gvar::horizontal_phantom_deltas(face, glyph, coordinates, count)?;
    Ok(pair[1] - pair[0])
}
