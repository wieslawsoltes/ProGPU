//! Native header-bound raster descriptors through the original shared atlas.
use super::*;
use crate::text::native_atlas::{NativeAtlasRaster, NativeColorGlyphBatch};
impl GlyphAtlas {
    /// Coverage identity compares exact native bound/scale/phase and segment
    /// bytes, independently of arena offsets and source positioned occurrences.
    pub async fn get_or_create_native_glyph(
        &mut self,
        raster: &Arc<NativeAtlasRaster>,
    ) -> Result<GlyphInfo, Error> {
        let key = Key::Native(raster.clone());
        if let Some(info) = self.native_cached(&key)? {
            return Ok(info);
        }
        self.reserve_acquisition()?;
        self.hinted_slots
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        let region = match self.reuse_failed(raster.size(), false) {
            Some(region) => region,
            None => self
                .allocate(raster.size(), true, false)
                .await?
                .ok_or(Error::Capacity)?,
        };
        let mut guard = Acquisition {
            atlas: self,
            key,
            region,
            committed: false,
        };
        let slot_key = Key::NativeSlot(raster.clone());
        let slot = match guard.atlas.hinted_slots.get(&slot_key) {
            Some(slot) => slot.clone(),
            None => {
                let before = guard.atlas.rasterizer.outline_counts()[0];
                let slot = guard
                    .atlas
                    .rasterizer
                    .upload_outline(raster.record(), raster.segments())
                    .await?;
                guard.atlas.new_glyphs +=
                    u64::from(guard.atlas.rasterizer.outline_counts()[0] - before);
                guard.atlas.hinted_slots.insert(slot_key, slot.clone());
                slot
            }
        };
        let pending = guard
            .atlas
            .stage(GlyphRasterRequest {
                slot,
                origin: raster.origin(),
                scale: raster.scale(),
                subpixel_x: f32::from(raster.phase()) * 0.25,
                atlas_origin: region.origin,
                size: raster.size(),
                mode: GlyphRasterMode::Grayscale,
            })
            .await?;
        let mut info = GlyphInfo {
            origin: region.origin,
            size: raster.size(),
            bearing: raster.origin(),
            raster_scale: raster.scale(),
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
    pub async fn get_or_create_native_color_glyph(
        &mut self,
        source: &Arc<NativeColorGlyphBatch>,
        index: u32,
    ) -> Result<GlyphInfo, Error> {
        let bitmap = *source
            .records()
            .get(index as usize)
            .ok_or(Error::InvalidInput)?;
        let range = bitmap.pixel_range(source.pixels().len())?;
        let key = Key::NativeColor {
            source: source.clone(),
            index,
        };
        if let Some(info) = self.native_cached(&key)? {
            return Ok(info);
        }
        self.reserve_acquisition()?;
        let size = [bitmap.width, bitmap.height];
        let region = match self.reuse_failed(size, true) {
            Some(region) => region,
            None => self
                .allocate(size, true, true)
                .await?
                .ok_or(Error::Capacity)?,
        };
        let mut guard = Acquisition {
            atlas: self,
            key,
            region,
            committed: false,
        };
        guard
            .atlas
            .rasterizer
            .write_color_glyph_rows(
                &guard.atlas.color,
                region.origin,
                size,
                bitmap.row_bytes,
                &source.pixels()[range],
            )
            .await?;
        let mut info = GlyphInfo {
            origin: region.origin,
            size,
            bearing: bitmap.bearing,
            render_size: bitmap.render_size,
            is_color_bitmap: true,
            region: Some(region),
            ..Default::default()
        };
        info.update_uv(guard.atlas.color_size());
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
    fn native_cached(&mut self, key: &Key) -> Result<Option<GlyphInfo>, Error> {
        if self.cache.get(key).is_none() {
            return Ok(None);
        }
        self.usages.try_reserve(1).map_err(|_| Error::Capacity)?;
        let cached = self.cache.get_mut(key).expect("native glyph cache entry");
        cached.frame = self.frame;
        let info = cached.info;
        self.record_usage(key, info);
        Ok(Some(info))
    }
}
