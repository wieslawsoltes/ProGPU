// Included only by the C shaping adapter, after its existing validation helpers.
// The producer and interaction algorithms remain in their original owners.
#include "progpu_native_hinted_source_run_internal.hpp"

namespace {
progpu_native_hinted_source_caret_stop source_wire_caret(const hinted_source_caret_stop& value) noexcept {
    return {value.input_position, value.line_index, value.x, value.y, value.height,
        value.bidi_level, static_cast<std::uint8_t>(value.trailing ? 1U : 0U), 0U, 0U};
}

bool cache_hinted_source_paragraph(progpu_native_hinted_paragraph& handle,
    const progpu_native_hinted_source_options& options) {
    const auto& p = *handle.generation;
    if (!p.has_source_geometry || p.source_styles.size() != p.styles.size() ||
        p.source_style_metrics.size() != p.styles.size() || p.source_logical_metrics.size() != p.logical_glyphs.size() ||
        p.source_glyphs.size() != p.glyphs.size() || p.source_lines.size() != p.lines.size() ||
        handle.interaction->source_boxes().size() > UINT32_MAX || handle.interaction->source_carets().size() > UINT32_MAX)
        return false;
    auto cache = std::make_shared<progpu_native_hinted_source_cache>();
    cache->options = options;
    for (std::size_t i = 0U; i < p.styles.size(); ++i)
        cache->styles.push_back({p.source_styles[i].em_size, p.source_style_metrics[i].ascent, p.source_style_metrics[i].descent});
    for (const auto& m : p.source_logical_metrics)
        cache->logical.push_back({m.advance_x, m.advance_y, m.offset_x, m.offset_y});
    for (const auto& m : p.source_glyphs)
        cache->glyphs.push_back({m.x, m.y, m.advance_x, m.advance_y, m.cluster, 0U});
    for (const auto& m : p.source_lines)
        cache->lines.push_back({m.width, m.top, m.height, m.baseline_offset, m.baseline_y, m.origin_x, m.glyph_start, m.glyph_count});
    for (const auto& b : handle.interaction->source_boxes())
        cache->boxes.push_back({b.input_start, b.input_end, b.line_index, b.bidi_level, 0U, 0U, 0U, b.x, b.y, b.width, b.height});
    for (const auto& c : handle.interaction->source_carets()) cache->carets.push_back(source_wire_caret(c));
    handle.source = std::move(cache);
    return true;
}

bool source_options_valid(const progpu_native_hinted_source_options& o) noexcept {
    return o.abi_version == PROGPU_NATIVE_ABI_VERSION && o.struct_size == sizeof(o) && o.version == 1U &&
        (o.flags & ~static_cast<std::uint32_t>(PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS)) == 0U &&
        o.em_policy <= PROGPU_NATIVE_SOURCE_EM_NEAREST_TIES_TO_EVEN &&
        o.advance_policy <= PROGPU_NATIVE_SOURCE_ADVANCE_PHYSICAL_TIES_TO_EVEN && o.offset_policy == 0U &&
        o.allow_emergency_break <= 1U && std::isfinite(o.em_size) && o.em_size > 0.0 &&
        std::isfinite(o.pixels_per_dip) && o.pixels_per_dip > 0.0 &&
        std::isfinite(o.maximum_width) && o.maximum_width >= 0.0 &&
        std::isfinite(o.line_height) && o.line_height >= 0.0 && std::isfinite(o.tab_origin);
}

bool has_hinted_source(const progpu_native_hinted_paragraph& p) noexcept {
    return p.generation->has_source_geometry && p.source != nullptr;
}
} // namespace

extern "C" {
progpu_native_status progpu_native_text_context_layout_hinted_source_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles, std::uint32_t style_count,
    const progpu_native_text_style_metrics* source_metrics,
    const progpu_native_hinted_paragraph_device_style* device_styles, std::uint32_t device_style_count,
    const std::int32_t* variations, std::uint32_t variation_count,
    const progpu_native_hinted_source_options* source_options,
    const progpu_native_hinted_source_style* source_styles, std::uint32_t source_style_count,
    progpu_native_hinted_paragraph** paragraph, progpu_native_text_paragraph_result* paragraph_result) {
    if (!valid_hinted_buffer(context, 1U) || !hinted_paragraph_shape_inputs_valid(shaping) ||
        !valid_hinted_buffer(layout, 1U) || layout->struct_size != sizeof(*layout) ||
        !valid_hinted_buffer(styles, style_count) || !valid_hinted_buffer(source_metrics, style_count) ||
        !valid_hinted_buffer(device_styles, device_style_count) || device_style_count != style_count ||
        !valid_hinted_buffer(variations, variation_count) || !valid_hinted_buffer(source_options, 1U) ||
        !valid_hinted_buffer(source_styles, source_style_count) || source_style_count != style_count ||
        !valid_hinted_buffer(paragraph, 1U) || !valid_hinted_buffer(paragraph_result, 1U) ||
        byte_ranges_overlap(paragraph, sizeof(*paragraph), paragraph_result, sizeof(*paragraph_result)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto aliases = [&](const void* output, std::uint64_t bytes) noexcept {
        return hinted_paragraph_factory_aliases_inputs(output, bytes, *context, *shaping, *layout,
            styles, style_count, source_metrics, device_styles, device_style_count, variations, variation_count) ||
            byte_ranges_overlap(output, bytes, source_options, sizeof(*source_options)) ||
            byte_ranges_overlap(output, bytes, source_styles, static_cast<std::uint64_t>(source_style_count) * sizeof(*source_styles));
    };
    if (aliases(paragraph, sizeof(*paragraph)) || aliases(paragraph_result, sizeof(*paragraph_result)) ||
        !source_options_valid(*source_options)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    for (std::uint32_t i = 0U; i < device_style_count; ++i) {
        const auto& d = device_styles[i];
        if (d.reserved != 0U || d.variation_count > 65535U || d.variation_start > variation_count ||
            d.variation_count > variation_count - d.variation_start) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        const auto options = *source_options;
        std::vector<hinted_paragraph_style_configuration> configurations;
        std::vector<hinted_source_style> source;
        std::vector<text_source_item_metrics> metrics;
        configurations.reserve(style_count); source.reserve(style_count); metrics.reserve(style_count);
        for (std::uint32_t i = 0U; i < style_count; ++i) {
            const auto& d = device_styles[i];
            const auto* axes = d.variation_count == 0U ? nullptr : variations + d.variation_start;
            configurations.push_back({d.font_index, d.source_scale,
                {d.x_pixels_per_em_26_6, d.y_pixels_per_em_26_6, static_cast<font_hint_policy>(d.interpreter),
                    d.x_phase_26_6, d.y_phase_26_6, {axes, d.variation_count}}, d.logical_units_per_physical_pixel});
            source.push_back({source_styles[i].em_size, options.pixels_per_dip,
                static_cast<hinted_source_em_policy>(options.em_policy),
                static_cast<hinted_source_advance_policy>(options.advance_policy)});
            metrics.push_back({source_styles[i].ascent, source_styles[i].descent});
        }
        const hinted_source_paragraph_layout source_layout{options.maximum_width, options.line_height,
            metrics, options.allow_emergency_break != 0U, (options.flags & PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS) != 0U};
        auto candidate = std::make_unique<progpu_native_hinted_paragraph>();
        progpu_native_text_paragraph_result diagnostic{};
        const auto status = try_layout_context_hinted_paragraph(context, *shaping, *layout,
            {styles, style_count}, {source_metrics, style_count}, configurations, candidate->generation, diagnostic,
            source, &source_layout);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        const auto interaction = create_hinted_paragraph_interaction(candidate->generation);
        if (interaction.status != PROGPU_NATIVE_STATUS_SUCCESS) return interaction.status;
        candidate->interaction = interaction.generation;
        if (!valid_hinted_paragraph(candidate.get()) || !cache_hinted_source_paragraph(*candidate, options))
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        *paragraph_result = diagnostic;
        *paragraph = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

progpu_native_status progpu_native_hinted_source_paragraph_borrow(
    const progpu_native_hinted_paragraph* paragraph, progpu_native_hinted_source_paragraph_view* view) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(view, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, view, sizeof(*view))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const auto& c = *paragraph->source;
    const progpu_native_hinted_source_paragraph_view candidate{c.options,
        static_cast<std::uint32_t>(c.styles.size()), static_cast<std::uint32_t>(c.logical.size()),
        static_cast<std::uint32_t>(c.glyphs.size()), static_cast<std::uint32_t>(c.lines.size()),
        static_cast<std::uint32_t>(c.boxes.size()), static_cast<std::uint32_t>(c.carets.size()),
        hinted_glyph_wire_data(c.styles), hinted_glyph_wire_data(c.logical), hinted_glyph_wire_data(c.glyphs),
        hinted_glyph_wire_data(c.lines), hinted_glyph_wire_data(c.boxes), hinted_glyph_wire_data(c.carets)};
    *view = candidate;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_reflow(
    const progpu_native_hinted_paragraph* paragraph, std::int32_t input_start, double maximum_width,
    progpu_native_hinted_paragraph** reflowed) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(reflowed, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, reflowed, sizeof(*reflowed))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const auto reflow = reflow_hinted_source_paragraph(*paragraph->generation, input_start, maximum_width);
    if (reflow.status != PROGPU_NATIVE_STATUS_SUCCESS) return reflow.status;
    const auto interaction = create_hinted_paragraph_interaction(reflow.generation);
    if (interaction.status != PROGPU_NATIVE_STATUS_SUCCESS) return interaction.status;
    try {
        auto candidate = std::make_unique<progpu_native_hinted_paragraph>();
        candidate->generation = reflow.generation; candidate->interaction = interaction.generation;
        auto options = paragraph->source->options; options.maximum_width = maximum_width;
        if (!valid_hinted_paragraph(candidate.get()) || !cache_hinted_source_paragraph(*candidate, options))
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        *reflowed = candidate.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

progpu_native_status progpu_native_hinted_source_paragraph_get_intrinsic_widths(
    const progpu_native_hinted_paragraph* paragraph, progpu_native_hinted_source_intrinsic_widths* widths) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(widths, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, widths, sizeof(*widths))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph) ||
        (paragraph->source->options.flags & PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS) == 0U ||
        !paragraph->generation->has_source_intrinsic_widths) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    *widths = {paragraph->generation->source_minimum_intrinsic_width, paragraph->generation->source_maximum_intrinsic_width};
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_hit_test(
    const progpu_native_hinted_paragraph* paragraph, double x, double y, progpu_native_hinted_source_hit* hit) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(hit, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, hit, sizeof(*hit))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    hinted_source_hit result{}; font_error error = font_error::none;
    if (!paragraph->interaction->hit_test_source(x, y, result, &error)) return status_from_error(error);
    *hit = {result.input_position, result.line_index,
        {result.bounds.x, result.bounds.y, result.bounds.width, result.bounds.height}, result.bidi_level,
        static_cast<std::uint8_t>(result.trailing ? 1U : 0U), static_cast<std::uint8_t>(result.inside ? 1U : 0U), 0U};
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_get_caret(
    const progpu_native_hinted_paragraph* paragraph, std::int32_t input_position, std::uint32_t trailing,
    progpu_native_hinted_source_caret_stop* caret) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(caret, 1U) || trailing > 1U ||
        hinted_paragraph_handle_aliases(*paragraph, caret, sizeof(*caret))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    hinted_source_caret_stop result{}; font_error error = font_error::none;
    if (!paragraph->interaction->source_caret(input_position, trailing != 0U, result, &error)) return status_from_error(error);
    *caret = source_wire_caret(result);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_get_selection(
    const progpu_native_hinted_paragraph* paragraph, std::int32_t input_start, std::int32_t input_end,
    progpu_native_hinted_source_rectangle* rectangles, std::uint32_t capacity, std::uint32_t* written) {
    const auto bytes = static_cast<std::uint64_t>(capacity) * sizeof(*rectangles);
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(rectangles, capacity) || !valid_hinted_buffer(written, 1U) ||
        byte_ranges_overlap(rectangles, bytes, written, sizeof(*written)) ||
        hinted_paragraph_handle_aliases(*paragraph, rectangles, bytes) ||
        hinted_paragraph_handle_aliases(*paragraph, written, sizeof(*written))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    try {
        // A selection cannot produce more rectangles than retained cluster boxes.
        // Do not allocate based on an arbitrarily large caller capacity.
        std::vector<hinted_source_rectangle> candidate(std::min<std::size_t>(capacity, paragraph->source->boxes.size()));
        std::uint32_t count = 0U; font_error error = font_error::none;
        if (!paragraph->interaction->source_selection(input_start, input_end, candidate, count, &error)) return status_from_error(error);
        if (count > candidate.size()) return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        for (std::size_t i = 0U; i < count; ++i)
            rectangles[i] = {candidate[i].x, candidate[i].y, candidate[i].width, candidate[i].height};
        *written = count;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

progpu_native_status progpu_native_hinted_glyph_resource_copy_source_metrics(
    const progpu_native_hinted_glyph_resource* resource, const std::uint32_t* positioned_indices, std::uint32_t glyph_count,
    double source_em_size, double source_pixels_per_dip, double* advances, std::uint32_t advance_capacity,
    progpu_native_hinted_source_glyph_offset* offsets, std::uint32_t offset_capacity) {
    const auto advance_bytes = static_cast<std::uint64_t>(advance_capacity) * sizeof(*advances);
    const auto offset_bytes = static_cast<std::uint64_t>(offset_capacity) * sizeof(*offsets);
    const auto index_bytes = static_cast<std::uint64_t>(glyph_count) * sizeof(*positioned_indices);
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(positioned_indices, glyph_count) ||
        !valid_hinted_buffer(advances, advance_capacity) || !valid_hinted_buffer(offsets, offset_capacity) ||
        advance_capacity < glyph_count || offset_capacity < glyph_count ||
        byte_ranges_overlap(advances, advance_bytes, offsets, offset_bytes) ||
        byte_ranges_overlap(advances, advance_bytes, positioned_indices, index_bytes) ||
        byte_ranges_overlap(offsets, offset_bytes, positioned_indices, index_bytes) ||
        hinted_glyph_resource_aliases(*resource, advances, static_cast<std::size_t>(advance_bytes)) ||
        hinted_glyph_resource_aliases(*resource, offsets, static_cast<std::size_t>(offset_bytes))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!resource->has_nominal_metrics || resource->source == nullptr) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    return copy_hinted_source_metrics(*resource->generation->paragraph(), resource->view, resource->nominal_metrics,
        {positioned_indices, glyph_count}, source_em_size, source_pixels_per_dip,
        {advances, advance_capacity}, {offsets, offset_capacity});
}

progpu_native_status progpu_native_hinted_source_paragraph_hit_test_line(
    const progpu_native_hinted_paragraph* paragraph, std::uint32_t line_index, double x, progpu_native_hinted_source_hit* hit) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(hit, 1U) ||
        hinted_paragraph_handle_aliases(*paragraph, hit, sizeof(*hit))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    hinted_source_hit result{}; font_error error = font_error::none;
    if (!paragraph->interaction->hit_test_source_line(line_index, x, result, &error)) return status_from_error(error);
    *hit = {result.input_position, result.line_index,
        {result.bounds.x, result.bounds.y, result.bounds.width, result.bounds.height}, result.bidi_level,
        static_cast<std::uint8_t>(result.trailing ? 1U : 0U), static_cast<std::uint8_t>(result.inside ? 1U : 0U), 0U};
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_get_line_caret(
    const progpu_native_hinted_paragraph* paragraph, std::uint32_t line_index, std::int32_t input_position, std::uint32_t trailing,
    progpu_native_hinted_source_caret_stop* caret) {
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(caret, 1U) || trailing > 1U ||
        hinted_paragraph_handle_aliases(*paragraph, caret, sizeof(*caret))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    hinted_source_caret_stop result{}; font_error error = font_error::none;
    if (!paragraph->interaction->source_line_caret(line_index, input_position, trailing != 0U, result, &error)) return status_from_error(error);
    *caret = source_wire_caret(result);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_hinted_source_paragraph_get_line_selection(
    const progpu_native_hinted_paragraph* paragraph, std::uint32_t line_index, std::int32_t input_start, std::int32_t input_end,
    progpu_native_hinted_source_rectangle* rectangles, std::uint32_t capacity, std::uint32_t* written) {
    const auto bytes = static_cast<std::uint64_t>(capacity) * sizeof(*rectangles);
    if (!valid_hinted_paragraph(paragraph) || !valid_hinted_buffer(rectangles, capacity) || !valid_hinted_buffer(written, 1U) ||
        byte_ranges_overlap(rectangles, bytes, written, sizeof(*written)) ||
        hinted_paragraph_handle_aliases(*paragraph, rectangles, bytes) ||
        hinted_paragraph_handle_aliases(*paragraph, written, sizeof(*written))) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!has_hinted_source(*paragraph)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    if (line_index >= paragraph->source->lines.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::vector<hinted_source_rectangle> candidate(std::min<std::size_t>(capacity, paragraph->source->boxes.size()));
        std::uint32_t count = 0U; font_error error = font_error::none;
        if (!paragraph->interaction->source_line_selection(line_index, input_start, input_end, candidate, count, &error))
            return status_from_error(error);
        if (count > candidate.size()) return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        for (std::size_t i = 0U; i < count; ++i)
            rectangles[i] = {candidate[i].x, candidate[i].y, candidate[i].width, candidate[i].height};
        *written = count;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

progpu_native_status progpu_native_hinted_glyph_resource_validate_source_run(
    const progpu_native_hinted_glyph_resource* resource, const std::uint32_t* positioned_indices, std::uint32_t glyph_count,
    double source_em_size, double source_pixels_per_dip, progpu_native_hinted_source_glyph_offset source_baseline_origin,
    const double* advances, const progpu_native_hinted_source_glyph_offset* offsets, progpu_native_hinted_source_run_frame* frame) {
    if (!valid_hinted_glyph_resource(resource) || !valid_hinted_buffer(frame, 1U) ||
        !valid_hinted_buffer(positioned_indices, glyph_count) || !valid_hinted_buffer(advances, glyph_count) ||
        !valid_hinted_buffer(offsets, glyph_count) || hinted_glyph_resource_aliases(*resource, frame, sizeof(*frame)) ||
        byte_ranges_overlap(frame, sizeof(*frame), positioned_indices, static_cast<std::uint64_t>(glyph_count) * sizeof(*positioned_indices)) ||
        byte_ranges_overlap(frame, sizeof(*frame), advances, static_cast<std::uint64_t>(glyph_count) * sizeof(*advances)) ||
        byte_ranges_overlap(frame, sizeof(*frame), offsets, static_cast<std::uint64_t>(glyph_count) * sizeof(*offsets)))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!resource->has_nominal_metrics || resource->source == nullptr) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    return validate_hinted_source_run(*resource->generation->paragraph(), resource->view, resource->nominal_metrics,
        {positioned_indices, glyph_count}, source_em_size, source_pixels_per_dip, source_baseline_origin,
        {advances, glyph_count}, {offsets, glyph_count}, *frame);
}
} // extern "C"
