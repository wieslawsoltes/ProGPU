//! Original hinted fitted-run geometry, exact source slots and immutable owners.
use super::*;
use crate::font::OutlinePoint;
use crate::text::hinting::*;

/// Exact source-indexed fitted occurrence, including explicit no-ink descriptors.
/// The source catalog is one actual retained run; no paragraph/font/run IDs are invented.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeHintedOccurrence {
    /// Original position in the fitted visual output, unchanged through selection.
    pub positioned_index: u32,
    /// Exact original retained native run glyph index.
    pub run_glyph_index: u32,
    /// Exact capture descriptor, never found by searching a glyph ID.
    pub descriptor_index: u32,
    /// Original physical outline/no-ink identity.
    pub outline_index: u32,
    /// Original source glyph ID.
    pub glyph_id: u32,
    /// Exact original source cluster.
    pub cluster: i32,
    /// Exact original exclusive source cluster end.
    pub cluster_end: i32,
    /// Original full source bidi level.
    pub bidi_level: i8,
    /// Original writer's logical position, unchanged by target placement.
    pub position: [f32; 2],
    /// Original writer's logical advance.
    pub advance: [f32; 2],
}
/// Original native fitted-geometry publication failures.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedSourceError {
    /// Caller source owners or scalar target metadata are invalid.
    InvalidArgument,
    /// Complete retained logical/positioned source inventories disagree.
    InvalidLayout,
    /// The exact physical-to-logical mapping cannot be retained.
    UnsupportedMapping,
    /// Actual original outline conversion rejected this source/policy.
    OutlineConversion(NativeHintedOutlineError),
    /// Original native executor inventory bounds are exceeded.
    InsufficientCapacity,
    /// Actual allocation failed before candidate publication.
    ResourceExhausted,
}
impl std::fmt::Display for NativeHintedSourceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native hinted source: {self:?}")
    }
}
impl std::error::Error for NativeHintedSourceError {}
fn allocate<T: Clone>(count: usize, value: T) -> Result<Vec<T>, NativeHintedSourceError> {
    filled(count, value).map_err(|_| NativeHintedSourceError::ResourceExhausted)
}
/// One immutable fitted source generation. Complete original run descriptors
/// convert exactly once; maximum-lines output does not discard source ownership.
#[derive(Debug)]
pub struct NativeHintedSourceGeometry {
    layout: Arc<NativeHintedTextLayout>,
    dpi: f32,
    projection: NativeHintedProjection,
    coverage: NativeHintedCoverage,
    outlines: Vec<NativeHintedOutline>,
    segments: Vec<Segment>,
    source_indices: Vec<u32>,
    run_indices: Vec<u32>,
    occurrences: Vec<NativeHintedOccurrence>,
}
impl NativeHintedSourceGeometry {
    /// Prepare the complete original fitted source. Exact Arc run identity is
    /// required; equal font bytes, glyph IDs or positions do not establish it.
    pub fn new(
        layout: Arc<NativeHintedTextLayout>,
        run: &Arc<NativeDeviceRun>,
        dpi: f32,
        projection: NativeHintedProjection,
        coverage: NativeHintedCoverage,
    ) -> Result<Self, NativeHintedSourceError> {
        use NativeHintedSourceError as E;
        if !Arc::ptr_eq(layout.run(), run)
            || run.batch().font().data().is_empty()
            || !dpi.is_finite()
            || dpi <= 0.0
        {
            return Err(E::InvalidArgument);
        }
        let units = 1.0 / dpi;
        if !units.is_finite()
            || units <= 0.0
            || layout.units_per_pixel != units
            || layout.units_per_pixel * dpi != 1.0
        {
            return Err(E::UnsupportedMapping);
        }
        if run.glyphs().len() > 1 << 24 || layout.glyphs.len() > 1 << 24 {
            return Err(E::InsufficientCapacity);
        }
        validate_layout(&layout, run)?;
        let required = native_hinted_run_outline_requirements(run, projection, coverage)
            .map_err(E::OutlineConversion)?;
        if required.outlines > 1 << 20
            || required.segments > 1 << 24
            || required.source_slots > 1 << 24
            || required.positioned_slots > 1 << 24
        {
            return Err(E::InsufficientCapacity);
        }
        let mut candidate = Self {
            layout,
            dpi,
            projection,
            coverage,
            outlines: allocate(required.outlines, NativeHintedOutline::default())?,
            segments: allocate(required.segments, Segment::line([0.0; 2], [0.0; 2]))?,
            source_indices: allocate(required.source_slots, 0)?,
            run_indices: allocate(required.positioned_slots, 0)?,
            occurrences: reserved(0).map_err(|_| E::ResourceExhausted)?,
        };
        let mut topology = allocate(required.scratch_points, OutlinePoint::default())?;
        let mut physical = allocate(required.scratch_points, [0.0; 2])?;
        let written = write_native_hinted_run_outlines(
            run,
            NativeHintedOutlineScratch {
                topology: &mut topology,
                physical_points: &mut physical,
            },
            &mut candidate.outlines,
            &mut candidate.segments,
            &mut candidate.source_indices,
            &mut candidate.run_indices,
            projection,
            coverage,
        )
        .map_err(E::OutlineConversion)?;
        if written != required {
            return Err(E::InvalidLayout);
        }
        candidate
            .occurrences
            .try_reserve_exact(candidate.layout.glyphs.len())
            .map_err(|_| E::ResourceExhausted)?;
        for (index, glyph) in candidate.layout.glyphs.iter().enumerate() {
            let descriptor = candidate.layout.descriptors[index];
            let outline = candidate.source_indices[descriptor as usize];
            if candidate.run_indices[glyph.glyph_index as usize] != outline {
                return Err(E::InvalidLayout);
            }
            candidate.occurrences.push(NativeHintedOccurrence {
                positioned_index: index as u32,
                run_glyph_index: glyph.glyph_index,
                descriptor_index: descriptor,
                outline_index: outline,
                glyph_id: glyph.glyph_id,
                cluster: glyph.cluster,
                cluster_end: candidate.layout.cluster_ends[index],
                bidi_level: candidate.layout.levels[index],
                position: glyph.position,
                advance: glyph.advance,
            });
        }
        Ok(candidate)
    }
    /// Exact original fitted layout, run, capture, source/font and variation owners.
    pub fn layout(&self) -> &Arc<NativeHintedTextLayout> {
        &self.layout
    }
    /// Exact physical target DPI selected at geometry publication.
    pub fn dpi_scale(&self) -> f32 {
        self.dpi
    }
    /// Original explicit CPU projection selection.
    pub fn projection(&self) -> NativeHintedProjection {
        self.projection
    }
    /// Original strict/nonzero/antialiased contract; run Monochrome is unadmitted.
    pub fn coverage(&self) -> NativeHintedCoverage {
        self.coverage
    }
    /// Complete original native physical outline inventory.
    pub fn outlines(&self) -> &[NativeHintedOutline] {
        &self.outlines
    }
    /// Exact original physical coordinates and retained metadata.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
    /// Original post-GSUB descriptor prefix, including all no-ink slots.
    pub fn source_outline_indices(&self) -> &[u32] {
        &self.source_indices
    }
    /// One exact outline/no-ink identity per original final run occurrence.
    pub fn run_outline_indices(&self) -> &[u32] {
        &self.run_indices
    }
    /// Actual fitted occurrences, including no-ink, with source identities intact.
    pub fn occurrences(&self) -> &[NativeHintedOccurrence] {
        &self.occurrences
    }
    /// Retain this exact source in the shared physical replay view. Shaped
    /// metadata remains queryable and selected views share its raster identity.
    pub fn into_replay(
        self: Arc<Self>,
    ) -> Result<
        crate::text::font_policy::FontReplayGeometry,
        crate::text::font_policy::FontPolicyError,
    > {
        crate::text::font_policy::FontReplayGeometry::from_shaped_source(self)
    }
    /// Complete source geometry copies, with all capacities checked before the
    /// first write. Original owners, unused tails and every source byte survive.
    pub fn copy_into(
        &self,
        outlines: &mut [NativeHintedOutline],
        segments: &mut [Segment],
        source_indices: &mut [u32],
        run_indices: &mut [u32],
        occurrences: &mut [NativeHintedOccurrence],
    ) -> Result<(), NativeHintedSourceError> {
        if outlines.len() < self.outlines.len()
            || segments.len() < self.segments.len()
            || source_indices.len() < self.source_indices.len()
            || run_indices.len() < self.run_indices.len()
            || occurrences.len() < self.occurrences.len()
        {
            return Err(NativeHintedSourceError::InsufficientCapacity);
        }
        outlines[..self.outlines.len()].copy_from_slice(&self.outlines);
        segments[..self.segments.len()].copy_from_slice(&self.segments);
        source_indices[..self.source_indices.len()].copy_from_slice(&self.source_indices);
        run_indices[..self.run_indices.len()].copy_from_slice(&self.run_indices);
        occurrences[..self.occurrences.len()].copy_from_slice(&self.occurrences);
        Ok(())
    }
}
fn validate_layout(
    layout: &NativeHintedTextLayout,
    run: &NativeDeviceRun,
) -> Result<(), NativeHintedSourceError> {
    use NativeHintedSourceError::InvalidLayout as E;
    let count = run.glyphs().len();
    let fitted = layout.glyphs.len();
    if !matches!(
        run.direction(),
        GposDirection::LeftToRight | GposDirection::RightToLeft
    ) || layout.options.direction != run.direction()
        || layout.options.trimming != NativeTextTrimming::None
        || !matches!(layout.paragraph_level, 0 | 1)
        || !layout.units_per_pixel.is_finite()
        || layout.units_per_pixel <= 0.0
        || !layout.options.scale.is_finite()
        || layout.options.scale <= 0.0
        || layout.options.scale != layout.units_per_pixel / 64.0
        || layout.logical.len() != count
        || layout.logical_run_indices.len() != count
        || layout.breaks.len() != count
        || layout.logical_levels.len() != count
        || layout.logical_cluster_ends.len() != count
        || layout.item_metrics.len() != count
        || !layout.justification.is_empty() && layout.justification.len() != count
        || layout.descriptors.len() != fitted
        || layout.levels.len() != fitted
        || layout.cluster_ends.len() != fitted
        || fitted > count
        || layout.lines.len() != layout.line_origins.len()
    {
        return Err(E);
    }
    let mut by_run = allocate(count, 0u32)?;
    let mut positioned = allocate(count, false)?;
    let mut logical = 0;
    let mut group = |first: usize, last: usize| -> Result<(), NativeHintedSourceError> {
        for original in first..last {
            let source = run.glyphs()[original];
            let restored = layout.logical[logical];
            let mut expected = source;
            expected.advance_y = source.advance_y.checked_neg().ok_or(E)?;
            expected.offset_y = source.offset_y.checked_neg().ok_or(E)?;
            let item = layout.item_metrics[logical];
            if layout.logical_run_indices[logical] as usize != original
                || restored != expected
                || source.cluster < 0
                || layout.logical_cluster_ends[logical] <= source.cluster
                || !(0..=125).contains(&layout.logical_levels[logical])
                || layout.logical_levels[logical] & 1
                    != i8::from(run.direction() == GposDirection::RightToLeft)
                || !item.ascent.is_finite()
                || item.ascent < 0.0
                || !item.descent.is_finite()
                || item.descent < 0.0
                || !(item.ascent + item.descent).is_finite()
            {
                return Err(E);
            }
            by_run[original] = logical as u32;
            logical += 1;
        }
        Ok(())
    };
    if run.direction() == GposDirection::LeftToRight {
        group(0, count)?;
    } else {
        let mut last = count;
        while last != 0 {
            let mut first = last - 1;
            while first != 0 && run.glyphs()[first - 1].cluster == run.glyphs()[last - 1].cluster {
                first -= 1;
            }
            group(first, last)?;
            last = first;
        }
    }
    for index in 1..count {
        let previous = layout.logical[index - 1].cluster;
        let current = layout.logical[index].cluster;
        if current < previous
            || current > previous && layout.logical_cluster_ends[index - 1] > current
            || current == previous
                && (layout.logical_cluster_ends[index] != layout.logical_cluster_ends[index - 1]
                    || layout.logical_levels[index] != layout.logical_levels[index - 1])
        {
            return Err(E);
        }
    }
    for (index, glyph) in layout.glyphs.iter().enumerate() {
        let original = glyph.glyph_index as usize;
        if original >= count || original >= run.descriptor_indices().len() {
            return Err(E);
        }
        let logical = by_run[original] as usize;
        if glyph.glyph_id != run.glyphs()[original].glyph_id
            || glyph.cluster != run.glyphs()[original].cluster
            || layout.descriptors[index] != run.descriptor_indices()[original]
            || positioned[original]
            || layout.levels[index] != layout.logical_levels[logical]
            || layout.cluster_ends[index] != layout.logical_cluster_ends[logical]
            || !glyph
                .position
                .iter()
                .chain(&glyph.advance)
                .all(|v| v.is_finite())
            || !(glyph.position[0] + glyph.advance[0]).is_finite()
            || !(glyph.position[1] + glyph.advance[1]).is_finite()
        {
            return Err(E);
        }
        positioned[original] = true;
    }
    let mut covered = 0;
    for (index, line) in layout.lines.iter().enumerate() {
        if line.glyph_start as usize != covered
            || line.glyph_count == 0
            || line.glyph_count as usize > fitted - covered
            || !line.width.is_finite()
            || line.width < 0.0
            || !line.baseline_y.is_finite()
            || !line.height.is_finite()
            || line.height < 0.0
            || !layout.line_origins[index].is_finite()
            || line.flags & !1 != 0
        {
            return Err(E);
        }
        covered += line.glyph_count as usize;
    }
    if covered != fitted {
        return Err(E);
    }
    Ok(())
}
