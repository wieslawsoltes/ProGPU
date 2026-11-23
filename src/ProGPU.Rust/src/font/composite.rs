//! Original component records and recursive unhinted TrueType expansion.
use super::{FontError, GlyphKind, OutlinePoint, SfntFace, bytes, outline};
use crate::path::Segment;
use std::ops::Range;

const XY: u16 = 2;
const MORE: u16 = 0x20;
const MAX_DEPTH: usize = 32;
mod varied;
pub use varied::VariedGlyphOutline;

/// Original component record. Raw flags and argument interpretation are retained.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompositeComponent {
    /// Complete original flags, including unknown and conflicting bits.
    pub flags: u16,
    /// Referenced source glyph slot, not validated by record decoding alone.
    pub glyph_index: u16,
    /// Signed XY offsets or unsigned parent/child attachment indices.
    pub arguments: [i32; 2],
    /// Original row-major 2×2 matrix: m00, m01, m10, m11.
    pub transform: [f32; 4],
}
impl CompositeComponent {
    fn apply(self, p: [f32; 2], offset: [f32; 2]) -> [f32; 2] {
        let m = self.transform;
        [
            p[0] * m[0] + p[1] * m[1] + offset[0],
            p[0] * m[2] + p[1] * m[3] + offset[1],
        ]
    }
}
/// Complete original component-record requirements, without recursive expansion.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CompositeRequirements {
    /// Ordered component count.
    pub component_count: u32,
    /// Instruction bytes selected by the final component's flags.
    pub instruction_bytes: u16,
}
/// Owned original descriptors and instructions retaining the selected source face.
#[derive(Clone, Debug)]
pub struct CompositeGlyph {
    source: SfntFace,
    glyph_index: u16,
    components: Vec<CompositeComponent>,
    instructions: Range<usize>,
}
impl CompositeGlyph {
    /// Original immutable selected face owner.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original glyph index.
    pub fn glyph_index(&self) -> u16 {
        self.glyph_index
    }
    /// Ordered original component records, without recursive interpretation.
    pub fn components(&self) -> &[CompositeComponent] {
        &self.components
    }
    /// Original instruction payload, retained but not executed.
    pub fn instructions(&self) -> &[u8] {
        &self
            .source
            .glyph_data(self.glyph_index)
            .expect("validated immutable composite")
            .bytes[self.instructions.clone()]
    }
}
/// Whole recursively expanded outline requirements, before allocation/publication.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ExpandedRequirements {
    /// Retained transformed points, including contours which emit no path.
    pub point_count: u32,
    /// Original emitted line/quadratic record count.
    pub path_segment_count: u32,
    /// Maximum child point scratch, reused across the entire expansion.
    pub simple_point_scratch_count: u32,
    /// Maximum child contour scratch, reused across the entire expansion.
    pub simple_contour_scratch_count: u16,
}
/// Immutable unhinted outline, preserving original recursive transform order.
/// This does not execute instructions, apply variations or select a raster policy.
#[derive(Clone, Debug)]
pub struct GlyphOutline {
    source: SfntFace,
    glyph_index: u16,
    points: Vec<[f32; 2]>,
    segments: Vec<Segment>,
}
impl GlyphOutline {
    /// Exact selected face retained beyond the caller's source lifetime.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original glyph index.
    pub fn glyph_index(&self) -> u16 {
        self.glyph_index
    }
    /// Original ordered points after component transforms and attachment.
    pub fn points(&self) -> &[[f32; 2]] {
        &self.points
    }
    /// Original ordered canonical line/quadratic path records.
    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }
}

fn component(data: &[u8], cursor: &mut usize) -> Option<CompositeComponent> {
    let flags = bytes::u16(data, *cursor)?;
    let glyph_index = bytes::u16(data, *cursor + 2)?;
    *cursor += 4;
    let words = flags & 1 != 0;
    let xy = flags & XY != 0;
    let width = if words { 2 } else { 1 };
    let mut arguments = [0; 2];
    for argument in &mut arguments {
        *argument = if words {
            let value = bytes::u16(data, *cursor)?;
            if xy {
                i32::from(value as i16)
            } else {
                i32::from(value)
            }
        } else {
            let value = *data.get(*cursor)?;
            if xy {
                i32::from(value as i8)
            } else {
                i32::from(value)
            }
        };
        *cursor += width;
    }
    let mut transform = [1.0, 0.0, 0.0, 1.0];
    // Native precedence is intentional even when several transform flags are set.
    let slots: &[usize] = if flags & 8 != 0 {
        &[0]
    } else if flags & 0x40 != 0 {
        &[0, 3]
    } else if flags & 0x80 != 0 {
        &[0, 1, 2, 3]
    } else {
        &[]
    };
    for &slot in slots {
        transform[slot] = f32::from(bytes::i16(data, *cursor)?) / 16384.0;
        *cursor += 2;
    }
    if flags & 8 != 0 {
        transform[3] = transform[0];
    }
    Some(CompositeComponent {
        flags,
        glyph_index,
        arguments,
        transform,
    })
}
fn inspect(face: &SfntFace, index: u16) -> Option<(CompositeRequirements, Range<usize>)> {
    let glyph = face.glyph_data(index)?;
    if glyph.bytes.len() < 10 || glyph.contour_count >= 0 {
        return None;
    }
    let mut cursor = 10;
    let mut flags = MORE;
    let mut count = 0u32;
    while flags & MORE != 0 {
        flags = component(glyph.bytes, &mut cursor)?.flags;
        count = count.checked_add(1)?;
    }
    let mut length = 0;
    if flags & 0x100 != 0 {
        length = bytes::u16(glyph.bytes, cursor)?;
        cursor += 2;
        bytes::bytes(glyph.bytes, cursor, usize::from(length))?;
    }
    Some((
        CompositeRequirements {
            component_count: count,
            instruction_bytes: length,
        },
        cursor..cursor + usize::from(length),
    ))
}
fn reserve<T>(count: usize) -> Result<Vec<T>, FontError> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(count)
        .map_err(|_| FontError::Capacity)?;
    Ok(result)
}
impl SfntFace {
    /// Validate complete original component records without resolving children.
    pub fn composite_requirements(&self, index: u16) -> Result<CompositeRequirements, FontError> {
        inspect(self, index)
            .map(|r| r.0)
            .ok_or(FontError::InvalidGlyph)
    }
    /// Atomically own original component descriptors and retain instruction bytes.
    pub fn composite_glyph(&self, index: u16) -> Result<CompositeGlyph, FontError> {
        let (required, instructions) = inspect(self, index).ok_or(FontError::InvalidGlyph)?;
        let data = self.glyph_data(index).ok_or(FontError::InvalidGlyph)?.bytes;
        let mut components = reserve(required.component_count as usize)?;
        let mut cursor = 10;
        for _ in 0..required.component_count {
            components.push(component(data, &mut cursor).ok_or(FontError::InvalidGlyph)?);
        }
        Ok(CompositeGlyph {
            source: self.clone(),
            glyph_index: index,
            components,
            instructions,
        })
    }
    /// Measure the original recursive outline. Cyclic, out-of-range and depth>32
    /// children are empty; invalid attachment components are omitted, as in native.
    /// The root must be a valid glyph slot. Malformed visited records fail atomically.
    pub fn expanded_requirements(&self, index: u16) -> Result<ExpandedRequirements, FontError> {
        let count = self.glyph_count().ok_or(FontError::InvalidArgument)?;
        if index >= count {
            return Err(FontError::InvalidArgument);
        }
        measure(self, index, count, &mut [0; MAX_DEPTH + 1], 0)
    }
    /// Decode the original unhinted simple/composite outline, retaining its source.
    /// Whole-tree count validation precedes allocation; no partial output escapes.
    pub fn glyph_outline(&self, index: u16) -> Result<GlyphOutline, FontError> {
        let required = self.expanded_requirements(index)?;
        let mut state = Expansion {
            points: reserve(required.point_count as usize)?,
            segments: reserve(required.path_segment_count as usize)?,
            contours: reserve(usize::from(required.simple_contour_scratch_count))?,
            scratch: reserve(required.simple_point_scratch_count as usize)?,
            variation: None,
            limits: required,
        };
        state.decode(
            self,
            index,
            self.glyph_count().ok_or(FontError::InvalidArgument)?,
            &mut [0; MAX_DEPTH + 1],
            0,
        )?;
        if state.points.len() != required.point_count as usize
            || state.segments.len() != required.path_segment_count as usize
        {
            return Err(FontError::InvalidGlyph);
        }
        Ok(GlyphOutline {
            source: self.clone(),
            glyph_index: index,
            points: state.points,
            segments: state.segments,
        })
    }
}
fn omitted(index: u16, count: u16, ancestors: &[u16; MAX_DEPTH + 1], depth: usize) -> bool {
    index >= count || depth > MAX_DEPTH || ancestors[..depth].contains(&index)
}
fn measure(
    face: &SfntFace,
    index: u16,
    count: u16,
    ancestors: &mut [u16; MAX_DEPTH + 1],
    depth: usize,
) -> Result<ExpandedRequirements, FontError> {
    let mut result = ExpandedRequirements::default();
    if omitted(index, count, ancestors, depth) {
        return Ok(result);
    }
    ancestors[depth] = index;
    let r = face.glyph_requirements(index)?;
    match r.kind {
        GlyphKind::Empty => return Ok(result),
        GlyphKind::Simple => {
            return Ok(ExpandedRequirements {
                point_count: r.point_count,
                path_segment_count: r.path_segment_count,
                simple_point_scratch_count: r.point_count,
                simple_contour_scratch_count: r.contour_count,
            });
        }
        GlyphKind::Composite => {}
    }
    let r = face.composite_requirements(index)?;
    let data = face.glyph_data(index).ok_or(FontError::InvalidGlyph)?.bytes;
    let mut cursor = 10;
    for _ in 0..r.component_count {
        let c = component(data, &mut cursor).ok_or(FontError::InvalidGlyph)?;
        let child = measure(face, c.glyph_index, count, ancestors, depth + 1)?;
        if child.point_count == 0 && child.path_segment_count == 0 {
            continue;
        }
        if c.flags & XY == 0
            && (c.arguments[0] < 0
                || c.arguments[1] < 0
                || c.arguments[0] as u32 >= result.point_count
                || c.arguments[1] as u32 >= child.point_count)
        {
            continue;
        }
        result.point_count = result
            .point_count
            .checked_add(child.point_count)
            .ok_or(FontError::InvalidGlyph)?;
        result.path_segment_count = result
            .path_segment_count
            .checked_add(child.path_segment_count)
            .ok_or(FontError::InvalidGlyph)?;
        result.simple_point_scratch_count = result
            .simple_point_scratch_count
            .max(child.simple_point_scratch_count);
        result.simple_contour_scratch_count = result
            .simple_contour_scratch_count
            .max(child.simple_contour_scratch_count);
    }
    Ok(result)
}
struct Expansion<'a> {
    points: Vec<[f32; 2]>,
    segments: Vec<Segment>,
    contours: Vec<u16>,
    scratch: Vec<OutlinePoint>,
    variation: Option<varied::Variation<'a>>,
    limits: ExpandedRequirements,
}
impl Expansion<'_> {
    fn decode(
        &mut self,
        face: &SfntFace,
        index: u16,
        count: u16,
        ancestors: &mut [u16; MAX_DEPTH + 1],
        depth: usize,
    ) -> Result<(), FontError> {
        if omitted(index, count, ancestors, depth) {
            return Ok(());
        }
        ancestors[depth] = index;
        let requirements = face.glyph_requirements(index)?;
        match requirements.kind {
            GlyphKind::Empty => return Ok(()),
            GlyphKind::Simple => {
                if self
                    .points
                    .len()
                    .checked_add(requirements.point_count as usize)
                    .is_none_or(|n| n > self.limits.point_count as usize)
                    || self
                        .segments
                        .len()
                        .checked_add(requirements.path_segment_count as usize)
                        .is_none_or(|n| n > self.limits.path_segment_count as usize)
                {
                    return Err(FontError::InvalidGlyph);
                }
                outline::decode_scratch(face, index, &mut self.contours, &mut self.scratch)?;
                if let Some(v) = self.variation {
                    let positions = super::gvar::apply_simple_with_capacity(
                        face,
                        index,
                        v.coordinates,
                        &self.contours,
                        &self.scratch,
                        Some(v.simple_capacity),
                    )?;
                    outline::append_segments(
                        &self.contours,
                        &self.scratch,
                        &positions,
                        &mut self.segments,
                    );
                    self.points.extend_from_slice(&positions);
                } else {
                    outline::append_segments(
                        &self.contours,
                        &self.scratch,
                        &[],
                        &mut self.segments,
                    );
                    self.points
                        .extend(self.scratch.iter().map(|p| [p.x as f32, p.y as f32]));
                }
                return Ok(());
            }
            GlyphKind::Composite => {}
        }
        let r = face.composite_requirements(index)?;
        let data = face.glyph_data(index).ok_or(FontError::InvalidGlyph)?.bytes;
        let offsets = self
            .variation
            .map(|v| {
                super::gvar::component_offsets_with_capacity(
                    face,
                    index,
                    v.coordinates,
                    r.component_count,
                    Some(v.component_capacity),
                )
            })
            .transpose()?;
        let parent_start = self.points.len();
        let mut cursor = 10;
        for component_index in 0..r.component_count {
            let c = component(data, &mut cursor).ok_or(FontError::InvalidGlyph)?;
            let point_start = self.points.len();
            let segment_start = self.segments.len();
            if c.flags & XY == 0 {
                if c.arguments[0] < 0 || c.arguments[0] as usize >= point_start - parent_start {
                    continue;
                }
                // The original varied route remeasures an attachment child as
                // its own root; the unvaried route retains ancestor-aware measure.
                let child = if self.variation.is_some() {
                    match face.expanded_requirements(c.glyph_index) {
                        Ok(r) => r,
                        Err(_) => continue,
                    }
                } else {
                    measure(face, c.glyph_index, count, ancestors, depth + 1)?
                };
                if c.arguments[1] < 0 || c.arguments[1] as u32 >= child.point_count {
                    continue;
                }
            }
            self.decode(face, c.glyph_index, count, ancestors, depth + 1)?;
            if self.points.len() == point_start && self.segments.len() == segment_start {
                continue;
            }
            let mut offset = if c.flags & XY != 0 {
                let mut raw = [c.arguments[0] as f32, c.arguments[1] as f32];
                if let Some(offsets) = &offsets {
                    raw[0] += offsets[component_index as usize][0];
                    raw[1] += offsets[component_index as usize][1];
                }
                if c.flags & 0x800 != 0 {
                    c.apply(raw, [0.0; 2])
                } else {
                    raw
                }
            } else {
                if c.arguments[0] as usize >= point_start - parent_start
                    || c.arguments[1] as usize >= self.points.len() - point_start
                {
                    self.points.truncate(point_start);
                    self.segments.truncate(segment_start);
                    continue;
                }
                let parent = self.points[parent_start + c.arguments[0] as usize];
                let child = c.apply(self.points[point_start + c.arguments[1] as usize], [0.0; 2]);
                [parent[0] - child[0], parent[1] - child[1]]
            };
            if c.flags & 4 != 0 {
                offset = offset.map(round_to_even);
            }
            for p in &mut self.points[point_start..] {
                *p = c.apply(*p, offset);
            }
            for s in &mut self.segments[segment_start..] {
                s.points[0] = c.apply(s.points[0], offset);
                s.points[1] = c.apply(s.points[1], offset);
                if s.kind != 0 {
                    s.points[2] = c.apply(s.points[2], offset);
                }
            }
        }
        Ok(())
    }
}
// Preserve the native floor/fraction arithmetic, including signed zero.
fn round_to_even(value: f32) -> f32 {
    let lower = value.floor();
    let fraction = value - lower;
    if fraction < 0.5 {
        lower
    } else if fraction > 0.5 {
        lower + 1.0
    } else if lower % 2.0 == 0.0 {
        lower
    } else {
        lower + 1.0
    }
}
