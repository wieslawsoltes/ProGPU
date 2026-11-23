//! Exact original fixed projection, phase removal and actual descriptor callbacks.
use super::*;
use crate::font::layout::{GposArithmetic, GposDeviceSource};
use std::cell::Cell;

fn narrow(value: i64) -> Result<i32, FontError> {
    i32::try_from(value).map_err(|_| FontError::InvalidFace)
}
impl NativeHintedProjection {
    pub(super) fn arithmetic(self) -> Result<GposArithmetic, NativeHintedProjectionError> {
        match self {
            Self::ScalarReference => Ok(GposArithmetic::ScalarReference),
            Self::Automatic | Self::IntrinsicSimd
                if cfg!(any(
                    target_arch = "aarch64",
                    target_arch = "x86_64",
                    all(target_arch = "x86", target_feature = "sse2")
                )) =>
            {
                Ok(GposArithmetic::IntrinsicSimd)
            }
            _ => Err(NativeHintedProjectionError::UnsupportedPolicy),
        }
    }
}
impl NativeHintedBatch {
    fn projection_scales(
        &self,
        policy: NativeHintedProjection,
    ) -> Result<([i32; 2], GposArithmetic), NativeHintedProjectionError> {
        let path = policy.arithmetic()?;
        let metrics = self.metrics();
        if metrics.units_per_em == 0
            || metrics
                .scale_16_16
                .iter()
                .any(|&s| s <= 0 || s > i64::from(i32::MAX))
        {
            return Err(NativeHintedProjectionError::UnsupportedFrame);
        }
        Ok((metrics.scale_16_16.map(|s| s as i32), path))
    }
    /// Project a complete original vector inventory with signed32 domain admission
    /// before writing any output. Coordinates are displacements: physical phase
    /// is not added. The immutable batch and safe borrows exclude source aliases.
    pub fn project_design_vectors(
        &self,
        input: &[[i32; 2]],
        output: &mut [[i32; 2]],
        policy: NativeHintedProjection,
    ) -> Result<(), NativeHintedProjectionError> {
        let (scales, path) = self.projection_scales(policy)?;
        if output.len() < input.len() {
            return Err(NativeHintedProjectionError::InsufficientCapacity);
        }
        let bound = |scale: i32, negative: bool| {
            let magnitude = if negative {
                2_147_483_648i64
            } else {
                2_147_483_647i64
            };
            let admitted = ((magnitude * 65_536 + 32_767) / i64::from(scale)).min(magnitude);
            if negative { -admitted } else { admitted }
        };
        let lower = scales.map(|s| bound(s, true));
        let upper = scales.map(|s| bound(s, false));
        for value in input {
            if (0..2).any(|i| i64::from(value[i]) < lower[i] || i64::from(value[i]) > upper[i]) {
                return Err(NativeHintedProjectionError::UnsupportedFrame);
            }
        }
        for (value, target) in input.iter().zip(output) {
            let sign = value.map(|v| i64::from(v >> 31));
            let magnitude = value.map(|v| i64::from(v).abs());
            let rounded = if path == GposArithmetic::IntrinsicSimd {
                let product = wide::i64x2::new(magnitude) * wide::i64x2::new(scales.map(i64::from));
                let rounded = (product + wide::i64x2::splat(32_768)) >> 16;
                let signs = wide::i64x2::new(sign);
                ((rounded ^ signs) - signs).to_array()
            } else {
                std::array::from_fn(|i| {
                    let rounded = (magnitude[i] * i64::from(scales[i]) + 32_768) >> 16;
                    (rounded ^ sign[i]) - sign[i]
                })
            };
            *target = rounded.map(|v| v as i32);
        }
        Ok(())
    }
    /// Read one actual original contour point, removing only this generation's
    /// physical phase after proving the complete signed32 subtraction domain.
    pub fn anchor_point(
        &self,
        descriptor: usize,
        point: usize,
    ) -> Result<[i32; 2], NativeHintedProjectionError> {
        let value = self
            .glyphs()
            .get(descriptor)
            .and_then(|g| g.points_26_6.get(point))
            .ok_or(NativeHintedProjectionError::InvalidArgument)?;
        let phase = self.request().phase_26_6.map(i64::from);
        for i in 0..2 {
            if phase[i] >= 64
                || value[i] < i64::from(i32::MIN) + phase[i]
                || value[i] > i64::from(i32::MAX) + phase[i]
            {
                return Err(NativeHintedProjectionError::UnsupportedFrame);
            }
        }
        Ok(std::array::from_fn(|i| (value[i] - phase[i]) as i32))
    }
}
#[derive(Clone, Copy, Debug, Default)]
pub(super) struct Auxiliary {
    pub figure: (usize, usize),
    pub punctuation: (usize, usize),
}
/// Synchronous bound view retains the exact shaper font-object identity while the
/// batch retains the same immutable underlying source allocation independently.
pub(super) struct BoundBatch<'a> {
    pub font: &'a SfntFace,
    pub coordinates: &'a [i16],
    pub batch: &'a NativeHintedBatch,
    pub policy: NativeHintedProjection,
    pub source_count: usize,
    pub auxiliary: Auxiliary,
    pub failure: &'a Cell<Option<NativeHintedProjectionError>>,
}
impl BoundBatch<'_> {
    fn failed<T>(&self, error: NativeHintedProjectionError) -> Result<T, FontError> {
        self.failure.set(Some(error));
        Err(FontError::InvalidFace)
    }
    pub fn bind(&self) -> Result<GposArithmetic, FontError> {
        if !std::ptr::eq(self.font.data().as_ptr(), self.batch.font().data().as_ptr())
            || self.font.data().len() != self.batch.font().data().len()
            || self.font.face_index() != self.batch.font().face_index()
        {
            return self.failed(NativeHintedProjectionError::InvalidArgument);
        }
        let metrics = self.batch.metrics();
        if self
            .font
            .header_metrics()
            .is_none_or(|h| h.units_per_em != metrics.units_per_em)
            || metrics.pixels_per_em.iter().any(|&v| v > u16::MAX as u32)
        {
            return self.failed(NativeHintedProjectionError::UnsupportedFrame);
        }
        let count = self.font.variation_axis_count().map_err(|_| {
            self.failure
                .set(Some(NativeHintedProjectionError::UnsupportedFrame));
            FontError::InvalidFace
        })?;
        if self.coordinates.len() != usize::from(count)
            || self.batch.request().variation_coordinates_16_16.len() != usize::from(count)
        {
            return self.failed(NativeHintedProjectionError::UnsupportedFrame);
        }
        for axis in 0..count {
            let raw = self.batch.request().variation_coordinates_16_16[usize::from(axis)];
            if self.font.normalize_variation_coordinate(axis, raw).ok()
                != Some(self.coordinates[usize::from(axis)])
            {
                return self.failed(NativeHintedProjectionError::UnsupportedFrame);
            }
        }
        match self.batch.projection_scales(self.policy) {
            Ok((_, arithmetic)) => Ok(arithmetic),
            Err(error) => self.failed(error),
        }
    }
    fn descriptor(&self, descriptor: u32, glyph: u32) -> Result<&NativeHintedGlyph, FontError> {
        if descriptor as usize >= self.source_count {
            return Err(FontError::InvalidFace);
        }
        self.batch
            .glyphs()
            .get(descriptor as usize)
            .filter(|g| g.glyph_id == glyph)
            .ok_or(FontError::InvalidFace)
    }
    /// Whole-prefix admission precedes all original four-lane metric writes.
    pub fn initialize(
        &self,
        glyphs: &mut [ShapingGlyph],
        direction: GposDirection,
    ) -> Result<(), FontError> {
        if direction == GposDirection::Unspecified
            || self.source_count > self.batch.glyphs().len()
            || glyphs.len() < self.source_count
        {
            return Err(FontError::InvalidArgument);
        }
        self.bind()?;
        let vertical = matches!(
            direction,
            GposDirection::TopToBottom | GposDirection::BottomToTop
        );
        let metrics = |source: &NativeHintedGlyph| -> Result<[i32; 4], FontError> {
            if !vertical {
                return Ok([
                    narrow(source.advance_26_6[0])?,
                    narrow(source.advance_26_6[1])?,
                    0,
                    0,
                ]);
            }
            let advance = narrow(source.vertical_advance_26_6)?;
            let hx = narrow(source.horizontal_bearing_26_6[0])?;
            let hy = narrow(source.horizontal_bearing_26_6[1])?;
            let vx = narrow(source.vertical_bearing_26_6[0])?;
            let vy = narrow(source.vertical_bearing_26_6[1])?;
            Ok([
                0,
                narrow(-i64::from(advance))?,
                narrow(i64::from(vx) - i64::from(hx))?,
                narrow(-i64::from(hy) - i64::from(vy))?,
            ])
        };
        for (index, glyph) in glyphs[..self.source_count].iter().enumerate() {
            metrics(self.descriptor(index as u32, glyph.glyph_id)?)?;
        }
        for (index, glyph) in glyphs[..self.source_count].iter_mut().enumerate() {
            let values = metrics(&self.batch.glyphs()[index])?;
            [
                glyph.advance_x,
                glyph.advance_y,
                glyph.offset_x,
                glyph.offset_y,
            ] = values;
        }
        Ok(())
    }
}
impl GposDeviceSource for BoundBatch<'_> {
    fn font(&self) -> &SfntFace {
        self.font
    }
    fn normalized_coordinates(&self) -> &[i16] {
        self.coordinates
    }
    fn pixels_per_em(&self) -> [u16; 2] {
        self.batch.metrics().pixels_per_em.map(|v| v as u16)
    }
    fn project_design(&self, vectors: [[i32; 2]; 2]) -> Result<[[i32; 2]; 2], FontError> {
        let mut output = [[0; 2]; 2];
        match self
            .batch
            .project_design_vectors(&vectors, &mut output, self.policy)
        {
            Ok(()) => Ok(output),
            Err(error) => self.failed(error),
        }
    }
    fn contour_point(
        &self,
        descriptor: usize,
        glyph: u32,
        point: u16,
    ) -> Result<[i32; 2], FontError> {
        if self
            .batch
            .glyphs()
            .get(descriptor)
            .is_none_or(|g| g.glyph_id != glyph)
        {
            return Err(FontError::InvalidFace);
        }
        match self.batch.anchor_point(descriptor, usize::from(point)) {
            Ok(p) => Ok(p),
            Err(e) => self.failed(e),
        }
    }
}
impl NativeDeviceSource for BoundBatch<'_> {
    fn mark_bounds(
        &self,
        _index: usize,
        descriptor: u32,
        glyph: u32,
    ) -> Result<Option<NativeDeviceMarkBounds>, FontError> {
        let g = self.descriptor(descriptor, glyph)?;
        let width = narrow(g.size_26_6[0])?;
        let height = narrow(g.size_26_6[1])?;
        if width < 0 || height < 0 {
            return Err(FontError::InvalidFace);
        }
        let bounds = NativeDeviceMarkBounds {
            x_bearing: narrow(g.horizontal_bearing_26_6[0])?,
            y_bearing: narrow(g.horizontal_bearing_26_6[1])?,
            width,
            negative_height: -height,
            horizontal_advance: narrow(g.horizontal_advance_26_6)?,
        };
        Ok((!g.points_26_6.is_empty()).then_some(bounds))
    }
    fn stretch_advance(
        &self,
        _index: usize,
        descriptor: u32,
        glyph: u32,
    ) -> Result<i32, FontError> {
        narrow(self.descriptor(descriptor, glyph)?.horizontal_advance_26_6)
    }
    fn space_advance(
        &self,
        kind: NativeDeviceSpaceKind,
        glyph: u16,
    ) -> Result<[i32; 2], FontError> {
        let (start, count) = match kind {
            NativeDeviceSpaceKind::Figure => self.auxiliary.figure,
            NativeDeviceSpaceKind::Punctuation => self.auxiliary.punctuation,
        };
        if count != 1 {
            return Err(FontError::InvalidFace);
        }
        let g = self
            .batch
            .glyphs()
            .get(start)
            .filter(|g| g.glyph_id == u32::from(glyph))
            .ok_or(FontError::InvalidFace)?;
        Ok([
            narrow(g.horizontal_advance_26_6)?,
            narrow(g.vertical_advance_26_6)?,
        ])
    }
}
