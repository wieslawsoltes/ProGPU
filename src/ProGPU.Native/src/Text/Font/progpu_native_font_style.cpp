#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// FontManager.ApplyStyleVariations at repository checkpoint 885f58c0.
// This pure C++20 policy layer keeps OS discovery and font ownership outside
// the text core and writes only caller-owned fixed-layout settings.

namespace progpu::native::text {
namespace {

constexpr auto weight_tag = open_type_tag::from_chars('w', 'g', 'h', 't');
constexpr auto width_tag = open_type_tag::from_chars('w', 'd', 't', 'h');
constexpr auto italic_tag = open_type_tag::from_chars('i', 't', 'a', 'l');
constexpr auto slant_tag = open_type_tag::from_chars('s', 'l', 'n', 't');
constexpr auto optical_size_tag = open_type_tag::from_chars('o', 'p', 's', 'z');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

bool normalize_request(font_style_request& request) noexcept {
    if (static_cast<std::uint8_t>(request.slant) >
        static_cast<std::uint8_t>(font_provider_slant::oblique)) {
        return false;
    }
    if (request.optical_size_fixed <= 0) {
        request.optical_size_fixed = 0;
    }
    if (request.weight == 0 && request.width == 0 &&
        request.slant == font_provider_slant::normal &&
        request.optical_size_fixed == 0) {
        request = {};
        return true;
    }
    request.weight = std::clamp(request.weight, 1, 1000);
    request.width = std::clamp(request.width, 1, 9);
    return true;
}

std::int32_t width_percent_fixed(std::int32_t width) noexcept {
    constexpr std::int32_t half = 1 << 15;
    switch (width) {
        case 1: return 50 << 16;
        case 2: return (62 << 16) + half;
        case 3: return 75 << 16;
        case 4: return (87 << 16) + half;
        case 5: return 100 << 16;
        case 6: return (112 << 16) + half;
        case 7: return 125 << 16;
        case 8: return 150 << 16;
        default: return 200 << 16;
    }
}

bool try_select_user_fixed(
    const sfnt_variation_axis& axis,
    const font_style_request& request,
    std::int32_t& result) noexcept {
    if (axis.tag == weight_tag) {
        result = request.weight << 16;
        return true;
    }
    if (axis.tag == width_tag) {
        result = width_percent_fixed(request.width);
        return true;
    }
    if (axis.tag == italic_tag) {
        result = request.slant == font_provider_slant::normal
            ? axis.minimum_fixed
            : axis.maximum_fixed;
        return true;
    }
    if (axis.tag == slant_tag) {
        if (request.slant == font_provider_slant::normal) {
            result = std::clamp(
                std::int32_t{0}, axis.minimum_fixed, axis.maximum_fixed);
        } else {
            const auto minimum_magnitude = axis.minimum_fixed < 0
                ? -static_cast<std::int64_t>(axis.minimum_fixed)
                : static_cast<std::int64_t>(axis.minimum_fixed);
            const auto maximum_magnitude = axis.maximum_fixed < 0
                ? -static_cast<std::int64_t>(axis.maximum_fixed)
                : static_cast<std::int64_t>(axis.maximum_fixed);
            result = minimum_magnitude >= maximum_magnitude
                ? axis.minimum_fixed
                : axis.maximum_fixed;
        }
        return true;
    }
    if (axis.tag == optical_size_tag && request.optical_size_fixed > 0) {
        result = request.optical_size_fixed;
        return true;
    }
    return false;
}

bool inspect(
    const sfnt_font_view& font,
    font_style_request request,
    font_style_variation_requirements& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if (!normalize_request(request)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint16_t axis_count = 0U;
    if (!font.try_get_variation_axis_count(axis_count, error)) return false;
    for (std::uint16_t index = 0U; index < axis_count; ++index) {
        sfnt_variation_axis axis{};
        if (!font.try_get_variation_axis(index, axis, error)) return false;
        std::int32_t user_fixed = 0;
        if (!try_select_user_fixed(axis, request, user_fixed)) continue;
        std::int16_t normalized = 0;
        if (!font.try_normalize_variation_coordinate(
                index, user_fixed, normalized, error)) {
            return false;
        }
        if (result.setting_count ==
            std::numeric_limits<std::uint16_t>::max()) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        ++result.setting_count;
    }
    return true;
}

} // namespace

bool try_get_font_style_variation_requirements(
    const sfnt_font_view& font,
    font_style_request request,
    font_style_variation_requirements& result,
    font_error* error) noexcept {
    return inspect(font, request, result, error);
}

bool try_resolve_font_style_variations(
    const sfnt_font_view& font,
    font_style_request request,
    std::span<font_style_variation> output,
    std::uint16_t& written,
    font_style_variation_requirements* requirements,
    font_error* error) noexcept {
    written = 0U;
    if (requirements != nullptr) *requirements = {};
    font_style_variation_requirements resolved{};
    if (!inspect(font, request, resolved, error)) return false;
    if (requirements != nullptr) *requirements = resolved;
    if (output.size() < resolved.setting_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (!normalize_request(request)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint16_t axis_count = 0U;
    if (!font.try_get_variation_axis_count(axis_count, error)) return false;
    for (std::uint16_t index = 0U; index < axis_count; ++index) {
        sfnt_variation_axis axis{};
        if (!font.try_get_variation_axis(index, axis, error)) return false;
        std::int32_t user_fixed = 0;
        if (!try_select_user_fixed(axis, request, user_fixed)) continue;
        std::int16_t normalized = 0;
        if (!font.try_normalize_variation_coordinate(
                index, user_fixed, normalized, error)) {
            return false;
        }
        output[written++] = font_style_variation{
            axis.tag, user_fixed, normalized, index};
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
