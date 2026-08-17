#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct allocation-free port of the applicable ProGPU-owned
// TextLayout.GenerateVerticalShapedLayout positioned-column behavior.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool valid_options(const text_layout_options& options) noexcept {
    return std::isfinite(options.scale) && options.scale > 0.0F &&
        std::isfinite(options.maximum_width) &&
        options.maximum_width >= 0.0F &&
        std::isfinite(options.line_height) && options.line_height >= 0.0F &&
        (options.direction == shaping_direction::top_to_bottom ||
            options.direction == shaping_direction::bottom_to_top) &&
        options.trimming == text_trimming::none &&
        static_cast<std::uint8_t>(options.alignment) <=
            static_cast<std::uint8_t>(text_alignment::justify);
}

bool valid_breaks(
    std::span<const text_line_break_kind> breaks_after) noexcept {
    return std::all_of(
        breaks_after.begin(),
        breaks_after.end(),
        [](text_line_break_kind value) {
            return static_cast<std::uint8_t>(value) <=
                static_cast<std::uint8_t>(
                    text_line_break_kind::mandatory);
        });
}

void get_capacities(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::uint32_t maximum_columns,
    std::uint32_t& glyph_capacity,
    std::uint32_t& column_capacity) noexcept {
    glyph_capacity = 0U;
    column_capacity = 0U;
    if (glyphs.empty()) return;

    std::size_t start = 0U;
    while (start < glyphs.size() &&
        (maximum_columns == 0U || column_capacity < maximum_columns)) {
        std::size_t end = start;
        do {
            ++end;
        } while (end < glyphs.size() &&
            breaks_after[end - 1U] != text_line_break_kind::mandatory);
        ++column_capacity;
        glyph_capacity = static_cast<std::uint32_t>(end);
        start = end;
    }
}

float alignment_shift(
    const text_layout_options& options,
    float content_width) noexcept {
    if (options.maximum_width <= content_width) return 0.0F;
    switch (options.alignment) {
        case text_alignment::center:
            return (options.maximum_width - content_width) * 0.5F;
        case text_alignment::right:
            return options.maximum_width - content_width;
        case text_alignment::left:
        case text_alignment::justify:
            return 0.0F;
    }
    return 0.0F;
}

bool try_round_to_even(float value, std::int32_t& result) noexcept {
    result = 0;
    if (!std::isfinite(value)) return false;
    const double floor_value = std::floor(static_cast<double>(value));
    const double fraction = static_cast<double>(value) - floor_value;
    double rounded = floor_value;
    if (fraction > 0.5 ||
        (fraction == 0.5 && std::fmod(std::abs(floor_value), 2.0) != 0.0)) {
        rounded = floor_value + 1.0;
    }
    if (rounded < static_cast<double>(std::numeric_limits<std::int32_t>::min()) ||
        rounded > static_cast<double>(std::numeric_limits<std::int32_t>::max())) {
        return false;
    }
    result = static_cast<std::int32_t>(rounded);
    return true;
}

template<typename ProjectMetrics>
void layout_vertical_core(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    const text_vertical_layout_requirements& requirements,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_column> columns,
    std::uint32_t& glyph_count,
    std::uint32_t& column_count,
    ProjectMetrics&& project_metrics) noexcept {
    std::size_t start = 0U;
    while (start < requirements.glyph_capacity) {
        std::size_t end = start;
        do {
            ++end;
        } while (end < requirements.glyph_capacity &&
            breaks_after[end - 1U] != text_line_break_kind::mandatory);

        const float column_x = static_cast<float>(column_count) *
            options.line_height;
        float cursor_y = 0.0F;
        for (std::size_t index = start; index < end; ++index) {
            const auto& glyph = glyphs[index];
            const auto metrics = project_metrics(index, glyph);
            positioned_glyphs[index] = positioned_text_glyph{
                static_cast<std::uint32_t>(index),
                glyph.glyph_id,
                glyph.cluster,
                column_x + options.line_height * 0.5F + metrics.offset_x,
                cursor_y + metrics.offset_y,
                metrics.advance_x,
                metrics.advance_y};
            cursor_y += metrics.advance_y;
        }
        const std::int32_t input_end = end < glyphs.size()
            ? glyphs[end].cluster
            : glyphs[end - 1U].cluster + 1;
        columns[column_count] = positioned_text_column{
            static_cast<std::uint32_t>(start),
            static_cast<std::uint32_t>(end - start),
            glyphs[start].cluster,
            input_end,
            std::abs(cursor_y),
            column_x,
            options.line_height,
            end == requirements.glyph_capacity && end < glyphs.size()};
        ++column_count;
        start = end;
    }

    glyph_count = requirements.glyph_capacity;
    const float shift = alignment_shift(
        options, static_cast<float>(column_count) * options.line_height);
    if (shift > 0.0F) {
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            positioned_glyphs[index].x += shift;
        }
        for (std::uint32_t index = 0U; index < column_count; ++index) {
            columns[index].x += shift;
        }
    }
}

} // namespace

bool try_get_vertical_text_layout_requirements(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    text_vertical_layout_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        breaks_after.size() != glyphs.size() || !valid_breaks(breaks_after) ||
        !valid_options(options)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    get_capacities(
        glyphs,
        breaks_after,
        options.maximum_lines,
        result.glyph_capacity,
        result.column_capacity);
    set_error(error, font_error::none);
    return true;
}

bool try_layout_vertical_shaped_text(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_column> columns,
    std::uint32_t& glyph_count,
    std::uint32_t& column_count,
    font_error* error) noexcept {
    glyph_count = 0U;
    column_count = 0U;
    text_vertical_layout_requirements requirements{};
    if (!try_get_vertical_text_layout_requirements(
            glyphs, breaks_after, options, requirements, error)) {
        return false;
    }
    if (positioned_glyphs.size() < requirements.glyph_capacity ||
        columns.size() < requirements.column_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    layout_vertical_core(
        glyphs,
        breaks_after,
        options,
        requirements,
        positioned_glyphs,
        columns,
        glyph_count,
        column_count,
        [&](std::size_t, const shaping_glyph& glyph) {
            return text_vertical_open_type_metrics{
                static_cast<float>(glyph.advance_x) * options.scale,
                static_cast<float>(glyph.advance_y) * options.scale,
                static_cast<float>(glyph.offset_x) * options.scale,
                static_cast<float>(glyph.offset_y) * options.scale};
        });
    set_error(error, font_error::none);
    return true;
}

bool try_layout_vertical_open_type_text(
    const sfnt_font_view& font,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::span<text_vertical_open_type_metrics> metric_scratch,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_column> columns,
    std::uint32_t& glyph_count,
    std::uint32_t& column_count,
    sfnt_glyph_phantom_variation_scratch* advance_scratch,
    font_error* error) noexcept {
    glyph_count = 0U;
    column_count = 0U;
    text_vertical_layout_requirements requirements{};
    if (!try_get_vertical_text_layout_requirements(
            glyphs, breaks_after, options, requirements, error)) {
        return false;
    }
    if (metric_scratch.size() < requirements.glyph_capacity ||
        positioned_glyphs.size() < requirements.glyph_capacity ||
        columns.size() < requirements.column_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header) || header.units_per_em == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    for (std::uint32_t index = 0U;
        index < requirements.glyph_capacity;
        ++index) {
        const auto& glyph = glyphs[index];
        if (glyph.glyph_id > std::numeric_limits<std::uint16_t>::max()) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const auto glyph_id = static_cast<std::uint16_t>(glyph.glyph_id);
        std::int32_t design_advance_height = 0;
        std::int32_t design_origin_y = 0;
        if (!font.try_get_design_advance_height(
                glyph_id, design_advance_height) ||
            !font.try_get_design_vertical_origin_y(
                glyph_id, design_origin_y)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        sfnt_design_advance_width_requirements advance_requirements{};
        if (!font.try_get_design_advance_width_requirements(
                glyph_id,
                normalized_coordinates,
                advance_requirements,
                error)) {
            return false;
        }
        float design_advance_width = 0.0F;
        const bool needs_phantom_scratch =
            advance_requirements.glyph_variation_item_count != 0U;
        if (needs_phantom_scratch && advance_scratch == nullptr) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
        const bool has_advance = needs_phantom_scratch
            ? font.try_get_design_advance_width(
                glyph_id,
                normalized_coordinates,
                design_advance_width,
                *advance_scratch,
                error)
            : font.try_get_design_advance_width(
                glyph_id,
                normalized_coordinates,
                design_advance_width,
                error);
        if (!has_advance) return false;

        std::int32_t base_width = 0;
        std::int32_t scaled_width = 0;
        std::int32_t scaled_advance_height = 0;
        std::int32_t scaled_origin_y = 0;
        if (!try_round_to_even(design_advance_width, base_width) ||
            !try_round_to_even(
                design_advance_width * options.scale, scaled_width) ||
            !try_round_to_even(
                static_cast<float>(design_advance_height) * options.scale,
                scaled_advance_height) ||
            !try_round_to_even(
                static_cast<float>(design_origin_y) * options.scale,
                scaled_origin_y)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const std::int32_t base_offset_x = -(base_width / 2);
        const std::int32_t scaled_offset_x = -(scaled_width / 2);
        const std::int32_t base_advance_y = -design_advance_height;
        const std::int32_t scaled_advance_y = -scaled_advance_height;
        const std::int32_t base_offset_y = -design_origin_y;
        const std::int32_t scaled_offset_y = -scaled_origin_y;
        metric_scratch[index] = text_vertical_open_type_metrics{
            static_cast<float>(glyph.advance_x) * options.scale,
            -(((static_cast<float>(glyph.advance_y) -
                static_cast<float>(base_advance_y)) *
                options.scale) + static_cast<float>(scaled_advance_y)),
            ((static_cast<float>(glyph.offset_x) -
                static_cast<float>(base_offset_x)) *
                options.scale) + static_cast<float>(scaled_offset_x),
            -(((static_cast<float>(glyph.offset_y) -
                static_cast<float>(base_offset_y)) *
                options.scale) + static_cast<float>(scaled_offset_y))};
    }

    layout_vertical_core(
        glyphs,
        breaks_after,
        options,
        requirements,
        positioned_glyphs,
        columns,
        glyph_count,
        column_count,
        [&](std::size_t index, const shaping_glyph&) {
            return metric_scratch[index];
        });
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
