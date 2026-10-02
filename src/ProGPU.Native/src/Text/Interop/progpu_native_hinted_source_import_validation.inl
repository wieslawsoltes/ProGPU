// Included inside the flat import's private namespace. No font execution or
// source geometry reconstruction: policy and measured-writer verification use
// the original retained integer/double inputs and exact occurrence witnesses.
template<class T> bool source_exact(T a, T b) noexcept {
    using Bits = std::conditional_t<sizeof(T) == 4U, std::uint32_t, std::uint64_t>;
    return std::bit_cast<Bits>(a) == std::bit_cast<Bits>(b);
}
shaping_glyph source_native_glyph(const progpu_native_text_shaping_glyph& g) noexcept {
    return {g.glyph_id, g.code_point, g.cluster, static_cast<shaping_glyph_flags>(g.flags),
        g.advance_x, g.advance_y, g.offset_x, g.offset_y};
}
bool source_same_glyph(const shaping_glyph& a, const progpu_native_text_shaping_glyph& b) noexcept {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster &&
        static_cast<std::uint32_t>(a.flags) == b.flags && a.advance_x == b.advance_x && a.advance_y == b.advance_y &&
        a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}
bool source_same_identity(const progpu_native_text_shaping_glyph& a, const progpu_native_text_shaping_glyph& b) noexcept {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster &&
        (a.flags & ~7U) == 0U && a.advance_y != INT32_MIN && a.offset_y != INT32_MIN;
}
bool source_prepared_edge(std::span<const progpu_native_text_shaping_glyph> prepared, std::uint32_t edge) noexcept {
    return edge <= prepared.size() && (edge == 0U || edge == prepared.size() ||
        (prepared[edge - 1U].cluster != prepared[edge].cluster && (prepared[edge].flags & 1U) == 0U));
}

bool valid_source_records(const progpu_native_hinted_glyph_resource_view& v,
    const progpu_native_hinted_source_glyph_resource_view& s) {
    const auto& p = s.source; const auto& o = p.options; const auto& c = v.counts;
    if (s.abi_version != PROGPU_NATIVE_ABI_VERSION || s.struct_size != sizeof(s) || s.version != 2U ||
        s.flags != 0U || s.reserved != 0U || o.abi_version != PROGPU_NATIVE_ABI_VERSION ||
        o.struct_size != sizeof(o) || o.version != 1U ||
        (o.flags & ~static_cast<std::uint32_t>(PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS)) != 0U ||
        o.em_policy > PROGPU_NATIVE_SOURCE_EM_FLOAT_CAPTURE_NEAREST_HALF_UP ||
        o.advance_policy > PROGPU_NATIVE_SOURCE_ADVANCE_IDEAL_UNITS || o.offset_policy > PROGPU_NATIVE_SOURCE_OFFSET_IDEAL_UNITS ||
        o.allow_emergency_break > 1U || !std::isfinite(o.em_size) || o.em_size <= 0.0 ||
        !std::isfinite(o.pixels_per_dip) || o.pixels_per_dip <= 0.0 ||
        !source_exact(static_cast<float>(o.pixels_per_dip), v.dpi_scale) ||
        !std::isfinite(o.maximum_width) || o.maximum_width < 0.0 || !std::isfinite(o.line_height) || o.line_height < 0.0 ||
        !std::isfinite(o.tab_origin) || !source_exact(static_cast<float>(o.maximum_width), v.layout.maximum_width) ||
        !source_exact(static_cast<float>(o.line_height), v.layout.line_height) || v.layout.alignment == PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        p.style_count != c.style_count || p.logical_count != c.logical_glyph_count || p.glyph_count != c.positioned_glyph_count ||
        p.line_count != c.line_count || c.cluster_box_count != 0U || c.caret_stop_count != 0U ||
        s.first_logical_glyph > p.logical_count || s.slice_count > p.logical_count ||
        !std::isfinite(s.intrinsic_widths.minimum) || s.intrinsic_widths.minimum < 0.0 ||
        !std::isfinite(s.intrinsic_widths.maximum) || s.intrinsic_widths.maximum < 0.0 ||
        ((o.flags & PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS) == 0U &&
            (s.intrinsic_widths.minimum != 0.0 || s.intrinsic_widths.maximum != 0.0))) return false;
    // Bound every transported span plus writer scratch before reading an array
    // or allocating it. This additive lane has a 256 MiB per-import work budget;
    // the unchanged base resource/font limits remain independently enforced.
    constexpr std::uint64_t byte_limit = 256U * 1024U * 1024U;
    std::uint64_t bytes = sizeof(owned_hinted_source_import);
    const auto span = [&]<class T>(const T* data, std::uint32_t count) {
        const auto amount = std::uint64_t{count} * sizeof(T);
        if (!readable(data, count) || amount > byte_limit - bytes) return false;
        bytes += amount; return true;
    };
    if (!span(p.styles, p.style_count) || !span(p.logical_metrics, p.logical_count) ||
        !span(p.glyph_metrics, p.glyph_count) || !span(p.line_metrics, p.line_count) ||
        !span(p.boxes, p.box_count) || !span(p.carets, p.caret_count) ||
        !span(s.raw_logical_glyphs, p.logical_count) || !span(s.effective_logical_glyphs, p.logical_count) ||
        !span(s.positioning_runs, c.run_count) || !span(s.prepared_glyphs, s.prepared_count) ||
        !span(s.slices, s.slice_count) || !span(s.slice_indices, p.logical_count) ||
        !span(s.fitted_lines, p.line_count) || !span(s.breaks_after, p.logical_count)) return false;
    constexpr std::size_t scratch_per_glyph = sizeof(shaping_glyph) + sizeof(text_source_glyph_metrics) +
        sizeof(text_source_item_metrics) + sizeof(positioned_text_glyph) + sizeof(positioned_text_line) +
        sizeof(text_source_glyph_position) + sizeof(text_source_line_metrics) + sizeof(text_visual_cluster_group) +
        sizeof(text_line_break_kind) + sizeof(std::uint32_t) + sizeof(std::int8_t) + sizeof(float);
    const auto extra_scratch = std::uint64_t{p.logical_count} * scratch_per_glyph +
        std::uint64_t{p.style_count} * sizeof(hinted_source_style) +
        std::uint64_t{p.line_count} * (sizeof(text_source_fitted_line) + sizeof(double));
    if (extra_scratch > byte_limit - bytes) return false;

    std::vector<hinted_source_style> policies(p.style_count);
    for (std::uint32_t i = 0U; i < p.style_count; ++i) {
        const auto& original = p.styles[i]; const auto& style = v.styles[i]; const auto& device = v.device_styles[i];
        policies[i] = {original.em_size, o.pixels_per_dip, static_cast<hinted_source_em_policy>(o.em_policy),
            static_cast<hinted_source_advance_policy>(o.advance_policy), static_cast<hinted_source_offset_policy>(o.offset_policy)};
        hinted_source_device_selection selected{};
        if (!resolve_hinted_source_device(policies[i], selected) ||
            !std::isfinite(original.ascent) || original.ascent < 0.0 || !std::isfinite(original.descent) || original.descent < 0.0 ||
            !source_exact(static_cast<float>(original.ascent), v.source_metrics[i].ascent) ||
            !source_exact(static_cast<float>(original.descent), v.source_metrics[i].descent) ||
            static_cast<float>(original.em_size) / static_cast<float>(v.font_sources[style.font_index].units_per_em) != style.scale ||
            device.x_pixels_per_em_26_6 != selected.pixels_per_em_26_6 || device.y_pixels_per_em_26_6 != selected.pixels_per_em_26_6 ||
            device.logical_units_per_physical_pixel != selected.logical_units_per_physical_pixel) return false;
    }
    std::uint32_t prepared_end = 0U;
    for (std::uint32_t i = 0U; i < c.run_count; ++i) {
        const auto& position = s.positioning_runs[i]; const auto& run = v.runs[i];
        if (position.reserved0 != 0U || position.reserved1 != 0U || position.prepared_start != prepared_end ||
            position.prepared_count > s.prepared_count - prepared_end ||
            (position.prepared_count != 0U && (position.prepared_count != run.logical_count ||
                position.prepared_count != run.source_descriptor_count))) return false;
        for (std::uint32_t j = 0U; j < run.logical_count && position.prepared_count != 0U; ++j) {
            const auto index = run.logical_start + j; const auto& owner = v.logical_owners[index];
            const auto& prepared = s.prepared_glyphs[prepared_end + owner.descriptor_index];
            if (!source_same_identity(prepared, s.raw_logical_glyphs[index]) ||
                owner.run_glyph_index != ((run.bidi_level & 1) == 0 ? owner.descriptor_index
                    : run.logical_count - 1U - owner.descriptor_index)) return false;
        }
        for (std::uint32_t j = 1U; j < position.prepared_count; ++j)
            if (s.prepared_glyphs[prepared_end + j].cluster < s.prepared_glyphs[prepared_end + j - 1U].cluster) return false;
        prepared_end += position.prepared_count;
    }
    if (prepared_end != s.prepared_count) return false;
    const auto safe_boundary = [&](std::uint32_t index) {
        if (index == 0U || index == p.logical_count) return true;
        const auto& glyph = v.logical_glyphs[index];
        if (glyph.cluster == v.logical_glyphs[index - 1U].cluster) return false;
        const auto run_index = v.logical_owners[index].run_index;
        if (run_index != v.logical_owners[index - 1U].run_index || (glyph.flags & 1U) == 0U) return true;
        const auto& position = s.positioning_runs[run_index];
        if (position.prepared_count == 0U) return false;
        const std::span prepared{s.prepared_glyphs + position.prepared_start, position.prepared_count};
        const auto found = std::lower_bound(prepared.begin(), prepared.end(), glyph.cluster,
            [](const auto& g, std::int32_t cluster) { return g.cluster < cluster; });
        return found != prepared.end() && found->cluster == glyph.cluster &&
            source_prepared_edge(prepared, static_cast<std::uint32_t>(found - prepared.begin()));
    };
    if (!safe_boundary(s.first_logical_glyph)) return false;
    std::uint32_t declared_slice_glyphs = 0U;
    for (std::uint32_t i = 0U; i < s.slice_count; ++i) {
        const auto& slice = s.slices[i];
        if (slice.reserved != 0U || slice.run_index >= c.run_count || slice.logical_count == 0U ||
            slice.logical_count != slice.prepared_count || slice.logical_start > p.logical_count ||
            slice.logical_count > p.logical_count - slice.logical_start ||
            slice.logical_count > p.logical_count - declared_slice_glyphs) return false;
        const auto& position = s.positioning_runs[slice.run_index]; const auto& run = v.runs[slice.run_index];
        if (position.prepared_count == 0U || slice.prepared_start > position.prepared_count ||
            slice.prepared_count > position.prepared_count - slice.prepared_start || slice.logical_start < run.logical_start ||
            slice.logical_start - run.logical_start > run.logical_count ||
            slice.logical_count > run.logical_count - (slice.logical_start - run.logical_start)) return false;
        const std::span prepared{s.prepared_glyphs + position.prepared_start, position.prepared_count};
        if (!source_prepared_edge(prepared, slice.prepared_start) ||
            !source_prepared_edge(prepared, slice.prepared_start + slice.prepared_count)) return false;
        for (std::uint32_t j = 0U; j < slice.logical_count; ++j) {
            const auto index = slice.logical_start + j; const auto descriptor = v.logical_owners[index].descriptor_index;
            if (s.slice_indices[index] != i || descriptor < slice.prepared_start ||
                descriptor - slice.prepared_start >= slice.prepared_count ||
                (s.effective_logical_glyphs[index].flags & prepared[descriptor].flags) != prepared[descriptor].flags) return false;
        }
        declared_slice_glyphs += slice.logical_count;
    }
    std::vector<shaping_glyph> logical(p.logical_count);
    std::vector<text_source_glyph_metrics> metrics(p.logical_count);
    std::vector<text_source_item_metrics> items(p.logical_count);
    std::vector<text_line_break_kind> breaks(p.logical_count);
    std::uint32_t used_slice_glyphs = 0U;
    for (std::uint32_t i = 0U; i < p.logical_count; ++i) {
        const auto& raw = s.raw_logical_glyphs[i]; const auto& effective = s.effective_logical_glyphs[i];
        const auto style = v.runs[v.logical_owners[i].run_index].style_index;
        if (!source_same_identity(raw, v.logical_glyphs[i]) || !source_same_identity(effective, raw) || s.breaks_after[i] > 2U) return false;
        shaping_glyph expected{}, fitted{};
        if (!project_hinted_source_advance(source_native_glyph(raw), policies[style].advance_policy, expected) ||
            !source_same_glyph(expected, v.logical_glyphs[i]) ||
            !project_hinted_source_advance(source_native_glyph(effective), policies[style].advance_policy, fitted) ||
            !project_hinted_source_geometry(fitted, policies[style], metrics[i])) return false;
        const auto& m = p.logical_metrics[i];
        if (!source_exact(metrics[i].advance_x, m.advance_x) || !source_exact(metrics[i].advance_y, m.advance_y) ||
            !source_exact(metrics[i].offset_x, m.offset_x) || !source_exact(metrics[i].offset_y, m.offset_y)) return false;
        if (s.slice_indices[i] == UINT32_MAX) {
            if (!source_same_glyph(source_native_glyph(raw), effective)) return false;
        } else {
            ++used_slice_glyphs;
            if (s.slice_indices[i] >= s.slice_count) return false;
            const auto& slice = s.slices[s.slice_indices[i]];
            if (i < slice.logical_start || i - slice.logical_start >= slice.logical_count) return false;
        }
        logical[i] = source_native_glyph(v.logical_glyphs[i]);
        items[i] = {p.styles[style].ascent, p.styles[style].descent};
        breaks[i] = static_cast<text_line_break_kind>(s.breaks_after[i]);
    }
    if (used_slice_glyphs != declared_slice_glyphs) return false;
    std::vector<text_source_fitted_line> fitted(p.line_count);
    std::uint32_t end = s.first_logical_glyph;
    for (std::uint32_t i = 0U; i < p.line_count; ++i) {
        const auto& line = s.fitted_lines[i];
        if (line.reserved != 0U || line.clipped > 1U || line.glyph_start != end - s.first_logical_glyph ||
            line.glyph_count == 0U || line.glyph_count > p.logical_count - end || !std::isfinite(line.width) || line.width < 0.0 ||
            !safe_boundary(end + line.glyph_count)) return false;
        const auto line_end = end + line.glyph_count;
        double width = 0.0;
        for (std::uint32_t j = end; j < line_end; ++j) {
            width += metrics[j].advance_x;
            if (j + 1U < line_end && breaks[j] == text_line_break_kind::mandatory) return false;
        }
        if (!source_exact(width, line.width)) return false;
        // Each replayed slice is exactly the selected line/run intersection;
        // unsafe final positioning edges may not silently use the raw full run.
        for (std::uint32_t first = end; first < line_end;) {
            const auto run_index = v.logical_owners[first].run_index; const auto& run = v.runs[run_index];
            const auto last = std::min(line_end, run.logical_start + run.logical_count);
            const bool replay = (first > run.logical_start && (v.logical_glyphs[first].flags & 1U) != 0U) ||
                (last < run.logical_start + run.logical_count && (v.logical_glyphs[last].flags & 1U) != 0U);
            const auto slice_index = s.slice_indices[first];
            if (replay) {
                if (slice_index == UINT32_MAX) return false;
                const auto& slice = s.slices[slice_index];
                if (slice.run_index != run_index || slice.logical_start != first || slice.logical_count != last - first) return false;
            }
            for (auto j = first; j < last; ++j)
                if (s.slice_indices[j] != (replay ? slice_index : UINT32_MAX)) return false;
            first = last;
        }
        fitted[i] = {line.glyph_start, line.glyph_count, line.width, line.clipped != 0U};
        end = line_end;
    }
    if (end != p.logical_count && (v.layout.maximum_lines == 0U || p.line_count != v.layout.maximum_lines)) return false;
    for (std::uint32_t i = 0U; i < p.logical_count; ++i)
        if ((i < s.first_logical_glyph || i >= end) && s.slice_indices[i] != UINT32_MAX) return false;

    const auto count = p.logical_count - s.first_logical_glyph;
    std::vector<positioned_text_glyph> glyphs(count);
    std::vector<positioned_text_line> lines(count);
    std::vector<text_source_glyph_position> source_glyphs(count);
    std::vector<text_source_line_metrics> source_lines(count);
    std::vector<text_visual_cluster_group> groups(count);
    std::vector<std::uint32_t> indices(count);
    std::vector<std::int8_t> levels(count);
    std::vector<float> origins(count);
    const auto& l = v.layout;
    const text_layout_options options{l.scale, l.maximum_width, l.line_height, l.maximum_lines,
        v.paragraph_level == 1 ? shaping_direction::right_to_left : shaping_direction::left_to_right,
        text_trimming::none, static_cast<text_alignment>(l.alignment), 0U, l.ellipsis_glyph_id, l.ellipsis_advance};
    std::uint32_t glyph_count{}, line_count{};
    font_error error{};
    if (!try_layout_source_measured_logical_shaped_text_retained(
        std::span<const shaping_glyph>{logical}.subspan(s.first_logical_glyph),
        std::span<const text_line_break_kind>{breaks}.subspan(s.first_logical_glyph),
        std::span{v.logical_bidi_levels, p.logical_count}.subspan(s.first_logical_glyph),
        std::span{v.glyph_scales, p.logical_count}.subspan(s.first_logical_glyph), v.paragraph_level,
        options, {0.0F, 0.0F, o.allow_emergency_break != 0U}, {groups, indices}, glyphs, lines, glyph_count, line_count,
        {levels, origins, {}}, {o.maximum_width, o.line_height,
            std::span<const text_source_glyph_metrics>{metrics}.subspan(s.first_logical_glyph),
            std::span<const text_source_item_metrics>{items}.subspan(s.first_logical_glyph), source_glyphs, source_lines, fitted}, &error) ||
        glyph_count != p.glyph_count || line_count != p.line_count) return false;
    std::vector<double> double_origins(line_count);
    for (std::uint32_t i = 0U; i < glyph_count; ++i) {
        const auto& expected = glyphs[i]; const auto& actual = v.positioned_glyphs[i];
        const auto& precise = source_glyphs[i]; const auto& original = p.glyph_metrics[i];
        if (actual.glyph_index != expected.glyph_index + s.first_logical_glyph || actual.glyph_id != expected.glyph_id ||
            actual.cluster != expected.cluster || levels[i] != v.positioned_bidi_levels[i] ||
            !source_exact(expected.x, actual.x) || !source_exact(expected.y, actual.y) ||
            !source_exact(expected.advance_x, actual.advance_x) || !source_exact(expected.advance_y, actual.advance_y) ||
            original.reserved != 0U || original.cluster != precise.cluster || !source_exact(original.x, precise.x) ||
            !source_exact(original.y, precise.y) || !source_exact(original.advance_x, precise.advance_x) ||
            !source_exact(original.advance_y, precise.advance_y)) return false;
    }
    for (std::uint32_t i = 0U; i < line_count; ++i) {
        const auto& expected = lines[i]; const auto& actual = v.lines[i];
        const auto& precise = source_lines[i]; const auto& original = p.line_metrics[i];
        if (actual.glyph_start != expected.glyph_start || actual.glyph_count != expected.glyph_count ||
            actual.input_start != expected.input_start || actual.input_end != expected.input_end || actual.clipped != expected.clipped ||
            actual.reserved0 != expected.flags || actual.reserved1 != expected.reserved1 || actual.reserved2 != expected.reserved2 ||
            !source_exact(actual.width, expected.width) || !source_exact(actual.height, expected.height) ||
            !source_exact(actual.baseline_y, expected.baseline_y) || !source_exact(v.line_origins[i], origins[i]) ||
            original.glyph_start != precise.glyph_start || original.glyph_count != precise.glyph_count ||
            !source_exact(original.width, precise.width) || !source_exact(original.top, precise.top) ||
            !source_exact(original.height, precise.height) || !source_exact(original.baseline_offset, precise.baseline_offset) ||
            !source_exact(original.baseline_y, precise.baseline_y) || !source_exact(original.origin_x, precise.origin_x)) return false;
        double_origins[i] = original.origin_x;
    }
    return interaction_detail::validate_retained_records(
        std::span{p.glyph_metrics, p.glyph_count}, std::span{p.line_metrics, p.line_count},
        std::span{v.positioned_cluster_ends, p.glyph_count}, std::span{v.positioned_bidi_levels, p.glyph_count},
        std::span{p.boxes, p.box_count}, std::span{p.carets, p.caret_count}, &error,
        true, std::span<const text_fragment_placement>{}, std::span<const double>{double_origins});
}
