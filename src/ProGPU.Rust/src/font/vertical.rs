//! Original static vertical metrics and independent, strict VVAR instances.
use super::{
    FontError, SfntFace, Span, Tag, bytes,
    item_variation::{OwnedMap, OwnedStore},
};

/// Original unscaled `vhea` fields, without a synthetic vertical policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VerticalHeaderMetrics {
    /// Signed ascent.
    pub ascender: i16,
    /// Signed descent.
    pub descender: i16,
    /// Signed line gap.
    pub line_gap: i16,
    /// Maximum original advance.
    pub advance_height_max: u16,
    /// Number of full advance/bearing pairs in vmtx.
    pub number_of_vertical_metrics: u16,
}
/// Original vmtx advance and optional bearing; missing differs from zero.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VerticalGlyphMetrics {
    /// Last complete advance repeats beyond the full metrics inventory.
    pub advance_height: u16,
    /// Original signed bearing, or `None` when its slot is unavailable.
    pub top_side_bearing: Option<i16>,
}
/// Neutral VVAR design-unit deltas. These do not select an outline family's
/// vertical origin or admit sideways drawing. Optional absence differs from zero.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct VerticalMetricsVariation {
    /// Explicit advance delta; absent VVAR gives zero.
    pub advance_height: f32,
    /// Optional top-side-bearing mapping.
    pub top_side_bearing: Option<f32>,
    /// Optional bottom-side-bearing mapping.
    pub bottom_side_bearing: Option<f32>,
    /// Optional vertical-origin mapping, independent of source origin selection.
    pub vertical_origin_y: Option<f32>,
    /// Whether the selected face contains a fully validated VVAR table.
    pub uses_vvar: bool,
}
#[derive(Clone, Debug)]
struct Views {
    store: OwnedStore,
    maps: [Option<OwnedMap>; 4],
}
/// Prepared VVAR generation owning its exact source, normalized request and
/// private scalar cache. Queries cannot substitute another font or mutate scalars.
#[derive(Clone, Debug)]
pub struct VerticalMetricsInstance {
    source: SfntFace,
    coordinates: Vec<i16>,
    glyph_count: u16,
    views: Option<Views>,
    scalars: Vec<f32>,
}
impl VerticalMetricsInstance {
    /// Exact selected immutable face retained by this instance.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Complete original normalized request, with no clamping.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Validated presence, including a present table with no nonzero deltas.
    pub fn uses_vvar(&self) -> bool {
        self.views.is_some()
    }
    /// Cached region scalars in original region order.
    pub fn region_scalars(&self) -> &[f32] {
        &self.scalars
    }
    /// Read one original glyph slot. Out-of-range glyphs fail even without VVAR.
    pub fn variation(&self, glyph: u16) -> Result<VerticalMetricsVariation, FontError> {
        if glyph >= self.glyph_count {
            return Err(FontError::InvalidArgument);
        }
        self.read(glyph)
    }
    /// Fill only the requested prefix, preserving the tail and leaving the whole
    /// destination unchanged on any error. Safe borrows prevent input aliasing.
    /// No allocation occurs; the original dry pass precedes publication.
    pub fn variations_into(
        &self,
        glyphs: &[u16],
        results: &mut [VerticalMetricsVariation],
    ) -> Result<(), FontError> {
        if results.len() < glyphs.len() {
            return Err(FontError::Capacity);
        }
        for &glyph in glyphs {
            self.variation(glyph)?;
        }
        for (&glyph, output) in glyphs.iter().zip(results) {
            // The same immutable validated generation passed the complete dry walk.
            *output = self.read(glyph)?;
        }
        Ok(())
    }
    fn read(&self, glyph: u16) -> Result<VerticalMetricsVariation, FontError> {
        let Some(v) = &self.views else {
            return Ok(VerticalMetricsVariation::default());
        };
        let mut values = [None; 4];
        for (kind, (map, value)) in v.maps.iter().zip(&mut values).enumerate() {
            if kind != 0 && map.is_none() {
                continue;
            }
            let (outer, inner) = map
                .as_ref()
                .map_or((0, glyph), |m| m.view().get(u32::from(glyph)));
            let delta = v
                .store
                .view()
                .delta_with_scalars(&self.scalars, outer, inner)?;
            if !delta.is_finite() {
                return Err(FontError::InvalidFace);
            }
            *value = Some(delta);
        }
        Ok(VerticalMetricsVariation {
            advance_height: values[0].unwrap(),
            top_side_bearing: values[1],
            bottom_side_bearing: values[2],
            vertical_origin_y: values[3],
            uses_vvar: true,
        })
    }
}

impl SfntFace {
    /// Original vhea lookup. Invalid/truncated optional data remains unavailable.
    pub fn vertical_header_metrics(&self) -> Option<VerticalHeaderMetrics> {
        let data = self.table(Tag::new(*b"vhea"))?.bytes;
        bytes::bytes(data, 0, 36)?;
        Some(VerticalHeaderMetrics {
            ascender: bytes::i16(data, 4)?,
            descender: bytes::i16(data, 6)?,
            line_gap: bytes::i16(data, 8)?,
            advance_height_max: bytes::u16(data, 10)?,
            number_of_vertical_metrics: bytes::u16(data, 34)?,
        })
    }
    /// Original vmtx lookup, including repeated advances and unavailable bearings.
    pub fn vertical_glyph_metrics(&self, glyph: u16) -> Option<VerticalGlyphMetrics> {
        let count = self.vertical_header_metrics()?.number_of_vertical_metrics;
        let data = self.table(Tag::new(*b"vmtx"))?.bytes;
        if count == 0 {
            return None;
        }
        let advance = usize::from(glyph.min(count - 1)) * 4;
        let bearing = if glyph < count {
            advance + 2
        } else {
            usize::from(count) * 4 + usize::from(glyph - count) * 2
        };
        Some(VerticalGlyphMetrics {
            advance_height: bytes::u16(data, advance)?,
            top_side_bearing: bytes::i16(data, bearing),
        })
    }
    /// Positive original glyf header bounds in minX/minY/maxX/maxY order.
    /// No contour reconstruction, CFF control bounds or curve extrema are inferred.
    pub fn glyph_bounds(&self, glyph: u16) -> Option<[i16; 4]> {
        if glyph >= self.glyph_count()? {
            return None;
        }
        let record = self.glyph_data(glyph)?;
        let b = record.bounds;
        (!record.bytes.is_empty() && b[2] > b[0] && b[3] > b[1]).then_some(b)
    }
    /// Static vmtx advance, otherwise original horizontal ascent minus descent.
    pub fn design_advance_height(&self, glyph: u16) -> Option<i32> {
        if let Some(v) = self.vertical_glyph_metrics(glyph) {
            return Some(i32::from(v.advance_height));
        }
        let h = self.horizontal_header_metrics()?;
        Some(i32::from(h.ascender) - i32::from(h.descender))
    }
    /// Original static VORG/vmtx/glyf/hhea origin selection. This is independent
    /// of varied CFF source control bounds and never automatically applies VVAR.
    pub fn design_vertical_origin_y(&self, glyph: u16) -> Option<i32> {
        if let Some(origin) = vorg_origin(self, glyph) {
            return Some(origin);
        }
        if let Some(bearing) = self
            .vertical_glyph_metrics(glyph)
            .and_then(|v| v.top_side_bearing)
            && let Some(b) = self.glyph_bounds(glyph)
        {
            return Some(i32::from(b[3]) + i32::from(bearing));
        }
        let h = self.horizontal_header_metrics()?;
        let advance = i32::from(h.ascender) - i32::from(h.descender);
        if let Some(b) = self.glyph_bounds(glyph) {
            return Some(
                i32::from(b[3]) + (advance - (i32::from(b[3]) - i32::from(b[1]))).div_euclid(2),
            );
        }
        Some(if glyph < self.glyph_count()? {
            advance.div_euclid(2)
        } else {
            i32::from(h.ascender)
        })
    }
    /// Validate the entire strict VVAR boundary, including unused mappings and
    /// raw directory duplicates. Absence remains distinct from a zero-region store.
    pub fn vertical_metrics_variation_region_count(
        &self,
        coordinates: &[i16],
    ) -> Result<Option<u16>, FontError> {
        Ok(preflight(self, coordinates)?
            .1
            .map(|v| v.store.view().region_count()))
    }
    /// Prepare exact-axis normalized VVAR deltas. Whole-table validation and all
    /// scalar checks complete before the owned candidate becomes observable.
    pub fn prepare_vertical_metrics_variation(
        &self,
        coordinates: &[i16],
    ) -> Result<VerticalMetricsInstance, FontError> {
        let (glyph_count, views) = preflight(self, coordinates)?;
        let mut scalars = Vec::new();
        if let Some(v) = &views {
            let store = v.store.view();
            scalars
                .try_reserve_exact(usize::from(store.region_count()))
                .map_err(|_| FontError::Capacity)?;
            for region in 0..store.region_count() {
                let scalar = store.region_scalar(coordinates, region)?;
                if !scalar.is_finite() || !(0.0..=1.0).contains(&scalar) {
                    return Err(FontError::InvalidFace);
                }
                scalars.push(scalar);
            }
        }
        let mut owned = Vec::new();
        owned
            .try_reserve_exact(coordinates.len())
            .map_err(|_| FontError::Capacity)?;
        owned.extend_from_slice(coordinates);
        Ok(VerticalMetricsInstance {
            source: self.clone(),
            coordinates: owned,
            glyph_count,
            views,
            scalars,
        })
    }
}

fn vorg_origin(face: &SfntFace, glyph: u16) -> Option<i32> {
    let data = face.table(Tag::new(*b"VORG"))?.bytes;
    bytes::bytes(data, 0, 8)?;
    let count = usize::from(bytes::u16(data, 6)?);
    bytes::bytes(data, 8, count * 4)?;
    let (mut low, mut high) = (0, count);
    while low < high {
        let mid = low + (high - low) / 2;
        let at = 8 + mid * 4;
        match glyph.cmp(&bytes::u16(data, at)?) {
            std::cmp::Ordering::Less => high = mid,
            std::cmp::Ordering::Greater => low = mid + 1,
            std::cmp::Ordering::Equal => return bytes::i16(data, at + 2).map(i32::from),
        }
    }
    bytes::i16(data, 4).map(i32::from)
}
fn strict_table(face: &SfntFace) -> Result<Option<Span>, FontError> {
    let mut table = None;
    for index in 0..usize::from(face.table_count) {
        let at = face.directory + index * 16;
        if bytes::bytes(face.data(), at, 4) != Some(b"VVAR") {
            continue;
        }
        if table.is_some() {
            return Err(FontError::InvalidFace);
        }
        let offset = bytes::u32(face.data(), at + 8).unwrap() as usize;
        let length = bytes::u32(face.data(), at + 12).unwrap() as usize;
        bytes::bytes(face.data(), offset, length).ok_or(FontError::InvalidFace)?;
        table = Some(Span { offset, length });
    }
    Ok(table)
}
fn preflight(face: &SfntFace, coordinates: &[i16]) -> Result<(u16, Option<Views>), FontError> {
    let glyphs = face.glyph_count().ok_or(FontError::InvalidFace)?;
    let axes = face.variation_axis_count()?;
    if coordinates.len() != usize::from(axes)
        || coordinates.iter().any(|c| !(-16384..=16384).contains(c))
    {
        return Err(FontError::InvalidArgument);
    }
    let Some(table) = strict_table(face)? else {
        return Ok((glyphs, None));
    };
    let fvar = face
        .table(Tag::new(*b"fvar"))
        .ok_or(FontError::InvalidFace)?
        .bytes;
    let data = table.get(face.data());
    if axes == 0
        || fvar.len() < 16
        || bytes::u32(fvar, 0) != Some(0x10000)
        || bytes::u16(fvar, 4).unwrap() < 16
        || bytes::u16(fvar, 10).unwrap() < 20
        || data.len() < 24
        || bytes::u32(data, 0) != Some(0x10000)
    {
        return Err(FontError::InvalidFace);
    }
    let offset = bytes::u32(data, 4).unwrap() as usize;
    if offset < 24 {
        return Err(FontError::InvalidFace);
    }
    let store = OwnedStore::parse(face, table, offset, axes)?;
    store.view().validate_vertical()?;
    let mut maps = [None, None, None, None];
    for (kind, slot) in maps.iter_mut().enumerate() {
        let offset = bytes::u32(data, 8 + kind * 4).unwrap() as usize;
        if offset == 0 {
            continue;
        }
        if offset < 24
            || bytes::bytes(data, offset, 4).is_none()
            || data[offset] != 0
            || data[offset + 1] & 0xc0 != 0
        {
            return Err(FontError::InvalidFace);
        }
        let map = OwnedMap::parse(face, table, offset)?;
        map.view().validate_vertical(store.view())?;
        *slot = Some(map);
    }
    if maps[0].is_none() {
        for glyph in 0..glyphs {
            if !store.view().valid_vertical_reference(0, glyph) {
                return Err(FontError::InvalidFace);
            }
        }
    }
    Ok((glyphs, Some(Views { store, maps })))
}
