//! Glyph-only original binary scan descriptors. Generic/RGB paths stay separate.
use crate::native_scene::wire::{self, constants::*};
const SEGMENT_MARKER: u32 = 256;
const CONTOUR_OVERRIDE: u32 = 512;
const ORIGINAL_FLAGS_MASK: u32 = 20_774_912;
const DESCRIPTOR_MASK: u32 = SEGMENT_MARKER | CONTOUR_OVERRIDE | ORIGINAL_FLAGS_MASK | 7;
pub(crate) fn segment(segment: &wire::PathSegment) -> bool {
    if ![segment.p0, segment.p1, segment.p2, segment.p3]
        .into_iter()
        .all(|p| p.x.is_finite() && p.y.is_finite())
    {
        return false;
    }
    if segment.pad0 == 0 {
        return segment.kind <= PATH_SEGMENT_CUBIC && segment.pad1 == 0 && segment.pad2 == 0;
    }
    segment.kind <= PATH_SEGMENT_QUADRATIC
        && segment.pad0 & SEGMENT_MARKER != 0
        && segment.pad0 & !DESCRIPTOR_MASK == 0
        && segment.pad2 != 0
}
pub(crate) fn outline(outline: &wire::GlyphOutline, segments: &[wire::PathSegment]) -> bool {
    outline_with(outline, segments.len(), |index| segments[index])
}
/// Original source reader contract also admits alignment-independent wire
/// records without allocating a second full outline inventory.
pub(crate) fn outline_with(
    outline: &wire::GlyphOutline,
    count: usize,
    mut read: impl FnMut(usize) -> wire::PathSegment,
) -> bool {
    if count == 0 {
        return false;
    }
    let first = read(0);
    if first.pad0 == 0 {
        return (0..count).all(|index| {
            let s = read(index);
            s.pad0 == 0 && segment(&s)
        });
    }
    if count > 256 || outline.raster_scale != 1.0 || outline.subpixel_x != 0.0 {
        return false;
    }
    let flags = first.pad0 & ORIGINAL_FLAGS_MASK;
    let equal = |a: wire::Point, b: wire::Point| a.x == b.x && a.y == b.y;
    let inside = |p: wire::Point| {
        p.x >= outline.min_x && p.x <= outline.max_x && p.y >= outline.min_y && p.y <= outline.max_y
    };
    let mut start = 0usize;
    while start < count {
        let first = read(start);
        if first.pad0 == 0
            || !segment(&first)
            || first.pad1 as usize != start
            || first.pad2 as usize > count - start
            || first.pad0 & ORIGINAL_FLAGS_MASK != flags
        {
            return false;
        }
        let end = start + first.pad2 as usize;
        let mut previous = first.p0;
        for index in start..end {
            let current = read(index);
            if !segment(&current)
                || current.pad0 != first.pad0
                || current.pad1 != first.pad1
                || current.pad2 != first.pad2
                || !equal(previous, current.p0)
                || !inside(current.p0)
                || !inside(current.p1)
                || (current.kind == PATH_SEGMENT_QUADRATIC && !inside(current.p2))
            {
                return false;
            }
            previous = if current.kind == PATH_SEGMENT_LINE {
                current.p1
            } else {
                current.p2
            };
        }
        if !equal(previous, first.p0) {
            return false;
        }
        start = end;
    }
    true
}
