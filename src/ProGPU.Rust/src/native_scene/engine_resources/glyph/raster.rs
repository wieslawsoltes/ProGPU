//! Original native glyph shelf placement and byte-exact coverage sharing.
use super::*;
use crate::text::{
    glyph::{GlyphUniforms, GpuGlyphRecord},
    paint::GlyphInstance,
};
use std::collections::HashMap;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeGlyphRaster {
    pub atlas_x: u32,
    pub atlas_y: u32,
    pub width: u32,
    pub height: u32,
    pub output_offset: u32,
    pub output_bytes_per_row: u32,
    pub x_start: f32,
    pub y_start: f32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeColorGlyphRaster {
    pub atlas_x: u32,
    pub atlas_y: u32,
}

/// Immutable raster work plus original source-indexed aliases. The source page
/// owns every outline, bitmap, positioned occurrence and target frame.
#[derive(Clone, Debug)]
pub struct NativeGlyphRasterPage {
    source: Arc<NativeGlyphPage>,
    records: Arc<[GpuGlyphRecord]>,
    uniforms: Arc<[GlyphUniforms]>,
    rasters: Arc<[NativeGlyphRaster]>,
    work_indices: Arc<[u32]>,
    color_rasters: Arc<[NativeColorGlyphRaster]>,
    instances: Arc<[GlyphInstance]>,
    source_alphas: Arc<[f32]>,
    atlas_size: u32,
    color_atlas_size: u32,
    coverage_staging_bytes: u64,
    has_binary: bool,
    has_vector: bool,
}
impl NativeGlyphRasterPage {
    /// `retained_atlas_size` is the actual engine generation's current size;
    /// a new native engine starts at 1024. No source font work occurs here.
    pub fn compile(
        source: Arc<NativeGlyphPage>,
        retained_atlas_size: u32,
        draw_opacity: f32,
    ) -> Result<Self, Error> {
        if !matches!(retained_atlas_size, 1024 | 2048 | 4096)
            || !draw_opacity.is_finite()
            || !(0.0..=1.0).contains(&draw_opacity)
        {
            return Err(Error::InvalidInput);
        }
        let mut records = allocate(source.outlines.len())?;
        let mut uniforms: Vec<GlyphUniforms> = allocate(source.outlines.len())?;
        let mut rasters: Vec<NativeGlyphRaster> = allocate(source.outlines.len())?;
        let mut work_indices = allocate(source.outlines.len())?;
        let byte_count = source
            .segments
            .len()
            .checked_mul(wire::PathSegment::WIRE_SIZE)
            .ok_or(Error::Capacity)?;
        let mut raw = allocate(byte_count)?;
        raw.resize(byte_count, 0);
        for (segment, bytes) in source
            .segments
            .iter()
            .zip(raw.chunks_exact_mut(wire::PathSegment::WIRE_SIZE))
        {
            segment.write_into(bytes).map_err(|_| Error::InvalidInput)?;
        }
        let mut shared: HashMap<([u32; 6], &[u8]), usize> = HashMap::new();
        shared
            .try_reserve(source.outlines.len())
            .map_err(|_| Error::Capacity)?;
        let (mut atlas_size, mut x, mut y, mut row, mut output) =
            (retained_atlas_size, 2u32, 2u32, 0u32, 0u32);
        let (mut has_binary, mut has_vector) = (false, false);
        for (index, outline) in source.outlines.iter().enumerate() {
            values::glyph_outline(outline, source.segments.len() as u64)
                .ok_or(Error::InvalidInput)?;
            let start = usize::try_from(outline.segment_offset).map_err(|_| Error::Capacity)?;
            let end = start
                .checked_add(outline.segment_count as usize)
                .ok_or(Error::Capacity)?;
            let selected = source.segments.get(start..end).ok_or(Error::InvalidInput)?;
            if !glyph_scan::outline(outline, selected) {
                return Err(Error::InvalidInput);
            }
            let binary = selected[0].pad0 != 0;
            if binary
                && source.engine_flags
                    & (ENGINE_GLYPH_RASTER_SHADER_FALLBACK
                        | ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK
                        | ENGINE_GLYPH_SCALAR_CPU_FALLBACK)
                    != 0
            {
                return Err(Error::UnsupportedExecution);
            }
            has_binary |= binary;
            has_vector |= !binary;
            let x_start = (outline.min_x * outline.raster_scale).floor() - 4.0;
            let y_start = (-outline.max_y * outline.raster_scale).floor() - 4.0;
            // The source expression is evaluated in float before assignment to double.
            let width = f64::from((outline.max_x * outline.raster_scale).ceil() + 4.0 - x_start);
            let height = f64::from((-outline.min_y * outline.raster_scale).ceil() + 4.0 - y_start);
            if !width.is_finite()
                || !height.is_finite()
                || width <= 0.0
                || height <= 0.0
                || width > 4092.0
                || height > 4092.0
            {
                return Err(Error::UnsupportedExecution);
            }
            let (width, height) = (width as u32, height as u32);
            records.push(GpuGlyphRecord {
                start_segment: count_u32(start)?,
                segment_count: count_u32(selected.len())?,
                bounds: [outline.min_x, outline.min_y, outline.max_x, outline.max_y],
                padding: [u32::from(binary), 0],
            });
            let bits = [
                outline.min_x,
                outline.min_y,
                outline.max_x,
                outline.max_y,
                outline.raster_scale,
                outline.subpixel_x,
            ]
            .map(f32::to_bits);
            let key = (
                bits,
                &raw[start * wire::PathSegment::WIRE_SIZE..end * wire::PathSegment::WIRE_SIZE],
            );
            if let Some(&previous) = shared.get(&key) {
                rasters.push(rasters[previous]);
                uniforms.push(uniforms[previous]);
                continue;
            }
            shared.insert(key, index);
            work_indices.push(count_u32(index)?);
            while width + 4 > atlas_size && atlas_size < 4096 {
                atlas_size *= 2;
            }
            if x + width + 2 > atlas_size {
                x = 2;
                y = y.checked_add(row + 2).ok_or(Error::Capacity)?;
                row = 0;
            }
            while y + height + 2 > atlas_size && atlas_size < 4096 {
                atlas_size *= 2;
            }
            if y + height + 2 > atlas_size {
                return Err(Error::Capacity);
            }
            let pitch = (width + 255) & !255;
            output = output.checked_add(511).ok_or(Error::Capacity)? & !511;
            let next = u64::from(output) + u64::from(pitch) * u64::from(height);
            let next = u32::try_from(next).map_err(|_| Error::Capacity)?;
            rasters.push(NativeGlyphRaster {
                atlas_x: x,
                atlas_y: y,
                width,
                height,
                output_offset: output,
                output_bytes_per_row: pitch,
                x_start,
                y_start,
            });
            uniforms.push(GlyphUniforms {
                x_start,
                y_start,
                scale: outline.raster_scale,
                glyph_index: count_u32(index)?,
                output_offset_words: output / 4,
                output_row_words: pitch / 4,
                width,
                height,
                subpixel_x: outline.subpixel_x,
                atlas_x: x as f32,
                atlas_y: y as f32,
                padding: 0.0,
            });
            output = next;
            x += width + 2;
            row = row.max(height);
        }
        let (color_rasters, color_atlas_size) = pack_color(&source.color_bitmaps)?;
        let mut instances = allocate(source.glyphs.len())?;
        let mut source_alphas = allocate(source.glyphs.len())?;
        for (index, glyph) in source.glyphs.iter().enumerate() {
            let color_index = source.color_bitmap_indices[index];
            let color = color_index != SCENE_NO_INDEX;
            let (bearing_size, texture_coordinates) = if color {
                let bitmap = source
                    .color_bitmaps
                    .get(color_index as usize)
                    .ok_or(Error::InvalidInput)?;
                let raster = color_rasters
                    .get(color_index as usize)
                    .ok_or(Error::InvalidInput)?;
                (
                    [
                        bitmap.bear_x,
                        bitmap.bear_y,
                        if bitmap.render_width > 0.0 {
                            bitmap.render_width
                        } else {
                            bitmap.width as f32
                        },
                        if bitmap.render_height > 0.0 {
                            bitmap.render_height
                        } else {
                            bitmap.height as f32
                        },
                    ],
                    [
                        raster.atlas_x as f32,
                        raster.atlas_y as f32,
                        (raster.atlas_x + bitmap.width) as f32,
                        (raster.atlas_y + bitmap.height) as f32,
                    ],
                )
            } else {
                let r = rasters
                    .get(glyph.outline_index as usize)
                    .ok_or(Error::InvalidInput)?;
                (
                    [r.x_start, r.y_start, r.width as f32, r.height as f32],
                    [
                        r.atlas_x as f32,
                        r.atlas_y as f32,
                        (r.atlas_x + r.width) as f32,
                        (r.atlas_y + r.height) as f32,
                    ],
                )
            };
            let mut instance = GlyphInstance {
                snapped_logical_position: [glyph.position.x, glyph.position.y],
                basis_x: [glyph.basis_x.x, glyph.basis_x.y],
                basis_y: [glyph.basis_y.x, glyph.basis_y.y],
                bearing_size,
                texture_coordinates,
                color: [
                    glyph.color.r,
                    glyph.color.g,
                    glyph.color.b,
                    glyph.color.a * draw_opacity,
                ],
                scale_bold_italic_flags: [
                    glyph.atlas_to_logical_scale,
                    glyph.bold_offset,
                    glyph.italic_skew,
                    if color { 8.0 } else { 0.0 },
                ],
                brush_index: -1.0,
                padding: 0.0,
            };
            let style = source.style_indices[index];
            if style != SCENE_NO_INDEX {
                let value = source
                    .styles
                    .styles()
                    .get(style as usize)
                    .ok_or(Error::InvalidInput)?;
                instance.brush_index = style as f32;
                instance.scale_bold_italic_flags[3] += 16.0 * value.coverage_transfer as f32;
            }
            let paint = source.paint_indices[index];
            if paint != SCENE_NO_INDEX {
                let value = source
                    .paints
                    .get(paint as usize)
                    .ok_or(Error::InvalidInput)?;
                let mode = source.rendering_modes[index];
                if mode > SCENE_TEXT_ALIASED {
                    return Err(Error::InvalidInput);
                }
                instance.padding = f32::from_bits(paint);
                instance.scale_bold_italic_flags[3] = if mode == SCENE_TEXT_ALIASED {
                    -1.0
                } else {
                    16.0 * value.coverage_transfer as f32
                };
            }
            instances.push(instance);
            source_alphas.push(glyph.color.a);
        }
        Ok(Self {
            source,
            records: records.into(),
            uniforms: uniforms.into(),
            rasters: rasters.into(),
            work_indices: work_indices.into(),
            color_rasters: color_rasters.into(),
            instances: instances.into(),
            source_alphas: source_alphas.into(),
            atlas_size,
            color_atlas_size,
            coverage_staging_bytes: u64::from(output),
            has_binary,
            has_vector,
        })
    }
    pub fn source(&self) -> &Arc<NativeGlyphPage> {
        &self.source
    }
    pub fn records(&self) -> &[GpuGlyphRecord] {
        &self.records
    }
    pub fn uniforms(&self) -> &[GlyphUniforms] {
        &self.uniforms
    }
    pub fn rasters(&self) -> &[NativeGlyphRaster] {
        &self.rasters
    }
    pub fn work_indices(&self) -> &[u32] {
        &self.work_indices
    }
    pub fn color_rasters(&self) -> &[NativeColorGlyphRaster] {
        &self.color_rasters
    }
    pub fn instances(&self) -> &[GlyphInstance] {
        &self.instances
    }
    pub fn source_alphas(&self) -> &[f32] {
        &self.source_alphas
    }
    pub fn atlas_size(&self) -> u32 {
        self.atlas_size
    }
    pub fn color_atlas_size(&self) -> u32 {
        self.color_atlas_size
    }
    pub fn coverage_staging_bytes(&self) -> u64 {
        self.coverage_staging_bytes
    }
    pub fn has_binary_glyphs(&self) -> bool {
        self.has_binary
    }
    pub fn has_vector_glyphs(&self) -> bool {
        self.has_vector
    }
}
fn pack_color(
    bitmaps: &[wire::ColorGlyphBitmap],
) -> Result<(Vec<NativeColorGlyphRaster>, u32), Error> {
    let mut rasters = allocate(bitmaps.len())?;
    rasters.resize(bitmaps.len(), NativeColorGlyphRaster::default());
    if bitmaps.is_empty() {
        return Ok((rasters, 0));
    }
    let mut size = 256;
    loop {
        let (mut x, mut y, mut row) = (2u32, 2u32, 0u32);
        let mut fits = true;
        for (bitmap, raster) in bitmaps.iter().zip(&mut rasters) {
            if bitmap.width + 4 > size {
                fits = false;
                break;
            }
            if x + bitmap.width + 2 > size {
                x = 2;
                y = y.checked_add(row + 2).ok_or(Error::Capacity)?;
                row = 0;
            }
            if y + bitmap.height + 2 > size {
                fits = false;
                break;
            }
            *raster = NativeColorGlyphRaster {
                atlas_x: x,
                atlas_y: y,
            };
            x += bitmap.width + 2;
            row = row.max(bitmap.height);
        }
        if fits {
            return Ok((rasters, size));
        }
        if size >= 4096 {
            return Err(Error::Capacity);
        }
        size *= 2;
    }
}
