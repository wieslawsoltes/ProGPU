//! Original HatchExtensionPipeline boundary compilation and immutable source ownership.
use super::{ArchiveCommand, ArchiveCommandKind};
use crate::{
    Error,
    geometry::{Mesh, VectorVertex},
    vector::{Brush, Matrix4, PathSegmentKind},
};
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;

/// Original 32-byte analytic hatch boundary descriptor.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct GpuHatchRecord {
    pub start_segment: u32,
    pub segment_count: u32,
    pub bounds: [f32; 4],
    pub padding: [u32; 2],
}
/// Original 48-byte polynomial/rational boundary record. Weight bits retain the
/// original independent padding words; unused points and words remain zero.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct GpuHatchSegment {
    pub points: [[f32; 2]; 4],
    pub segment_type: u32,
    pub padding: [u32; 3],
}
const _: () = {
    assert!(size_of::<GpuHatchRecord>() == 32);
    assert!(size_of::<GpuHatchSegment>() == 48);
    assert!(std::mem::offset_of!(GpuHatchSegment, segment_type) == 32);
};

/// Original retained hatch command, separate from general PathAtlas fills.
/// All original figure flags remain owned; this extension consumes figures in
/// source order without applying generic fill-rule, Boolean or stroke policies.
#[derive(Clone, Debug)]
pub struct ArchiveHatchDraw {
    command: Arc<ArchiveCommand>,
    segments: Arc<[GpuHatchSegment]>,
    bounds: [f32; 4],
    transform: Matrix4,
    opacity: f32,
}
impl ArchiveHatchDraw {
    /// Compile the original boundary records privately before publication. Both
    /// the legacy DrawHatch kind and original built-in extension id 3 are admitted.
    pub fn new(
        command: Arc<ArchiveCommand>,
        transform: Matrix4,
        opacity: f32,
    ) -> Result<Self, Error> {
        if command.kind != ArchiveCommandKind::DrawHatch
            && !(command.kind == ArchiveCommandKind::DrawExtension && command.extension_id == 3)
        {
            return Err(Error::InvalidInput);
        }
        let mut result = Self {
            command,
            segments: Arc::from([]),
            bounds: [f32::MAX, f32::MAX, f32::MIN, f32::MIN],
            transform,
            opacity,
        };
        result.validate()?;
        if result.is_active() {
            let (segments, bounds) = boundary(&result.command)?;
            result.segments = segments.into();
            result.bounds = bounds;
        }
        Ok(result)
    }
    /// Ordinary CompilePicture DrawHatch only appends to the active Vector
    /// batch. A dedicated extension drawcall belongs to explicit extension 3;
    /// the independent static-DXF producer must select its own source route.
    pub fn uses_extension_pipeline(&self) -> bool {
        self.command.kind == ArchiveCommandKind::DrawExtension
    }
    pub fn command(&self) -> &Arc<ArchiveCommand> {
        &self.command
    }
    pub fn transform(&self) -> Matrix4 {
        self.transform
    }
    pub fn opacity(&self) -> f32 {
        self.opacity
    }
    pub fn segments(&self) -> &[GpuHatchSegment] {
        &self.segments
    }
    /// Original control-point extent, absent when no actual boundary was emitted.
    pub fn bounds(&self) -> Option<[f32; 4]> {
        (!self.segments.is_empty()).then_some(self.bounds)
    }
    pub fn brush(&self) -> Result<Arc<Brush>, Error> {
        Ok(Arc::new(
            self.command
                .brush
                .as_ref()
                .ok_or(Error::InvalidInput)?
                .to_vector()
                .map_err(super::drawing_error)?,
        ))
    }
    pub fn placed(&self, transform: Matrix4) -> Result<Self, Error> {
        let mut next = self.clone();
        next.transform = self.transform * transform;
        next.validate()?;
        Ok(next)
    }
    pub fn with_opacity(&self, opacity: f32) -> Result<Self, Error> {
        let mut next = self.clone();
        next.opacity *= opacity;
        next.validate()?;
        Ok(next)
    }
    pub fn validate(&self) -> Result<(), Error> {
        if self.command.use_gpu_transforms {
            return Err(Error::UnsupportedExecution);
        }
        if self.transform.0.iter().flatten().any(|v| !v.is_finite()) || !self.opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    /// Register the original brush even when this path later emits no segments.
    pub(crate) fn is_active(&self) -> bool {
        self.command.path.is_some() && self.command.brush.is_some()
    }
}

/// Actual complete frame/static-compilation generation. Its original records
/// are shared by all ordered draws; each source command remains retained.
#[derive(Debug, Default)]
pub(crate) struct HatchBatch {
    pub records: Vec<GpuHatchRecord>,
    pub segments: Vec<GpuHatchSegment>,
    pub sources: Vec<Arc<ArchiveHatchDraw>>,
}
impl HatchBatch {
    pub fn append(
        &mut self,
        draw: &Arc<ArchiveHatchDraw>,
        mesh: &mut Mesh,
        brush: f32,
    ) -> Result<(), Error> {
        let Some([min_x, min_y, max_x, max_y]) = draw.bounds() else {
            return Ok(());
        };
        let record = u32::try_from(self.records.len()).map_err(|_| Error::Capacity)?;
        // The original record ordinal is carried as f32 in vertex color.z.
        if record >= 1 << 24 {
            return Err(Error::Capacity);
        }
        let start = u32::try_from(self.segments.len()).map_err(|_| Error::Capacity)?;
        let count = u32::try_from(draw.segments.len()).map_err(|_| Error::Capacity)?;
        start.checked_add(count).ok_or(Error::Capacity)?;
        self.records.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.sources.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.segments
            .try_reserve(draw.segments.len())
            .map_err(|_| Error::Capacity)?;
        let local = [
            [min_x, min_y],
            [max_x, min_y],
            [max_x, max_y],
            [min_x, max_y],
        ];
        let uv = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
        let vertices = std::array::from_fn(|i| VectorVertex {
            position: draw.transform.transform_point(local[i]),
            color: [local[i][0], local[i][1], record as f32, 0.0],
            texture_coordinate: uv[i],
            brush_index: brush,
            shape_size: [max_x - min_x, max_y - min_y],
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            shape_type: 9.0,
        });
        mesh.push_quad(vertices)?;
        self.records.push(GpuHatchRecord {
            start_segment: start,
            segment_count: count,
            bounds: [min_x, min_y, max_x, max_y],
            padding: [0; 2],
        });
        self.segments.extend_from_slice(&draw.segments);
        self.sources.push(draw.clone());
        Ok(())
    }
}

fn boundary(command: &ArchiveCommand) -> Result<(Vec<GpuHatchSegment>, [f32; 4]), Error> {
    let path = command.path.as_ref().ok_or(Error::InvalidInput)?;
    let mut segments = Vec::new();
    let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    for figure in &path.figures {
        if figure.segments.is_empty() {
            continue;
        }
        let mut current = figure.start;
        update_bounds(&mut bounds, current)?;
        for segment in &figure.segments {
            let mut gpu = GpuHatchSegment::default();
            gpu.points[0] = current;
            let last = match segment.kind {
                PathSegmentKind::Line(point) => {
                    gpu.points[1] = point;
                    1
                }
                PathSegmentKind::Quadratic(points) => {
                    gpu.points[1..3].copy_from_slice(&points);
                    gpu.segment_type = 1;
                    2
                }
                PathSegmentKind::Cubic(points) => {
                    gpu.points[1..4].copy_from_slice(&points);
                    gpu.segment_type = 2;
                    3
                }
                PathSegmentKind::RationalQuadratic { points, weight } => {
                    gpu.points[1..3].copy_from_slice(&points);
                    weights(&gpu.points[..3], &[weight], 4.0)?;
                    gpu.segment_type = 4;
                    gpu.padding[0] = weight.to_bits();
                    2
                }
                PathSegmentKind::RationalCubic {
                    points,
                    weights: values,
                } => {
                    gpu.points[1..4].copy_from_slice(&points);
                    weights(&gpu.points, &values, 8.0)?;
                    gpu.segment_type = 5;
                    gpu.padding[0] = values[0].to_bits();
                    gpu.padding[1] = values[1].to_bits();
                    3
                }
                // Original extension has no Arc branch: neither append nor
                // update current point. Generic arc lowering would change it.
                PathSegmentKind::Arc(_) => continue,
            };
            for point in &gpu.points[1..=last] {
                update_bounds(&mut bounds, *point)?;
            }
            current = gpu.points[last];
            push(&mut segments, gpu)?;
        }
        if figure.is_closed && current != figure.start {
            push(
                &mut segments,
                GpuHatchSegment {
                    points: [current, figure.start, [0.0; 2], [0.0; 2]],
                    ..Default::default()
                },
            )?;
            update_bounds(&mut bounds, figure.start)?;
        }
    }
    Ok((segments, bounds))
}
fn push(segments: &mut Vec<GpuHatchSegment>, value: GpuHatchSegment) -> Result<(), Error> {
    if segments.len() == u32::MAX as usize {
        return Err(Error::Capacity);
    }
    segments.try_reserve(1).map_err(|_| Error::Capacity)?;
    segments.push(value);
    Ok(())
}
fn update_bounds(bounds: &mut [f32; 4], point: [f32; 2]) -> Result<(), Error> {
    if point.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    bounds[0] = minimum(bounds[0], point[0]);
    bounds[1] = minimum(bounds[1], point[1]);
    bounds[2] = maximum(bounds[2], point[0]);
    bounds[3] = maximum(bounds[3], point[1]);
    Ok(())
}
fn weights(points: &[[f32; 2]], values: &[f32], factor: f64) -> Result<(), Error> {
    let scale = points
        .iter()
        .flatten()
        .fold(1.0f64, |m, v| m.max(f64::from(v.abs())));
    let limit = f64::from(f32::MAX) / (factor * scale);
    if values
        .iter()
        .any(|v| !v.is_finite() || *v <= 0.0 || f64::from(*v) > limit)
    {
        return Err(Error::InvalidInput);
    }
    Ok(())
}

// Source Math.Min/Max retain the corresponding signed-zero endpoint.
fn minimum(a: f32, b: f32) -> f32 {
    if a < b || (a == b && a.is_sign_negative()) {
        a
    } else {
        b
    }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a > b || (a == b && !a.is_sign_negative()) {
        a
    } else {
        b
    }
}
