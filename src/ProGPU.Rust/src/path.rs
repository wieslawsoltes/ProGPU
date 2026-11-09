//! Original analytic path records, validation and GPU-first execution policy.
//! Segment coordinates and arc/rational parameter bits are never flattened.

use crate::Error;
use bytemuck::{Pod, Zeroable};

/// Original physical raster origin, scale and sample-grid request.
#[derive(Clone, Copy, Debug)]
pub struct RasterRequest {
    /// Physical x/y origin before division by source scale.
    pub origin: [f32; 2],
    /// Positive independent source-to-physical scales.
    pub scale: [f32; 2],
    /// Physical output width and height.
    pub size: [u32; 2],
    /// Original samples per axis, in 1..=8.
    pub sample_grid: u32,
}

/// Requested compute execution route; unavailable forced routes fail closed.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ExecutionPreference {
    /// Select the fastest supported, validated route.
    #[default]
    Automatic,
    /// Require native compute shaders.
    NativeCompute,
    /// Require an equivalent non-compute GPU shader stage.
    GpuShader,
    /// Require an intrinsic-SIMD CPU implementation.
    CpuSimd,
    /// Require the scalar reference implementation.
    ScalarReference,
}

/// Resolved path used for diagnostics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExecutionPath {
    /// Original equivalent GPU fragment/render execution.
    GpuShader,
    /// The canonical native compute kernel.
    NativeCompute,
}

/// Canonical filled-path winding rule.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum FillRule {
    /// Any nonzero signed winding fills a sample.
    NonZero = 0,
    /// Odd signed winding fills a sample.
    EvenOdd = 1,
}

/// Exact 48-byte storage record shared with native path rasterization.
///
/// Raw records are checked in full before GPU publication, including unused
/// points and reserved fields. Arc parameters contain original float bit patterns.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Pod, Zeroable)]
pub struct Segment {
    /// Original points; meaning follows the native segment kind.
    pub points: [[f32; 2]; 4],
    /// 0 line, 1 quadratic, 2 cubic, 3 arc, 4 rational quadratic, 5 rational cubic.
    pub kind: u32,
    /// Reserved zeros or exact arc/rational parameter bits.
    pub parameters: [u32; 3],
}
const _: () = assert!(size_of::<Segment>() == 48);

impl Segment {
    /// A straight edge retaining its original endpoint order.
    pub fn line(start: [f32; 2], end: [f32; 2]) -> Self {
        Self {
            points: [start, end, [0.0; 2], [0.0; 2]],
            kind: 0,
            parameters: [0; 3],
        }
    }
    /// A quadratic curve, in start/control/end order.
    pub fn quadratic(points: [[f32; 2]; 3]) -> Self {
        Self {
            points: [points[0], points[1], points[2], [0.0; 2]],
            kind: 1,
            parameters: [0; 3],
        }
    }
    /// A cubic curve, in start/control/control/end order.
    pub fn cubic(points: [[f32; 2]; 4]) -> Self {
        Self {
            points,
            kind: 2,
            parameters: [0; 3],
        }
    }

    /// Validate the original native semantic segment contract, allowing arcs
    /// and positive-weight rational curves without changing coordinate bits.
    pub fn validate(&self) -> Result<(), Error> {
        if self.kind > 5 || self.points.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let weights = self.parameters.map(f32::from_bits);
        let scale = self
            .points
            .iter()
            .flatten()
            .fold(1.0f64, |a, b| a.max(f64::from(b.abs())));
        let valid = match self.kind {
            0..=2 => self.parameters == [0; 3],
            3 => {
                self.points[3][0] > 0.0
                    && self.points[3][1] > 0.0
                    && weights.iter().all(|w| w.is_finite())
            }
            4 => {
                self.points[3] == [0.0; 2]
                    && weights[0].is_finite()
                    && weights[0] > 0.0
                    && f64::from(weights[0]) <= f64::from(f32::MAX) / (4.0 * scale)
                    && self.parameters[1] == 0
                    && self.parameters[2] == 0
            }
            5 => {
                weights[..2].iter().all(|w| {
                    w.is_finite()
                        && *w > 0.0
                        && f64::from(*w) <= f64::from(f32::MAX) / (8.0 * scale)
                }) && self.parameters[2] == 0
            }
            _ => false,
        };
        if valid {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
}

/// An immutable original segment inventory and its source bounds.
#[derive(Clone, Debug)]
pub struct Path {
    segments: std::sync::Arc<[Segment]>,
    bounds: [f32; 4],
    fill_rule: FillRule,
    program: Option<std::sync::Arc<RasterProgram>>,
}

/// Compiler-owned records. Leaf tokens remain relative to this record range;
/// the raster encoder adjusts segment offsets, never Boolean token indices.
#[derive(Debug)]
pub(crate) struct RasterProgram {
    pub records: Box<[crate::vector::GpuPathRecord]>,
    pub tokens: Box<[crate::vector::GpuPathRecord]>,
    pub operation: u32,
    pub index_b: u32,
}
impl RasterProgram {
    fn validate(&self) -> Result<(), Error> {
        if self.operation & 0x8000_0000 == 0 {
            return if (1..=5).contains(&self.operation)
                && self.index_b == 1
                && self.records.len() == 2
                && self.tokens.is_empty()
            {
                Ok(())
            } else {
                Err(Error::InvalidInput)
            };
        }
        if self.records.is_empty()
            || self.index_b as usize != self.records.len()
            || !(1..=63).contains(&self.tokens.len())
            || (self.operation & 0x7fff_ffff) as usize != self.tokens.len()
        {
            return Err(Error::InvalidInput);
        }
        let mut depth = 0usize;
        for record in &self.tokens {
            if record.segment_count != 0
                || record.bounds.map(f32::to_bits) != [0; 4]
                || record.fill_rule != 0
                || record.pad != 0
            {
                return Err(Error::InvalidInput);
            }
            let token = record.start_segment;
            if token & 0x8000_0000 != 0 {
                if depth < 2 || !(1..=5).contains(&(token & 0x7fff_ffff)) {
                    return Err(Error::InvalidInput);
                }
                depth -= 1;
            } else {
                if token != 0x4000_0000 && token as usize >= self.records.len() {
                    return Err(Error::InvalidInput);
                }
                depth += 1;
                if depth > 16 {
                    return Err(Error::InvalidInput);
                }
            }
        }
        if depth == 1 {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
}

impl Path {
    /// Retain a fully validated segment inventory with original minX/minY/maxX/maxY.
    /// The caller supplies the source bounds; they are not reconstructed or rounded.
    pub fn new(
        segments: impl Into<std::sync::Arc<[Segment]>>,
        bounds: [f32; 4],
        fill_rule: FillRule,
    ) -> Result<Self, Error> {
        let segments = segments.into();
        if segments.is_empty()
            || segments.len() > 1 << 20
            || bounds.iter().any(|v| !v.is_finite())
            || bounds[0] > bounds[2]
            || bounds[1] > bounds[3]
        {
            return Err(Error::InvalidInput);
        }
        for segment in segments.iter() {
            segment.validate()?;
        }
        Ok(Self {
            segments,
            bounds,
            fill_rule,
            program: None,
        })
    }

    pub(crate) fn with_program(
        segments: Vec<Segment>,
        bounds: [f32; 4],
        fill_rule: FillRule,
        program: RasterProgram,
    ) -> Result<Self, Error> {
        program.validate()?;
        let mut path = Self::new(segments, bounds, fill_rule)?;
        for record in &program.records {
            if record.segment_count == 0
                || record
                    .start_segment
                    .checked_add(record.segment_count)
                    .is_none_or(|end| end as usize > path.segments.len())
                || record.fill_rule > 1
                || record.pad != 0
                || record.bounds.iter().any(|v| !v.is_finite())
                || record.bounds[0] > record.bounds[2]
                || record.bounds[1] > record.bounds[3]
            {
                return Err(Error::InvalidInput);
            }
        }
        path.program = Some(std::sync::Arc::new(program));
        Ok(path)
    }
    pub(crate) fn identity(&self) -> usize {
        std::sync::Arc::as_ptr(&self.segments) as *const Segment as usize
    }
    pub(crate) fn raster_identity(&self) -> (usize, usize) {
        (
            self.identity(),
            self.program
                .as_ref()
                .map_or(0, |p| std::sync::Arc::as_ptr(p) as usize),
        )
    }

    /// Read the original immutable segment inventory. Boolean inputs retain
    /// each ordered operand occurrence without reconstructing result contours.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
    /// Original source bounds, unchanged from creation.
    pub fn bounds(&self) -> [f32; 4] {
        self.bounds
    }
    /// Original root fill rule. Boolean operands retain their own separate
    /// rules in the compiler-owned program.
    pub fn fill_rule(&self) -> FillRule {
        self.fill_rule
    }

    /// Whether this input requires the original general Boolean evaluator.
    pub fn is_boolean(&self) -> bool {
        self.program.is_some()
    }
}

#[cfg(feature = "gpu")]
mod raster;
#[cfg(feature = "gpu")]
pub use raster::{AtlasTile, CoverageAtlas, CoverageMask, PathRasterizer};

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rational_parameters_retain_bits_and_reject_unsafe_weights() {
        let mut segment = Segment::quadratic([[0.0, 0.0], [8.0, 16.0], [16.0, 0.0]]);
        segment.kind = 4;
        segment.parameters[0] = 0.75f32.to_bits();
        segment.validate().unwrap();
        assert_eq!(segment.parameters[0], 0x3f400000);
        for weight in [0.0, -1.0, f32::INFINITY, f32::NAN, f32::MAX] {
            segment.parameters[0] = weight.to_bits();
            assert!(segment.validate().is_err());
        }
    }
    #[test]
    fn all_original_slots_are_validated() {
        let mut segment = Segment::line([0.0, 0.0], [1.0, 1.0]);
        segment.points[3][1] = f32::NAN;
        assert!(segment.validate().is_err());
        segment.points[3][1] = 0.0;
        segment.parameters[2] = 1;
        assert!(segment.validate().is_err());
    }
}
