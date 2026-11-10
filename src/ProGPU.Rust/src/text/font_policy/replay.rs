//! Original captured batch/shaped-source replay and retained selected occurrence views.
use super::*;
use crate::text::hinted_source::{NativeHintedOccurrence, NativeHintedSourceGeometry};
pub(super) const MAXIMUM_SLOTS: usize = 1 << 24;
pub(super) const MAXIMUM_OUTLINES: usize = 1 << 20;
const MAXIMUM_BINARY_SEGMENTS: usize = 256;
const SCAN_MARKER: u32 = 0x100;
const CONTOUR_OVERRIDE: u32 = 0x200;
const ORIGINAL_FLAGS_MASK: u32 = (0x01 | 0x04 | 0x08 | 0x10 | 0x20 | 0x100) << 16;

/// One original captured font descriptor at the caller's logical baseline.
/// This record deliberately has no shaping, cluster, advance or bidi metadata.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FontGlyphPlacement {
    /// Exact original batch slot, retained through selected views.
    pub descriptor_index: u32,
    /// Exact original captured glyph ID.
    pub glyph_id: u32,
    /// Original outline identity, including NATIVE_HINTED_NO_OUTLINE.
    pub outline_index: u32,
    /// Original caller-provided logical baseline coordinates, unchanged.
    pub position: [f32; 2],
}
#[derive(Debug)]
enum SourceOwner {
    Batch(Arc<NativeFontOutlineGeometry>),
    Shaped(Arc<NativeHintedSourceGeometry>),
}
#[derive(Debug)]
struct Generation {
    source: SourceOwner,
    dpi_scale: f32,
    coverage: NativeHintedCoverage,
    outlines: Vec<GpuGlyphRecord>,
    segments: Vec<Segment>,
}
/// Immutable retained physical geometry and original batch/shaped placements. Selections
/// share the exact original source/raster generation; retiring the creating
/// provider/cache/batch or another view cannot invalidate a retained view.
#[derive(Debug)]
pub struct FontReplayGeometry {
    generation: Arc<Generation>,
    placements: Vec<FontGlyphPlacement>,
    occurrences: Option<Vec<NativeHintedOccurrence>>,
}
impl FontReplayGeometry {
    /// Import every original descriptor without shaping, hinting again or
    /// reconstructing layout metadata. Caller positions freeze before geometry
    /// preparation; complete source/geometry admission precedes publication.
    pub fn from_batch(
        batch: Arc<NativeHintedBatch>,
        positions: &[[f32; 2]],
        dpi_scale: f32,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, FontPolicyError> {
        validate_dpi(dpi_scale)?;
        if positions.len() > MAXIMUM_SLOTS
            || positions.len() != native_hinted_batch_counts(&batch)?.glyphs as usize
        {
            return Err(FontPolicyError::InvalidArgument);
        }
        let positions = copy(positions)?;
        validate_positions(&positions)?;
        let owner = Arc::new(NativeFontOutlineGeometry::new(batch, projection, coverage)?);
        let counts = owner.counts();
        require(
            counts.descriptors as usize == positions.len()
                && counts.outlines as usize <= MAXIMUM_OUTLINES
                && counts.segments as usize <= MAXIMUM_SLOTS,
        )?;
        let (outlines, segments, descriptors) = copy_geometry(&owner)?;
        validate_geometry(
            &outlines,
            &segments,
            coverage == NativeHintedCoverage::Monochrome,
        )?;
        Self::publish(
            owner,
            dpi_scale,
            coverage,
            outlines,
            segments,
            descriptors,
            positions,
        )
    }
    pub(super) fn publish(
        owner: Arc<NativeFontOutlineGeometry>,
        dpi_scale: f32,
        coverage: NativeHintedCoverage,
        native_outlines: Vec<NativeHintedOutline>,
        segments: Vec<Segment>,
        descriptors: Vec<NativeFontOutlineDescriptor>,
        positions: Vec<[f32; 2]>,
    ) -> Result<Self, FontPolicyError> {
        require(descriptors.len() == positions.len())?;
        let mut outlines = Vec::new();
        outlines
            .try_reserve_exact(native_outlines.len())
            .map_err(|_| FontPolicyError::ResourceExhausted)?;
        for outline in native_outlines {
            outlines.push(GpuGlyphRecord {
                start_segment: u32::try_from(outline.segment_offset)
                    .map_err(|_| FontPolicyError::InvalidGeneration)?,
                segment_count: u32::try_from(outline.segment_count)
                    .map_err(|_| FontPolicyError::InvalidGeneration)?,
                bounds: outline.bounds,
                padding: [0; 2],
            });
        }
        let mut placements = Vec::new();
        placements
            .try_reserve_exact(descriptors.len())
            .map_err(|_| FontPolicyError::ResourceExhausted)?;
        for (index, (descriptor, position)) in descriptors.into_iter().zip(positions).enumerate() {
            require(
                descriptor.outline_index == NATIVE_HINTED_NO_OUTLINE
                    || (descriptor.outline_index as usize) < outlines.len(),
            )?;
            placements.push(FontGlyphPlacement {
                descriptor_index: index as u32,
                glyph_id: descriptor.glyph_index,
                outline_index: descriptor.outline_index,
                position,
            });
        }
        Ok(Self {
            generation: Arc::new(Generation {
                source: SourceOwner::Batch(owner),
                dpi_scale,
                coverage,
                outlines,
                segments,
            }),
            placements,
            occurrences: None,
        })
    }
    /// Import an actual native fitted source without decoding, shaping or hinting
    /// again. Original occurrence metadata and all raw descriptor slots remain owned.
    pub fn from_shaped_source(
        source: Arc<NativeHintedSourceGeometry>,
    ) -> Result<Self, FontPolicyError> {
        let dpi_scale = source.dpi_scale();
        validate_dpi(dpi_scale)?;
        require(
            source.outlines().len() <= MAXIMUM_OUTLINES
                && source.segments().len() <= MAXIMUM_SLOTS
                && source.occurrences().len() <= MAXIMUM_SLOTS
                && source.source_outline_indices().len() <= MAXIMUM_SLOTS
                && source.run_outline_indices().len() <= MAXIMUM_SLOTS
                && source.coverage() != NativeHintedCoverage::Monochrome,
        )?;
        validate_geometry(source.outlines(), source.segments(), false)?;
        let mut outlines = Vec::new();
        outlines
            .try_reserve_exact(source.outlines().len())
            .map_err(|_| FontPolicyError::ResourceExhausted)?;
        for &outline in source.outlines() {
            outlines.push(
                outline
                    .gpu_record()
                    .map_err(|_| FontPolicyError::InvalidGeneration)?,
            );
        }
        let segments = copy(source.segments())?;
        let occurrences = copy(source.occurrences())?;
        let mut placements = Vec::new();
        placements
            .try_reserve_exact(occurrences.len())
            .map_err(|_| FontPolicyError::ResourceExhausted)?;
        for occurrence in &occurrences {
            require(
                occurrence.outline_index == NATIVE_HINTED_NO_OUTLINE
                    || (occurrence.outline_index as usize) < outlines.len(),
            )?;
            require(
                occurrence
                    .position
                    .iter()
                    .chain(&occurrence.advance)
                    .all(|v| v.is_finite()),
            )?;
            placements.push(FontGlyphPlacement {
                descriptor_index: occurrence.descriptor_index,
                glyph_id: occurrence.glyph_id,
                outline_index: occurrence.outline_index,
                position: occurrence.position,
            });
        }
        Ok(Self {
            generation: Arc::new(Generation {
                dpi_scale,
                coverage: source.coverage(),
                outlines,
                segments,
                source: SourceOwner::Shaped(source),
            }),
            placements,
            occurrences: Some(occurrences),
        })
    }
    /// Retain selected occurrence slots in caller order, including duplicates and
    /// no-ink. Original descriptor IDs/positions never change or get renumbered.
    pub fn select_occurrences(&self, indices: &[usize]) -> Result<Self, FontPolicyError> {
        let indices = copy(indices)?;
        if indices.iter().any(|&index| index >= self.placements.len()) {
            return Err(FontPolicyError::InvalidArgument);
        }
        let mut placements = Vec::new();
        placements
            .try_reserve_exact(indices.len())
            .map_err(|_| FontPolicyError::ResourceExhausted)?;
        placements.extend(indices.iter().map(|&index| self.placements[index]));
        let occurrences = if let Some(original) = &self.occurrences {
            let mut selected = Vec::new();
            selected
                .try_reserve_exact(indices.len())
                .map_err(|_| FontPolicyError::ResourceExhausted)?;
            selected.extend(indices.iter().map(|&index| original[index]));
            Some(selected)
        } else {
            None
        };
        Ok(Self {
            generation: Arc::clone(&self.generation),
            placements,
            occurrences,
        })
    }
    pub(crate) fn generation_identity(&self) -> usize {
        Arc::as_ptr(&self.generation) as usize
    }
    /// Exact selected capture/geometry owner; paired replay retains only the
    /// original Unhinted owner here, not its separate monochrome witness.
    /// None identifies a separately retained shaped source.
    pub fn source_geometry(&self) -> Option<&Arc<NativeFontOutlineGeometry>> {
        match &self.generation.source {
            SourceOwner::Batch(source) => Some(source),
            _ => None,
        }
    }
    /// Actual shaped native layout/capture owner, absent for metadata-free batches.
    pub fn shaped_source_geometry(&self) -> Option<&Arc<NativeHintedSourceGeometry>> {
        match &self.generation.source {
            SourceOwner::Shaped(source) => Some(source),
            _ => None,
        }
    }
    /// True only when the actual original producer retained source shaping metadata.
    pub fn has_shaping_metadata(&self) -> bool {
        self.occurrences.is_some()
    }
    /// Selected original shaped occurrences; None rejects metadata-free batch access.
    pub fn shaped_occurrences(&self) -> Option<&[NativeHintedOccurrence]> {
        self.occurrences.as_deref()
    }
    /// Caller placement-only records, absent when the source owns shaped occurrences.
    pub fn font_placements(&self) -> Option<&[FontGlyphPlacement]> {
        self.occurrences
            .is_none()
            .then_some(self.placements.as_slice())
    }
    /// Actual original immutable GPU outline records.
    pub fn outlines(&self) -> &[GpuGlyphRecord] {
        &self.generation.outlines
    }
    /// Actual original geometry coordinates and explicit retained scan metadata.
    pub fn segments(&self) -> &[Segment] {
        &self.generation.segments
    }
    /// Common renderer placement projection of each selected descriptor, including
    /// no-ink. Use font_placements/shaped_occurrences to preserve metadata semantics.
    pub fn placements(&self) -> &[FontGlyphPlacement] {
        &self.placements
    }
    /// Exact positive original target DPI captured by the caller.
    pub fn dpi_scale(&self) -> f32 {
        self.generation.dpi_scale
    }
    /// Explicit immutable coverage contract, never a source raster-mode alias.
    pub fn coverage(&self) -> NativeHintedCoverage {
        self.generation.coverage
    }
    /// Original actual segment metadata determines the binary scan requirement;
    /// an empty source remains empty even when its explicit policy is monochrome.
    pub fn requires_binary_scan_conversion(&self) -> bool {
        self.generation
            .segments
            .first()
            .is_some_and(|s| s.parameters[0] != 0)
    }
    /// Exact retained source/raster generation identity across selected views.
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.generation, &other.generation)
    }
}
pub(super) fn validate_dpi(dpi: f32) -> Result<(), FontPolicyError> {
    if !dpi.is_finite() || dpi <= 0.0 || !(1.0 / dpi).is_finite() {
        Err(FontPolicyError::InvalidArgument)
    } else {
        Ok(())
    }
}
pub(super) fn validate_positions(positions: &[[f32; 2]]) -> Result<(), FontPolicyError> {
    if positions.iter().flatten().any(|v| !v.is_finite()) {
        Err(FontPolicyError::InvalidArgument)
    } else {
        Ok(())
    }
}
pub(super) fn copy_geometry(
    owner: &NativeFontOutlineGeometry,
) -> Result<
    (
        Vec<NativeHintedOutline>,
        Vec<Segment>,
        Vec<NativeFontOutlineDescriptor>,
    ),
    FontPolicyError,
> {
    let counts = owner.counts();
    let mut outlines = filled(counts.outlines as usize, NativeHintedOutline::default())?;
    let mut segments = filled(counts.segments as usize, Segment::line([0.0; 2], [0.0; 2]))?;
    let mut descriptors = filled(
        counts.descriptors as usize,
        NativeFontOutlineDescriptor::default(),
    )?;
    owner.copy_into(&mut outlines, &mut segments, &mut descriptors)?;
    Ok((outlines, segments, descriptors))
}
fn inside(point: [f32; 2], bounds: [f32; 4]) -> bool {
    point[0] >= bounds[0] && point[0] <= bounds[2] && point[1] >= bounds[1] && point[1] <= bounds[3]
}
/// Complete original source-adapter admission, separate from native contour
/// lowering. It retains exact per-contour binary metadata and original 256 limit.
pub(super) fn validate_geometry(
    outlines: &[NativeHintedOutline],
    segments: &[Segment],
    monochrome: bool,
) -> Result<(), FontPolicyError> {
    let mut geometry_end = 0;
    for outline in outlines {
        require(
            geometry_end <= segments.len()
                && outline.segment_offset == geometry_end
                && outline.segment_count > 0
                && outline.segment_count <= segments.len() - geometry_end
                && outline.raster_scale == 1.0
                && outline.subpixel_x == 0.0
                && outline.bounds.iter().all(|v| v.is_finite())
                && outline.bounds[2] > outline.bounds[0]
                && outline.bounds[3] > outline.bounds[1],
        )?;
        if monochrome {
            require(outline.segment_count <= MAXIMUM_BINARY_SEGMENTS)?;
        }
        let mut first = [0.0; 2];
        let mut previous = [0.0; 2];
        let mut closed = true;
        let mut contour_start = 0;
        let mut contour_count = 0;
        let mut scan_mode = 0;
        for (index, segment) in segments[geometry_end..geometry_end + outline.segment_count]
            .iter()
            .enumerate()
        {
            require(
                segment.kind <= (if monochrome { 1 } else { 2 })
                    && segment.points.iter().flatten().all(|v| v.is_finite()),
            )?;
            let beginning = if monochrome {
                index == contour_start + contour_count
            } else {
                closed
            };
            if monochrome {
                let allowed = SCAN_MARKER | CONTOUR_OVERRIDE | ORIGINAL_FLAGS_MASK | 7;
                require(
                    segment.parameters[0] & SCAN_MARKER != 0
                        && segment.parameters[0] & !allowed == 0,
                )?;
                require(
                    segment.parameters[0] & ORIGINAL_FLAGS_MASK
                        == segments[geometry_end].parameters[0] & ORIGINAL_FLAGS_MASK,
                )?;
                if beginning {
                    require(
                        closed
                            && segment.parameters[1] as usize == index
                            && segment.parameters[2] > 0
                            && segment.parameters[2] as usize <= outline.segment_count - index,
                    )?;
                    contour_start = segment.parameters[1] as usize;
                    contour_count = segment.parameters[2] as usize;
                    scan_mode = segment.parameters[0];
                }
                require(
                    segment.parameters[1] as usize == contour_start
                        && segment.parameters[2] as usize == contour_count
                        && segment.parameters[0] == scan_mode,
                )?;
            } else {
                require(segment.parameters == [0; 3])?;
            }
            if beginning {
                first = segment.points[0];
            } else {
                require(previous == segment.points[0])?;
            }
            let end = segment.points[segment.kind as usize + 1];
            require(
                inside(segment.points[0], outline.bounds)
                    && inside(segment.points[1], outline.bounds)
                    && (segment.kind < 1 || inside(segment.points[2], outline.bounds))
                    && (segment.kind < 2 || inside(segment.points[3], outline.bounds)),
            )?;
            closed = end == first;
            if monochrome && index + 1 == contour_start + contour_count {
                require(closed)?;
            }
            previous = end;
        }
        require(closed && (!monochrome || contour_start + contour_count == outline.segment_count))?;
        geometry_end += outline.segment_count;
    }
    require(geometry_end == segments.len())
}
