//! Original native cluster-preserving L1/L2 visual line order.
use super::*;

/// Count complete contiguous clusters after whole level admission.
pub fn native_visual_order_requirements(
    glyphs: &[ShapingGlyph],
    levels: &[i8],
) -> Result<NativeVisualOrderRequirements, FontError> {
    if glyphs.len() != levels.len()
        || glyphs.len() > u32::MAX as usize
        || levels.iter().any(|&level| !(0..=125).contains(&level))
    {
        return Err(FontError::InvalidArgument);
    }
    let mut groups = 0;
    let mut start = 0;
    while start < glyphs.len() {
        groups += 1;
        let cluster = glyphs[start].cluster;
        start += 1;
        while start < glyphs.len() && glyphs[start].cluster == cluster {
            start += 1;
        }
    }
    Ok(NativeVisualOrderRequirements {
        glyph_capacity: glyphs.len(),
        group_capacity: groups,
    })
}
pub(super) fn prepare(
    glyphs: &[ShapingGlyph],
    levels: &[i8],
    paragraph: i8,
    groups: &mut [NativeVisualClusterGroup],
    preserve_trailing_spaces: bool,
) -> Result<usize, FontError> {
    let required = native_visual_order_requirements(glyphs, levels)?;
    if !matches!(paragraph, 0 | 1) {
        return Err(FontError::InvalidArgument);
    }
    if groups.len() < required.group_capacity {
        return Err(FontError::Capacity);
    }
    let mut start = 0;
    let mut count = 0;
    while start < glyphs.len() {
        let mut end = start + 1;
        while end < glyphs.len() && glyphs[end].cluster == glyphs[start].cluster {
            end += 1;
        }
        groups[count] = NativeVisualClusterGroup {
            glyph_start: start as u32,
            glyph_count: (end - start) as u32,
            bidi_level: levels[start],
        };
        count += 1;
        start = end;
    }
    let groups = &mut groups[..count];
    if !preserve_trailing_spaces {
        for group in groups.iter_mut().rev() {
            if !glyphs[group.glyph_start as usize..(group.glyph_start + group.glyph_count) as usize]
                .iter()
                .all(|g| matches!(g.code_point, 0x20 | 0x09))
            {
                break;
            }
            group.bidi_level = paragraph;
        }
    }
    let mut maximum = 0;
    let mut lowest_odd = i8::MAX;
    for group in groups.iter() {
        maximum = maximum.max(group.bidi_level);
        if group.bidi_level & 1 != 0 {
            lowest_odd = lowest_odd.min(group.bidi_level);
        }
    }
    if lowest_odd != i8::MAX {
        for level in (lowest_odd..=maximum).rev() {
            let mut start = 0;
            while start < count {
                while start < count && groups[start].bidi_level < level {
                    start += 1;
                }
                let mut end = start;
                while end < count && groups[end].bidi_level >= level {
                    end += 1;
                }
                groups[start..end].reverse();
                start = end;
            }
        }
    }
    Ok(count)
}
/// Write exact original logical glyph indices in visual order. Only the used
/// scratch prefix is modified; preserve-trailing selects the original explicit
/// source soft-wrap policy, never an implicit general bidi change.
pub fn native_visual_indices_into(
    glyphs: &[ShapingGlyph],
    levels: &[i8],
    paragraph: i8,
    groups: &mut [NativeVisualClusterGroup],
    indices: &mut [u32],
    preserve_trailing_spaces: bool,
) -> Result<usize, FontError> {
    let count = prepare(glyphs, levels, paragraph, groups, preserve_trailing_spaces)?;
    if indices.len() < glyphs.len() {
        return Err(FontError::Capacity);
    }
    let mut written = 0;
    for group in &groups[..count] {
        for index in group.glyph_start..group.glyph_start + group.glyph_count {
            indices[written] = index;
            written += 1;
        }
    }
    Ok(written)
}
/// Copy original shaping records in visual group order without changing metrics
/// or source cluster/flags. Safe borrows replace the original alias preconditions.
pub fn native_reorder_visual_into(
    glyphs: &[ShapingGlyph],
    levels: &[i8],
    paragraph: i8,
    groups: &mut [NativeVisualClusterGroup],
    output: &mut [ShapingGlyph],
) -> Result<usize, FontError> {
    let count = prepare(glyphs, levels, paragraph, groups, false)?;
    if output.len() < glyphs.len() {
        return Err(FontError::Capacity);
    }
    let mut written = 0;
    for group in &groups[..count] {
        let source =
            &glyphs[group.glyph_start as usize..(group.glyph_start + group.glyph_count) as usize];
        output[written..written + source.len()].copy_from_slice(source);
        written += source.len();
    }
    Ok(written)
}
