//! Original COLR version-zero layer inventory, including v1's legacy base list.
use super::*;

/// One original ordered COLR glyph layer, using the selected immutable CPAL
/// palette. SVG geometry/brush layers are a separate source contract.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ColrGlyphLayer {
    /// Original layer glyph identifier, without recursive substitution.
    pub glyph: u16,
    /// Straight RGBA; original missing/foreground palette entries use white.
    pub color: [f32; 4],
}
/// Original retained color layer. Legacy COLR uses glyph/color; SVG owns its
/// source geometry/material with the original SVG-coordinate flag. The fields
/// remain independent instead of fabricating glyph IDs for SVG geometry.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct FontColorLayer {
    /// Original legacy layer glyph, default zero for SVG.
    pub glyph: u16,
    /// Original legacy straight color, default zero for SVG.
    pub color: [f32; 4],
    /// Owned original SVG geometry; absent for a legacy glyph layer.
    pub geometry: Option<Arc<crate::vector::PathGeometry>>,
    /// Owned original SVG material; absent for a legacy glyph layer.
    pub brush: Option<Arc<crate::vector::Brush>>,
    /// Original independent source coordinate policy.
    pub uses_svg_coordinates: bool,
}
impl ManagedFont {
    /// Original color-layer capability: any matching legacy COLR base (including
    /// an empty one), otherwise a nonempty successfully parsed SVG layer list.
    pub fn has_color_layers(&self, glyph: u16) -> Result<bool, FontError> {
        if self.has_colr_glyph(glyph)? {
            return Ok(true);
        }
        Ok(self
            .svg_color_layers(glyph)?
            .is_some_and(|layers| !layers.is_empty()))
    }
    /// Original COLR-before-SVG layer selection. A matching empty legacy base
    /// prevents SVG fallback. Returned SVG snapshots share the selected font's
    /// actual cached generation; callers cannot mutate that shared generation.
    pub fn color_layers(&self, glyph: u16) -> Result<Option<Arc<[FontColorLayer]>>, FontError> {
        if let Some(layers) = self.colr_glyph_layers(glyph)? {
            let mut output = Vec::new();
            output
                .try_reserve_exact(layers.len())
                .map_err(|_| FontError::Capacity)?;
            for layer in layers {
                output.push(FontColorLayer {
                    glyph: layer.glyph,
                    color: layer.color,
                    ..Default::default()
                });
            }
            return Ok(Some(output.into()));
        }
        self.svg_color_layers(glyph)
    }
    /// Original per-selected-font SVG cache, including negative parse results.
    /// Missing tables do not allocate entries. XML/shape rejection keeps the
    /// source None result; capacity/source read failures publish no cache entry.
    pub fn svg_color_layers(&self, glyph: u16) -> Result<Option<Arc<[FontColorLayer]>>, FontError> {
        if self.source().table(Tag::new(*b"SVG ")).is_none() {
            return Ok(None);
        }
        let mut cache = self
            .state
            .svg_layers
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some(cached) = cache.get(&glyph) {
            return Ok(cached.clone());
        }
        let layers = match self.svg_glyph_document(glyph)? {
            Some(xml) => super::svg_parser::parse(&xml, glyph, self.metadata().units_per_em)?,
            None => None,
        };
        cache.try_reserve(1).map_err(|_| FontError::Capacity)?;
        cache.insert(glyph, layers.clone());
        Ok(layers)
    }
    /// Whether the original legacy COLR base list contains this glyph, including
    /// a base record with zero layers. This does not infer SVG/paint-v1 support.
    pub fn has_colr_glyph(&self, glyph: u16) -> Result<bool, FontError> {
        Ok(self.colr_base_record(glyph)?.is_some())
    }
    /// Complete original ordered legacy layers, or None for no matching base.
    /// An empty matching layer list remains Some(empty), without SVG fallback.
    /// Reads retain the original whole-font bound rather than imposing a new
    /// declared-layer-count or per-table-bound rule on the managed source route.
    pub fn colr_glyph_layers(&self, glyph: u16) -> Result<Option<Vec<ColrGlyphLayer>>, FontError> {
        let Some(at) = self.colr_base_record(glyph)? else {
            return Ok(None);
        };
        let first = u32::from(metadata::word(self.source(), at.wrapping_add(2))?);
        let count = metadata::word(self.source(), at.wrapping_add(4))?;
        let mut layers = Vec::new();
        layers
            .try_reserve_exact(usize::from(count))
            .map_err(|_| FontError::Capacity)?;
        for index in 0..u32::from(count) {
            let at = self
                .root
                .raw
                .colr_layer_records
                .wrapping_add((first + index) * 4);
            let glyph = metadata::word(self.source(), at)?;
            let palette = metadata::word(self.source(), at.wrapping_add(2))?;
            let color = self
                .metadata()
                .palette
                .get(usize::from(palette))
                .copied()
                .unwrap_or([1.0; 4]);
            layers.push(ColrGlyphLayer { glyph, color });
        }
        Ok(Some(layers))
    }
    fn colr_base_record(&self, glyph: u16) -> Result<Option<u32>, FontError> {
        let mut low = 0i32;
        let mut high = i32::from(self.root.raw.colr_base_count) - 1;
        while low <= high {
            let mid = (low + high) / 2;
            let at = self.root.raw.colr_base_records.wrapping_add(mid as u32 * 6);
            let candidate = metadata::word(self.source(), at)?;
            match candidate.cmp(&glyph) {
                std::cmp::Ordering::Equal => return Ok(Some(at)),
                std::cmp::Ordering::Less => low = mid + 1,
                std::cmp::Ordering::Greater => high = mid - 1,
            }
        }
        Ok(None)
    }
}
