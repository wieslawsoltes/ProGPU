//! Original prepared physical-outline replay, independent of shaping metadata.
//! The retained source owns exact descriptor/no-ink slots. Integer origins are
//! resolved only after the complete actual target translation is composed.
use super::{font_policy::FontReplayGeometry, glyph::GpuGlyphRecord, paint::*};
use crate::{
    Error,
    vector::{Brush, Matrix4},
};
use std::{ops::Range, sync::Arc};
mod paint;
#[cfg(feature = "gpu")]
mod resolve;
#[cfg(feature = "gpu")]
pub(crate) use paint::HintedMaterialInstances;
pub use paint::{GpuHintedGlyphPaint, HintedMaterialPaint};
#[cfg(feature = "gpu")]
pub use resolve::ResolvedHintedReplay;

/// Immutable captured font geometry and original source paint domain. This
/// descriptor contains placement only: it never invents a shaped paragraph.
#[derive(Clone, Debug)]
pub struct HintedReplayDraw {
    /// Exact physical outline generation, retained through selected views.
    pub geometry: Arc<FontReplayGeometry>,
    /// Original selected occurrence range, including duplicate/no-ink slots.
    pub range: Range<usize>,
    /// Original logical command-local baseline translation.
    pub position: [f32; 2],
    /// Original untranslated source ink/brush domain, as x/y/width/height.
    pub source_ink_bounds: [f32; 4],
    /// Original source paint. None records no paint while retaining admission.
    pub brush: Option<Arc<Brush>>,
    /// Original texture-brush mapping, mutually exclusive with a registered brush.
    pub image_brush: Option<Arc<crate::image::ImageBrush>>,
    /// Original grayscale or aliased presentation; ClearType is unadmitted.
    pub rendering_mode: TextRenderingMode,
    /// Auto retains source positions; IntegerPixel rounds the complete target origin.
    pub hinting_mode: TextHintingMode,
    /// Exact explicit grayscale contrast or original legacy transfer.
    pub coverage_transfer: GlyphCoverageTransfer,
}
impl HintedReplayDraw {
    /// Retain all placement slots with the original default replay policies.
    /// The supplied source domain remains authoritative; it is not raster padding.
    pub fn new(geometry: Arc<FontReplayGeometry>, source_ink_bounds: [f32; 4]) -> Self {
        let count = geometry.placements().len();
        Self {
            geometry,
            range: 0..count,
            position: [0.0; 2],
            source_ink_bounds,
            brush: None,
            image_brush: None,
            rendering_mode: TextRenderingMode::Grayscale,
            hinting_mode: TextHintingMode::Auto,
            coverage_transfer: GlyphCoverageTransfer::default(),
        }
    }

    /// Whole-range source and target preflight, before atlas/style/list mutation.
    /// Exact DPI and translation-only placement are original physical-source gates.
    pub fn validate(&self, context: TextPaintContext) -> Result<(), Error> {
        self.selected()?;
        if self.brush.is_some() && self.image_brush.is_some() {
            return Err(Error::InvalidInput);
        }
        let dpi = self.geometry.dpi_scale();
        let translation = identity_translation(context.transform)?;
        if !dpi.is_finite()
            || dpi <= 0.0
            || dpi.to_bits() != context.dpi_scale.to_bits()
            || context.static_zoom != 1.0
            || context.gpu_transforms_active
            || context.camera_view != Matrix4::IDENTITY
            || !context.semantic_opacity.is_finite()
            || !matches!(
                self.rendering_mode,
                TextRenderingMode::Grayscale | TextRenderingMode::Aliased
            )
            || !matches!(
                self.hinting_mode,
                TextHintingMode::Auto | TextHintingMode::IntegerPixel
            )
            || (self.hinting_mode == TextHintingMode::IntegerPixel
                && self.rendering_mode != TextRenderingMode::Aliased)
            || (self.coverage_transfer.encoding() != 0
                && self.rendering_mode != TextRenderingMode::Grayscale)
            || !finite_rectangle(self.source_ink_bounds)
            || context.clip.is_some_and(|clip| !finite_rectangle(clip))
        {
            return Err(Error::UnsupportedExecution);
        }
        if self.geometry.requires_binary_scan_conversion()
            && (self.rendering_mode != TextRenderingMode::Aliased
                || self.hinting_mode != TextHintingMode::IntegerPixel
                || self
                    .geometry
                    .outlines()
                    .iter()
                    .any(|outline| outline.segment_count > 256))
        {
            return Err(Error::UnsupportedExecution);
        }
        let ink = self.ink_bounds()?;
        if ink.is_some_and(|ink| {
            ink[0] < self.source_ink_bounds[0]
                || ink[1] < self.source_ink_bounds[1]
                || ink[0] + ink[2] > self.source_ink_bounds[0] + self.source_ink_bounds[2]
                || ink[1] + ink[3] > self.source_ink_bounds[1] + self.source_ink_bounds[3]
        }) {
            return Err(Error::InvalidInput);
        }
        if self.hinting_mode == TextHintingMode::IntegerPixel {
            // No-ink slots participate before any source owner is retained by a renderer.
            for placement in self.selected()? {
                self.raster_origin(placement.position, translation)?;
            }
            self.raster_bounds(translation)?;
        } else if let Some(ink) = ink {
            if ![
                ink[0] + translation[0],
                ink[0] + ink[2] + translation[0],
                ink[1] + translation[1],
                ink[1] + ink[3] + translation[1],
            ]
            .iter()
            .all(|v| v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
        }
        Ok(())
    }

    /// Original logical ink extent, with Y-up physical bounds mapped to Y-down.
    /// Division by the exact prepared DPI intentionally is not reciprocal multiplication.
    pub fn ink_bounds(&self) -> Result<Option<[f32; 4]>, Error> {
        if !finite(self.position) {
            return Err(Error::InvalidInput);
        }
        let dpi = self.geometry.dpi_scale();
        let mut bounds = Bounds::default();
        for placement in self.selected()? {
            let origin = add(placement.position, self.position);
            if !finite(origin) {
                return Err(Error::InvalidInput);
            }
            if placement.outline_index == u32::MAX {
                continue;
            }
            let record = self.record(placement.outline_index)?;
            bounds.include(
                add(origin, [record.bounds[0] / dpi, -record.bounds[3] / dpi]),
                add(origin, [record.bounds[2] / dpi, -record.bounds[1] / dpi]),
            )?;
        }
        bounds.finish()
    }

    /// Resolve the actual target origin; half ties go toward positive infinity.
    /// Source arrays and the captured physical font phase are never modified.
    pub fn raster_origin(
        &self,
        position: [f32; 2],
        translation: [f32; 2],
    ) -> Result<[f32; 2], Error> {
        let origin = add(add(position, self.position), translation);
        match self.hinting_mode {
            TextHintingMode::Auto if finite(origin) => Ok(origin),
            TextHintingMode::IntegerPixel => integer_origin(origin, self.geometry.dpi_scale()),
            _ => Err(Error::UnsupportedExecution),
        }
    }

    /// Private raster/culling extent in the complete actual target frame. This
    /// padded bound does not replace source ink, hit geometry or brush domains.
    pub fn raster_bounds(&self, translation: [f32; 2]) -> Result<Option<[f32; 4]>, Error> {
        let dpi = self.geometry.dpi_scale();
        let mut bounds = Bounds::default();
        for placement in self.selected()? {
            if placement.outline_index == u32::MAX {
                continue;
            }
            let raster = hinted_raster_bounds(self.record(placement.outline_index)?)?;
            let origin = self.raster_origin(placement.position, translation)?;
            let offset = raster.origin.map(|v| v as f32 / dpi);
            let extent = raster.size.map(|v| v as f32 / dpi);
            bounds.include(add(origin, offset), add(origin, add(offset, extent)))?;
        }
        bounds.finish()
    }

    /// Original compositor storage bounds. Its inverse-DPI multiplication is
    /// intentionally distinct from the admission/culling bounds above.
    pub fn paint_storage_bounds(&self) -> Result<Option<[f32; 4]>, Error> {
        let inverse_dpi = 1.0 / self.geometry.dpi_scale();
        let mut bounds = Bounds::default();
        for placement in self.selected()? {
            if placement.outline_index == u32::MAX {
                continue;
            }
            let raster = hinted_raster_bounds(self.record(placement.outline_index)?)?;
            let origin = self.raster_origin(placement.position, [0.0; 2])?;
            let offset = raster.origin.map(|v| v as f32 * inverse_dpi);
            let extent = raster.size.map(|v| v as f32 * inverse_dpi);
            bounds.include(add(origin, offset), add(origin, add(offset, extent)))?;
        }
        bounds.finish()
    }
    fn selected(&self) -> Result<&[super::font_policy::FontGlyphPlacement], Error> {
        self.geometry
            .placements()
            .get(self.range.clone())
            .ok_or(Error::InvalidInput)
    }
    fn record(&self, index: u32) -> Result<&GpuGlyphRecord, Error> {
        let record = self
            .geometry
            .outlines()
            .get(index as usize)
            .ok_or(Error::InvalidInput)?;
        validate_hinted_record(record, self.geometry.segments().len())?;
        Ok(record)
    }
}

/// Exact original four-pixel padded physical atlas extent.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HintedRasterBounds {
    /// Physical left/top in screen coordinates, before atlas placement.
    pub origin: [i32; 2],
    /// Physical width/height, independent of allocation-class padding.
    pub size: [u32; 2],
}
/// Validate a complete original physical outline descriptor before GPU mutation.
pub fn validate_hinted_record(record: &GpuGlyphRecord, segment_count: usize) -> Result<(), Error> {
    if record.segment_count == 0
        || (record.start_segment as usize)
            .checked_add(record.segment_count as usize)
            .is_none_or(|end| end > segment_count)
        || !record.bounds.iter().all(|v| v.is_finite())
        || record.bounds[0] > record.bounds[2]
        || record.bounds[1] > record.bounds[3]
    {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
/// Preserve the original checked floor/ceil-to-i32 conversion before integer
/// padding. Neither saturation nor float padding replaces source arithmetic.
pub fn hinted_raster_bounds(record: &GpuGlyphRecord) -> Result<HintedRasterBounds, Error> {
    fn narrow(value: f32) -> Result<i32, Error> {
        if !value.is_finite()
            || f64::from(value) < f64::from(i32::MIN)
            || f64::from(value) > f64::from(i32::MAX)
        {
            return Err(Error::Capacity);
        }
        Ok(value as i32)
    }
    let origin = [
        narrow(record.bounds[0].floor())?
            .checked_sub(4)
            .ok_or(Error::Capacity)?,
        narrow((-record.bounds[3]).floor())?
            .checked_sub(4)
            .ok_or(Error::Capacity)?,
    ];
    let end = [
        narrow(record.bounds[2].ceil())?
            .checked_add(4)
            .ok_or(Error::Capacity)?,
        narrow((-record.bounds[1]).ceil())?
            .checked_add(4)
            .ok_or(Error::Capacity)?,
    ];
    let width = end[0].checked_sub(origin[0]).ok_or(Error::Capacity)?;
    let height = end[1].checked_sub(origin[1]).ok_or(Error::Capacity)?;
    if width <= 0 || height <= 0 {
        return Err(Error::InvalidInput);
    }
    Ok(HintedRasterBounds {
        origin,
        size: [width as u32, height as u32],
    })
}
pub(crate) fn identity_translation(matrix: Matrix4) -> Result<[f32; 2], Error> {
    let translation = [matrix.0[3][0], matrix.0[3][1]];
    let mut identity = Matrix4::IDENTITY;
    identity.0[3][0] = translation[0];
    identity.0[3][1] = translation[1];
    if !finite(translation) || matrix != identity {
        return Err(Error::UnsupportedExecution);
    }
    Ok(translation)
}
fn integer_origin(position: [f32; 2], dpi: f32) -> Result<[f32; 2], Error> {
    if !dpi.is_finite() || dpi <= 0.0 || !finite(position) {
        return Err(Error::InvalidInput);
    }
    let physical = position.map(|v| v * dpi);
    if !finite(physical) {
        return Err(Error::InvalidInput);
    }
    let candidate = physical.map(|v| {
        let floor = v.floor();
        (if v - floor >= 0.5 { floor + 1.0 } else { floor }) / dpi
    });
    if !finite(candidate) {
        return Err(Error::InvalidInput);
    }
    Ok(candidate)
}
fn finite(point: [f32; 2]) -> bool {
    point.iter().all(|v| v.is_finite())
}
fn add(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [a[0] + b[0], a[1] + b[1]]
}
fn finite_rectangle(rect: [f32; 4]) -> bool {
    rect.iter().all(|v| v.is_finite())
        && rect[2] >= 0.0
        && rect[3] >= 0.0
        && (rect[0] + rect[2]).is_finite()
        && (rect[1] + rect[3]).is_finite()
}
struct Bounds {
    minimum: [f32; 2],
    maximum: [f32; 2],
    any: bool,
}
impl Default for Bounds {
    fn default() -> Self {
        Self {
            minimum: [f32::MAX; 2],
            maximum: [f32::MIN; 2],
            any: false,
        }
    }
}
impl Bounds {
    fn include(&mut self, low: [f32; 2], high: [f32; 2]) -> Result<(), Error> {
        if !finite(low) || !finite(high) {
            return Err(Error::InvalidInput);
        }
        for axis in 0..2 {
            // System.Numerics vector min/max choose their second operand on ties.
            self.minimum[axis] = if self.minimum[axis] < low[axis] {
                self.minimum[axis]
            } else {
                low[axis]
            };
            self.maximum[axis] = if self.maximum[axis] > high[axis] {
                self.maximum[axis]
            } else {
                high[axis]
            };
        }
        self.any = true;
        Ok(())
    }
    fn finish(self) -> Result<Option<[f32; 4]>, Error> {
        if !self.any {
            return Ok(None);
        }
        let rect = [
            self.minimum[0],
            self.minimum[1],
            self.maximum[0] - self.minimum[0],
            self.maximum[1] - self.minimum[1],
        ];
        if !finite_rectangle(rect) {
            return Err(Error::InvalidInput);
        }
        Ok(Some(rect))
    }
}
