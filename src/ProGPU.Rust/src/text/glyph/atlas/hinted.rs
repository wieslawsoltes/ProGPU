//! Original captured physical records through the shared coverage atlas.
use super::*;
use crate::text::{
    font_policy::FontReplayGeometry,
    hinted_replay::{hinted_raster_bounds, validate_hinted_record},
};

impl GlyphAtlas {
    /// Acquire the exact captured raster-generation/outline key. Selected views
    /// share residency and GPU slots; source-equivalent captures remain distinct.
    /// No font instruction, glyph mapping, sizing or phase selection occurs here.
    pub async fn get_or_create_hinted_glyph(
        &mut self,
        geometry: &Arc<FontReplayGeometry>,
        outline_index: u32,
    ) -> Result<GlyphInfo, Error> {
        let record = *geometry
            .outlines()
            .get(outline_index as usize)
            .ok_or(Error::InvalidInput)?;
        let key = Key::hinted(geometry, outline_index);
        if self.cache.get(&key).is_some() {
            self.usages.try_reserve(1).map_err(|_| Error::Capacity)?;
            let cached = self
                .cache
                .get_mut(&key)
                .expect("existing captured cache entry");
            cached.frame = self.frame;
            let info = cached.info;
            self.record_usage(&key, info);
            return Ok(info);
        }
        validate_hinted_record(&record, geometry.segments().len())?;
        let binary = geometry.segments()[record.start_segment as usize].parameters[0] != 0;
        if binary
            && (record.segment_count > 256
                || self.execution_path() != GlyphExecutionPath::NativeCompute)
        {
            return Err(Error::UnsupportedExecution);
        }
        let bounds = hinted_raster_bounds(&record)?;
        self.reserve_acquisition()?;
        self.hinted_slots
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        let region = match self.reuse_failed(bounds.size, false) {
            Some(region) => region,
            None => self
                .allocate(bounds.size, true, false)
                .await?
                .ok_or(Error::Capacity)?,
        };
        let mut guard = Acquisition {
            atlas: self,
            key,
            region,
            committed: false,
        };
        let slot = match guard.atlas.hinted_slots.get(&guard.key) {
            Some(slot) => slot.clone(),
            None => {
                let before = guard.atlas.rasterizer.outline_counts()[0];
                // upload_outline rebases only the descriptor offset; canonical
                // coordinates, bounds, scan metadata and contour offsets survive.
                let slot = guard
                    .atlas
                    .rasterizer
                    .upload_outline(record, geometry.segments())
                    .await?;
                guard.atlas.new_glyphs +=
                    u64::from(guard.atlas.rasterizer.outline_counts()[0] - before);
                guard
                    .atlas
                    .hinted_slots
                    .insert(guard.key.clone(), slot.clone());
                slot
            }
        };
        let pending = guard
            .atlas
            .stage(GlyphRasterRequest {
                slot,
                origin: bounds.origin.map(|v| v as f32),
                scale: 1.0,
                subpixel_x: 0.0,
                atlas_origin: region.origin,
                size: bounds.size,
                mode: if binary {
                    GlyphRasterMode::Binary
                } else {
                    GlyphRasterMode::Grayscale
                },
            })
            .await?;
        let mut info = GlyphInfo {
            origin: region.origin,
            size: bounds.size,
            bearing: bounds.origin.map(|v| v as f32),
            raster_scale: 1.0,
            region: Some(region),
            ..Default::default()
        };
        info.update_uv(guard.atlas.size());
        if pending {
            guard.atlas.pending_keys.push(guard.key.clone());
        }
        let key = guard.key.clone();
        let frame = guard.atlas.frame;
        guard.atlas.cache.insert_reserved(CachedGlyph {
            key: key.clone(),
            info,
            frame,
            capacity_fallback: false,
        });
        guard.atlas.record_usage(&key, info);
        guard.committed = true;
        Ok(info)
    }
}
