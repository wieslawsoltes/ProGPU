//! Original font lookup, raw control-point envelope and four-pixel raster margin.
use super::*;
use crate::font::ManagedGlyphOutline;

impl GlyphAtlas {
    /// Map the original code point, preserving the source's four explicit
    /// whitespace shortcuts (space, tab, LF, CR) and selected-font advance.
    /// Sizes currently require finite positive em. Bitmap/legacy COLR routing
    /// precedes ordinary outlines; SVG geometry remains a separate pending port.
    pub async fn get_or_create_glyph(
        &mut self,
        font: &ManagedFont,
        code_point: u32,
        size: f32,
        subpixel_x: u8,
    ) -> Result<GlyphInfo, GlyphGpuError> {
        validate_size(size)?;
        let glyph = font.glyph_index(code_point)?;
        if matches!(code_point, 0x20 | 0x09 | 0x0a | 0x0d) {
            return Ok(GlyphInfo {
                advance: font.advance_width(glyph, size)?,
                ..Default::default()
            });
        }
        self.get_or_create_glyph_by_index(font, glyph, size, subpixel_x, false)
            .await
    }
    /// Acquire the exact font instance/glyph/size/phase. `prefer_atlas` permits
    /// LRU reuse only after growth is exhausted, protecting current-frame users.
    /// Capacity sentinels retry only in a later frame with this flag set.
    ///
    /// A batch can return a pending entry; flush before submitting a consumer.
    /// Cancelling this future retires its reservation and pending raster claims.
    pub async fn get_or_create_glyph_by_index(
        &mut self,
        font: &ManagedFont,
        glyph: u16,
        size: f32,
        subpixel_x: u8,
        prefer_atlas: bool,
    ) -> Result<GlyphInfo, GlyphGpuError> {
        validate_size(size)?;
        let key = Key::new(font, glyph, size, subpixel_x);
        if let Some(cached) = self.cache.get(&key) {
            if !cached.capacity_fallback || !prefer_atlas || cached.frame == self.frame {
                self.usages.try_reserve(1).map_err(|_| Error::Capacity)?;
                let cached = self.cache.get_mut(&key).expect("existing font cache entry");
                cached.frame = self.frame;
                let info = cached.info;
                self.record_usage(&key, info);
                return Ok(info);
            }
            self.cache.remove(&key);
        }
        if let Some(info) = self
            .try_bitmap(font, glyph, size, &key, prefer_atlas)
            .await?
        {
            return Ok(info);
        }
        if font.has_colr_glyph(glyph)? {
            // Original vector-layer sentinel, deliberately not atlas residency.
            let info = GlyphInfo {
                size: [size as u32; 2],
                bearing: [0.0, -size * 0.8],
                advance: size,
                ..Default::default()
            };
            self.reserve_acquisition()?;
            if self.batch_depth != 0 && !info.size.contains(&0) {
                self.pending_keys.push(key.clone());
            }
            self.cache.insert_reserved(CachedGlyph {
                key,
                info,
                frame: self.frame,
                capacity_fallback: false,
            });
            return Ok(info);
        }
        if font.svg_glyph_document(glyph)?.is_some() {
            return Err(GlyphGpuError::UnsupportedSvgGlyph);
        }
        let advance = font.advance_width(glyph, size)?;
        let scale = size / f32::from(font.metadata().units_per_em);
        if !scale.is_finite() || scale <= 0.0 {
            return Err(Error::InvalidInput.into());
        }
        let outline = font.glyph_outline(glyph)?;
        let bounds = outline
            .as_deref()
            .map(|outline| raster_bounds(outline, scale))
            .transpose()?
            .flatten();
        self.reserve_acquisition()?;
        let Some(bounds) = bounds else {
            let info = GlyphInfo {
                advance,
                ..Default::default()
            };
            self.cache.insert_reserved(CachedGlyph {
                key,
                info,
                frame: self.frame,
                capacity_fallback: false,
            });
            return Ok(info);
        };
        // Original hinted failure-ticket best fit also handles Rust async
        // cancellation of ordinary uploads: no abandoned region leaks its shelf.
        let region = match self.reuse_failed(bounds.size, false) {
            Some(region) => Some(region),
            None => self.allocate(bounds.size, prefer_atlas, false).await?,
        };
        let Some(region) = region else {
            let info = GlyphInfo {
                advance,
                ..Default::default()
            };
            self.cache.insert_reserved(CachedGlyph {
                key,
                info,
                frame: self.frame,
                capacity_fallback: true,
            });
            return Ok(info);
        };
        let mut guard = Acquisition {
            atlas: self,
            key,
            region,
            committed: false,
        };
        let before = guard.atlas.rasterizer.outline_counts()[0];
        let slot = guard
            .atlas
            .rasterizer
            .upload_font_glyph(font, glyph)
            .await?;
        guard.atlas.new_glyphs += u64::from(guard.atlas.rasterizer.outline_counts()[0] - before);
        let request = GlyphRasterRequest {
            slot,
            origin: bounds.origin.map(|v| v as f32),
            scale,
            subpixel_x: f32::from(subpixel_x) * 0.25,
            atlas_origin: region.origin,
            size: bounds.size,
            mode: GlyphRasterMode::Grayscale,
        };
        let pending = guard.atlas.stage(request).await?;
        let mut info = GlyphInfo {
            origin: region.origin,
            size: bounds.size,
            bearing: bounds.origin.map(|v| v as f32),
            advance,
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
    async fn try_bitmap(
        &mut self,
        font: &ManagedFont,
        glyph: u16,
        size: f32,
        key: &Key,
        prefer: bool,
    ) -> Result<Option<GlyphInfo>, GlyphGpuError> {
        let Some(decoded) = font.decode_bitmap_glyph(glyph, size)? else {
            return Ok(None);
        };
        let advance = font.advance_width(glyph, size)?;
        self.reserve_acquisition()?;
        let region = match self.reuse_failed(decoded.size, true) {
            Some(region) => Some(region),
            None => self.allocate(decoded.size, prefer, true).await?,
        };
        // Original bitmap allocation failure continues into COLR/outline policy,
        // rather than caching a new color-only sentinel or resizing the bitmap.
        let Some(region) = region else {
            return Ok(None);
        };
        let mut guard = Acquisition {
            atlas: self,
            key: key.clone(),
            region,
            committed: false,
        };
        guard
            .atlas
            .rasterizer
            .write_color_glyph(
                &guard.atlas.color,
                region.origin,
                decoded.size,
                decoded.pixels(),
            )
            .await?;
        let mut info = GlyphInfo {
            origin: region.origin,
            size: decoded.size,
            bearing: decoded.bearing,
            render_size: decoded.render_size,
            advance,
            raster_scale: decoded.raster_scale,
            is_color_bitmap: true,
            region: Some(region),
            ..Default::default()
        };
        info.update_uv(guard.atlas.color_size());
        let frame = guard.atlas.frame;
        guard.atlas.cache.insert_reserved(CachedGlyph {
            key: key.clone(),
            info,
            frame,
            capacity_fallback: false,
        });
        guard.atlas.record_usage(key, info);
        guard.committed = true;
        Ok(Some(info))
    }
}
fn validate_size(size: f32) -> Result<(), Error> {
    if !size.is_finite() || size <= 0.0 {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
struct RasterBounds {
    origin: [i32; 2],
    size: [u32; 2],
}
fn raster_bounds(outline: &ManagedGlyphOutline, scale: f32) -> Result<Option<RasterBounds>, Error> {
    let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    let mut any = false;
    let mut visit = |point: [f32; 2]| -> Result<(), Error> {
        let point = [point[0] * scale, -point[1] * scale];
        for axis in 0..2 {
            if !point[axis].is_finite() {
                return Err(Error::InvalidInput);
            }
            bounds[axis] = min(bounds[axis], point[axis]);
            bounds[axis + 2] = max(bounds[axis + 2], point[axis]);
        }
        Ok(())
    };
    for figure in outline.figures() {
        visit(figure.start)?;
        any = true;
        for segment in &figure.segments {
            if segment.kind <= 2 {
                for &point in &segment.points[1..segment.kind as usize + 2] {
                    visit(point)?;
                }
            }
        }
    }
    if !any {
        return Ok(None);
    }
    let mut origin = [0; 2];
    let mut size = [0; 2];
    for axis in 0..2 {
        origin[axis] = narrow(bounds[axis].floor())?
            .checked_sub(4)
            .ok_or(Error::Capacity)?;
        let end = narrow(bounds[axis + 2].ceil())?
            .checked_add(4)
            .ok_or(Error::Capacity)?;
        let span = end.checked_sub(origin[axis]).ok_or(Error::Capacity)?;
        if span <= 0 {
            return Ok(None);
        }
        size[axis] = span as u32;
    }
    Ok(Some(RasterBounds { origin, size }))
}
fn narrow(value: f32) -> Result<i32, Error> {
    if f64::from(value) < f64::from(i32::MIN) || f64::from(value) > f64::from(i32::MAX) {
        return Err(Error::Capacity);
    }
    Ok(value as i32)
}
// Original finite Math.Min/Max signed-zero ordering, independent of the GPU
// descriptor's analytic bounds and PathGeometry's finite-point filtering.
fn min(a: f32, b: f32) -> f32 {
    if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() | b.to_bits())
    } else {
        a.min(b)
    }
}
fn max(a: f32, b: f32) -> f32 {
    if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() & b.to_bits())
    } else {
        a.max(b)
    }
}
