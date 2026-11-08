//! Original recursive varied requirements and immutable outline publication.
use super::{
    Expansion, FontError, GlyphKind, GlyphOutline, MAX_DEPTH, Segment, SfntFace, component,
    omitted, reserve,
};
use crate::font::gvar;

/// Owned unhinted TrueType outline at explicit normalized variation coordinates.
/// Keeps the selected source and request independently of the caller's buffers.
/// No hinting, shaping, device metrics or raster policy is inferred.
#[derive(Clone, Debug)]
pub struct VariedGlyphOutline {
    outline: GlyphOutline,
    coordinates: Vec<i16>,
}
impl VariedGlyphOutline {
    /// Original immutable selected font face.
    pub fn source(&self) -> &SfntFace {
        self.outline.source()
    }
    /// Original glyph slot, including a valid empty slot.
    pub fn glyph_index(&self) -> u16 {
        self.outline.glyph_index()
    }
    /// Exact supplied normalized coordinates. Extra axes are retained but ignored.
    pub fn coordinates(&self) -> &[i16] {
        &self.coordinates
    }
    /// Ordered varied points after recursive transforms and attachment.
    pub fn points(&self) -> &[[f32; 2]] {
        self.outline.points()
    }
    /// Original contour lowering at the selected varied positions.
    pub fn segments(&self) -> &[Segment] {
        self.outline.segments()
    }
}

#[derive(Clone, Copy)]
pub(super) struct Variation<'a> {
    pub coordinates: &'a [i16],
    pub simple_capacity: u32,
    pub component_capacity: u32,
}
#[derive(Default)]
struct Limits {
    simple: u32,
    component: u32,
}

impl SfntFace {
    /// Decode the original complete recursive TrueType variation route.
    /// Preflight visits every original child, including omitted attachments, and
    /// preserves the original whole-tree scratch admission. Outputs publish only
    /// after exact point/segment counts match the original unvaried topology.
    pub fn varied_glyph_outline(
        &self,
        index: u16,
        coordinates: &[i16],
    ) -> Result<VariedGlyphOutline, FontError> {
        let font = self.gvar()?;
        let axes = font
            .as_ref()
            .map_or(0, |f| usize::from(f.header().axis_count));
        if coordinates.len() < axes {
            return Err(FontError::Capacity);
        }
        let required = self.expanded_requirements(index)?;
        let count = self.glyph_count().ok_or(FontError::InvalidArgument)?;
        let mut limits = Limits::default();
        preflight(
            self,
            font.as_ref(),
            index,
            &mut [0; MAX_DEPTH + 1],
            0,
            0,
            &mut limits,
        )
        .map_err(|_| FontError::InvalidGlyph)?;
        let mut state = Expansion {
            points: reserve(required.point_count as usize)?,
            segments: reserve(required.path_segment_count as usize)?,
            contours: reserve(usize::from(required.simple_contour_scratch_count))?,
            scratch: reserve(required.simple_point_scratch_count as usize)?,
            limits: required,
            variation: Some(Variation {
                coordinates: &coordinates[..axes],
                simple_capacity: limits.simple,
                component_capacity: limits.component,
            }),
        };
        state
            .decode(self, index, count, &mut [0; MAX_DEPTH + 1], 0)
            .map_err(|_| FontError::InvalidGlyph)?;
        if state.points.len() != required.point_count as usize
            || state.segments.len() != required.path_segment_count as usize
        {
            return Err(FontError::InvalidGlyph);
        }
        let mut owned_coordinates = reserve(coordinates.len())?;
        owned_coordinates.extend_from_slice(coordinates);
        Ok(VariedGlyphOutline {
            outline: GlyphOutline {
                source: self.clone(),
                glyph_index: index,
                points: state.points,
                segments: state.segments,
            },
            coordinates: owned_coordinates,
        })
    }
}
fn preflight(
    face: &SfntFace,
    font: Option<&gvar::Font>,
    index: u16,
    ancestors: &mut [u16; MAX_DEPTH + 1],
    depth: usize,
    component_base: u32,
    limits: &mut Limits,
) -> Result<(), FontError> {
    let count = face.glyph_count().ok_or(FontError::InvalidGlyph)?;
    if omitted(index, count, ancestors, depth) {
        return Ok(());
    }
    ancestors[depth] = index;
    let r = face.glyph_requirements(index)?;
    if r.kind == GlyphKind::Empty {
        return Ok(());
    }
    let tuples = font.map_or(Ok((0, 0)), |f| f.tuple_requirements(index))?.0;
    if r.kind == GlyphKind::Simple {
        if tuples != 0 {
            limits.simple = limits.simple.max(
                r.point_count
                    .checked_add(4)
                    .ok_or(FontError::InvalidGlyph)?,
            );
        }
        return Ok(());
    }
    let r = face.composite_requirements(index)?;
    if tuples != 0 {
        limits.component = limits.component.max(
            r.component_count
                .checked_add(4)
                .ok_or(FontError::InvalidGlyph)?,
        );
    }
    let next_base = component_base
        .checked_add(r.component_count)
        .ok_or(FontError::InvalidGlyph)?;
    let data = face.glyph_data(index).ok_or(FontError::InvalidGlyph)?.bytes;
    let mut cursor = 10;
    for _ in 0..r.component_count {
        let c = component(data, &mut cursor).ok_or(FontError::InvalidGlyph)?;
        preflight(
            face,
            font,
            c.glyph_index,
            ancestors,
            depth + 1,
            next_base,
            limits,
        )?;
    }
    Ok(())
}
