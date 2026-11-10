//! Original explicit Unhinted/Monochrome paired capture and scan-only transfer.
use super::replay::{
    MAXIMUM_OUTLINES, MAXIMUM_SLOTS, copy_geometry, validate_dpi, validate_geometry,
    validate_positions,
};
use super::*;
const UNHINTED_SCAN_MASK: u32 = 7 | 0x200 | (0x38 << 16);
struct Snapshot {
    glyphs: Vec<NativeHintedGlyphRecord>,
    tags: Vec<u8>,
    ends: Vec<i32>,
    outlines: Vec<NativeHintedOutline>,
    descriptors: Vec<NativeFontOutlineDescriptor>,
    segments: Vec<Segment>,
}
fn read_snapshot(
    batch: &NativeHintedBatch,
    owner: &NativeFontOutlineGeometry,
) -> Result<Snapshot, FontPolicyError> {
    let counts = native_hinted_batch_counts(batch)?;
    let geometry = owner.counts();
    require(
        counts.glyphs as usize <= MAXIMUM_SLOTS
            && counts.points as usize <= MAXIMUM_SLOTS
            && counts.contours as usize <= MAXIMUM_SLOTS
            && geometry.descriptors == counts.glyphs
            && geometry.outlines as usize <= MAXIMUM_OUTLINES
            && geometry.segments as usize <= MAXIMUM_SLOTS,
    )?;
    let mut glyphs = filled(counts.glyphs as usize, NativeHintedGlyphRecord::default())?;
    let mut points = filled(counts.points as usize, NativeHintedPointRecord::default())?;
    let mut tags = filled(counts.points as usize, 0)?;
    let mut ends = filled(counts.contours as usize, 0)?;
    copy_native_hinted_batch(
        batch,
        &mut glyphs,
        &mut points,
        &mut tags,
        &mut ends,
        NativeHintedTransportPolicy::Automatic,
    )?;
    let (outlines, segments, descriptors) = copy_geometry(owner)?;
    validate_geometry(&outlines, &segments, true)?;
    Ok(Snapshot {
        glyphs,
        tags,
        ends,
        outlines,
        descriptors,
        segments,
    })
}
fn range(start: u32, count: u32, length: usize) -> bool {
    (start as usize) <= length && count as usize <= length - start as usize
}
// Private: only the public same-provider/same-font frozen-request factory below
// can supply a pair. Arbitrary externally assembled snapshots are never admitted.
fn transfer(original: &Snapshot, witness: &Snapshot) -> Result<Vec<Segment>, FontPolicyError> {
    require(
        original.glyphs.len() == witness.glyphs.len()
            && original.descriptors.len() == original.glyphs.len()
            && witness.descriptors.len() == original.glyphs.len()
            && original.tags.len() == witness.tags.len()
            && original.ends.len() == witness.ends.len()
            && original.outlines.len() == witness.outlines.len()
            && original.segments.len() == witness.segments.len(),
    )?;
    let mut next_point = 0u32;
    let mut next_contour = 0u32;
    for (index, (a, b)) in original.glyphs.iter().zip(&witness.glyphs).enumerate() {
        require(
            a.glyph_index == b.glyph_index
                && a.point_offset == b.point_offset
                && a.point_count == b.point_count
                && a.contour_offset == b.contour_offset
                && a.contour_count == b.contour_count
                && a.point_offset == next_point
                && a.contour_offset == next_contour
                && range(a.point_offset, a.point_count, original.tags.len())
                && range(a.contour_offset, a.contour_count, original.ends.len()),
        )?;
        let outline = original.descriptors[index].outline_index;
        require(
            original.descriptors[index].glyph_index == a.glyph_index
                && witness.descriptors[index].glyph_index == a.glyph_index
                && outline == witness.descriptors[index].outline_index
                && (outline == u32::MAX || (outline as usize) < original.outlines.len()),
        )?;
        for p in 0..a.point_count {
            require(
                original.tags[(a.point_offset + p) as usize] & 3
                    == witness.tags[(b.point_offset + p) as usize] & 3,
            )?;
        }
        let mut previous = -1;
        for c in 0..a.contour_count {
            let end = original.ends[(a.contour_offset + c) as usize];
            require(
                end > previous
                    && (end as u32) < a.point_count
                    && end == witness.ends[(b.contour_offset + c) as usize],
            )?;
            previous = end;
        }
        require(if a.point_count == 0 {
            a.contour_count == 0
        } else {
            i32::try_from(a.point_count)
                .ok()
                .is_some_and(|count| previous == count - 1)
        })?;
        next_point = next_point
            .checked_add(a.point_count)
            .ok_or(FontPolicyError::InvalidGeneration)?;
        next_contour = next_contour
            .checked_add(a.contour_count)
            .ok_or(FontPolicyError::InvalidGeneration)?;
    }
    require(
        next_point as usize == original.tags.len() && next_contour as usize == original.ends.len(),
    )?;
    for (a, b) in original.outlines.iter().zip(&witness.outlines) {
        require(a.segment_offset == b.segment_offset && a.segment_count == b.segment_count)?;
    }
    for (a, b) in original.segments.iter().zip(&witness.segments) {
        require(
            a.kind == b.kind
                && a.parameters[1] == b.parameters[1]
                && a.parameters[2] == b.parameters[2]
                && a.parameters[0] & 0x100 != 0
                && b.parameters[0] & 0x100 != 0,
        )?;
    }
    // Complete topology is proven before any output allocation/publication.
    // Copy exact original float component bits and bounds; only the documented
    // scan/dropout mask changes. Precision, ownership and other flags stay original.
    let mut segments = copy(&original.segments)?;
    for (target, witness) in segments.iter_mut().zip(&witness.segments) {
        target.parameters[0] = (target.parameters[0] & !UNHINTED_SCAN_MASK)
            | (witness.parameters[0] & UNHINTED_SCAN_MASK);
    }
    Ok(segments)
}
fn require_capture(
    batch: &NativeHintedBatch,
    font: &SfntFace,
    request: &NativeHintedRequest,
    ids: &[u32],
) -> Result<(), FontPolicyError> {
    require(
        batch.font().data().as_ptr() == font.data().as_ptr()
            && batch.font().data().len() == font.data().len()
            && batch.font().face_index() == font.face_index()
            && batch.request() == request
            && batch.glyphs().len() == ids.len()
            && batch
                .glyphs()
                .iter()
                .zip(ids)
                .all(|(glyph, &id)| glyph.glyph_id == id),
    )
}
/// Capture original instruction-free physical outlines and an independent actual
/// monochrome scan witness using the same exclusive provider and selected font.
/// All inputs freeze once; complete topology agreement precedes scan-only transfer.
/// Local witness owners retire before publication. The returned geometry retains
/// only the actual original Unhinted generation and unchanged caller placements.
/// This explicit composition does not select GDI/GDI+ or any source raster mode.
pub fn capture_unfitted_font_replay(
    provider: &mut dyn NativeHintedProvider,
    font: &SfntFace,
    glyph_ids: &[u32],
    positions: &[[f32; 2]],
    request: &NativeHintedRequest,
    dpi_scale: f32,
    projection: NativeHintedProjection,
) -> Result<FontReplayGeometry, FontPolicyError> {
    validate_dpi(dpi_scale)?;
    if glyph_ids.len() > MAXIMUM_SLOTS
        || positions.len() != glyph_ids.len()
        || request.variation_coordinates_16_16.len() > 1 << 20
        || !matches!(
            projection,
            NativeHintedProjection::Automatic
                | NativeHintedProjection::IntrinsicSimd
                | NativeHintedProjection::ScalarReference
        )
    {
        return Err(FontPolicyError::InvalidArgument);
    }
    let ids = copy(glyph_ids)?;
    let positions = copy(positions)?;
    let coordinates = copy(&request.variation_coordinates_16_16)?;
    validate_positions(&positions)?;
    let frozen = NativeHintedRequest {
        pixels_per_em_26_6: request.pixels_per_em_26_6,
        hint_policy: request.hint_policy,
        phase_26_6: request.phase_26_6,
        variation_coordinates_16_16: coordinates,
        outline_policy: NativeOutlineLoadPolicy::Unhinted,
    };
    let witness_request = NativeHintedRequest {
        pixels_per_em_26_6: frozen.pixels_per_em_26_6,
        hint_policy: frozen.hint_policy,
        phase_26_6: frozen.phase_26_6,
        variation_coordinates_16_16: copy(&frozen.variation_coordinates_16_16)?,
        outline_policy: NativeOutlineLoadPolicy::Monochrome,
    };
    // The scope mirrors original using lifetimes: witness geometry and both local
    // batch handles retire before validating/converting/publishing the result.
    let (owner, original, segments) = {
        let raw = provider.capture(font, &frozen, &ids)?;
        require_capture(&raw, font, &frozen, &ids)?;
        let witness = provider.capture(font, &witness_request, &ids)?;
        require_capture(&witness, font, &witness_request, &ids)?;
        let witness_owner = NativeFontOutlineGeometry::new(
            Arc::clone(&witness),
            projection,
            NativeHintedCoverage::Monochrome,
        )?;
        let owner = Arc::new(NativeFontOutlineGeometry::new(
            Arc::clone(&raw),
            projection,
            NativeHintedCoverage::Monochrome,
        )?);
        let original = read_snapshot(&raw, &owner)?;
        let scan = read_snapshot(&witness, &witness_owner)?;
        require(original.glyphs.len() == ids.len())?;
        for (glyph, &id) in original.glyphs.iter().zip(&ids) {
            require(glyph.glyph_index == id)?;
        }
        let segments = transfer(&original, &scan)?;
        (owner, original, segments)
    };
    validate_geometry(&original.outlines, &segments, true)?;
    FontReplayGeometry::publish(
        owner,
        dpi_scale,
        NativeHintedCoverage::Monochrome,
        original.outlines,
        segments,
        original.descriptors,
        positions,
    )
}
