#include "progpu_native_open_type_projection_internal.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct port of ProGPU-owned OpenTypeTextShaper.Shape metric projection.
// The shaping engine keeps exact integer design units; this bounded pass
// publishes the managed font-size-scaled Y-down contract without allocation.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool is_direction(shaping_direction direction) noexcept {
    return direction == shaping_direction::left_to_right ||
        direction == shaping_direction::right_to_left ||
        direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
}

bool is_vertical(shaping_direction direction) noexcept {
    return direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
}

bool try_round_to_even(float value, std::int32_t& result) noexcept {
    result = 0;
    if (!std::isfinite(value)) return false;
    const double floor_value = std::floor(static_cast<double>(value));
    const double fraction = static_cast<double>(value) - floor_value;
    double rounded = floor_value;
    if (fraction > 0.5 ||
        (fraction == 0.5 &&
            std::fmod(std::abs(floor_value), 2.0) != 0.0)) {
        rounded = floor_value + 1.0;
    }
    if (rounded <
            static_cast<double>(std::numeric_limits<std::int32_t>::min()) ||
        rounded >
            static_cast<double>(std::numeric_limits<std::int32_t>::max())) {
        return false;
    }
    result = static_cast<std::int32_t>(rounded);
    return true;
}

} // namespace

namespace detail {

bool try_project_open_type_shape_glyph(
    const sfnt_font_view& font,
    std::span<const std::int16_t> normalized_coordinates,
    const shaping_glyph& glyph,
    float scale,
    shaping_direction direction,
    open_type_shaped_glyph& result,
    sfnt_glyph_phantom_variation_scratch* advance_scratch,
    font_error* error) noexcept {
    result = {};
    if (!std::isfinite(scale) || scale <= 0.0F ||
        !is_direction(direction) ||
        glyph.glyph_id > std::numeric_limits<std::uint16_t>::max()) {
        set_error(error, glyph.glyph_id >
                std::numeric_limits<std::uint16_t>::max()
            ? font_error::invalid_glyph
            : font_error::invalid_argument);
        return false;
    }

    float advance_y = static_cast<float>(glyph.advance_y) * scale;
    float offset_x = static_cast<float>(glyph.offset_x) * scale;
    float offset_y = static_cast<float>(glyph.offset_y) * scale;
    if (is_vertical(direction) && scale != 1.0F) {
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
        sfnt_design_advance_width_requirements requirements{};
        if (!font.try_get_design_advance_width_requirements(
                glyph_id,
                normalized_coordinates,
                requirements,
                error)) {
            return false;
        }
        const bool needs_scratch =
            requirements.glyph_variation_item_count != 0U;
        if (needs_scratch && advance_scratch == nullptr) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
        float design_advance_width = 0.0F;
        const bool has_advance = needs_scratch
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
                design_advance_width * scale, scaled_width) ||
            !try_round_to_even(
                static_cast<float>(design_advance_height) * scale,
                scaled_advance_height) ||
            !try_round_to_even(
                static_cast<float>(design_origin_y) * scale,
                scaled_origin_y)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const std::int32_t base_advance_y = -design_advance_height;
        const std::int32_t scaled_advance_y = -scaled_advance_height;
        const std::int32_t base_offset_x = -(base_width / 2);
        const std::int32_t scaled_offset_x = -(scaled_width / 2);
        const std::int32_t base_offset_y = -design_origin_y;
        const std::int32_t scaled_offset_y = -scaled_origin_y;
        advance_y =
            ((static_cast<float>(glyph.advance_y) -
                static_cast<float>(base_advance_y)) * scale) +
            static_cast<float>(scaled_advance_y);
        offset_x =
            ((static_cast<float>(glyph.offset_x) -
                static_cast<float>(base_offset_x)) * scale) +
            static_cast<float>(scaled_offset_x);
        offset_y =
            ((static_cast<float>(glyph.offset_y) -
                static_cast<float>(base_offset_y)) * scale) +
            static_cast<float>(scaled_offset_y);
    }

    result = open_type_shaped_glyph{
        glyph.glyph_id,
        glyph.code_point,
        glyph.cluster,
        glyph.flags,
        static_cast<float>(glyph.advance_x) * scale,
        -advance_y,
        offset_x,
        -offset_y};
    set_error(error, font_error::none);
    return true;
}

} // namespace detail

bool try_project_open_type_shape_result(
    const sfnt_font_view& font,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<const shaping_glyph> glyphs,
    float font_size,
    shaping_direction direction,
    std::span<open_type_shaped_glyph> output,
    sfnt_glyph_phantom_variation_scratch* advance_scratch,
    font_error* error) noexcept {
    if (output.size() < glyphs.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header) || header.units_per_em == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (!std::isfinite(font_size) || font_size <= 0.0F ||
        !is_direction(direction)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const float scale = font_size / static_cast<float>(header.units_per_em);
    for (const auto& glyph : glyphs) {
        open_type_shaped_glyph projected{};
        if (!detail::try_project_open_type_shape_glyph(
                font,
                normalized_coordinates,
                glyph,
                scale,
                direction,
                projected,
                advance_scratch,
                error)) {
            return false;
        }
    }
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        if (!detail::try_project_open_type_shape_glyph(
                font,
                normalized_coordinates,
                glyphs[index],
                scale,
                direction,
                output[index],
                advance_scratch,
                error)) {
            return false;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
