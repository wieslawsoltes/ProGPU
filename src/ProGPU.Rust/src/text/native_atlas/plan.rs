//! Original native raster descriptors, source slots and ordered text instances.
use super::*;
#[derive(Clone, Debug)]
pub struct NativeAtlasRaster {
    pub(super) outline: Arc<NativeDesignOutline>,
    pub(super) record: GpuGlyphRecord,
    pub(super) scale: f32,
    pub(super) phase: u8,
    pub(super) origin: [f32; 2],
    pub(super) size: [u32; 2],
}
impl NativeAtlasRaster {
    pub fn outline(&self) -> &Arc<NativeDesignOutline> {
        &self.outline
    }
    pub fn record(&self) -> GpuGlyphRecord {
        self.record
    }
    pub fn segments(&self) -> &[Segment] {
        self.outline.segments()
    }
    pub fn scale(&self) -> f32 {
        self.scale
    }
    pub fn phase(&self) -> u8 {
        self.phase
    }
    pub fn origin(&self) -> [f32; 2] {
        self.origin
    }
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeAtlasReference {
    Outline(u32),
    Bitmap(u32),
}
#[derive(Clone, Copy, Debug)]
pub struct NativeAtlasOccurrence {
    pub source_slot: u32,
    pub reference: NativeAtlasReference,
    pub style_index: u32,
    pub position: [f32; 2],
    pub basis_x: [f32; 2],
    pub basis_y: [f32; 2],
    pub atlas_to_logical_scale: f32,
    pub bold_offset: f32,
    pub italic_skew: f32,
}
#[derive(Debug)]
pub struct NativeAtlasPlan {
    pub(super) draw: NativeDesignAtlasDraw,
    pub(super) dpi_scale: f32,
    pub(super) rasters: Vec<Arc<NativeAtlasRaster>>,
    pub(super) occurrences: Vec<NativeAtlasOccurrence>,
    pub(super) styles: Vec<GpuTextStyle>,
}
impl NativeAtlasPlan {
    pub fn draw(&self) -> &NativeDesignAtlasDraw {
        &self.draw
    }
    pub fn dpi_scale(&self) -> f32 {
        self.dpi_scale
    }
    pub fn rasters(&self) -> &[Arc<NativeAtlasRaster>] {
        &self.rasters
    }
    pub fn occurrences(&self) -> &[NativeAtlasOccurrence] {
        &self.occurrences
    }
    pub fn styles(&self) -> &[GpuTextStyle] {
        &self.styles
    }
}
pub(super) fn prepare(
    draw: &NativeDesignAtlasDraw,
    dpi: f32,
) -> Result<Arc<NativeAtlasPlan>, Error> {
    draw.validate()?;
    if draw.selected.len() > 1 << 23 {
        return Err(Error::Capacity);
    }
    if !dpi.is_finite() || dpi <= 0.0 || draw.colors.as_ref().is_some_and(|c| c.dpi_scale() != dpi)
    {
        return Err(Error::InvalidInput);
    }
    let transform_scale = draw.frame.scale()?;
    if let Some(colors) = &draw.colors {
        // Original native color pages admit the complete descriptor inventory,
        // including records not referenced by the selected positioned slots.
        if colors
            .records()
            .iter()
            .any(|r| r.width > 4092 || r.height > 4092)
        {
            return Err(Error::UnsupportedExecution);
        }
    }
    let matrix = draw.frame.transform.map(|v| v as f32);
    let mut plan = NativeAtlasPlan {
        draw: draw.clone(),
        dpi_scale: dpi,
        rasters: Vec::new(),
        occurrences: reserve(draw.selected.len() * 2)?,
        styles: reserve(draw.paints.len())?,
    };
    let mut sizes = reserve(draw.paints.len())?;
    for p in draw.paints.iter() {
        let size = (p.font_size * dpi * transform_scale).clamp(4.0, 128.0);
        if !size.is_finite() {
            return Err(Error::InvalidInput);
        }
        sizes.push(size);
        let mut color = p.color;
        color[3] *= draw.frame.opacity;
        if !color.into_iter().all(f32::is_finite) {
            return Err(Error::InvalidInput);
        }
        let mode =
            if draw.frame.suppress_cleartype && p.rendering_mode == TextRenderingMode::ClearType {
                TextRenderingMode::Grayscale
            } else {
                p.rendering_mode
            };
        plan.styles.push(GpuTextStyle {
            color,
            rendering_mode: mode as u32,
            padding: [0; 3],
        });
    }
    let mut descriptor_groups = HashMap::<(u32, u32), u32>::new();
    let mut segment_count = 0usize;
    descriptor_groups
        .try_reserve(draw.selected.len())
        .map_err(|_| Error::Capacity)?;
    for &slot_index in draw.selected.iter() {
        let slot = draw.geometry.slots()[slot_index as usize];
        let Some(style) = slot.style_index else {
            continue;
        };
        let paint = draw.paints[style as usize];
        let size = sizes[style as usize];
        let source_position = [
            slot.position[0] + draw.position[0],
            slot.position[1] + draw.position[1],
        ];
        let color_placement = draw
            .colors
            .as_ref()
            .and_then(|c| c.bitmap_for_slot(slot_index));
        // Explicit retained color records already own positioned placement;
        // the backend consumes those source positions without a second snap.
        let (phase, local) = if color_placement.is_some() {
            (0, source_position)
        } else {
            draw.frame.placement(source_position, size, dpi)
        };
        if phase > 3 || !local.into_iter().all(f32::is_finite) {
            return Err(Error::InvalidInput);
        }
        let (reference, ratio) = if let Some(color) = color_placement {
            let record = draw.colors.as_ref().ok_or(Error::InvalidInput)?.records()
                [color.bitmap_index as usize];
            if record.width > 4092 || record.height > 4092 {
                return Err(Error::UnsupportedExecution);
            }
            (
                NativeAtlasReference::Bitmap(color.bitmap_index),
                color.atlas_to_logical_scale,
            )
        } else {
            let Some(outline_index) = slot.outline_index else {
                continue;
            };
            let source = &draw.geometry.outlines()[outline_index as usize];
            if !source.has_ink() {
                continue;
            }
            let key = (outline_index, size.to_bits());
            let base = if let Some(&base) = descriptor_groups.get(&key) {
                base
            } else {
                let data = source
                    .font()
                    .glyph_data(source.glyph_id())
                    .ok_or(Error::UnsupportedExecution)?;
                if data.bytes.is_empty() {
                    continue;
                }
                let bounds = data.bounds.map(f32::from);
                if bounds[2] <= bounds[0] || bounds[3] <= bounds[1] {
                    return Err(Error::UnsupportedExecution);
                }
                let scale = size / f32::from(draw.geometry.styles()[style as usize].units_per_em);
                let origin = [
                    (bounds[0] * scale).floor() - 4.0,
                    (-bounds[3] * scale).floor() - 4.0,
                ];
                // Original operations are float; widening happens only after
                // the complete ceil + padding - start expression.
                let extent = [
                    (bounds[2] * scale).ceil() + 4.0 - origin[0],
                    (-bounds[1] * scale).ceil() + 4.0 - origin[1],
                ];
                if !scale.is_finite()
                    || scale <= 0.0
                    || extent
                        .into_iter()
                        .any(|v| !v.is_finite() || v <= 0.0 || v > 4092.0)
                {
                    return Err(Error::UnsupportedExecution);
                }
                let count = u32::try_from(source.segments().len()).map_err(|_| Error::Capacity)?;
                if count == 0 {
                    continue;
                }
                segment_count = segment_count
                    .checked_add(count as usize)
                    .ok_or(Error::Capacity)?;
                if segment_count > 1 << 24 || plan.rasters.len() > ((1 << 20) - 4) {
                    return Err(Error::Capacity);
                }
                // Static native TT has ordinary zero scan words. Never relabel
                // a binary/hinted stream as the ordinary vector coverage route.
                if source.segments().iter().any(|s| s.parameters != [0; 3]) {
                    return Err(Error::InvalidInput);
                }
                let base = u32::try_from(plan.rasters.len()).map_err(|_| Error::Capacity)?;
                if base > u32::MAX - 4 {
                    return Err(Error::Capacity);
                }
                plan.rasters.try_reserve(4).map_err(|_| Error::Capacity)?;
                for phase in 0..4 {
                    plan.rasters.push(Arc::new(NativeAtlasRaster {
                        outline: source.clone(),
                        record: GpuGlyphRecord {
                            start_segment: 0,
                            segment_count: count,
                            bounds,
                            padding: [0; 2],
                        },
                        scale,
                        phase,
                        origin,
                        size: extent.map(|v| v as u32),
                    }));
                }
                descriptor_groups.insert(key, base);
                base
            };
            (
                NativeAtlasReference::Outline(base + u32::from(phase)),
                paint.font_size * dpi / size,
            )
        };
        // Original semantic draw state narrows its affine once, then projects
        // the already resolved local position and basis with float arithmetic.
        let position = [
            local[0] * matrix[0] + local[1] * matrix[2] + matrix[4],
            local[0] * matrix[1] + local[1] * matrix[3] + matrix[5],
        ];
        if !position.into_iter().all(f32::is_finite) || !ratio.is_finite() || ratio <= 0.0 {
            return Err(Error::InvalidInput);
        }
        for pass in 0..if paint.bold { 2 } else { 1 } {
            plan.occurrences.push(NativeAtlasOccurrence {
                source_slot: slot_index,
                reference,
                style_index: style,
                position,
                basis_x: [matrix[0], matrix[1]],
                basis_y: [matrix[2], matrix[3]],
                atlas_to_logical_scale: ratio,
                bold_offset: pass as f32 * paint.font_size * 0.035,
                italic_skew: if paint.italic { 0.22 } else { 0.0 },
            });
        }
    }
    Ok(Arc::new(plan))
}
