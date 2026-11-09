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
impl ManagedFont {
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
