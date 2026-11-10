//! Original v2 whole-resource admission; deterministic writer replay uses only
//! retained records and never loads a font, shapes text or captures fresh pixels.
use super::*;
fn exact(a: f64, b: f64) -> bool {
    a.to_bits() == b.to_bits()
}
fn exact32(a: f32, b: f32) -> bool {
    a.to_bits() == b.to_bits()
}
fn same_identity(a: ShapingGlyph, b: ShapingGlyph) -> bool {
    a.glyph_id == b.glyph_id
        && a.code_point == b.code_point
        && a.cluster == b.cluster
        && a.flags & !7 == 0
        && a.advance_y != i32::MIN
        && a.offset_y != i32::MIN
}
fn metrics(a: NativeSourceGlyphMetrics, b: NativeSourceGlyphMetrics) -> bool {
    exact(a.advance_x, b.advance_x)
        && exact(a.advance_y, b.advance_y)
        && exact(a.offset_x, b.offset_x)
        && exact(a.offset_y, b.offset_y)
}
fn prepared_edge(p: &[ShapingGlyph], edge: usize) -> bool {
    edge <= p.len()
        && (edge == 0
            || edge == p.len()
            || p[edge - 1].cluster != p[edge].cluster && p[edge].flags & 1 == 0)
}
/// Base admission has already established all font/style/run/owner indices.
pub(super) fn admit_records(
    v: &NativeParagraphResource,
    s: &NativeSourceResource,
) -> Result<(), NativeSourceError> {
    let o = s.metadata.options;
    let n = v.logical_glyphs.len();
    let count = v.positioned_glyphs.len();
    let line_count = v.lines.len();
    let first = s.metadata.first_logical_glyph as usize;
    require(
        s.metadata.version == 2
            && s.metadata.flags == 0
            && s.metadata.reserved == 0
            && (!o.preserve_whole_run_placement || s.slices.is_empty())
            && o.em_size.is_finite()
            && o.em_size > 0.0
            && o.pixels_per_dip.is_finite()
            && o.pixels_per_dip > 0.0
            && exact32(o.pixels_per_dip as f32, v.metadata.dpi_scale)
            && o.maximum_width.is_finite()
            && o.maximum_width >= 0.0
            && o.line_height.is_finite()
            && o.line_height >= 0.0
            && o.tab_origin.is_finite()
            && exact32(o.maximum_width as f32, v.metadata.layout.maximum_width)
            && exact32(o.line_height as f32, v.metadata.layout.line_height)
            && v.metadata.layout.alignment != NativeTextAlignment::Justify
            && s.styles.len() == v.styles.len()
            && s.logical_metrics.len() == n
            && s.glyph_metrics.len() == count
            && s.line_metrics.len() == line_count
            && v.boxes.is_empty()
            && v.carets.is_empty()
            && first <= n
            && s.slices.len() <= n
            && s.metadata.intrinsic_widths.minimum.is_finite()
            && s.metadata.intrinsic_widths.minimum >= 0.0
            && s.metadata.intrinsic_widths.maximum.is_finite()
            && s.metadata.intrinsic_widths.maximum >= 0.0
            && (o.measure_intrinsic_widths
                || s.metadata.intrinsic_widths.minimum == 0.0
                    && s.metadata.intrinsic_widths.maximum == 0.0)
            && s.raw_logical_glyphs.len() == n
            && s.effective_logical_glyphs.len() == n
            && s.positioning_runs.len() == v.runs.len()
            && s.slice_indices.len() == n
            && s.fitted_lines.len() == line_count
            && s.breaks_after.len() == n,
    )?;
    // The original additive lane limits every transported span plus the exact
    // writer scratch inventory to 256 MiB, independently of the base font budget.
    const LIMIT: usize = 256 * 1024 * 1024;
    let scratch = std::mem::size_of::<ShapingGlyph>()
        + std::mem::size_of::<NativeSourceGlyphMetrics>()
        + std::mem::size_of::<NativeSourceItemMetrics>()
        + std::mem::size_of::<NativePositionedTextGlyph>()
        + std::mem::size_of::<NativePositionedTextLine>()
        + std::mem::size_of::<NativeSourceGlyphPosition>()
        + std::mem::size_of::<NativeSourceLineMetrics>()
        + std::mem::size_of::<NativeVisualClusterGroup>()
        + std::mem::size_of::<LineBreakKind>()
        + 4
        + 1
        + 4;
    let extra = n
        .checked_mul(scratch)
        .and_then(|b| {
            b.checked_add(
                s.styles
                    .len()
                    .checked_mul(std::mem::size_of::<NativeSourcePolicy>())?,
            )
        })
        .and_then(|b| {
            b.checked_add(
                line_count.checked_mul(std::mem::size_of::<NativeSourceFittedLine>() + 8)?,
            )
        })
        .ok_or(NativeSourceError::InvalidArgument)?;
    require(
        s.storage_bytes()
            .and_then(|b| b.checked_add(extra))
            .is_some_and(|b| b <= LIMIT),
    )?;
    let mut policies = reserve(s.styles.len())?;
    for (i, &original) in s.styles.iter().enumerate() {
        let style = v.styles[i];
        let device = v.device_styles[i];
        let policy = o.policy(original.em_size);
        let selected = policy.resolve_device()?;
        require(
            original.ascent.is_finite()
                && original.ascent >= 0.0
                && original.descent.is_finite()
                && original.descent >= 0.0
                && exact32(original.ascent as f32, v.source_metrics[i].ascent)
                && exact32(original.descent as f32, v.source_metrics[i].descent)
                && original.em_size as f32
                    / v.font_sources[style.font_index as usize].units_per_em as f32
                    == style.scale
                && device.x_pixels_per_em_26_6 == selected.pixels_per_em_26_6
                && device.y_pixels_per_em_26_6 == selected.pixels_per_em_26_6
                && device.logical_units_per_physical_pixel
                    == selected.logical_units_per_physical_pixel,
        )?;
        policies.push(policy);
    }
    let mut prepared_end = 0;
    for (i, position) in s.positioning_runs.iter().enumerate() {
        let run = v.runs[i];
        require(
            position.reserved == [0; 2]
                && position.prepared_start as usize == prepared_end
                && position.prepared_count as usize <= s.prepared_glyphs.len() - prepared_end
                && (position.prepared_count == 0
                    || position.prepared_count == run.logical_count
                        && position.prepared_count == run.source_descriptor_count),
        )?;
        let prepared =
            &s.prepared_glyphs[prepared_end..prepared_end + position.prepared_count as usize];
        if !prepared.is_empty() {
            for j in 0..run.logical_count as usize {
                let index = run.logical_start as usize + j;
                let owner = v.logical_owners[index];
                let g = prepared
                    .get(owner.descriptor_index as usize)
                    .ok_or(NativeSourceError::InvalidArgument)?;
                require(
                    same_identity(*g, s.raw_logical_glyphs[index])
                        && g.flags & s.raw_logical_glyphs[index].flags == g.flags
                        && owner.run_glyph_index
                            == if run.bidi_level & 1 == 0 {
                                owner.descriptor_index
                            } else {
                                run.logical_count - 1 - owner.descriptor_index
                            },
                )?;
            }
            require(prepared.windows(2).all(|p| p[0].cluster <= p[1].cluster))?;
        }
        prepared_end += prepared.len();
    }
    require(prepared_end == s.prepared_glyphs.len())?;
    let safe_boundary = |index: usize| {
        if index == 0 || index == n {
            return true;
        }
        let g = v.logical_glyphs[index];
        if g.cluster == v.logical_glyphs[index - 1].cluster {
            return false;
        }
        let run = v.logical_owners[index].run_index as usize;
        if run != v.logical_owners[index - 1].run_index as usize || g.flags & 1 == 0 {
            return true;
        }
        let position = s.positioning_runs[run];
        let prepared = &s.prepared_glyphs[position.prepared_start as usize
            ..(position.prepared_start + position.prepared_count) as usize];
        let edge = prepared.partition_point(|p| p.cluster < g.cluster);
        edge < prepared.len()
            && prepared[edge].cluster == g.cluster
            && prepared_edge(prepared, edge)
    };
    require(safe_boundary(first))?;
    let mut declared = 0;
    for (i, slice) in s.slices.iter().enumerate() {
        require(
            slice.reserved == 0
                && (slice.run_index as usize) < v.runs.len()
                && slice.logical_count != 0
                && slice.logical_count == slice.prepared_count
                && slice.logical_start as usize <= n
                && slice.logical_count as usize <= n - slice.logical_start as usize
                && slice.logical_count as usize <= n - declared,
        )?;
        let position = s.positioning_runs[slice.run_index as usize];
        let run = v.runs[slice.run_index as usize];
        require(
            position.prepared_count != 0
                && slice.prepared_start <= position.prepared_count
                && slice.prepared_count <= position.prepared_count - slice.prepared_start
                && slice.logical_start >= run.logical_start
                && slice.logical_start - run.logical_start <= run.logical_count
                && slice.logical_count
                    <= run.logical_count - (slice.logical_start - run.logical_start),
        )?;
        let prepared = &s.prepared_glyphs[position.prepared_start as usize
            ..(position.prepared_start + position.prepared_count) as usize];
        require(
            prepared_edge(prepared, slice.prepared_start as usize)
                && prepared_edge(
                    prepared,
                    (slice.prepared_start + slice.prepared_count) as usize,
                ),
        )?;
        for index in
            slice.logical_start as usize..(slice.logical_start + slice.logical_count) as usize
        {
            let descriptor = v.logical_owners[index].descriptor_index;
            require(
                s.slice_indices[index] as usize == i
                    && descriptor >= slice.prepared_start
                    && descriptor - slice.prepared_start < slice.prepared_count
                    && s.effective_logical_glyphs[index].flags
                        & prepared[descriptor as usize].flags
                        == prepared[descriptor as usize].flags,
            )?;
        }
        declared += slice.logical_count as usize;
    }
    let mut projected = reserve(n)?;
    let mut items = reserve(n)?;
    let mut used = 0;
    for i in 0..n {
        let raw = s.raw_logical_glyphs[i];
        let effective = s.effective_logical_glyphs[i];
        let style = v.runs[v.logical_owners[i].run_index as usize].style_index as usize;
        require(
            same_identity(raw, v.logical_glyphs[i])
                && same_identity(effective, raw)
                && policies[style].project_advance(raw)? == v.logical_glyphs[i],
        )?;
        let m = policies[style].project_geometry(policies[style].project_advance(effective)?)?;
        require(metrics(m, s.logical_metrics[i]))?;
        if s.slice_indices[i] == u32::MAX {
            require(raw == effective)?;
        } else {
            used += 1;
            let slice = s
                .slices
                .get(s.slice_indices[i] as usize)
                .ok_or(NativeSourceError::InvalidArgument)?;
            require(
                i >= slice.logical_start as usize
                    && i - (slice.logical_start as usize) < slice.logical_count as usize,
            )?;
        }
        projected.push(m);
        items.push(NativeSourceItemMetrics {
            ascent: s.styles[style].ascent,
            descent: s.styles[style].descent,
        });
    }
    require(used == declared)?;
    let width_policy = NativeSourceWidthPolicy::select(&policies);
    let mut end = first;
    for line in &s.fitted_lines {
        require(
            line.reserved == 0
                && line.clipped <= 1
                && line.glyph_start as usize == end - first
                && line.glyph_count != 0
                && line.glyph_count as usize <= n - end
                && line.width.is_finite()
                && line.width >= 0.0
                && safe_boundary(end + line.glyph_count as usize),
        )?;
        let last = end + line.glyph_count as usize;
        let mut width = NativeSourceWidthAccumulator::default();
        for (i, m) in projected.iter().enumerate().take(last).skip(end) {
            width.append(m.advance_x, width_policy)?;
            require(i + 1 == last || s.breaks_after[i] != LineBreakKind::Mandatory)?;
        }
        require(exact(width.width, line.width))?;
        let mut begin = end;
        while begin < last {
            let run_index = v.logical_owners[begin].run_index;
            let run = v.runs[run_index as usize];
            let stop = last.min((run.logical_start + run.logical_count) as usize);
            let replay = !o.preserve_whole_run_placement
                && (begin > run.logical_start as usize && v.logical_glyphs[begin].flags & 1 != 0
                    || stop < (run.logical_start + run.logical_count) as usize
                        && v.logical_glyphs[stop].flags & 1 != 0);
            let slice_index = s.slice_indices[begin];
            if replay {
                let slice = s
                    .slices
                    .get(slice_index as usize)
                    .ok_or(NativeSourceError::InvalidArgument)?;
                require(
                    slice.run_index == run_index
                        && slice.logical_start as usize == begin
                        && slice.logical_count as usize == stop - begin,
                )?;
            }
            for &index in &s.slice_indices[begin..stop] {
                require(index == if replay { slice_index } else { u32::MAX })?;
            }
            begin = stop;
        }
        end = last;
    }
    require(
        end == n
            || v.metadata.layout.maximum_lines != 0
                && line_count == v.metadata.layout.maximum_lines as usize,
    )?;
    for (i, &index) in s.slice_indices.iter().enumerate() {
        require(i >= first && i < end || index == u32::MAX)?;
    }
    let mut layout = v.metadata.layout;
    layout.direction = if v.metadata.paragraph_level == 1 {
        GposDirection::RightToLeft
    } else {
        GposDirection::LeftToRight
    };
    let written = writer::write(
        &v.logical_glyphs[first..],
        &s.breaks_after[first..],
        &v.logical_bidi_levels[first..],
        &v.glyph_scales[first..],
        v.metadata.paragraph_level,
        layout,
        o,
        &projected[first..],
        &items[first..],
        &s.fitted_lines,
        width_policy,
    )?;
    require(written.glyphs.len() == count && written.lines.len() == line_count)?;
    for i in 0..count {
        let expected = written.raster_glyphs[i];
        let actual = v.positioned_glyphs[i];
        let precise = written.glyphs[i];
        let original = s.glyph_metrics[i];
        require(
            actual.glyph_index == expected.glyph_index + first as u32
                && actual.glyph_id == expected.glyph_id
                && actual.cluster == expected.cluster
                && written.levels[i] == v.positioned_bidi_levels[i]
                && exact32(expected.position[0], actual.x)
                && exact32(expected.position[1], actual.y)
                && exact32(expected.advance[0], actual.advance_x)
                && exact32(expected.advance[1], actual.advance_y)
                && original.reserved == 0
                && original.cluster == precise.cluster
                && exact(original.x, precise.x)
                && exact(original.y, precise.y)
                && exact(original.advance_x, precise.advance_x)
                && exact(original.advance_y, precise.advance_y),
        )?;
    }
    for i in 0..line_count {
        let expected = written.raster_lines[i];
        let actual = v.lines[i];
        let precise = written.lines[i];
        let original = s.line_metrics[i];
        require(
            actual.glyph_start == expected.glyph_start
                && actual.glyph_count == expected.glyph_count
                && actual.input_start == expected.input_start
                && actual.input_end == expected.input_end
                && actual.clipped == u8::from(expected.clipped)
                && actual.flags == expected.flags
                && actual.reserved0 == 0
                && actual.reserved1 == 0
                && exact32(actual.width, expected.width)
                && exact32(actual.height, expected.height)
                && exact32(actual.baseline_y, expected.baseline_y)
                && exact32(v.line_origins[i], written.origins[i])
                && original.glyph_start == precise.glyph_start
                && original.glyph_count == precise.glyph_count
                && exact(original.width, precise.width)
                && exact(original.top, precise.top)
                && exact(original.height, precise.height)
                && exact(original.baseline_offset, precise.baseline_offset)
                && exact(original.baseline_y, precise.baseline_y)
                && exact(original.origin_x, precise.origin_x),
        )?;
    }
    interaction::admit_records(
        &s.glyph_metrics,
        &s.line_metrics,
        &v.positioned_cluster_ends,
        &v.positioned_bidi_levels,
        &s.boxes,
        &s.carets,
    )
}
