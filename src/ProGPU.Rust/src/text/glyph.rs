//! Original glyph GPU transport and canonical raster dispatch. Geometry remains
//! font-owned, with original segment/record/uniform bytes and ordered bounds.
use crate::{
    font::{FontError, ManagedFont},
    path::Segment,
};
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
#[cfg(feature = "gpu")]
mod raster;
#[cfg(feature = "gpu")]
pub use raster::{
    GlyphCoverageTexture, GlyphExecutionPath, GlyphGpuError, GlyphGpuMetrics, GlyphGpuSlot,
    GlyphRasterMode, GlyphRasterRequest, GlyphRasterizer, GlyphRasterizerOptions,
};

/// Original 32-byte glyph record consumed by both canonical glyph kernels.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GpuGlyphRecord {
    /// Original first segment slot.
    pub start_segment: u32,
    /// Complete original selected segment count.
    pub segment_count: u32,
    /// Original minX/minY/maxX/maxY, without replacing bounds by raster extents.
    pub bounds: [f32; 4],
    /// Original reserved record words.
    pub padding: [u32; 2],
}
/// Original 48-byte uniform payload; dynamic bindings use separate 256-byte slots.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct GlyphUniforms {
    /// Original physical horizontal raster origin.
    pub x_start: f32,
    /// Original physical vertical raster origin, screen Y downward.
    pub y_start: f32,
    /// Original scalar outline-to-physical scale.
    pub scale: f32,
    /// Actual uploaded record slot.
    pub glyph_index: u32,
    /// Original output base in packed words.
    pub output_offset_words: u32,
    /// Original padded output row pitch in packed words.
    pub output_row_words: u32,
    /// Actual physical glyph coverage width.
    pub width: u32,
    /// Actual physical glyph coverage height.
    pub height: u32,
    /// Original physical subpixel phase, never recomputed by this transport.
    pub subpixel_x: f32,
    /// Actual destination atlas X used by the fragment route.
    pub atlas_x: f32,
    /// Actual destination atlas Y used by the fragment route.
    pub atlas_y: f32,
    /// Original reserved float word.
    pub padding: f32,
}
const _: () = {
    assert!(size_of::<GpuGlyphRecord>() == 32);
    assert!(size_of::<GlyphUniforms>() == 48);
};

/// Immutable compiled original unhinted glyph, retaining its selected font and
/// all source segment bits independently of CPU outline-cache retirement.
#[derive(Clone, Debug)]
pub struct GpuGlyphOutline {
    font: ManagedFont,
    glyph: u16,
    record: GpuGlyphRecord,
    segments: Arc<[Segment]>,
}
impl GpuGlyphOutline {
    /// Actual selected font instance.
    pub fn font(&self) -> &ManagedFont {
        &self.font
    }
    /// Original glyph slot.
    pub fn glyph_index(&self) -> u16 {
        self.glyph
    }
    /// Original record, with start zero for this isolated outline.
    pub fn record(&self) -> GpuGlyphRecord {
        self.record
    }
    /// Original line/quadratic/cubic records including required closing lines.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
}
impl ManagedFont {
    /// Original AppendGpuOutlineData, sharing one caller-owned segment stream.
    /// Empty output returns the original all-zero descriptor, even when the
    /// destination already contains preceding glyphs. Errors publish no append.
    pub fn append_gpu_outline_data(
        &self,
        glyph: u16,
        segments: &mut Vec<Segment>,
    ) -> Result<GpuGlyphRecord, FontError> {
        let outline = self.glyph_outline(glyph)?;
        let Some(outline) = outline else {
            return Ok(GpuGlyphRecord::default());
        };
        let additional = outline.figures().iter().try_fold(0usize, |n, f| {
            n.checked_add(f.segments.len())
                .and_then(|n| n.checked_add(usize::from(!f.segments.is_empty())))
                .ok_or(FontError::Capacity)
        })?;
        let total = segments
            .len()
            .checked_add(additional)
            .ok_or(FontError::Capacity)?;
        if total > i32::MAX as usize {
            return Err(FontError::Capacity);
        }
        segments
            .try_reserve(additional)
            .map_err(|_| FontError::Capacity)?;
        let start = segments.len() as u32;
        let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
        for figure in outline.figures() {
            if figure.segments.is_empty() {
                continue;
            }
            let mut current = figure.start;
            for s in &figure.segments {
                let emitted = match s.kind {
                    0 => Segment::line(current, s.points[1]),
                    1 => Segment::quadratic([current, s.points[1], s.points[2]]),
                    2 => Segment::cubic([current, s.points[1], s.points[2], s.points[3]]),
                    _ => continue,
                };
                update_bounds(&mut bounds, &emitted);
                current = emitted.points[emitted.kind as usize + 1];
                segments.push(emitted);
            }
            if current != figure.start {
                let closing = Segment::line(current, figure.start);
                update_bounds(&mut bounds, &closing);
                segments.push(closing);
            }
        }
        let count = segments.len() as u32 - start;
        Ok(if count == 0 {
            GpuGlyphRecord::default()
        } else {
            GpuGlyphRecord {
                start_segment: start,
                segment_count: count,
                bounds,
                padding: [0; 2],
            }
        })
    }
    /// Compile only this requested glyph; normal atlas use never scans a font's
    /// complete glyph inventory on first touch.
    pub fn compile_gpu_glyph(&self, glyph: u16) -> Result<GpuGlyphOutline, FontError> {
        let mut segments = Vec::new();
        let record = self.append_gpu_outline_data(glyph, &mut segments)?;
        Ok(GpuGlyphOutline {
            font: self.clone(),
            glyph,
            record,
            segments: segments.into(),
        })
    }
    /// Explicit original whole-font compilation. Sparse atlas consumers should
    /// use `compile_gpu_glyph` to preserve lazy first-use behavior.
    pub fn compile_gpu_outline_data(
        &self,
    ) -> Result<(Vec<GpuGlyphRecord>, Vec<Segment>), FontError> {
        let mut records = Vec::new();
        let count = usize::from(self.metadata().glyph_count);
        records
            .try_reserve_exact(count)
            .map_err(|_| FontError::Capacity)?;
        let mut segments = Vec::new();
        for glyph in 0..count {
            records.push(self.append_gpu_outline_data(glyph as u16, &mut segments)?);
        }
        Ok((records, segments))
    }
}
fn update_point(bounds: &mut [f32; 4], point: [f32; 2]) {
    // Original comparisons deliberately ignore unordered point components.
    for axis in 0..2 {
        if point[axis] < bounds[axis] {
            bounds[axis] = point[axis];
        }
        if point[axis] > bounds[axis + 2] {
            bounds[axis + 2] = point[axis];
        }
    }
}
fn update_bounds(bounds: &mut [f32; 4], segment: &Segment) {
    let p = segment.points;
    update_point(bounds, p[0]);
    if segment.kind != 1 {
        for point in &p[1..segment.kind as usize + 2] {
            update_point(bounds, *point);
        }
        return;
    }
    update_point(bounds, p[2]);
    for axis in 0..2 {
        let denominator = p[0][axis] - 2.0 * p[1][axis] + p[2][axis];
        if denominator.abs() > 1e-6 {
            let t = (p[0][axis] - p[1][axis]) / denominator;
            if t > 0.0 && t < 1.0 {
                let value = (1.0 - t) * (1.0 - t) * p[0][axis]
                    + 2.0 * (1.0 - t) * t * p[1][axis]
                    + t * t * p[2][axis];
                // Original Math.Min/Max propagation, separate from UpdateMinMax.
                if !bounds[axis].is_nan() {
                    bounds[axis] = if value.is_nan() {
                        value
                    } else if value == 0.0 && bounds[axis] == 0.0 {
                        f32::from_bits(value.to_bits() | bounds[axis].to_bits())
                    } else {
                        bounds[axis].min(value)
                    };
                }
                if !bounds[axis + 2].is_nan() {
                    bounds[axis + 2] = if value.is_nan() {
                        value
                    } else if value == 0.0 && bounds[axis + 2] == 0.0 {
                        f32::from_bits(value.to_bits() & bounds[axis + 2].to_bits())
                    } else {
                        bounds[axis + 2].max(value)
                    };
                }
            }
        }
    }
}
