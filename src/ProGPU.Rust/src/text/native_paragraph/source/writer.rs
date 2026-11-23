//! Original double measured writer; every float output is narrowed independently.
use super::*;
pub(super) struct Written {
    pub raster_glyphs: Vec<NativePositionedTextGlyph>,
    pub raster_lines: Vec<NativePositionedTextLine>,
    pub levels: Vec<i8>,
    pub origins: Vec<f32>,
    pub frames: Vec<NativeTextLineFrame>,
    pub glyphs: Vec<NativeSourceGlyphPosition>,
    pub lines: Vec<NativeSourceLineMetrics>,
}
fn max(a: f64, b: f64) -> f64 {
    if a < b { b } else { a }
}
fn finite(v: f64) -> bool {
    v.is_finite() && v.abs() <= f64::from(f32::MAX)
}
fn envelope(
    items: &[NativeSourceItemMetrics],
) -> Result<NativeSourceItemMetrics, NativeSourceError> {
    // Original paired independent SSE2/NEON reductions, separate from the ordered
    // double line-top prefix. The target intrinsic keeps its signed-zero rule.
    let mut peak = wide::f64x2::splat(0.0);
    for m in items {
        require(
            m.ascent.is_finite()
                && m.ascent >= 0.0
                && m.ascent <= f64::from(f32::MAX)
                && m.descent.is_finite()
                && m.descent >= 0.0
                && m.descent <= f64::from(f32::MAX),
        )?;
        peak = peak.max(wide::f64x2::from([m.ascent, m.descent]));
    }
    let pair = peak.to_array();
    Ok(NativeSourceItemMetrics {
        ascent: pair[0],
        descent: pair[1],
    })
}

fn safe(g: &[ShapingGlyph], i: usize) -> bool {
    i == 0 || i >= g.len() || g[i - 1].cluster != g[i].cluster && g[i].flags & 1 == 0
}
fn scan(
    g: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    metrics: &[NativeSourceGlyphMetrics],
    start: usize,
    o: NativeSourceOptions,
    final_line: bool,
) -> NativeSourceFittedLine {
    let (mut width, mut visible, mut break_width, mut cluster_width) = (0.0, 0.0, 0.0, 0.0);
    let (mut last_break, mut last_cluster) = (start, start);
    let result = |end: usize, width: f64, clipped: bool| NativeSourceFittedLine {
        glyph_start: start as u32,
        glyph_count: (end - start) as u32,
        width,
        clipped: u32::from(clipped),
        reserved: 0,
    };
    for i in start..g.len() {
        if i > start && safe(g, i) {
            last_cluster = i;
            cluster_width = width;
        }
        let next = width + metrics[i].advance_x;
        if !next.is_finite() {
            return result(i + 1, next, false);
        }
        let legal = breaks[i] != LineBreakKind::Prohibited && safe(g, i + 1);
        let mandatory = legal && breaks[i] == LineBreakKind::Mandatory;
        if !is_native_trailing_space(g[i].code_point) {
            visible = next;
        }
        let trailing = legal
            && is_native_trailing_space(g[i].code_point)
            && (o.maximum_width <= 0.0 || visible <= o.maximum_width);
        if o.maximum_width > 0.0 && next > o.maximum_width && i > start && !trailing {
            if last_break > start {
                return result(last_break, break_width, final_line);
            }
            if !o.allow_emergency_break {
                if legal {
                    return result(i + 1, next, final_line);
                }
                width = next;
                continue;
            }
            if last_cluster > start {
                return result(last_cluster, cluster_width, final_line);
            }
            let mut end = i + 1;
            let mut hard = next;
            while end < g.len() && !safe(g, end) {
                hard += metrics[end].advance_x;
                end += 1;
            }
            return result(end, hard, final_line);
        }
        if mandatory {
            return result(i + 1, next, false);
        }
        if legal {
            last_break = i + 1;
            break_width = next;
        }
        width = next;
    }
    result(g.len(), width, false)
}
#[allow(clippy::too_many_arguments)]
pub(super) fn write(
    logical: &[ShapingGlyph],
    breaks: &[LineBreakKind],
    levels: &[i8],
    scales: &[f32],
    paragraph: i8,
    layout: NativeTextLayoutOptions,
    o: NativeSourceOptions,
    metrics: &[NativeSourceGlyphMetrics],
    items: &[NativeSourceItemMetrics],
    fitted: &[NativeSourceFittedLine],
    width_policy: NativeSourceWidthPolicy,
) -> Result<Written, NativeSourceError> {
    let count = logical.len();
    require(
        count <= u32::MAX as usize
            && breaks.len() == count
            && levels.len() == count
            && (scales.is_empty() || scales.len() == count)
            && metrics.len() == count
            && items.len() == count
            && (paragraph == 0 || paragraph == 1)
            && levels.iter().all(|v| (0..=125).contains(v))
            && scales.iter().all(|v| v.is_finite() && *v > 0.0)
            && width_policy.is_valid()
            && layout.trimming == NativeTextTrimming::None
            && layout.alignment != NativeTextAlignment::Justify
            && layout.scale.is_finite()
            && layout.scale > 0.0
            && layout.maximum_width.is_finite()
            && layout.maximum_width >= 0.0
            && layout.line_height.is_finite()
            && layout.line_height >= 0.0
            && matches!(
                layout.direction,
                GposDirection::LeftToRight | GposDirection::RightToLeft
            )
            && layout.ellipsis_advance.is_finite()
            && layout.ellipsis_advance >= 0.0
            && (layout.ellipsis_advance * layout.scale).is_finite()
            && layout.collapse_width == -1.0
            && o.maximum_width.is_finite()
            && o.maximum_width >= 0.0
            && o.line_height.is_finite()
            && o.line_height >= 0.0
            && o.tab_origin.is_finite(),
    )?;
    require(
        !matches!(width_policy, NativeSourceWidthPolicy::IdealUnits { .. })
            || !fitted.is_empty()
            || count == 0,
    )?;
    let maximum = envelope(items)?;
    require(
        max(o.line_height, maximum.ascent + maximum.descent) * count as f64 <= f64::from(f32::MAX),
    )?;
    for (g, &scale) in logical.iter().zip(scales) {
        let scaled = (wide::i32x4::from([g.advance_x, g.advance_y, g.offset_x, g.offset_y])
            .round_float()
            * wide::f32x4::splat(scale))
        .to_array();
        require(scaled.iter().all(|v| v.is_finite()))?;
    }
    for (g, m) in logical.iter().zip(metrics) {
        require(
            g.glyph_id < u32::MAX - 1
                && finite(m.advance_x)
                && m.advance_y == 0.0
                && finite(m.offset_x)
                && finite(m.offset_y),
        )?;
    }
    let mut scanned = Vec::new();
    let lines = if !fitted.is_empty() {
        require(
            fitted.len() <= u32::MAX as usize
                && (layout.maximum_lines == 0 || fitted.len() <= layout.maximum_lines as usize),
        )?;
        let mut start = 0;
        for line in fitted {
            require(
                line.glyph_start as usize == start
                    && line.glyph_count != 0
                    && line.glyph_count as usize <= count - start
                    && finite(line.width)
                    && line.width >= 0.0,
            )?;
            let end = start + line.glyph_count as usize;
            let mut width = NativeSourceWidthAccumulator::default();
            for m in &metrics[start..end] {
                width.append(m.advance_x, width_policy)?;
            }
            require(width.width == line.width)?;
            start = end;
        }
        require(
            start == count
                || layout.maximum_lines != 0 && fitted.len() == layout.maximum_lines as usize,
        )?;
        fitted
    } else {
        let mut start = 0;
        while start < count {
            let final_line =
                layout.maximum_lines != 0 && scanned.len() + 1 >= layout.maximum_lines as usize;
            let line = scan(logical, breaks, metrics, start, o, final_line);
            require(
                line.glyph_count != 0
                    && line.glyph_count as usize <= count - start
                    && finite(line.width),
            )?;
            start += line.glyph_count as usize;
            scanned.try_reserve(1).map_err(|_| FontError::Capacity)?;
            scanned.push(line);
            if final_line {
                break;
            }
        }
        &scanned
    };
    let mut result = Written {
        raster_glyphs: reserve(count)?,
        raster_lines: reserve(lines.len())?,
        levels: reserve(count)?,
        origins: reserve(lines.len())?,
        frames: reserve(lines.len())?,
        glyphs: reserve(count)?,
        lines: reserve(lines.len())?,
    };
    let mut groups = alloc(count, NativeVisualClusterGroup::default())?;
    let mut indices = alloc(count, 0u32)?;
    let mut top = 0.0;
    for line in lines {
        let start = line.glyph_start as usize;
        let end = start + line.glyph_count as usize;
        let line_logical = &logical[start..end];
        let line_levels = &levels[start..end];
        let mut preserve = false;
        let mut content_width = line.width;
        let mut leading_space = 0.0;
        if o.preserve_soft_wrap_space_geometry
            && end < count
            && breaks[end - 1] != LineBreakKind::Mandatory
        {
            let tail = native_soft_wrap_space_start(line_logical);
            preserve = tail != line_logical.len();
            if preserve {
                let level = line_levels[tail];
                require(line_levels[tail..].iter().all(|v| *v == level))?;
                if level & 1 != paragraph & 1 {
                    let mut content = NativeSourceWidthAccumulator::default();
                    let mut spaces = NativeSourceWidthAccumulator::default();
                    for (i, m) in metrics[start..end].iter().enumerate() {
                        require(i < tail || m.advance_x >= 0.0)?;
                        if i < tail {
                            content.append(m.advance_x, width_policy)?;
                        } else {
                            spaces.append(m.advance_x, width_policy)?;
                        }
                    }
                    require(content.width >= 0.0 && spaces.width >= 0.0)?;
                    content_width = content.width;
                    if level & 1 != 0 {
                        leading_space = spaces.width;
                    }
                }
            }
        }
        let visual = native_visual_indices_into(
            line_logical,
            line_levels,
            paragraph,
            &mut groups,
            &mut indices,
            preserve,
        )?;
        let output_start = result.glyphs.len();
        let line_items = envelope(&items[start..end])?;
        let height = max(o.line_height, line_items.ascent + line_items.descent);
        let baseline = top + line_items.ascent;
        let mut cursor_x = 0.0 - leading_space;
        let mut cursor_y = baseline;
        let mut group = 0;
        let mut group_end = if visual == 0 {
            0
        } else {
            groups[0].glyph_count as usize
        };
        for (i, &index) in indices[..visual].iter().enumerate() {
            let index = start + index as usize;
            let g = logical[index];
            let m = metrics[index];
            if i == group_end {
                group += 1;
                group_end += groups[group].glyph_count as usize;
            }
            let position = NativeSourceGlyphPosition {
                x: cursor_x + m.offset_x,
                y: cursor_y + m.offset_y,
                advance_x: m.advance_x,
                advance_y: m.advance_y,
                cluster: g.cluster,
                reserved: 0,
            };
            require(finite(position.x) && finite(position.y))?;
            result.glyphs.push(position);
            result.raster_glyphs.push(NativePositionedTextGlyph {
                glyph_index: index as u32,
                glyph_id: g.glyph_id,
                cluster: g.cluster,
                position: [position.x as f32, position.y as f32],
                advance: [m.advance_x as f32, m.advance_y as f32],
            });
            result.levels.push(groups[group].bidi_level);
            cursor_x += m.advance_x;
            cursor_y += m.advance_y;
        }
        let shift = if o.maximum_width <= content_width {
            0.0
        } else {
            match layout.alignment {
                NativeTextAlignment::Center => (o.maximum_width - content_width) * 0.5,
                NativeTextAlignment::Right => o.maximum_width - content_width,
                _ => 0.0,
            }
        };
        let origin = if shift > 0.0 { 0.0 + shift } else { 0.0 };
        result.origins.push(origin as f32);
        if shift > 0.0 {
            for i in output_start..result.glyphs.len() {
                result.glyphs[i].x += shift;
                require(finite(result.glyphs[i].x))?;
                result.raster_glyphs[i].position[0] = result.glyphs[i].x as f32;
            }
        }
        let output_count = result.glyphs.len() - output_start;
        let input_end = if end < count {
            logical[end].cluster
        } else {
            logical[end - 1]
                .cluster
                .checked_add(1)
                .ok_or(NativeSourceError::InvalidArgument)?
        };
        let final_line =
            layout.maximum_lines != 0 && result.lines.len() + 1 >= layout.maximum_lines as usize;
        result.raster_lines.push(NativePositionedTextLine {
            glyph_start: output_start as u32,
            glyph_count: output_count as u32,
            input_start: logical[start].cluster,
            input_end,
            width: content_width as f32,
            baseline_y: baseline as f32,
            height: height as f32,
            clipped: line.clipped != 0 || final_line && end < count,
            flags: 0,
        });
        result.frames.push(NativeTextLineFrame {
            top,
            ascent: line_items.ascent as f32,
            measured: true,
        });
        result.lines.push(NativeSourceLineMetrics {
            width: content_width,
            top,
            height,
            baseline_offset: line_items.ascent,
            baseline_y: baseline,
            origin_x: origin,
            glyph_start: output_start as u32,
            glyph_count: output_count as u32,
        });
        top += height;
    }
    Ok(result)
}
