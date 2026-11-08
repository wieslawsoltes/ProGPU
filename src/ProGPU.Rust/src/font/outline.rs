//! Original simple TrueType point decoding and contour-to-path lowering.
use super::{FontError, GlyphData, SfntFace, bytes};
use crate::path::Segment;
use std::ops::Range;

/// Original glyph record classification, before recursive composite inspection.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum GlyphKind {
    /// Empty loca slot or zero-contour record.
    #[default]
    Empty,
    /// Positive-contour TrueType record.
    Simple,
    /// Negative-contour component record, requiring separate expansion.
    Composite,
}

/// Complete original simple-record requirements; composite records defer decoding.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GlyphRequirements {
    /// Native empty/simple/composite classification.
    pub kind: GlyphKind,
    /// Original contour endpoint slots.
    pub contour_count: u16,
    /// Expanded original points, including single-point contours.
    pub point_count: u32,
    /// Exact original contour writer's segment count.
    pub path_segment_count: u32,
    /// Original instruction payload length, without execution.
    pub instruction_bytes: u16,
}

/// Original integer design coordinate and complete expanded flag byte.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct OutlinePoint {
    /// Accumulated signed X coordinate.
    pub x: i32,
    /// Accumulated signed Y coordinate.
    pub y: i32,
    /// Complete source flag, retaining repeat and reserved bits.
    pub flags: u8,
}
impl OutlinePoint {
    /// Whether the original flag marks a point on the curve.
    pub fn on_curve(self) -> bool {
        self.flags & 1 != 0
    }
    fn position(self) -> [f32; 2] {
        [self.x as f32, self.y as f32]
    }
}

/// Immutable decoded simple glyph retaining the exact selected source generation.
/// Instructions are preserved but never executed. This is not a hinted outline,
/// a shaping result or a source font raster policy.
#[derive(Clone, Debug)]
pub struct SimpleGlyph {
    source: SfntFace,
    glyph_index: u16,
    requirements: GlyphRequirements,
    instructions: Range<usize>,
    contours: Vec<u16>,
    points: Vec<OutlinePoint>,
}
impl SimpleGlyph {
    /// Exact immutable source face retained by this geometry.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original glyph index, without a fabricated character mapping.
    pub fn glyph_index(&self) -> u16 {
        self.glyph_index
    }
    /// Complete original point and segment requirements.
    pub fn requirements(&self) -> GlyphRequirements {
        self.requirements
    }
    /// Original strictly increasing inclusive contour endpoints.
    pub fn contour_end_points(&self) -> &[u16] {
        &self.contours
    }
    /// Original expanded flags and unscaled design coordinates.
    pub fn points(&self) -> &[OutlinePoint] {
        &self.points
    }
    /// Borrowed original instruction bytes, preserved through source retirement.
    pub fn instructions(&self) -> &[u8] {
        &self
            .source
            .glyph_data(self.glyph_index)
            .expect("immutable validated glyph")
            .bytes[self.instructions.clone()]
    }
    /// Lower original contours to exact canonical line/quadratic path records.
    /// Single-point contours retain points but emit no segments.
    pub fn segments(&self) -> Result<Vec<Segment>, FontError> {
        self.write_segments(&[])
    }
    /// Lower the same original topology using explicit corresponding positions.
    /// An empty position inventory retains original coordinates, as in native.
    /// Nonempty inventories must match every point, including single-point contours.
    pub fn segments_with_positions(
        &self,
        positions: &[[f32; 2]],
    ) -> Result<Vec<Segment>, FontError> {
        self.write_segments(positions)
    }
    fn write_segments(&self, positions: &[[f32; 2]]) -> Result<Vec<Segment>, FontError> {
        if !positions.is_empty() && positions.len() != self.points.len() {
            return Err(FontError::InvalidArgument);
        }
        let mut result = Vec::new();
        result
            .try_reserve_exact(self.requirements.path_segment_count as usize)
            .map_err(|_| FontError::Capacity)?;
        append_segments(&self.contours, &self.points, positions, &mut result);
        debug_assert_eq!(result.len(), self.requirements.path_segment_count as usize);
        Ok(result)
    }
}
// Callers preflight topology and reserve the complete output before mutation.
pub(super) fn append_segments(
    contours: &[u16],
    points: &[OutlinePoint],
    positions: &[[f32; 2]],
    result: &mut Vec<Segment>,
) {
    let mut start = 0;
    for &end in contours {
        let end = usize::from(end) + 1;
        let contour = &points[start..end];
        let varied = if positions.is_empty() {
            &[][..]
        } else {
            &positions[start..end]
        };
        start = end;
        let count = contour.len();
        if count < 2 {
            continue;
        }
        let coordinate = |i: usize| {
            if varied.is_empty() {
                contour[i].position()
            } else {
                varied[i]
            }
        };
        let mut index = 0;
        let mut current = if contour[0].on_curve() {
            index = 1;
            coordinate(0)
        } else if contour[count - 1].on_curve() {
            coordinate(count - 1)
        } else {
            midpoint(coordinate(0), coordinate(count - 1))
        };
        let mut processed = 0;
        while processed < count {
            let slot = index % count;
            if contour[slot].on_curve() {
                let end = coordinate(slot);
                result.push(Segment::line(current, end));
                current = end;
                index += 1;
                processed += 1;
            } else {
                let next = (index + 1) % count;
                let control = coordinate(slot);
                let end = if contour[next].on_curve() {
                    index += 2;
                    processed += 2;
                    coordinate(next)
                } else {
                    index += 1;
                    processed += 1;
                    midpoint(control, coordinate(next))
                };
                result.push(Segment::quadratic([current, control, end]));
                current = end;
            }
        }
    }
}
fn midpoint(left: [f32; 2], right: [f32; 2]) -> [f32; 2] {
    [(left[0] + right[0]) * 0.5, (left[1] + right[1]) * 0.5]
}

struct Layout {
    requirements: GlyphRequirements,
    instructions: Range<usize>,
    flags: usize,
    x: usize,
    y: usize,
}
// Allocation-free whole-record preflight. Counts are bounded by the original
// 16-bit contour endpoints, so coordinate accumulation fits signed 32 bits.
fn inspect(glyph: GlyphData<'_>) -> Option<Layout> {
    if glyph.contour_count <= 0 || glyph.bytes.len() < 10 {
        return None;
    }
    let data = glyph.bytes;
    let contour_count = glyph.contour_count as u16;
    let instruction_length = 10 + usize::from(contour_count) * 2;
    bytes::bytes(data, 10, usize::from(contour_count) * 2)?;
    let mut previous = 0;
    for index in 0..usize::from(contour_count) {
        let end = bytes::u16(data, 10 + index * 2)?;
        if index > 0 && end <= previous {
            return None;
        }
        previous = end;
    }
    let point_count = u32::from(previous) + 1;
    let instruction_bytes = bytes::u16(data, instruction_length)?;
    let instruction_start = instruction_length + 2;
    bytes::bytes(data, instruction_start, usize::from(instruction_bytes))?;
    let flags = instruction_start + usize::from(instruction_bytes);
    let mut cursor = flags;
    let (mut expanded, mut contour_index, mut contour_start, mut transitions) =
        (0u32, 0usize, 0u32, 0u32);
    let (mut first_on, mut previous_on) = (false, false);
    let (mut x_bytes, mut y_bytes, mut segments) = (0usize, 0usize, 0u32);
    while expanded < point_count {
        let flag = *data.get(cursor)?;
        cursor += 1;
        let mut copies = 1u32;
        if flag & 8 != 0 {
            copies += u32::from(*data.get(cursor)?);
            cursor += 1;
        }
        if copies > point_count - expanded {
            return None;
        }
        let width = |short, same| {
            if flag & short != 0 {
                1
            } else if flag & same != 0 {
                0
            } else {
                2
            }
        };
        x_bytes += copies as usize * width(2, 16);
        y_bytes += copies as usize * width(4, 32);
        for _ in 0..copies {
            let on = flag & 1 != 0;
            if expanded == contour_start {
                first_on = on;
                previous_on = on;
                transitions = 0;
            } else {
                if !previous_on && on {
                    transitions += 1;
                }
                previous_on = on;
            }
            expanded += 1;
            if expanded - 1 == u32::from(bytes::u16(data, 10 + contour_index * 2)?) {
                let count = expanded - contour_start;
                if count >= 2 {
                    if !previous_on && first_on {
                        transitions += 1;
                    }
                    segments += count - transitions;
                }
                contour_start = expanded;
                contour_index += 1;
            }
        }
    }
    let x = cursor;
    bytes::bytes(data, x, x_bytes)?;
    let y = x + x_bytes;
    bytes::bytes(data, y, y_bytes)?;
    Some(Layout {
        requirements: GlyphRequirements {
            kind: GlyphKind::Simple,
            contour_count,
            point_count,
            path_segment_count: segments,
            instruction_bytes,
        },
        instructions: instruction_start..flags,
        flags,
        x,
        y,
    })
}

impl SfntFace {
    /// Inspect the entire original simple glyph without allocation or publication.
    /// Empty and composite records retain native classification semantics.
    pub fn glyph_requirements(&self, glyph_index: u16) -> Result<GlyphRequirements, FontError> {
        let glyph = self
            .glyph_data(glyph_index)
            .ok_or(FontError::InvalidGlyph)?;
        if glyph.bytes.is_empty() || glyph.contour_count == 0 {
            return Ok(GlyphRequirements::default());
        }
        if glyph.contour_count < 0 {
            return Ok(GlyphRequirements {
                kind: GlyphKind::Composite,
                ..Default::default()
            });
        }
        inspect(glyph)
            .map(|l| l.requirements)
            .ok_or(FontError::InvalidGlyph)
    }
    /// Atomically decode a positive-contour record in original design units.
    /// Empty/composite records require their independent outline route.
    pub fn simple_glyph(&self, glyph_index: u16) -> Result<SimpleGlyph, FontError> {
        let glyph = self
            .glyph_data(glyph_index)
            .ok_or(FontError::InvalidGlyph)?;
        let layout = inspect(glyph).ok_or(FontError::InvalidGlyph)?;
        let mut contours = Vec::new();
        let mut points = Vec::new();
        decode(glyph, &layout, &mut contours, &mut points)?;
        Ok(SimpleGlyph {
            source: self.clone(),
            glyph_index,
            requirements: layout.requirements,
            instructions: layout.instructions,
            contours,
            points,
        })
    }
}

fn decode(
    glyph: GlyphData<'_>,
    layout: &Layout,
    contours: &mut Vec<u16>,
    points: &mut Vec<OutlinePoint>,
) -> Result<(), FontError> {
    contours.clear();
    contours
        .try_reserve_exact(usize::from(layout.requirements.contour_count))
        .map_err(|_| FontError::Capacity)?;
    for index in 0..usize::from(layout.requirements.contour_count) {
        contours.push(bytes::u16(glyph.bytes, 10 + index * 2).ok_or(FontError::InvalidGlyph)?);
    }
    points.clear();
    points
        .try_reserve_exact(layout.requirements.point_count as usize)
        .map_err(|_| FontError::Capacity)?;
    let mut cursor = layout.flags;
    while points.len() < layout.requirements.point_count as usize {
        let flag = glyph.bytes[cursor];
        cursor += 1;
        let mut copies = 1;
        if flag & 8 != 0 {
            copies += usize::from(glyph.bytes[cursor]);
            cursor += 1;
        }
        points.extend(std::iter::repeat_n(
            OutlinePoint {
                flags: flag,
                ..Default::default()
            },
            copies,
        ));
    }
    for (axis, mut cursor) in [layout.x, layout.y].into_iter().enumerate() {
        let mut value = 0i32;
        for point in points.iter_mut() {
            if point.flags & (2 << axis) != 0 {
                let delta = i32::from(glyph.bytes[cursor]);
                cursor += 1;
                value += if point.flags & (16 << axis) != 0 {
                    delta
                } else {
                    -delta
                };
            } else if point.flags & (16 << axis) == 0 {
                value += i32::from(bytes::i16(glyph.bytes, cursor).ok_or(FontError::InvalidGlyph)?);
                cursor += 2;
            }
            if axis == 0 {
                point.x = value;
            } else {
                point.y = value;
            }
        }
    }
    Ok(())
}
/// Reuse the original maximum-simple-record scratch across composite children.
pub(super) fn decode_scratch(
    face: &SfntFace,
    index: u16,
    contours: &mut Vec<u16>,
    points: &mut Vec<OutlinePoint>,
) -> Result<(), FontError> {
    let glyph = face.glyph_data(index).ok_or(FontError::InvalidGlyph)?;
    let layout = inspect(glyph).ok_or(FontError::InvalidGlyph)?;
    decode(glyph, &layout, contours, points)
}
