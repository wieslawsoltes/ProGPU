//! Original native vertical column writer and OpenType projection. Raw ABI
//! Y-down glyphs and actual native Y-up shaping remain distinct entry points.
use super::*;
use crate::font::SfntFace;
mod interaction;
mod projection;
mod retained;
pub use interaction::*;
pub use projection::{NativeProjectedGlyph, native_project_open_type_into};
pub use retained::NativeVerticalText;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativePositionedTextColumn {
    pub glyph_start: u32,
    pub glyph_count: u32,
    pub input_start: i32,
    pub input_end: i32,
    pub height: f32,
    pub x: f32,
    pub width: f32,
    pub clipped: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeVerticalRequirements {
    pub glyph_capacity: usize,
    pub column_capacity: usize,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativeVerticalMetrics {
    pub advance_x: f32,
    pub advance_y: f32,
    pub offset_x: f32,
    pub offset_y: f32,
}
fn vertical(d: GposDirection) -> bool {
    matches!(d, GposDirection::TopToBottom | GposDirection::BottomToTop)
}
/// Original mandatory-break partition. Width never invents a vertical soft wrap.
pub fn native_vertical_requirements(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    options: NativeTextLayoutOptions,
) -> Result<NativeVerticalRequirements, FontError> {
    if glyphs.len() > u32::MAX as usize
        || breaks.len() != glyphs.len()
        || !options.scale.is_finite()
        || options.scale <= 0.0
        || !options.maximum_width.is_finite()
        || options.maximum_width < 0.0
        || !options.line_height.is_finite()
        || options.line_height < 0.0
        || !vertical(options.direction)
        || options.trimming != NativeTextTrimming::None
    {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeVerticalRequirements::default();
    let mut start = 0;
    while start < glyphs.len()
        && (options.maximum_lines == 0 || result.column_capacity < options.maximum_lines as usize)
    {
        let mut end = start + 1;
        while end < glyphs.len() && breaks[end - 1] != LineBreakKind::Mandatory {
            end += 1;
        }
        result.column_capacity += 1;
        result.glyph_capacity = end;
        start = end;
    }
    Ok(result)
}
fn write(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    options: NativeTextLayoutOptions,
    required: NativeVerticalRequirements,
    output: &mut [NativePositionedTextGlyph],
    columns: &mut [NativePositionedTextColumn],
    mut project: impl FnMut(usize, ShapingGlyph) -> NativeVerticalMetrics,
) -> [usize; 2] {
    let mut start = 0;
    let mut column_count = 0;
    while start < required.glyph_capacity {
        let mut end = start + 1;
        while end < required.glyph_capacity && breaks[end - 1] != LineBreakKind::Mandatory {
            end += 1;
        }
        let x = column_count as f32 * options.line_height;
        let mut y = 0f32;
        for index in start..end {
            let g = glyphs[index];
            let m = project(index, g);
            output[index] = NativePositionedTextGlyph {
                glyph_index: index as u32,
                glyph_id: g.glyph_id,
                cluster: g.cluster,
                position: [x + options.line_height * 0.5 + m.offset_x, y + m.offset_y],
                advance: [m.advance_x, m.advance_y],
            };
            y += m.advance_y;
        }
        columns[column_count] = NativePositionedTextColumn {
            glyph_start: start as u32,
            glyph_count: (end - start) as u32,
            input_start: glyphs[start].cluster,
            input_end: if end < glyphs.len() {
                glyphs[end].cluster
            } else {
                glyphs[end - 1].cluster.wrapping_add(1)
            },
            height: y.abs(),
            x,
            width: options.line_height,
            clipped: end == required.glyph_capacity && end < glyphs.len(),
        };
        column_count += 1;
        start = end;
    }
    let width = column_count as f32 * options.line_height;
    let shift = if options.maximum_width <= width {
        0.0
    } else {
        match options.alignment {
            NativeTextAlignment::Center => (options.maximum_width - width) * 0.5,
            NativeTextAlignment::Right => options.maximum_width - width,
            _ => 0.0,
        }
    };
    if shift > 0.0 {
        for g in &mut output[..required.glyph_capacity] {
            g.position[0] += shift;
        }
        for c in &mut columns[..column_count] {
            c.x += shift;
        }
    }
    [required.glyph_capacity, column_count]
}
/// Original raw positioned-column producer. Input Y metrics are already in the
/// native interop Y-down convention; this entry performs no font projection.
pub fn native_layout_vertical_into(
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    options: NativeTextLayoutOptions,
    output: &mut [NativePositionedTextGlyph],
    columns: &mut [NativePositionedTextColumn],
) -> Result<[usize; 2], FontError> {
    let required = native_vertical_requirements(glyphs, breaks, options)?;
    if output.len() < required.glyph_capacity || columns.len() < required.column_capacity {
        return Err(FontError::Capacity);
    }
    Ok(write(
        glyphs,
        breaks,
        options,
        required,
        output,
        columns,
        |_, g| NativeVerticalMetrics {
            advance_x: g.advance_x as f32 * options.scale,
            advance_y: g.advance_y as f32 * options.scale,
            offset_x: g.offset_x as f32 * options.scale,
            offset_y: g.offset_y as f32 * options.scale,
        },
    ))
}
/// Original font-aware Y-up to Y-down vertical producer. Every used metric is
/// admitted into caller scratch before output writing. Existing font variation
/// access owns its internal phantom scratch; this route is not allocation-free.
pub fn native_layout_vertical_open_type_into(
    font: &SfntFace,
    coordinates: &[i16],
    glyphs: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    options: NativeTextLayoutOptions,
    scratch: &mut [NativeVerticalMetrics],
    output: &mut [NativePositionedTextGlyph],
    columns: &mut [NativePositionedTextColumn],
) -> Result<[usize; 2], FontError> {
    let required = native_vertical_requirements(glyphs, breaks, options)?;
    if scratch.len() < required.glyph_capacity
        || output.len() < required.glyph_capacity
        || columns.len() < required.column_capacity
    {
        return Err(FontError::Capacity);
    }
    if font.header_metrics().is_none_or(|h| h.units_per_em == 0) {
        return Err(FontError::InvalidFace);
    }
    for i in 0..required.glyph_capacity {
        let p = projection::project(
            font,
            coordinates,
            glyphs[i],
            options.scale,
            options.direction,
        )?;
        scratch[i] = NativeVerticalMetrics {
            advance_x: p.advance_x,
            advance_y: p.advance_y,
            offset_x: p.offset_x,
            offset_y: p.offset_y,
        };
    }
    Ok(write(
        glyphs,
        breaks,
        options,
        required,
        output,
        columns,
        |i, _| scratch[i],
    ))
}
/// Original complete-column metrics, independent of glyph ink or interaction.
pub fn native_measure_text_columns(
    columns: &[NativePositionedTextColumn],
    maximum_width: f32,
) -> Result<NativeTextLayoutMetrics, FontError> {
    if !maximum_width.is_finite() || maximum_width < 0.0 {
        return Err(FontError::InvalidArgument);
    }
    let mut result = NativeTextLayoutMetrics::default();
    let mut left = f32::INFINITY;
    let mut right = f32::NEG_INFINITY;
    for c in columns {
        let far = c.x + c.width;
        if !c.height.is_finite()
            || c.height < 0.0
            || !c.x.is_finite()
            || !c.width.is_finite()
            || c.width < 0.0
            || !far.is_finite()
        {
            return Err(FontError::InvalidArgument);
        }
        left = source_min(left, c.x);
        right = source_max(right, far);
        result.content_height = source_max(result.content_height, c.height);
    }
    if !columns.is_empty() {
        result.content_width = source_max(0.0, right - left);
    }
    result.measured_width = if maximum_width > 0.0 {
        maximum_width
    } else {
        result.content_width
    };
    result.measured_height = result.content_height;
    Ok(result)
}
// Preserve std::min/max operand order and signed-zero ties.
fn source_min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn source_max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
