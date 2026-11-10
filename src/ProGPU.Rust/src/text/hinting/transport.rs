//! Original fixed-width whole-batch transport, independent of outline conversion.
use super::*;
use bytemuck::{Pod, Zeroable};

/// Exact original 16-byte fixed-width point; no phase or unit conversion.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Pod, Zeroable)]
pub struct NativeHintedPointRecord {
    /// Original raw horizontal 26.6 coordinate.
    pub x_26_6: i64,
    /// Original raw vertical 26.6 coordinate.
    pub y_26_6: i64,
}
/// Exact original 136-byte descriptor. Tags share the point offset; contour end
/// values stay original glyph-local indices. No font execution occurs on copy.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Pod, Zeroable)]
pub struct NativeHintedGlyphRecord {
    /// Exact original glyph ID, retaining repeated/no-ink descriptors.
    pub glyph_index: u32,
    /// First original point and tag.
    pub point_offset: u32,
    /// Complete original point/tag count.
    pub point_count: u32,
    /// First original contour end.
    pub contour_offset: u32,
    /// Complete original contour count.
    pub contour_count: u32,
    /// Exact original raw outline-flag bits.
    pub outline_flags: u32,
    /// Original slot X advance.
    pub advance_x_26_6: i64,
    /// Original slot Y advance.
    pub advance_y_26_6: i64,
    /// Original horizontal X bearing.
    pub horizontal_bearing_x_26_6: i64,
    /// Original horizontal Y bearing.
    pub horizontal_bearing_y_26_6: i64,
    /// Original width.
    pub width_26_6: i64,
    /// Original height.
    pub height_26_6: i64,
    /// Original horizontal metric advance.
    pub horizontal_advance_26_6: i64,
    /// Original vertical X bearing.
    pub vertical_bearing_x_26_6: i64,
    /// Original vertical Y bearing.
    pub vertical_bearing_y_26_6: i64,
    /// Original vertical metric advance.
    pub vertical_advance_26_6: i64,
    /// Original linear horizontal advance.
    pub linear_horizontal_advance_16_16: i64,
    /// Original linear vertical advance.
    pub linear_vertical_advance_16_16: i64,
    /// Original left side-bearing delta.
    pub left_side_bearing_delta_26_6: i64,
    /// Original right side-bearing delta.
    pub right_side_bearing_delta_26_6: i64,
}
/// Exact original complete fixed-width transport counts.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Pod, Zeroable)]
pub struct NativeHintedBatchCounts {
    /// Original complete descriptor count.
    pub glyphs: u32,
    /// Original complete point/tag count.
    pub points: u32,
    /// Original complete contour-end count.
    pub contours: u32,
}
const _: () = {
    assert!(size_of::<NativeHintedPointRecord>() == 16);
    assert!(std::mem::offset_of!(NativeHintedPointRecord, y_26_6) == 8);
    assert!(size_of::<NativeHintedGlyphRecord>() == 136);
    assert!(std::mem::offset_of!(NativeHintedGlyphRecord, advance_x_26_6) == 24);
    assert!(size_of::<NativeHintedBatchCounts>() == 12);
};
/// Original independent transport selection.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeHintedTransportPolicy {
    /// Bulk-copy the canonical owned i64 point storage and widen contour ends.
    #[default]
    Automatic,
    /// Require original admitted native SIMD architecture.
    IntrinsicSimd,
    /// Explicit fieldwise exact integer reference copy.
    ScalarReference,
}
/// Actual selected copy path. Canonical Rust point storage is always i64, so
/// Automatic uses the original long64 bulk route, including on Windows.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeHintedTransportPath {
    BulkCopy,
    IntrinsicSimd,
    ScalarReference,
}
/// Original fixed-width batch copy error domain.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeHintedTransportError {
    /// Invalid caller request.
    InvalidArgument = 1,
    /// Original whole-batch topology/count inventory is invalid.
    InvalidBatch = 2,
    /// At least one complete caller output inventory is insufficient.
    InsufficientCapacity = 3,
    /// Explicit SIMD is unavailable on the original admitted architectures.
    UnsupportedPolicy = 4,
}
impl std::fmt::Display for NativeHintedTransportError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "native hinted transport: {self:?}")
    }
}
impl std::error::Error for NativeHintedTransportError {}
/// Whole original count/topology preflight. No output is published on a later
/// descriptor error or overflow; repeated/no-ink descriptors are retained.
pub fn native_hinted_batch_counts(
    batch: &NativeHintedBatch,
) -> Result<NativeHintedBatchCounts, NativeHintedTransportError> {
    use NativeHintedTransportError::InvalidBatch;
    let mut result = NativeHintedBatchCounts {
        glyphs: u32::try_from(batch.glyphs().len()).map_err(|_| InvalidBatch)?,
        ..Default::default()
    };
    for glyph in batch.glyphs() {
        if glyph.points_26_6.len() != glyph.tags.len()
            || glyph.points_26_6.len() > (u32::MAX - result.points) as usize
            || glyph.contour_ends.len() > (u32::MAX - result.contours) as usize
            || glyph.points_26_6.is_empty() != glyph.contour_ends.is_empty()
        {
            return Err(InvalidBatch);
        }
        let mut previous = -1;
        for &end in &glyph.contour_ends {
            if end <= previous || end as usize >= glyph.points_26_6.len() {
                return Err(InvalidBatch);
            }
            previous = end;
        }
        if !glyph.points_26_6.is_empty() && previous as usize != glyph.points_26_6.len() - 1 {
            return Err(InvalidBatch);
        }
        result.points += glyph.points_26_6.len() as u32;
        result.contours += glyph.contour_ends.len() as u32;
    }
    Ok(result)
}
fn select(
    policy: NativeHintedTransportPolicy,
) -> Result<NativeHintedTransportPath, NativeHintedTransportError> {
    match policy {
        NativeHintedTransportPolicy::Automatic => Ok(NativeHintedTransportPath::BulkCopy),
        NativeHintedTransportPolicy::ScalarReference => {
            Ok(NativeHintedTransportPath::ScalarReference)
        }
        NativeHintedTransportPolicy::IntrinsicSimd
            if cfg!(any(
                target_arch = "aarch64",
                target_arch = "x86_64",
                all(target_arch = "x86", target_feature = "sse2")
            )) =>
        {
            Ok(NativeHintedTransportPath::IntrinsicSimd)
        }
        _ => Err(NativeHintedTransportError::UnsupportedPolicy),
    }
}
fn copy_points(
    input: &[[i64; 2]],
    output: &mut [NativeHintedPointRecord],
    path: NativeHintedTransportPath,
) {
    if path == NativeHintedTransportPath::BulkCopy {
        output[..input.len()].copy_from_slice(bytemuck::cast_slice(input));
        return;
    }
    for (&point, target) in input.iter().zip(output) {
        let value = if path == NativeHintedTransportPath::IntrinsicSimd {
            wide::i64x2::new(point).to_array()
        } else {
            point
        };
        *target = NativeHintedPointRecord {
            x_26_6: value[0],
            y_26_6: value[1],
        };
    }
}
fn copy_contours(input: &[i16], output: &mut [i32], path: NativeHintedTransportPath) {
    let mut index = 0;
    if path != NativeHintedTransportPath::ScalarReference {
        while input.len() - index >= 8 {
            let lanes = wide::i16x8::new(
                input[index..index + 8]
                    .try_into()
                    .expect("bounded original contour lanes"),
            );
            output[index..index + 8].copy_from_slice(&wide::i32x8::from(lanes).to_array());
            index += 8;
        }
    }
    for (&value, target) in input[index..].iter().zip(&mut output[index..]) {
        *target = i32::from(value);
    }
}
/// Copy one complete immutable captured generation, without allocation, font
/// calls or GPU work. Whole topology and all capacities precede every write;
/// safe borrows exclude source/output aliases, and every unused tail is untouched.
pub fn copy_native_hinted_batch(
    batch: &NativeHintedBatch,
    glyphs: &mut [NativeHintedGlyphRecord],
    points: &mut [NativeHintedPointRecord],
    tags: &mut [u8],
    contours: &mut [i32],
    policy: NativeHintedTransportPolicy,
) -> Result<NativeHintedTransportPath, NativeHintedTransportError> {
    let path = select(policy)?;
    let counts = native_hinted_batch_counts(batch)?;
    if glyphs.len() < counts.glyphs as usize
        || points.len() < counts.points as usize
        || tags.len() < counts.points as usize
        || contours.len() < counts.contours as usize
    {
        return Err(NativeHintedTransportError::InsufficientCapacity);
    }
    let mut point_offset = 0;
    let mut contour_offset = 0;
    for (glyph, target) in batch.glyphs().iter().zip(glyphs) {
        let point_count = glyph.points_26_6.len() as u32;
        let contour_count = glyph.contour_ends.len() as u32;
        *target = NativeHintedGlyphRecord {
            glyph_index: glyph.glyph_id,
            point_offset,
            point_count,
            contour_offset,
            contour_count,
            outline_flags: glyph.outline_flags as u32,
            advance_x_26_6: glyph.advance_26_6[0],
            advance_y_26_6: glyph.advance_26_6[1],
            horizontal_bearing_x_26_6: glyph.horizontal_bearing_26_6[0],
            horizontal_bearing_y_26_6: glyph.horizontal_bearing_26_6[1],
            width_26_6: glyph.size_26_6[0],
            height_26_6: glyph.size_26_6[1],
            horizontal_advance_26_6: glyph.horizontal_advance_26_6,
            vertical_bearing_x_26_6: glyph.vertical_bearing_26_6[0],
            vertical_bearing_y_26_6: glyph.vertical_bearing_26_6[1],
            vertical_advance_26_6: glyph.vertical_advance_26_6,
            linear_horizontal_advance_16_16: glyph.linear_advances_16_16[0],
            linear_vertical_advance_16_16: glyph.linear_advances_16_16[1],
            left_side_bearing_delta_26_6: glyph.side_bearing_deltas_26_6[0],
            right_side_bearing_delta_26_6: glyph.side_bearing_deltas_26_6[1],
        };
        let point_range = point_offset as usize..(point_offset + point_count) as usize;
        copy_points(&glyph.points_26_6, &mut points[point_range.clone()], path);
        tags[point_range].copy_from_slice(&glyph.tags);
        copy_contours(
            &glyph.contour_ends,
            &mut contours[contour_offset as usize..(contour_offset + contour_count) as usize],
            path,
        );
        point_offset += point_count;
        contour_offset += contour_count;
    }
    Ok(path)
}
