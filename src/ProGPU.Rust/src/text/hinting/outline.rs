//! Original complete hinted contour admission and atomic physical outline writer.
use super::*;
use crate::font::OutlinePoint;
use crate::path::Segment;
use crate::text::glyph::GpuGlyphRecord;

/// Original no-ink descriptor marker; descriptor slots themselves are retained.
pub const NATIVE_HINTED_NO_OUTLINE: u32 = u32::MAX;
/// Original independent coverage contracts, never source raster-mode aliases.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeHintedCoverage {
    /// Original strict flag/scan-policy rejection.
    #[default]
    Strict = 0,
    /// Explicit original nonzero vector coverage; no dependency raster parity.
    NonzeroVector = 1,
    /// Explicit antialiased vector coverage retaining original dropout metadata.
    AntialiasedVector = 2,
    /// Original already-positioned batches only; original binary scan metadata.
    Monochrome = 3,
}
/// Original outline-writer failure domain, independent of interpreter/shaping.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeHintedOutlineError {
    /// Invalid caller metadata.
    InvalidArgument = 1,
    /// Source generation or descriptor occurrence identity is invalid.
    InvalidRun = 2,
    /// Original contour tags/endpoints cannot form the required topology.
    InvalidTopology = 3,
    /// Original outline/tag/scan metadata is outside the explicit contract.
    UnsupportedFlags = 4,
    /// Original physical point or implicit midpoint is not exactly representable.
    UnsupportedCoordinates = 5,
    /// Requested coverage/projection is not admitted for this source route.
    UnsupportedPolicy = 6,
    /// Caller storage or original u32 output inventory is insufficient.
    InsufficientCapacity = 7,
}
impl std::fmt::Display for NativeHintedOutlineError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native hinted outline: {self:?}")
    }
}
impl std::error::Error for NativeHintedOutlineError {}
/// Complete original requirements, including every no-ink/repeated source slot.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeHintedOutlineRequirements {
    /// Original source descriptor prefix; auxiliaries are excluded from run geometry.
    pub source_slots: usize,
    /// Final shaped occurrences; zero for already-positioned batch geometry.
    pub positioned_slots: usize,
    /// Renderable actual outlines after whole original topology admission.
    pub outlines: usize,
    /// Complete original line/quadratic/cubic record inventory.
    pub segments: usize,
    /// Largest original point inventory required by either reusable scratch slice.
    pub scratch_points: usize,
}
/// Explicit caller-owned temporary point storage. Rust borrows establish complete
/// source/output/scratch disjointness, including unused caller tails.
pub struct NativeHintedOutlineScratch<'a> {
    /// Original on-curve topology copy; unused tail remains untouched.
    pub topology: &'a mut [OutlinePoint],
    /// Exact physical Y-up points, with original phase already included once.
    pub physical_points: &'a mut [[f32; 2]],
}
/// Original native renderer outline descriptor. No-ink slots have no descriptor
/// here; their source/occurrence index remains NATIVE_HINTED_NO_OUTLINE.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeHintedOutline {
    /// First original path record in the complete geometry inventory.
    pub segment_offset: usize,
    /// Original emitted segment count.
    pub segment_count: usize,
    /// Original minX/minY/maxX/maxY over all exact captured control points.
    pub bounds: [f32; 4],
    /// Original constant physical raster scale, exactly 1.
    pub raster_scale: f32,
    /// Original constant separate phase, exactly zero; actual phase is in points.
    pub subpixel_x: f32,
}
impl NativeHintedOutline {
    /// Convert a descriptor produced by this writer to its canonical GPU record.
    /// Arbitrary caller-constructed descriptors still receive full count admission.
    pub fn gpu_record(self) -> Result<GpuGlyphRecord, NativeHintedOutlineError> {
        if self.raster_scale != 1.0
            || self.subpixel_x != 0.0
            || self.bounds.iter().any(|v| !v.is_finite())
        {
            return Err(NativeHintedOutlineError::InvalidArgument);
        }
        Ok(GpuGlyphRecord {
            start_segment: u32::try_from(self.segment_offset)
                .map_err(|_| NativeHintedOutlineError::InsufficientCapacity)?,
            segment_count: u32::try_from(self.segment_count)
                .map_err(|_| NativeHintedOutlineError::InsufficientCapacity)?,
            bounds: self.bounds,
            padding: [0; 2],
        })
    }
}
fn select_simd(policy: NativeHintedProjection) -> Result<bool, NativeHintedOutlineError> {
    match policy {
        NativeHintedProjection::ScalarReference => Ok(false),
        NativeHintedProjection::Automatic | NativeHintedProjection::IntrinsicSimd
            if cfg!(any(
                target_arch = "aarch64",
                target_arch = "x86_64",
                all(target_arch = "x86", target_feature = "sse2")
            )) =>
        {
            Ok(true)
        }
        _ => Err(NativeHintedOutlineError::UnsupportedPolicy),
    }
}
fn physical(point: [i64; 2]) -> [f32; 2] {
    point.map(|v| v as f32 * (1.0 / 64.0))
}
fn midpoint(left: [f32; 2], right: [f32; 2]) -> [f32; 2] {
    [(left[0] + right[0]) * 0.5, (left[1] + right[1]) * 0.5]
}
fn exact_point(point: [i64; 2]) -> bool {
    point
        .iter()
        .all(|&v| (i64::from(i32::MIN)..=i64::from(i32::MAX)).contains(&v))
        && physical(point)
            .iter()
            .zip(point)
            .all(|(&v, raw)| f64::from(v) == raw as f64 / 64.0)
}
fn exact_midpoint(left: [i64; 2], right: [i64; 2]) -> bool {
    let value = midpoint(physical(left), physical(right));
    (0..2).all(|i| f64::from(value[i]) == (left[i] as f64 + right[i] as f64) / 128.0)
}
fn kind(tag: u8) -> u8 {
    tag & 3
}
#[derive(Clone, Copy, Debug, Default)]
struct Info {
    segments: usize,
    bounds: [f32; 4],
    renderable: bool,
}
fn inspect_glyph(
    glyph: &NativeHintedGlyph,
    coverage: NativeHintedCoverage,
) -> Result<Info, NativeHintedOutlineError> {
    use NativeHintedOutlineError as E;
    let monochrome = coverage == NativeHintedCoverage::Monochrome;
    let antialiased = coverage == NativeHintedCoverage::AntialiasedVector || monochrome;
    let vector = coverage == NativeHintedCoverage::NonzeroVector || antialiased;
    let outline_flags = 0x01
        | 0x04
        | if vector { 0x08 | 0x100 } else { 0 }
        | if antialiased { 0x10 | 0x20 } else { 0 };
    let tag_bits: u8 =
        0x03 | 0x18 | if vector { 0x04 } else { 0 } | if antialiased { 0xe0 } else { 0 };
    if glyph.outline_flags < 0 || glyph.outline_flags & !outline_flags != 0 {
        return Err(E::UnsupportedFlags);
    }
    if glyph.points_26_6.len() != glyph.tags.len()
        || glyph.points_26_6.len() > 32768
        || glyph.points_26_6.is_empty() != glyph.contour_ends.is_empty()
    {
        return Err(E::InvalidTopology);
    }
    let mut info = Info::default();
    for (index, (&point, &tag)) in glyph.points_26_6.iter().zip(&glyph.tags).enumerate() {
        if tag & !tag_bits != 0 || antialiased && tag & 0xe0 != 0 && tag & 0x04 == 0 {
            return Err(E::UnsupportedFlags);
        }
        if kind(tag) == 3 {
            return Err(E::InvalidTopology);
        }
        if monochrome && kind(tag) == 2 {
            return Err(E::UnsupportedPolicy);
        }
        if !exact_point(point) {
            return Err(E::UnsupportedCoordinates);
        }
        let point = physical(point);
        if index == 0 {
            info.bounds = [point[0], point[1], point[0], point[1]];
        } else {
            info.bounds[0] = info.bounds[0].min(point[0]);
            info.bounds[1] = info.bounds[1].min(point[1]);
            info.bounds[2] = info.bounds[2].max(point[0]);
            info.bounds[3] = info.bounds[3].max(point[1]);
        }
    }
    let mut start = 0;
    for &raw_end in &glyph.contour_ends {
        if raw_end < 0 || (raw_end as usize) < start || raw_end as usize >= glyph.points_26_6.len()
        {
            return Err(E::InvalidTopology);
        }
        let count = raw_end as usize + 1 - start;
        let points = &glyph.points_26_6[start..start + count];
        let tags = &glyph.tags[start..start + count];
        start += count;
        if vector && tags[1..].iter().any(|&tag| tag & 0x04 != 0) {
            return Err(E::UnsupportedFlags);
        }
        if kind(tags[0]) == 2 || kind(tags[0]) == 0 && kind(tags[count - 1]) == 2 {
            return Err(E::InvalidTopology);
        }
        if count < 2 {
            continue;
        }
        if kind(tags[0]) == 0
            && kind(tags[count - 1]) == 0
            && !exact_midpoint(points[0], points[count - 1])
        {
            return Err(E::UnsupportedCoordinates);
        }
        let mut index = usize::from(kind(tags[0]) == 1);
        let mut processed = 0;
        while processed < count {
            let current = index % count;
            match kind(tags[current]) {
                1 => {
                    index += 1;
                    processed += 1;
                }
                0 => {
                    let next = (index + 1) % count;
                    if kind(tags[next]) == 2 {
                        return Err(E::InvalidTopology);
                    }
                    if kind(tags[next]) == 1 {
                        index += 2;
                        processed += 2;
                    } else {
                        if !exact_midpoint(points[current], points[next]) {
                            return Err(E::UnsupportedCoordinates);
                        }
                        index += 1;
                        processed += 1;
                    }
                }
                _ => {
                    if kind(tags[(index + 1) % count]) != 2 || kind(tags[(index + 2) % count]) != 1
                    {
                        return Err(E::InvalidTopology);
                    }
                    index += 3;
                    processed += 3;
                }
            }
            info.segments += 1;
        }
    }
    if start != glyph.points_26_6.len() {
        return Err(E::InvalidTopology);
    }
    info.renderable =
        info.segments != 0 && info.bounds[2] > info.bounds[0] && info.bounds[3] > info.bounds[1];
    Ok(info)
}
fn inspect_batch(
    batch: &NativeHintedBatch,
    source_count: usize,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    use NativeHintedOutlineError as E;
    if batch.request().phase_26_6.iter().any(|&v| v >= 64)
        || source_count > batch.glyphs().len()
        || source_count > u32::MAX as usize
    {
        return Err(E::InvalidRun);
    }
    let mut required = NativeHintedOutlineRequirements {
        source_slots: source_count,
        ..Default::default()
    };
    for glyph in &batch.glyphs()[..source_count] {
        let info = inspect_glyph(glyph, coverage)?;
        required.scratch_points = required.scratch_points.max(glyph.points_26_6.len());
        if info.renderable {
            if info.segments > u32::MAX as usize - required.segments {
                return Err(E::InsufficientCapacity);
            }
            required.outlines += 1;
            required.segments += info.segments;
        }
    }
    Ok(required)
}
fn inspect_run(
    run: &NativeDeviceRun,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    if run.source_descriptor_count() > run.batch().glyphs().len()
        || run.source_descriptor_count() > u32::MAX as usize
        || run.descriptor_indices().len() != run.glyphs().len()
    {
        return Err(NativeHintedOutlineError::InvalidRun);
    }
    for (glyph, &descriptor) in run.glyphs().iter().zip(run.descriptor_indices()) {
        if descriptor as usize >= run.source_descriptor_count()
            || run.batch().glyphs()[descriptor as usize].glyph_id != glyph.glyph_id
        {
            return Err(NativeHintedOutlineError::InvalidRun);
        }
    }
    let mut required = inspect_batch(run.batch(), run.source_descriptor_count(), coverage)?;
    required.positioned_slots = run.glyphs().len();
    Ok(required)
}
/// Query every original batch descriptor, including no-ink slots, before writing.
/// Already-positioned batch geometry admits the explicit monochrome contract.
pub fn native_hinted_batch_outline_requirements(
    batch: &NativeHintedBatch,
    policy: NativeHintedProjection,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    select_simd(policy)?;
    inspect_batch(batch, batch.glyphs().len(), coverage)
}
/// Query the actual run prefix and every final descriptor occurrence. Original
/// run geometry excludes monochrome; it is not silently upgraded from a batch.
pub fn native_hinted_run_outline_requirements(
    run: &NativeDeviceRun,
    policy: NativeHintedProjection,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    select_simd(policy)?;
    if coverage == NativeHintedCoverage::Monochrome {
        return Err(NativeHintedOutlineError::UnsupportedPolicy);
    }
    inspect_run(run, coverage)
}
fn convert_points(input: &[[i64; 2]], output: &mut [[f32; 2]], simd: bool) {
    let mut index = 0;
    if simd {
        while input.len() - index >= 2 {
            let lanes = wide::i32x4::new([
                input[index][0] as i32,
                input[index][1] as i32,
                input[index + 1][0] as i32,
                input[index + 1][1] as i32,
            ]);
            let values = (lanes.round_float() * wide::f32x4::splat(1.0 / 64.0)).to_array();
            output[index] = [values[0], values[1]];
            output[index + 1] = [values[2], values[3]];
            index += 2;
        }
    }
    for (point, target) in input[index..].iter().zip(&mut output[index..]) {
        *target = physical(*point);
    }
}
fn write_contour(
    tags: &[u8],
    topology: &[OutlinePoint],
    points: &[[f32; 2]],
    segments: &mut [Segment],
    cubic: bool,
) -> usize {
    let count = points.len();
    let mut index = 0;
    let mut processed = 0;
    let mut written = 0;
    let on_curve = |i: usize| {
        if cubic {
            kind(tags[i]) == 1
        } else {
            topology[i].on_curve()
        }
    };
    let mut current = if on_curve(0) {
        index = 1;
        points[0]
    } else if on_curve(count - 1) {
        points[count - 1]
    } else {
        midpoint(points[0], points[count - 1])
    };
    while processed < count {
        let selected = index % count;
        let selected_kind = if cubic {
            kind(tags[selected])
        } else {
            u8::from(on_curve(selected))
        };
        if selected_kind == 1 {
            let end = points[selected];
            segments[written] = Segment::line(current, end);
            current = end;
            index += 1;
            processed += 1;
        } else if selected_kind == 0 {
            let next = (index + 1) % count;
            let control = points[selected];
            let end = if on_curve(next) {
                index += 2;
                processed += 2;
                points[next]
            } else {
                index += 1;
                processed += 1;
                midpoint(control, points[next])
            };
            segments[written] = Segment::quadratic([current, control, end]);
            current = end;
        } else {
            let first = points[selected];
            let second = points[(index + 1) % count];
            let end = points[(index + 2) % count];
            segments[written] = Segment::cubic([current, first, second, end]);
            current = end;
            index += 3;
            processed += 3;
        }
        written += 1;
    }
    written
}
fn write_glyph(
    glyph: &NativeHintedGlyph,
    scratch: &mut NativeHintedOutlineScratch<'_>,
    segments: &mut [Segment],
    simd: bool,
    monochrome: bool,
) {
    convert_points(&glyph.points_26_6, scratch.physical_points, simd);
    for (tag, point) in glyph.tags.iter().zip(scratch.topology.iter_mut()) {
        *point = OutlinePoint {
            x: 0,
            y: 0,
            flags: u8::from(kind(*tag) == 1),
        };
    }
    let mut start = 0;
    let mut written = 0;
    let mut scan_mode = if glyph.outline_flags & 0x08 != 0 {
        2
    } else {
        (if glyph.outline_flags & 0x10 != 0 {
            4
        } else {
            0
        }) | (if glyph.outline_flags & 0x20 != 0 {
            0
        } else {
            1
        })
    };
    for &raw_end in &glyph.contour_ends {
        let count = raw_end as usize + 1 - start;
        let tags = &glyph.tags[start..start + count];
        let points = &scratch.physical_points[start..start + count];
        let topology = &scratch.topology[start..start + count];
        start += count;
        if tags[0] & 0x04 != 0 {
            scan_mode = u32::from((tags[0] & 0xe0) >> 5);
        }
        if count < 2 {
            continue;
        }
        let contour_start = written;
        written += write_contour(
            tags,
            topology,
            points,
            &mut segments[written..],
            tags.iter().any(|&tag| kind(tag) == 2),
        );
        if monochrome {
            for segment in &mut segments[contour_start..written] {
                segment.parameters = [
                    0x100
                        | scan_mode
                        | ((glyph.outline_flags as u32) << 16)
                        | if tags[0] & 0x04 != 0 { 0x200 } else { 0 },
                    contour_start as u32,
                    (written - contour_start) as u32,
                ];
            }
        }
    }
}
fn capacity(
    required: NativeHintedOutlineRequirements,
    scratch: &NativeHintedOutlineScratch<'_>,
    outlines: &[NativeHintedOutline],
    segments: &[Segment],
    source_indices: &[u32],
    positioned_capacity: usize,
) -> Result<(), NativeHintedOutlineError> {
    if scratch.topology.len() < required.scratch_points
        || scratch.physical_points.len() < required.scratch_points
        || outlines.len() < required.outlines
        || segments.len() < required.segments
        || source_indices.len() < required.source_slots
        || positioned_capacity < required.positioned_slots
    {
        return Err(NativeHintedOutlineError::InsufficientCapacity);
    }
    Ok(())
}
fn write_batch(
    batch: &NativeHintedBatch,
    required: NativeHintedOutlineRequirements,
    mut scratch: NativeHintedOutlineScratch<'_>,
    outlines: &mut [NativeHintedOutline],
    segments: &mut [Segment],
    source_indices: &mut [u32],
    simd: bool,
    coverage: NativeHintedCoverage,
) {
    let mut outline = 0;
    let mut offset = 0;
    for (index, glyph) in batch.glyphs()[..required.source_slots].iter().enumerate() {
        // Complete immutable first-pass admission proves the same second walk.
        let info = inspect_glyph(glyph, coverage).expect("admitted immutable native outline");
        if !info.renderable {
            source_indices[index] = NATIVE_HINTED_NO_OUTLINE;
            continue;
        }
        write_glyph(
            glyph,
            &mut scratch,
            &mut segments[offset..offset + info.segments],
            simd,
            coverage == NativeHintedCoverage::Monochrome,
        );
        outlines[outline] = NativeHintedOutline {
            segment_offset: offset,
            segment_count: info.segments,
            bounds: info.bounds,
            raster_scale: 1.0,
            subpixel_x: 0.0,
        };
        source_indices[index] = outline as u32;
        outline += 1;
        offset += info.segments;
    }
}
/// Write one exact complete batch atomically. All topology, policy and full
/// capacities are checked before touching any output or scratch; tails remain
/// untouched. No allocation, interpreter call, shaping or GPU work occurs.
pub fn write_native_hinted_batch_outlines(
    batch: &NativeHintedBatch,
    scratch: NativeHintedOutlineScratch<'_>,
    outlines: &mut [NativeHintedOutline],
    segments: &mut [Segment],
    source_indices: &mut [u32],
    policy: NativeHintedProjection,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    let simd = select_simd(policy)?;
    let required = inspect_batch(batch, batch.glyphs().len(), coverage)?;
    capacity(required, &scratch, outlines, segments, source_indices, 0)?;
    write_batch(
        batch,
        required,
        scratch,
        outlines,
        segments,
        source_indices,
        simd,
        coverage,
    );
    Ok(required)
}
/// Write the original run prefix and original final occurrence mapping atomically.
/// Auxiliary descriptors never become source outlines; repeat/reversal preserves
/// the retained descriptor identity. Monochrome remains batch-only.
pub fn write_native_hinted_run_outlines(
    run: &NativeDeviceRun,
    scratch: NativeHintedOutlineScratch<'_>,
    outlines: &mut [NativeHintedOutline],
    segments: &mut [Segment],
    source_indices: &mut [u32],
    positioned_indices: &mut [u32],
    policy: NativeHintedProjection,
    coverage: NativeHintedCoverage,
) -> Result<NativeHintedOutlineRequirements, NativeHintedOutlineError> {
    let simd = select_simd(policy)?;
    if coverage == NativeHintedCoverage::Monochrome {
        return Err(NativeHintedOutlineError::UnsupportedPolicy);
    }
    let required = inspect_run(run, coverage)?;
    capacity(
        required,
        &scratch,
        outlines,
        segments,
        source_indices,
        positioned_indices.len(),
    )?;
    write_batch(
        run.batch(),
        required,
        scratch,
        outlines,
        segments,
        source_indices,
        simd,
        coverage,
    );
    for (target, &descriptor) in positioned_indices.iter_mut().zip(run.descriptor_indices()) {
        *target = source_indices[descriptor as usize];
    }
    Ok(required)
}
