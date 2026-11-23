//! Complete original base resource admission. The additive source lane requires
//! empty float interaction and separately admits every version-two source record;
//! the raw lane retains its original float interaction proof.
use super::*;
fn scalar(c: u32) -> bool {
    char::from_u32(c).is_some()
}
fn feature(f: NativeFeature) -> bool {
    f.tag.0 != 0
        && f.tag
            .0
            .to_be_bytes()
            .iter()
            .all(|c| (0x20..=0x7e).contains(c))
        && f.start <= f.end
}
fn scalars(v: &[UnicodeScalar]) -> bool {
    let mut end = 0u64;
    for s in v {
        let far = u64::from(s.input_index) + u64::from(s.input_length);
        if !scalar(s.code_point)
            || s.reserved != 0
            || s.input_length == 0
            || u64::from(s.input_index) < end
            || far > i32::MAX as u64
        {
            return false;
        }
        end = far;
    }
    true
}
fn digit(p: u32) -> bool {
    if p == 0 {
        return true;
    }
    let zero = p & 0x001f_ffff;
    p & !(0x001f_ffff | 0x8000_0000 | 0x4000_0000) == 0
        && zero != 0
        && zero <= 0x10fff6
        && (0..10).all(|i| unicode::decimal_digit_value(zero + i) == Some(i as u8))
}
fn extent(x: f32, y: f32, w: f32, h: f32) -> bool {
    [x, y, w, h, x + w, y + h]
        .into_iter()
        .all(|v| v.is_finite())
        && w >= 0.0
        && h >= 0.0
}
fn min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
pub(super) fn validate(
    v: &NativeParagraphResource,
    source_geometry: bool,
) -> Result<(), NativeParagraphGeometryError> {
    let m = v.metadata;
    let l = m.layout;
    policy(m.dpi_scale, m.projection, m.coverage)?;
    let n = v.logical_glyphs.len();
    let positioned = v.positioned_glyphs.len();
    let sources = v.source_scalars.len();
    let styles = v.styles.len();
    let runs = v.runs.len();
    let lines = v.lines.len();
    checked(
        matches!(m.paragraph_level, 0 | 1)
            && matches!(
                m.shaping_direction,
                GposDirection::Unspecified
                    | GposDirection::LeftToRight
                    | GposDirection::RightToLeft
            )
            && l.scale.is_finite()
            && l.scale > 0.0
            && l.maximum_width.is_finite()
            && l.maximum_width >= 0.0
            && l.line_height.is_finite()
            && l.line_height >= 0.0
            && matches!(
                l.direction,
                GposDirection::Unspecified
                    | GposDirection::LeftToRight
                    | GposDirection::RightToLeft
            )
            && l.trimming == NativeTextTrimming::None
            && l.collapse_width == -1.0
            && l.ellipsis_advance.is_finite()
            && l.ellipsis_advance >= 0.0
            && extent(0.0, 0.0, m.metrics.content_width, m.metrics.content_height)
            && extent(
                0.0,
                0.0,
                m.metrics.measured_width,
                m.metrics.measured_height,
            ),
    )?;
    checked(
        v.font_sources.len() <= MAX_OUTLINES
            && v.font_sources.len() <= v.font_bytes.len() / 12
            && v.font_bytes.len() <= MAX_FONT_BYTES
            && v.outlines.len() <= MAX_OUTLINES,
    )?;
    let counts = [
        sources,
        v.admitted_scalars.len(),
        styles,
        runs,
        n,
        positioned,
        lines,
        v.boxes.len(),
        v.carets.len(),
        v.variation_coordinates_16_16.len(),
        v.normalized_coordinates.len(),
        v.segments.len(),
        v.source_outline_indices.len(),
        v.run_outline_indices.len(),
        v.pre_context.len(),
        v.post_context.len(),
        v.features.len(),
    ];
    checked(counts.into_iter().all(|c| c <= MAX_SLOTS))?;
    checked(
        v.admitted_scalars.len() == sources
            && v.scalar_levels.len() == sources
            && v.device_styles.len() == styles
            && v.source_metrics.len() == styles
            && v.run_slices.len() == runs
            && v.logical_owners.len() == n
            && v.logical_cluster_ends.len() == n
            && v.logical_bidi_levels.len() == n
            && v.glyph_scales.len() == n
            && v.positioned_owners.len() == positioned
            && v.positioned_cluster_ends.len() == positioned
            && v.positioned_bidi_levels.len() == positioned
            && v.positioned_outline_indices.len() == positioned
            && v.line_origins.len() == lines
            && v.outline_owners.len() == v.outlines.len(),
    )?;
    checked(
        scalars(&v.source_scalars)
            && scalars(&v.admitted_scalars)
            && scalars(&v.pre_context)
            && scalars(&v.post_context),
    )?;
    for i in 0..sources {
        let s = v.source_scalars[i];
        let a = v.admitted_scalars[i];
        let b = v.scalar_levels[i];
        checked(
            a.input_index == s.input_index
                && a.input_length == s.input_length
                && !matches!(a.code_point, 9 | 0xfffc)
                && b.input_index == s.input_index
                && b.input_length == s.input_length
                && (0..=125).contains(&b.level)
                && b.reserved == 0,
        )?;
    }
    checked(
        v.features.iter().copied().all(feature)
            && v.normalized_coordinates
                .iter()
                .all(|c| (-16384..=16384).contains(c)),
    )?;
    let mut fonts = reserve(v.font_sources.len())?;
    let mut glyph_counts = reserve(v.font_sources.len())?;
    let mut axes = reserve(v.font_sources.len())?;
    let mut byte_end = 0usize;
    for f in &v.font_sources {
        checked(
            f.byte_offset as usize == byte_end
                && f.byte_count != 0
                && f.byte_count as usize <= v.font_bytes.len() - byte_end
                && f.face_index <= u16::MAX as u32,
        )?;
        let data = copy(&v.font_bytes[byte_end..byte_end + f.byte_count as usize])?;
        let font = SfntFace::new(data, f.face_index)
            .map_err(|_| NativeParagraphGeometryError::InvalidLayout)?;
        let h = font
            .header_metrics()
            .ok_or(NativeParagraphGeometryError::InvalidLayout)?;
        checked(h.units_per_em != 0 && u32::from(h.units_per_em) == f.units_per_em)?;
        glyph_counts.push(
            font.glyph_count()
                .ok_or(NativeParagraphGeometryError::InvalidLayout)?,
        );
        axes.push(
            font.variation_axis_count()
                .map_err(|_| NativeParagraphGeometryError::InvalidLayout)?,
        );
        fonts.push(font);
        byte_end += f.byte_count as usize;
    }
    checked(byte_end == v.font_bytes.len())?;
    let mut scalar_end = 0usize;
    let mut variation_end = 0usize;
    let mut source_bidi = false;
    for i in 0..styles {
        let s = v.styles[i];
        let d = v.device_styles[i];
        let metric = v.source_metrics[i];
        checked(
            s.scalar_start as usize == scalar_end
                && s.scalar_count != 0
                && s.scalar_count as usize <= sources - scalar_end
                && (s.font_index as usize) < fonts.len()
                && s.scale.is_finite()
                && s.scale > 0.0
                && s.feature_start as usize <= v.features.len()
                && s.feature_count as usize <= v.features.len() - s.feature_start as usize
                && digit(s.digit_substitution)
                && scalar(s.percent)
                && scalar(s.group_separator)
                && scalar(s.decimal_separator)
                && d.font_index == s.font_index
                && d.source_scale == s.scale
                && d.reserved == 0
                && d.logical_units_per_physical_pixel == 1.0 / m.dpi_scale
                && d.logical_units_per_physical_pixel * m.dpi_scale == 1.0
                && d.x_pixels_per_em_26_6 != 0
                && d.y_pixels_per_em_26_6 != 0
                && d.x_pixels_per_em_26_6 <= i32::MAX as u32
                && d.y_pixels_per_em_26_6 <= i32::MAX as u32
                && matches!(d.interpreter, 35 | 40)
                && d.x_phase_26_6 < 64
                && d.y_phase_26_6 < 64
                && d.variation_start as usize == variation_end
                && d.variation_count as usize
                    <= v.variation_coordinates_16_16.len() - variation_end
                && d.variation_count == u32::from(axes[s.font_index as usize])
                && v.normalized_coordinates.len() == usize::from(axes[s.font_index as usize])
                && metric.ascent.is_finite()
                && metric.ascent >= 0.0
                && metric.descent.is_finite()
                && metric.descent >= 0.0
                && (metric.ascent + metric.descent).is_finite(),
        )?;
        for axis in 0..axes[s.font_index as usize] {
            let normalized = fonts[s.font_index as usize]
                .normalize_variation_coordinate(
                    axis,
                    v.variation_coordinates_16_16[variation_end + usize::from(axis)],
                )
                .map_err(|_| NativeParagraphGeometryError::InvalidLayout)?;
            checked(v.normalized_coordinates[usize::from(axis)] == normalized)?;
        }
        source_bidi |= s.digit_substitution & 0x4000_0000 != 0;
        scalar_end += s.scalar_count as usize;
        variation_end += d.variation_count as usize;
    }
    checked(
        scalar_end == sources
            && variation_end == v.variation_coordinates_16_16.len()
            && source_bidi == m.source_digit_bidi,
    )?;
    let mut logical_end = 0usize;
    let mut source_end = 0usize;
    let mut run_end = 0usize;
    let mut outline_end = 0usize;
    let mut segment_end = 0usize;
    scalar_end = 0;
    for i in 0..runs {
        let r = v.runs[i];
        let slice = v.run_slices[i];
        checked(
            r.scalar_start as usize == scalar_end
                && r.scalar_count != 0
                && r.scalar_count as usize <= sources - scalar_end
                && r.logical_start as usize == logical_end
                && r.logical_count as usize <= n - logical_end
                && (r.style_index as usize) < styles
                && (r.font_index as usize) < fonts.len()
                && (0..=125).contains(&r.bidi_level)
                && slice.source_start as usize == source_end
                && slice.source_count == r.source_descriptor_count
                && slice.source_count as usize <= v.source_outline_indices.len() - source_end
                && slice.run_start as usize == run_end
                && slice.run_count == r.logical_count
                && slice.run_count as usize <= v.run_outline_indices.len() - run_end
                && slice.outline_start as usize == outline_end
                && slice.outline_count as usize <= v.outlines.len() - outline_end
                && slice.segment_start as usize == segment_end
                && slice.segment_count as usize <= v.segments.len() - segment_end,
        )?;
        let style = v.styles[r.style_index as usize];
        let device = v.device_styles[r.style_index as usize];
        checked(
            r.font_index == style.font_index
                && r.source_scale == style.scale
                && r.logical_units_per_physical_pixel == device.logical_units_per_physical_pixel
                && r.scalar_start >= style.scalar_start
                && r.scalar_start - style.scalar_start <= style.scalar_count
                && r.scalar_count <= style.scalar_count - (r.scalar_start - style.scalar_start),
        )?;
        for j in 0..r.scalar_count as usize {
            checked(i32::from(v.scalar_levels[scalar_end + j].level) == r.bidi_level)?;
        }
        let mut ink = 0;
        for j in 0..slice.source_count as usize {
            let index = v.source_outline_indices[source_end + j];
            if index == NATIVE_HINTED_NO_OUTLINE {
                continue;
            }
            checked(ink < slice.outline_count as usize && index as usize == outline_end + ink)?;
            let owner = v.outline_owners[index as usize];
            checked(owner.run_index == i as u32 && owner.descriptor_index == j as u32)?;
            ink += 1;
        }
        checked(ink == slice.outline_count as usize)?;
        let mut occurrences = alloc(r.logical_count as usize, false)?;
        let mut descriptor_ids = alloc(slice.source_count as usize, u32::MAX)?;
        let mut reversed_end = r.logical_count;
        let mut group_start = 0;
        let mut group_end = 0;
        for j in 0..r.logical_count {
            let index = logical_end + j as usize;
            let g = v.logical_glyphs[index];
            let o = v.logical_owners[index];
            checked(
                o.run_index == i as u32
                    && o.run_glyph_index < r.logical_count
                    && !occurrences[o.run_glyph_index as usize]
                    && o.descriptor_index < slice.source_count
                    && g.glyph_id < u32::from(glyph_counts[r.font_index as usize])
                    && scalar(g.code_point)
                    && g.flags & !7 == 0
                    && g.cluster >= 0
                    && v.logical_cluster_ends[index] > g.cluster
                    && i32::from(v.logical_bidi_levels[index]) == r.bidi_level
                    && v.glyph_scales[index] == r.logical_units_per_physical_pixel / 64.0
                    && v.run_outline_indices[run_end + o.run_glyph_index as usize]
                        == v.source_outline_indices[source_end + o.descriptor_index as usize],
            )?;
            let scalars = &v.source_scalars
                [r.scalar_start as usize..(r.scalar_start + r.scalar_count) as usize];
            let cluster = scalars.partition_point(|s| s.input_index < g.cluster as u32);
            checked(
                scalars
                    .get(cluster)
                    .is_some_and(|s| s.input_index == g.cluster as u32)
                    && v.source_scalars.last().is_some_and(|s| {
                        v.logical_cluster_ends[index] as u64
                            <= u64::from(s.input_index) + u64::from(s.input_length)
                    })
                    && (descriptor_ids[o.descriptor_index as usize] == u32::MAX
                        || descriptor_ids[o.descriptor_index as usize] == g.glyph_id),
            )?;
            descriptor_ids[o.descriptor_index as usize] = g.glyph_id;
            if r.bidi_level & 1 == 0 {
                checked(o.run_glyph_index == j)?;
            } else {
                if j == group_end {
                    group_start = j;
                    group_end = j + 1;
                    while group_end < r.logical_count
                        && v.logical_glyphs[logical_end + group_end as usize].cluster == g.cluster
                    {
                        group_end += 1;
                    }
                    reversed_end -= group_end - group_start;
                }
                checked(o.run_glyph_index == reversed_end + j - group_start)?;
            }
            occurrences[o.run_glyph_index as usize] = true;
            if index != 0 {
                let previous = v.logical_glyphs[index - 1].cluster;
                checked(
                    g.cluster >= previous
                        && (g.cluster <= previous
                            || v.logical_cluster_ends[index - 1] <= g.cluster)
                        && (g.cluster != previous
                            || v.logical_cluster_ends[index] == v.logical_cluster_ends[index - 1]),
                )?;
            }
        }
        scalar_end += r.scalar_count as usize;
        logical_end += r.logical_count as usize;
        source_end += slice.source_count as usize;
        run_end += slice.run_count as usize;
        outline_end += slice.outline_count as usize;
        segment_end += slice.segment_count as usize;
    }
    checked(
        scalar_end == sources
            && logical_end == n
            && source_end == v.source_outline_indices.len()
            && run_end == v.run_outline_indices.len()
            && outline_end == v.outlines.len()
            && segment_end == v.segments.len(),
    )?;
    let mut next_segment = 0usize;
    for (i, o) in v.outlines.iter().enumerate() {
        checked(
            o.segment_offset == next_segment
                && o.segment_count != 0
                && o.segment_count <= v.segments.len() - next_segment
                && o.raster_scale == 1.0
                && o.subpixel_x == 0.0
                && o.bounds.iter().all(|v| v.is_finite())
                && o.bounds[2] > o.bounds[0]
                && o.bounds[3] > o.bounds[1],
        )?;
        let mut first = v.segments[next_segment].points[0];
        let mut end = first;
        for segment in &v.segments[next_segment..next_segment + o.segment_count] {
            checked(
                segment.kind <= 2
                    && segment.parameters == [0; 3]
                    && segment.points.iter().flatten().all(|v| v.is_finite()),
            )?;
            if segment.points[0] != end {
                checked(end == first)?;
                first = segment.points[0];
            }
            end = segment.points[segment.kind as usize + 1];
            let inside = |p: [f32; 2]| {
                p[0] >= o.bounds[0]
                    && p[0] <= o.bounds[2]
                    && p[1] >= o.bounds[1]
                    && p[1] <= o.bounds[3]
            };
            checked(
                segment.points[..segment.kind as usize + 2]
                    .iter()
                    .copied()
                    .all(inside),
            )?;
        }
        checked(end == first)?;
        let slice = v.run_slices[v.outline_owners[i].run_index as usize];
        checked(
            o.segment_offset >= slice.segment_start as usize
                && o.segment_offset + o.segment_count
                    <= slice.segment_start as usize + slice.segment_count as usize,
        )?;
        next_segment += o.segment_count;
    }
    checked(next_segment == v.segments.len())?;
    for (i, g) in v.positioned_glyphs.iter().enumerate() {
        let owner = v.positioned_owners[i];
        let index = g.glyph_index as usize;
        checked(index < n && owner == v.logical_owners[index])?;
        checked(
            [
                g.x,
                g.y,
                g.advance_x,
                g.advance_y,
                g.x + g.advance_x,
                g.y + g.advance_y,
            ]
            .into_iter()
            .all(|v| v.is_finite())
                && g.glyph_id == v.logical_glyphs[index].glyph_id
                && g.cluster == v.logical_glyphs[index].cluster
                && g.font_index == v.runs[owner.run_index as usize].font_index
                && v.positioned_cluster_ends[i] == v.logical_cluster_ends[index]
                && (0..=125).contains(&v.positioned_bidi_levels[i])
                && v.positioned_outline_indices[i]
                    == v.source_outline_indices[v.run_slices[owner.run_index as usize].source_start
                        as usize
                        + owner.descriptor_index as usize],
        )?;
    }
    let mut covered = 0;
    for (i, line) in v.lines.iter().enumerate() {
        checked(
            line.glyph_start as usize == covered
                && line.glyph_count != 0
                && line.glyph_count as usize <= positioned - covered
                && line.input_start >= 0
                && line.input_end >= line.input_start
                && extent(0.0, line.baseline_y, line.width, line.height)
                && v.line_origins[i].is_finite()
                && line.clipped <= 1
                && line.flags & !1 == 0
                && line.reserved0 == 0
                && line.reserved1 == 0,
        )?;
        let mut first = u32::MAX;
        let mut last = 0;
        for g in &v.positioned_glyphs[covered..covered + line.glyph_count as usize] {
            first = first.min(g.glyph_index);
            last = last.max(g.glyph_index);
        }
        let input_end = if (last as usize) + 1 < n {
            v.logical_glyphs[last as usize + 1].cluster
        } else {
            v.logical_glyphs[last as usize]
                .cluster
                .checked_add(1)
                .ok_or(NativeParagraphGeometryError::InvalidLayout)?
        };
        checked(
            line.input_start == v.logical_glyphs[first as usize].cluster
                && line.input_end == input_end,
        )?;
        covered += line.glyph_count as usize;
    }
    checked(covered == positioned)?;
    for b in &v.boxes {
        checked(
            b.input_start >= 0
                && b.input_end > b.input_start
                && (b.line_index as usize) < lines
                && (0..=125).contains(&b.bidi_level)
                && b.reserved == [0; 3]
                && extent(b.x, b.y, b.width, b.height),
        )?;
    }
    for c in &v.carets {
        checked(
            c.input_position >= 0
                && (c.line_index as usize) < lines
                && (0..=125).contains(&c.bidi_level)
                && c.trailing <= 1
                && c.reserved == [0; 2]
                && extent(c.x, c.y, 0.0, c.height),
        )?;
    }
    if source_geometry {
        checked(v.boxes.is_empty() && v.carets.is_empty())
    } else {
        interaction(v)
    }
}
fn interaction(v: &NativeParagraphResource) -> Result<(), NativeParagraphGeometryError> {
    let exact = |a: f32, b: f32| a.to_bits() == b.to_bits();
    let mut box_index = 0;
    let mut caret_index = 0;
    let mut previous: Option<NativeParagraphCaretRecord> = None;
    let mut top = 0f64;
    for (line_index, line) in v.lines.iter().enumerate() {
        let bottom = top + f64::from(line.height);
        checked(
            bottom.is_finite()
                && bottom <= f64::from(f32::MAX)
                && line.baseline_y >= top as f32
                && line.baseline_y <= bottom as f32,
        )?;
        let mut pen = v.line_origins[line_index];
        let mut index = line.glyph_start as usize;
        let end = index + line.glyph_count as usize;
        while index < end {
            let start = index;
            let cluster = v.positioned_glyphs[index].cluster;
            let mut left = pen;
            let mut right = pen;
            let mut cluster_end = v.positioned_cluster_ends[index];
            loop {
                let next = pen + v.positioned_glyphs[index].advance_x;
                checked(next.is_finite())?;
                left = min(left, min(pen, next));
                right = max(right, max(pen, next));
                pen = next;
                cluster_end = cluster_end.max(v.positioned_cluster_ends[index]);
                index += 1;
                if index == end || v.positioned_glyphs[index].cluster != cluster {
                    break;
                }
            }
            let b = NativeParagraphBoxRecord {
                input_start: cluster,
                input_end: cluster_end,
                line_index: line_index as u32,
                bidi_level: v.positioned_bidi_levels[start],
                reserved: [0; 3],
                x: left,
                y: top as f32,
                width: max(0.0, right - left),
                height: line.height,
            };
            let a = v
                .boxes
                .get(box_index)
                .ok_or(NativeParagraphGeometryError::InvalidLayout)?;
            checked(
                a.input_start == b.input_start
                    && a.input_end == b.input_end
                    && a.line_index == b.line_index
                    && a.bidi_level == b.bidi_level
                    && a.reserved == b.reserved
                    && exact(a.x, b.x)
                    && exact(a.y, b.y)
                    && exact(a.width, b.width)
                    && exact(a.height, b.height),
            )?;
            box_index += 1;
            let rtl = b.bidi_level & 1 != 0;
            let leading = NativeParagraphCaretRecord {
                input_position: if rtl { b.input_end } else { b.input_start },
                line_index: b.line_index,
                x: b.x,
                y: b.y,
                height: b.height,
                bidi_level: b.bidi_level,
                trailing: u8::from(rtl),
                reserved: [0; 2],
            };
            let trailing = NativeParagraphCaretRecord {
                input_position: if rtl { b.input_start } else { b.input_end },
                x: b.x + b.width,
                trailing: u8::from(!rtl),
                ..leading
            };
            for c in [leading, trailing] {
                if previous.is_some_and(|p| {
                    p.input_position == c.input_position
                        && p.trailing == c.trailing
                        && (p.x - c.x).abs() < 0.0001
                        && (p.y - c.y).abs() < 0.0001
                }) {
                    continue;
                }
                let a = v
                    .carets
                    .get(caret_index)
                    .ok_or(NativeParagraphGeometryError::InvalidLayout)?;
                checked(
                    a.input_position == c.input_position
                        && a.line_index == c.line_index
                        && a.bidi_level == c.bidi_level
                        && a.trailing == c.trailing
                        && a.reserved == c.reserved
                        && exact(a.x, c.x)
                        && exact(a.y, c.y)
                        && exact(a.height, c.height),
                )?;
                caret_index += 1;
                previous = Some(c);
            }
        }
        top = bottom;
    }
    checked(box_index == v.boxes.len() && caret_index == v.carets.len())
}
