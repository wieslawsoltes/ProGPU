//! Original native instance transport, with immutable source and atlas leases.
use super::*;
use crate::text::{glyph::GlyphAtlas, paint::GlyphInstance};
#[derive(Debug)]
pub struct ResolvedNativeAtlas {
    pub instances: Vec<GlyphInstance>,
    pub styles: Vec<GpuTextStyle>,
    pub source: Arc<NativeAtlasPlan>,
}
impl NativeAtlasPlan {
    /// Resolve only into the caller's active atlas transaction. The plan already
    /// admitted all source slots, descriptors and placement arithmetic.
    pub async fn resolve_pending(
        self: &Arc<Self>,
        atlas: &mut GlyphAtlas,
    ) -> Result<ResolvedNativeAtlas, Error> {
        if !atlas.is_batch_active() {
            return Err(Error::InvalidInput);
        }
        let mut result = ResolvedNativeAtlas {
            instances: reserve(self.occurrences.len())?,
            styles: reserve(self.styles.len())?,
            source: self.clone(),
        };
        result.styles.extend_from_slice(&self.styles);
        // Native MIL retains all four descriptor slots per source glyph. Acquire
        // each once; exact canonical raster identities share coverage work.
        let mut coverage = reserve(self.rasters.len())?;
        for raster in &self.rasters {
            coverage.push(atlas.get_or_create_native_glyph(raster).await?);
        }
        let mut colors = reserve(self.draw.colors.as_ref().map_or(0, |v| v.records().len()))?;
        if let Some(source) = &self.draw.colors {
            for index in 0..source.records().len() {
                colors.push(
                    atlas
                        .get_or_create_native_color_glyph(source, index as u32)
                        .await?,
                );
            }
        }
        for occurrence in &self.occurrences {
            let (info, is_color) = match occurrence.reference {
                NativeAtlasReference::Outline(index) => (coverage[index as usize], false),
                NativeAtlasReference::Bitmap(index) => (colors[index as usize], true),
            };
            let paint = self.draw.paints[occurrence.style_index as usize];
            let size = std::array::from_fn::<_, 2, _>(|i| {
                if is_color && info.render_size[i] > 0.0 {
                    info.render_size[i]
                } else {
                    info.size[i] as f32
                }
            });
            result.instances.push(GlyphInstance {
                snapped_logical_position: occurrence.position,
                basis_x: occurrence.basis_x,
                basis_y: occurrence.basis_y,
                bearing_size: [info.bearing[0], info.bearing[1], size[0], size[1]],
                texture_coordinates: [
                    info.origin[0] as f32,
                    info.origin[1] as f32,
                    (info.origin[0] + info.size[0]) as f32,
                    (info.origin[1] + info.size[1]) as f32,
                ],
                color: [1.0; 4],
                scale_bold_italic_flags: [
                    occurrence.atlas_to_logical_scale,
                    occurrence.bold_offset,
                    occurrence.italic_skew,
                    if is_color { 8.0 } else { 0.0 }
                        + 16.0 * paint.coverage_transfer.encoding() as f32,
                ],
                brush_index: occurrence.style_index as f32,
                padding: 0.0,
            });
        }
        Ok(result)
    }
}
