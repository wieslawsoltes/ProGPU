use super::*;
pub(super) fn rasterize(
    page: &NativeGlyphRasterPage,
    policy: NativeGlyphCpuPolicy,
    coverage_bytes: &mut [u8],
) -> Result<(), Error> {
    let mut crossings = Vec::new();
    let mut metadata = Vec::new();
    let intrinsic = policy == NativeGlyphCpuPolicy::IntrinsicSimd;
    let cached_scalar = policy.implementation() == NativeGlyphCpuImplementation::CachedScalar;
    let segments = page.source().segments();
    if intrinsic {
        let mut maximum_segments = 0u32;
        for &index in page.work_indices() {
            let record = page.records()[page.uniforms()[index as usize].glyph_index as usize];
            maximum_segments = maximum_segments.max(record.segment_count);
        }
        crossings
            .try_reserve_exact(
                (maximum_segments as usize)
                    .checked_mul(24)
                    .ok_or(Error::Capacity)?,
            )
            .map_err(|_| Error::Capacity)?;
        metadata
            .try_reserve_exact(segments.len())
            .map_err(|_| Error::Capacity)?;
        for segment in segments {
            metadata.push(CurveMetadata::from_segment(segment));
        }
    }
    for &index in page.work_indices() {
        let uniform = &page.uniforms()[index as usize];
        let raster = &page.rasters()[index as usize];
        let record = &page.records()[uniform.glyph_index as usize];
        let inverse_scale = 1.0 / uniform.scale;
        let glyph_sample_step = 0.125 * inverse_scale;
        for y in 0..raster.height {
            if intrinsic {
                let pixel_y = uniform.y_start + y as f32;
                let mut scanline_offsets = [0usize; 9];
                crossings.clear();
                for sample_y in 0..8 {
                    scanline_offsets[sample_y] = crossings.len();
                    let glyph_y = -(pixel_y + 0.0625 + sample_y as f32 * 0.125) * inverse_scale;
                    visit::<true>(glyph_y, record, segments, &metadata, |x, direction| {
                        // Three original roots * eight rows is the reserved bound.
                        crossings.push(Crossing { x, direction });
                    });
                }
                scanline_offsets[8] = crossings.len();
                let mut x = 0;
                while x + 1 < raster.width {
                    let first =
                        (uniform.x_start + x as f32 + 0.0625 - uniform.subpixel_x) * inverse_scale;
                    let second = (uniform.x_start + (x + 1) as f32 + 0.0625 - uniform.subpixel_x)
                        * inverse_scale;
                    let mut total = [0u32; 2];
                    let mut winding = intrinsics::Winding::pair(first, second, glyph_sample_step);
                    for sample_y in 0..8 {
                        let scanline =
                            &crossings[scanline_offsets[sample_y]..scanline_offsets[sample_y + 1]];
                        let covered = if cached_scalar {
                            intrinsics::cached_scalar(first, second, glyph_sample_step, scanline)
                        } else {
                            winding.reset();
                            for crossing in scanline {
                                winding.add(crossing.x, crossing.direction);
                            }
                            winding.covered_pair()
                        };
                        total[0] += covered[0];
                        total[1] += covered[1];
                    }
                    let offset = raster.output_offset as usize
                        + y as usize * raster.output_bytes_per_row as usize
                        + x as usize;
                    coverage_bytes[offset] = coverage(total[0]);
                    coverage_bytes[offset + 1] = coverage(total[1]);
                    x += 2;
                }
                if x < raster.width {
                    let glyph_x =
                        (uniform.x_start + x as f32 + 0.0625 - uniform.subpixel_x) * inverse_scale;
                    let mut total = 0;
                    let mut winding = intrinsics::Winding::single(glyph_x, glyph_sample_step);
                    for sample_y in 0..8 {
                        let scanline =
                            &crossings[scanline_offsets[sample_y]..scanline_offsets[sample_y + 1]];
                        if cached_scalar {
                            total += intrinsics::cached_scalar(
                                glyph_x,
                                glyph_x,
                                glyph_sample_step,
                                scanline,
                            )[0];
                        } else {
                            winding.reset();
                            for crossing in scanline {
                                winding.add(crossing.x, crossing.direction);
                            }
                            total += winding.covered();
                        }
                    }
                    let offset = raster.output_offset as usize
                        + y as usize * raster.output_bytes_per_row as usize
                        + x as usize;
                    coverage_bytes[offset] = coverage(total);
                }
            } else {
                for x in 0..raster.width {
                    let mut covered = 0u32;
                    let pixel_x = uniform.x_start + x as f32;
                    let pixel_y = uniform.y_start + y as f32;
                    let first_glyph_x = (pixel_x + 0.0625 - uniform.subpixel_x) / uniform.scale;
                    for sample_y in 0..8 {
                        let glyph_y = -(pixel_y + 0.0625 + sample_y as f32 * 0.125) / uniform.scale;
                        for sample_x in 0..8 {
                            let glyph_x = first_glyph_x + sample_x as f32 * glyph_sample_step;
                            covered += u32::from(
                                crossings::winding(glyph_x, glyph_y, record, segments) != 0,
                            );
                        }
                    }
                    let offset = raster.output_offset as usize
                        + y as usize * raster.output_bytes_per_row as usize
                        + x as usize;
                    coverage_bytes[offset] = coverage(covered);
                }
            }
        }
    }
    Ok(())
}
