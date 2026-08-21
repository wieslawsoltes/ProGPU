#include "progpu_native_space_fallback_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>

// Exact bounded port of the ProGPU-owned special-space fallback and metrics
// stages in ProGPU.Text/OpenTypeTextShaper.cs at repository checkpoint
// 17fa9643. Work is O(1) per glyph except the fixed ten-digit figure-space
// probe, uses caller-owned variation scratch, and performs no allocation.

namespace progpu::native::text::detail {
namespace {

enum class space_fallback : std::uint8_t {
    none = 0U,
    em = 1U,
    em2 = 2U,
    em3 = 3U,
    em4 = 4U,
    em5 = 5U,
    em6 = 6U,
    em16 = 16U,
    four_em18 = 17U,
    space = 18U,
    figure = 19U,
    punctuation = 20U,
    narrow = 21U
};

space_fallback get_space_fallback(std::uint32_t code_point) noexcept {
    switch (code_point) {
        case 0x0020U: case 0x00A0U: return space_fallback::space;
        case 0x2000U: case 0x2002U: return space_fallback::em2;
        case 0x2001U: case 0x2003U: case 0x3000U:
            return space_fallback::em;
        case 0x2004U: return space_fallback::em3;
        case 0x2005U: return space_fallback::em4;
        case 0x2006U: return space_fallback::em6;
        case 0x2007U: return space_fallback::figure;
        case 0x2008U: return space_fallback::punctuation;
        case 0x2009U: return space_fallback::em5;
        case 0x200AU: return space_fallback::em16;
        case 0x202FU: return space_fallback::narrow;
        case 0x205FU: return space_fallback::four_em18;
        default: return space_fallback::none;
    }
}

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

std::int32_t clamp_i16(std::int64_t value) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        value,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

std::int64_t round_to_even(float value) noexcept {
    const auto lower = std::floor(value);
    const auto fraction = value - lower;
    if (fraction < 0.5F) return static_cast<std::int64_t>(lower);
    if (fraction > 0.5F) return static_cast<std::int64_t>(lower + 1.0F);
    return static_cast<std::int64_t>(
        std::fmod(lower, 2.0F) == 0.0F ? lower : lower + 1.0F);
}

bool try_get_advance_width(
    const sfnt_font_view& font,
    std::uint16_t glyph,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    float& advance,
    font_error* error) noexcept {
    return scratch == nullptr
        ? font.try_get_design_advance_width(
            glyph, normalized_coordinates, advance, error)
        : font.try_get_design_advance_width(
            glyph,
            normalized_coordinates,
            advance,
            scratch->advance_width,
            error);
}

} // namespace

bool try_map_space_fallback(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    std::uint16_t& glyph,
    font_error* error) noexcept {
    if (glyph != 0U || get_space_fallback(code_point) == space_fallback::none) {
        set_error(error, font_error::none);
        return true;
    }
    std::uint16_t space = 0U;
    if (!font.try_get_glyph_index(0x20U, space)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (space != 0U) glyph = space;
    set_error(error, font_error::none);
    return true;
}

bool try_apply_space_fallback(
    const sfnt_font_view& font,
    shaping_direction direction,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch* scratch,
    shaping_glyph& glyph,
    font_error* error) noexcept {
    const auto fallback = get_space_fallback(glyph.code_point);
    if (fallback == space_fallback::none) {
        set_error(error, font_error::none);
        return true;
    }
    std::uint16_t original = 0U;
    std::uint16_t space = 0U;
    if (!font.try_get_glyph_index(glyph.code_point, original) ||
        !font.try_get_glyph_index(0x20U, space)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (original != 0U || space == 0U) {
        set_error(error, font_error::none);
        return true;
    }

    const bool vertical = direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
    const std::int64_t sign = vertical ? -1 : 1;
    std::int64_t advance = vertical ? glyph.advance_y : glyph.advance_x;
    const auto raw_fallback = static_cast<std::uint8_t>(fallback);
    if ((fallback >= space_fallback::em && fallback <= space_fallback::em6) ||
        fallback == space_fallback::em16) {
        sfnt_header_metrics header{};
        if (!font.try_get_header_metrics(header)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto divisor = static_cast<std::int64_t>(raw_fallback);
        advance = sign *
            ((static_cast<std::int64_t>(header.units_per_em) + divisor / 2) /
                divisor);
    } else if (fallback == space_fallback::four_em18) {
        sfnt_header_metrics header{};
        if (!font.try_get_header_metrics(header)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        advance = sign *
            (static_cast<std::int64_t>(header.units_per_em) * 4 / 18);
    } else if (fallback == space_fallback::figure) {
        for (std::uint32_t code_point = 0x30U; code_point <= 0x39U;
             ++code_point) {
            std::uint16_t candidate = 0U;
            if (!font.try_get_glyph_index(code_point, candidate)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (candidate == 0U) continue;
            if (vertical) {
                std::int32_t height = 0;
                if (!font.try_get_design_advance_height(candidate, height)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                advance = -static_cast<std::int64_t>(height);
            } else {
                float width = 0.0F;
                if (!try_get_advance_width(
                        font,
                        candidate,
                        normalized_coordinates,
                        scratch,
                        width,
                        error)) {
                    return false;
                }
                advance = round_to_even(width);
            }
            break;
        }
    } else if (fallback == space_fallback::punctuation) {
        std::uint16_t punctuation = 0U;
        if (!font.try_get_glyph_index(0x2EU, punctuation)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (punctuation == 0U &&
            !font.try_get_glyph_index(0x2CU, punctuation)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (punctuation != 0U) {
            if (vertical) {
                std::int32_t height = 0;
                if (!font.try_get_design_advance_height(
                        punctuation, height)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                advance = -static_cast<std::int64_t>(height);
            } else {
                float width = 0.0F;
                if (!try_get_advance_width(
                        font,
                        punctuation,
                        normalized_coordinates,
                        scratch,
                        width,
                        error)) {
                    return false;
                }
                advance = round_to_even(width);
            }
        }
    } else if (fallback == space_fallback::narrow) {
        advance /= 2;
    }

    if (vertical) glyph.advance_y = clamp_i16(advance);
    else glyph.advance_x = clamp_i16(advance);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail
