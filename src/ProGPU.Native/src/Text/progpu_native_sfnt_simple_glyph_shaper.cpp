#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct allocation-free port of ProGPU-owned SfntSimpleGlyphShaper.cs at
// checkpoint 668fc254. UTF-16 and rounding behavior remain value-identical;
// caller spans replace managed arrays and callback traffic.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t soft_hyphen = 0x00ADU;
constexpr std::uint32_t maximum_simple_glyph_count = 65536U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool high_surrogate(char16_t value) noexcept {
    return value >= u'\xD800' && value <= u'\xDBFF';
}

bool low_surrogate(char16_t value) noexcept {
    return value >= u'\xDC00' && value <= u'\xDFFF';
}

std::uint32_t read_code_point(
    std::span<const char16_t> text,
    std::size_t index,
    std::size_t& code_unit_count) noexcept {
    const char16_t first = text[index];
    if (high_surrogate(first) && index + 1U < text.size() &&
        low_surrogate(text[index + 1U])) {
        code_unit_count = 2U;
        return 0x10000U +
            ((static_cast<std::uint32_t>(first) - 0xD800U) << 10U) +
            (static_cast<std::uint32_t>(text[index + 1U]) - 0xDC00U);
    }
    code_unit_count = 1U;
    return static_cast<std::uint32_t>(first);
}

bool try_get_mapped_glyph(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    std::uint16_t blank_glyph_index,
    std::uint16_t hyphen_glyph_index,
    std::uint16_t& glyph_index) noexcept {
    if (code_point == soft_hyphen) {
        glyph_index = hyphen_glyph_index != 0U
            ? hyphen_glyph_index
            : blank_glyph_index;
        return true;
    }
    if (is_sfnt_simple_formatting_control(code_point)) {
        glyph_index = blank_glyph_index;
        return true;
    }
    return font.try_get_glyph_index(code_point, glyph_index);
}

bool try_round_to_even(double value, std::int32_t& result) noexcept {
    result = 0;
    if (!std::isfinite(value)) return false;
    const double magnitude = std::abs(value);
    const double floor_value = std::floor(magnitude);
    const double fraction = magnitude - floor_value;
    double rounded = floor_value;
    if (fraction > 0.5 ||
        (fraction == 0.5 && std::fmod(floor_value, 2.0) != 0.0)) {
        rounded += 1.0;
    }
    if (std::signbit(value)) rounded = -rounded;
    if (rounded <
            static_cast<double>(std::numeric_limits<std::int32_t>::min()) ||
        rounded >
            static_cast<double>(std::numeric_limits<std::int32_t>::max())) {
        return false;
    }
    result = static_cast<std::int32_t>(rounded);
    return true;
}

bool try_get_scaled_advance(
    const sfnt_simple_glyph_metrics& metrics,
    std::uint16_t units_per_em,
    double font_em_size,
    double scaling_factor,
    bool is_sideways,
    std::int32_t& result) noexcept {
    const std::uint32_t design_advance = is_sideways
        ? metrics.advance_height
        : metrics.advance_width;
    const double scaled = static_cast<double>(design_advance) *
        font_em_size * scaling_factor / units_per_em;
    return try_round_to_even(scaled, result);
}

} // namespace

bool is_sfnt_simple_formatting_control(std::uint32_t code_point) noexcept {
    return code_point < 0x20U ||
        (code_point >= 0x7FU && code_point <= 0x9FU);
}

bool try_read_sfnt_simple_code_point(
    std::span<const char16_t> text,
    std::size_t text_index,
    std::uint32_t& code_point,
    std::uint32_t& code_unit_count,
    font_error* error) noexcept {
    code_point = 0U;
    code_unit_count = 0U;
    if (text_index >= text.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t count = 0U;
    code_point = read_code_point(text, text_index, count);
    code_unit_count = static_cast<std::uint32_t>(count);
    set_error(error, font_error::none);
    return true;
}

bool try_get_sfnt_simple_glyph_run_requirements(
    std::span<const char16_t> text,
    sfnt_simple_glyph_run_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (text.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t glyph_count = 0U;
    for (std::size_t index = 0U; index < text.size();) {
        if (glyph_count == maximum_simple_glyph_count) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        std::size_t code_unit_count = 0U;
        (void)read_code_point(text, index, code_unit_count);
        index += code_unit_count;
        ++glyph_count;
    }
    result = sfnt_simple_glyph_run_requirements{
        static_cast<std::uint32_t>(text.size()), glyph_count};
    set_error(error, font_error::none);
    return true;
}

bool try_build_sfnt_simple_glyph_run(
    const sfnt_font_view& font,
    std::span<const char16_t> text,
    std::uint16_t blank_glyph_index,
    std::uint16_t hyphen_glyph_index,
    std::span<std::uint16_t> cluster_map,
    std::span<std::uint16_t> glyph_indices,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    glyph_count = 0U;
    sfnt_simple_glyph_run_requirements requirements{};
    if (!try_get_sfnt_simple_glyph_run_requirements(
            text, requirements, error)) {
        return false;
    }
    if (cluster_map.size() < requirements.cluster_map_count ||
        glyph_indices.size() < requirements.glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (std::size_t index = 0U; index < text.size();) {
        std::size_t code_unit_count = 0U;
        const std::uint32_t code_point =
            read_code_point(text, index, code_unit_count);
        std::uint16_t ignored = 0U;
        if (!try_get_mapped_glyph(
                font,
                code_point,
                blank_glyph_index,
                hyphen_glyph_index,
                ignored)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        index += code_unit_count;
    }

    std::uint32_t output_index = 0U;
    for (std::size_t index = 0U; index < text.size();) {
        std::size_t code_unit_count = 0U;
        const std::uint32_t code_point =
            read_code_point(text, index, code_unit_count);
        std::uint16_t glyph_index = 0U;
        (void)try_get_mapped_glyph(
            font,
            code_point,
            blank_glyph_index,
            hyphen_glyph_index,
            glyph_index);
        glyph_indices[output_index] = glyph_index;
        for (std::size_t offset = 0U; offset < code_unit_count; ++offset) {
            cluster_map[index + offset] =
                static_cast<std::uint16_t>(output_index);
        }
        ++output_index;
        index += code_unit_count;
    }
    glyph_count = output_index;
    set_error(error, font_error::none);
    return true;
}

bool try_fill_sfnt_simple_glyph_advances(
    std::span<const char16_t> text,
    std::span<const std::uint16_t> cluster_map,
    std::span<const std::uint16_t> glyph_indices,
    std::span<const sfnt_simple_glyph_metrics> glyph_metrics,
    std::uint16_t design_units_per_em,
    double font_em_size,
    double scaling_factor,
    bool is_sideways,
    std::span<std::uint8_t> glyph_state_scratch,
    std::span<std::int32_t> glyph_advances,
    font_error* error) noexcept {
    if (cluster_map.size() < text.size() ||
        glyph_metrics.size() < glyph_indices.size() ||
        glyph_state_scratch.size() < glyph_indices.size() ||
        glyph_advances.size() < glyph_indices.size() ||
        !std::isfinite(font_em_size) || !std::isfinite(scaling_factor)) {
        set_error(error,
            glyph_state_scratch.size() < glyph_indices.size() ||
                glyph_advances.size() < glyph_indices.size()
                ? font_error::insufficient_buffer
                : font_error::invalid_argument);
        return false;
    }
    const std::uint16_t units_per_em = design_units_per_em == 0U
        ? 1U
        : design_units_per_em;
    std::fill_n(
        glyph_state_scratch.begin(), glyph_indices.size(), std::uint8_t{0U});
    for (std::size_t index = 0U; index < text.size(); ++index) {
        const std::size_t mapped = cluster_map[index];
        if (mapped >= glyph_indices.size() ||
            glyph_state_scratch[mapped] != 0U) {
            continue;
        }
        std::size_t ignored_count = 0U;
        const std::uint32_t code_point =
            read_code_point(text, index, ignored_count);
        glyph_state_scratch[mapped] =
            is_sfnt_simple_formatting_control(code_point) ? 1U : 2U;
    }
    for (std::size_t index = 0U; index < glyph_indices.size(); ++index) {
        if (glyph_state_scratch[index] == 1U) continue;
        std::int32_t ignored = 0;
        if (!try_get_scaled_advance(
                glyph_metrics[index],
                units_per_em,
                font_em_size,
                scaling_factor,
                is_sideways,
                ignored)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    for (std::size_t index = 0U; index < glyph_indices.size(); ++index) {
        std::int32_t advance = 0;
        if (glyph_state_scratch[index] != 1U) {
            (void)try_get_scaled_advance(
                glyph_metrics[index],
                units_per_em,
                font_em_size,
                scaling_factor,
                is_sideways,
                advance);
        }
        glyph_advances[index] = advance;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
