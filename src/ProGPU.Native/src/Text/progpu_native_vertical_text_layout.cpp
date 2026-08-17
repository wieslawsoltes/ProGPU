#include "progpu_native_text.hpp"

#include "Shaping/progpu_native_open_type_projection_internal.hpp"

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
        open_type_shaped_glyph projected{};
        if (!detail::try_project_open_type_shape_glyph(
                font,
                normalized_coordinates,
                glyphs[index],
                options.scale,
                options.direction,
                projected,
                advance_scratch,
                error)) {
            return false;
        }
        metric_scratch[index] = text_vertical_open_type_metrics{
            projected.advance_x,
            projected.advance_y,
            projected.offset_x,
            projected.offset_y};
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
