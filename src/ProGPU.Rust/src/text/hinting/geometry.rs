//! Original owned already-positioned batch geometry and complete atomic copies.
use super::*;
use crate::font::OutlinePoint;
use crate::path::Segment;

/// Original complete immutable geometry counts, with no invented shaping records.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct NativeFontOutlineCounts {
    /// All source slots, including no-ink and duplicate glyphs.
    pub descriptors: u32,
    /// Renderable physical outline records.
    pub outlines: u32,
    /// Original path records with exact scan metadata where admitted.
    pub segments: u32,
}
/// Original metadata-free source descriptor; placement belongs to the caller.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct NativeFontOutlineDescriptor {
    /// Exact captured original glyph ID.
    pub glyph_index: u32,
    /// Actual outline slot or NATIVE_HINTED_NO_OUTLINE.
    pub outline_index: u32,
}
/// Original geometry publication/copy error classes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedGeometryError {
    /// Invalid source identity or complete copy capacities.
    InvalidArgument,
    /// Original geometry/coverage/projection admission failed.
    Unsupported(NativeHintedOutlineError),
    /// Actual owned vector allocation failed.
    ResourceExhausted,
    /// Immutable requirement and write passes disagreed.
    Internal,
}
impl std::fmt::Display for NativeHintedGeometryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native hinted geometry: {self:?}")
    }
}
impl std::error::Error for NativeHintedGeometryError {}
/// Owned exact physical geometry retaining the actual original batch. It has no
/// paragraph, shaping, cluster or position metadata and survives face/cache/context
/// retirement. Copies never rerun fonts, shaping or contour lowering.
#[derive(Debug)]
pub struct NativeFontOutlineGeometry {
    source: Arc<NativeHintedBatch>,
    outlines: Vec<NativeHintedOutline>,
    segments: Vec<Segment>,
    source_indices: Vec<u32>,
}
fn filled<T: Clone>(count: usize, value: T) -> Result<Vec<T>, NativeHintedGeometryError> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(count)
        .map_err(|_| NativeHintedGeometryError::ResourceExhausted)?;
    output.resize(count, value);
    Ok(output)
}
impl NativeFontOutlineGeometry {
    /// Prepare the complete original physical batch geometry once, atomically.
    /// No actual font execution or shaping occurs, and caller placement stays
    /// independent of the exact captured generation and its physical phase.
    pub fn new(
        source: Arc<NativeHintedBatch>,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, NativeHintedGeometryError> {
        let required = native_hinted_batch_outline_requirements(&source, projection, coverage)
            .map_err(|error| match error {
                NativeHintedOutlineError::InvalidArgument
                | NativeHintedOutlineError::InvalidRun => {
                    NativeHintedGeometryError::InvalidArgument
                }
                other => NativeHintedGeometryError::Unsupported(other),
            })?;
        let mut candidate = Self {
            source,
            outlines: filled(required.outlines, NativeHintedOutline::default())?,
            segments: filled(required.segments, Segment::line([0.0; 2], [0.0; 2]))?,
            source_indices: filled(required.source_slots, 0)?,
        };
        let mut topology = filled(required.scratch_points, OutlinePoint::default())?;
        let mut physical = filled(required.scratch_points, [0.0; 2])?;
        let written = write_native_hinted_batch_outlines(
            &candidate.source,
            NativeHintedOutlineScratch {
                topology: &mut topology,
                physical_points: &mut physical,
            },
            &mut candidate.outlines,
            &mut candidate.segments,
            &mut candidate.source_indices,
            projection,
            coverage,
        )
        .map_err(|_| NativeHintedGeometryError::Internal)?;
        if written != required {
            return Err(NativeHintedGeometryError::Internal);
        }
        Ok(candidate)
    }
    /// Actual independently retained captured source owner.
    pub fn source(&self) -> &Arc<NativeHintedBatch> {
        &self.source
    }
    /// Original proven complete inventory, including every no-ink source slot.
    pub fn counts(&self) -> NativeFontOutlineCounts {
        NativeFontOutlineCounts {
            descriptors: self.source_indices.len() as u32,
            outlines: self.outlines.len() as u32,
            segments: self.segments.len() as u32,
        }
    }
    /// Borrow actual immutable physical outline records.
    pub fn outlines(&self) -> &[NativeHintedOutline] {
        &self.outlines
    }
    /// Borrow actual immutable original path records and scan metadata.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
    /// Borrow every original descriptor's outline/no-ink identity in source order.
    pub fn source_outline_indices(&self) -> &[u32] {
        &self.source_indices
    }
    /// Copy all original geometry and descriptor records after complete capacity
    /// preflight. Safe borrows exclude full-capacity aliases; tails and all outputs
    /// on error remain unchanged. No allocation or source callback occurs.
    pub fn copy_into(
        &self,
        outlines: &mut [NativeHintedOutline],
        segments: &mut [Segment],
        descriptors: &mut [NativeFontOutlineDescriptor],
    ) -> Result<(), NativeHintedGeometryError> {
        if outlines.len() < self.outlines.len()
            || segments.len() < self.segments.len()
            || descriptors.len() < self.source_indices.len()
        {
            return Err(NativeHintedGeometryError::InvalidArgument);
        }
        outlines[..self.outlines.len()].copy_from_slice(&self.outlines);
        segments[..self.segments.len()].copy_from_slice(&self.segments);
        for (index, &outline_index) in self.source_indices.iter().enumerate() {
            descriptors[index] = NativeFontOutlineDescriptor {
                glyph_index: self.source.glyphs()[index].glyph_id,
                outline_index,
            };
        }
        Ok(())
    }
}
