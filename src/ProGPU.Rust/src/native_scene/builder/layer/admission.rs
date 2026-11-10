//! Borrowed source-mask admission before allocating a candidate resource.
use super::*;
use crate::native_scene::semantic::{layer_mask as scalar, stored_stop_count, valid_brush_records};

pub(super) fn brush(m: &wire::LayerBrushMask, stops: &[wire::GradientStop]) -> bool {
    let stored = stored_stop_count(&m.brush);
    m.struct_size == wire::LayerBrushMask::WIRE_SIZE as u32
        && m.kind == SCENE_LAYER_MASK_BRUSH
        && m.flags == 0
        && m.gradient_stop_count == stored
        && scalar::bounds(m.bounds)
        && scalar::transform(m.transform)
        && scalar::opacity(m.opacity)
        && m.reserved0 == 0
        && (m.brush.kind == SCENE_BRUSH_TILE_PATTERN
            || u64::from(m.brush.stop_offset) + u64::from(stored) <= stops.len() as u64)
        && valid_brush_records(&m.brush, stops)
}
pub(super) fn geometry(
    m: &wire::LayerGeometryMask,
    primitives: &[wire::GeometryPrimitive],
    stops: &[wire::GradientStop],
) -> bool {
    let stored = stored_stop_count(&m.brush);
    if m.struct_size != wire::LayerGeometryMask::WIRE_SIZE as u32
        || m.kind != SCENE_LAYER_MASK_GEOMETRY
        || m.flags != 0
        || m.reserved0 != 0
        || m.reserved1 != 0
        || m.reserved2 != 0
        || m.primitive_count == 0
        || u64::from(m.primitive_offset) + u64::from(m.primitive_count) > primitives.len() as u64
        || m.gradient_stop_count != stored
        || !scalar::bounds(m.bounds)
        || !scalar::transform(m.transform)
        || !scalar::opacity(m.opacity)
        || m.brush.kind != SCENE_BRUSH_TILE_PATTERN
            && u64::from(m.brush.stop_offset) + u64::from(stored) > stops.len() as u64
        || !valid_brush_records(&m.brush, stops)
    {
        return false;
    }
    primitives[m.primitive_offset as usize..(m.primitive_offset + m.primitive_count) as usize]
        .iter()
        .all(values::geometry)
}
pub(super) fn picture(m: &wire::LayerPictureMask, streams: &[u8]) -> bool {
    let extent = m.flags & SCENE_PICTURE_MASK_SOURCE_EXTENT != 0;
    if m.struct_size != wire::LayerPictureMask::WIRE_SIZE as u32
        || m.kind != SCENE_LAYER_MASK_PICTURE
        || m.flags & !SCENE_PICTURE_MASK_SOURCE_EXTENT != 0
        || if extent {
            !(1..=16384).contains(&m.reserved0) || !(1..=16384).contains(&m.reserved1)
        } else {
            m.reserved0 != 0 || m.reserved1 != 0
        }
        || m.stream_size == 0
        || u64::from(m.stream_offset) + u64::from(m.stream_size) > streams.len() as u64
        || !scalar::bounds(m.bounds)
        || !scalar::transform(m.transform)
        || !scalar::opacity(m.opacity)
        || m.stream_size < wire::Header::WIRE_SIZE as u32
        || m.stream_size > MAX_STREAM_BYTES
    {
        return false;
    }
    let Some(h) = wire::Header::read(
        &streams[m.stream_offset as usize..(m.stream_offset + m.stream_size) as usize],
    ) else {
        return false;
    };
    h.struct_size >= wire::Header::WIRE_SIZE as u32
        && h.magic == SCENE_STREAM_MAGIC
        && h.stream_version == SCENE_STREAM_VERSION
        && h.endian_marker == SCENE_STREAM_ENDIAN_MARKER
        && h.flags == 0
        && h.total_size == m.stream_size
        && h.scene_id != 0
        && h.generation != 0
        && h.reserved0 == 0
        && h.reserved1 == 0
}
pub(super) fn vector(
    paths: &[wire::ClipPath],
    segments: &[wire::PathSegment],
    nodes: &[wire::PathBooleanNode],
    opacity: f32,
) -> bool {
    let size = paths.len() as u64 * wire::ClipPath::WIRE_SIZE as u64
        + segments.len() as u64 * wire::PathSegment::WIRE_SIZE as u64
        + nodes.len() as u64 * wire::PathBooleanNode::WIRE_SIZE as u64;
    if paths.is_empty()
        || paths.len() > 64
        || segments.is_empty()
        || nodes.len() > 64 * 63
        || size > u64::from(u32::MAX)
        || !scalar::opacity(opacity)
    {
        return false;
    }
    let mut expected = 0u64;
    for p in paths {
        if p.segment_count == 0
            || p.segment_offset > segments.len() as u64
            || p.segment_count > segments.len() as u64 - p.segment_offset
            || ![p.min_x, p.min_y, p.max_x, p.max_y]
                .into_iter()
                .all(f32::is_finite)
            || p.max_x <= p.min_x
            || p.max_y <= p.min_y
            || !scalar::transform(p.transform)
            || p.fill_rule > FILL_RULE_EVEN_ODD
            || !matches!(p.sample_grid, 1 | 4 | 8)
            || p.operation > CLIP_DIFFERENCE
            || p.reserved != 0
            || !values::boolean(
                p.segment_offset,
                p.segment_count,
                p.boolean_node_offset,
                p.boolean_node_count,
                p.fill_rule,
                nodes,
            )
            || p.boolean_node_count != 0 && p.boolean_node_offset != expected
        {
            return false;
        }
        expected += p.boolean_node_count;
    }
    expected == nodes.len() as u64 && segments.iter().all(|s| values::segment(s, true))
}

pub(super) struct Composite<'a> {
    pub brushes: &'a [wire::LayerBrushMask],
    pub geometry_masks: &'a [wire::LayerGeometryMask],
    pub primitives: &'a [wire::GeometryPrimitive],
    pub pictures: &'a [wire::LayerPictureMask],
    pub streams: &'a [u8],
    pub paths: &'a [wire::ClipPath],
    pub segments: &'a [wire::PathSegment],
    pub nodes: &'a [wire::PathBooleanNode],
    pub stops: &'a [wire::GradientStop],
    pub opacity: f32,
}
impl Composite<'_> {
    pub fn byte_size(&self) -> Option<usize> {
        [
            self.brushes
                .len()
                .checked_mul(wire::LayerBrushMask::WIRE_SIZE)?,
            self.geometry_masks
                .len()
                .checked_mul(wire::LayerGeometryMask::WIRE_SIZE)?,
            self.primitives
                .len()
                .checked_mul(wire::GeometryPrimitive::WIRE_SIZE)?,
            self.pictures
                .len()
                .checked_mul(wire::LayerPictureMask::WIRE_SIZE)?,
            self.streams.len(),
            self.paths.len().checked_mul(wire::ClipPath::WIRE_SIZE)?,
            self.segments
                .len()
                .checked_mul(wire::PathSegment::WIRE_SIZE)?,
            self.nodes
                .len()
                .checked_mul(wire::PathBooleanNode::WIRE_SIZE)?,
            self.stops
                .len()
                .checked_mul(wire::GradientStop::WIRE_SIZE)?,
        ]
        .into_iter()
        .try_fold(0usize, usize::checked_add)
    }
    pub fn valid(&self) -> bool {
        let components = self.brushes.len()
            + self.geometry_masks.len()
            + self.pictures.len()
            + usize::from(!self.paths.is_empty());
        if !(2..=64).contains(&components)
            || self.brushes.is_empty() && self.geometry_masks.is_empty() && self.pictures.is_empty()
            || self.geometry_masks.is_empty() != self.primitives.is_empty()
            || self.pictures.is_empty() != self.streams.is_empty()
            || self.stops.len() > SCENE_MAX_GRADIENT_STOPS as usize
            || !scalar::opacity(self.opacity)
            || self.paths.is_empty() && (!self.segments.is_empty() || !self.nodes.is_empty())
            || self.byte_size().is_none_or(|size| size > u32::MAX as usize)
        {
            return false;
        }
        if !self.brushes.iter().all(|m| brush(m, self.stops)) {
            return false;
        }
        let mut expected = 0u32;
        for mask in self.geometry_masks {
            if mask.primitive_offset != expected || !geometry(mask, self.primitives, self.stops) {
                return false;
            }
            expected += mask.primitive_count;
        }
        if expected as usize != self.primitives.len() {
            return false;
        }
        expected = 0;
        for mask in self.pictures {
            if mask.stream_offset != expected || !picture(mask, self.streams) {
                return false;
            }
            expected += mask.stream_size;
        }
        expected as usize == self.streams.len()
            && (self.paths.is_empty() || vector(self.paths, self.segments, self.nodes, 1.0))
    }
}
