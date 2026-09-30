#include "progpu_native_space_fallback_internal.hpp"
#include "progpu_native_open_type_gpos_internal.hpp"

#include <algorithm>
#include <array>
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

bool try_apply_device_space_fallback(
    const sfnt_font_view& font,
    shaping_direction direction,
    std::span<const std::int16_t> normalized_coordinates,
    shaping_glyph& glyph,
    const gpos_device_frame& frame,
    const device_space_advances& advances,
    font_error* error) noexcept {
    const auto fail = [error](font_error value) noexcept {
        set_error(error, value);
        return false;
    };
    const auto valid_coordinates = [](std::span<const std::int16_t> values) noexcept {
        const auto start = reinterpret_cast<std::uintptr_t>(values.data());
        constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
        return values.size() <= maximum / sizeof(std::int16_t) &&
            (values.empty() || (values.data() != nullptr &&
                start % alignof(std::int16_t) == 0U)) &&
            values.size_bytes() <= maximum - start;
    };
    if (frame.font != &font || font.data().empty() || frame.owner == nullptr ||
        frame.project_design == nullptr ||
        (frame.arithmetic_path != gpos_arithmetic_path::intrinsic_simd &&
         frame.arithmetic_path != gpos_arithmetic_path::scalar_reference) ||
        (direction != shaping_direction::left_to_right &&
         direction != shaping_direction::right_to_left &&
         direction != shaping_direction::top_to_bottom &&
         direction != shaping_direction::bottom_to_top) ||
        normalized_coordinates.size() != frame.normalized_coordinates.size() ||
        !valid_coordinates(normalized_coordinates) ||
        !valid_coordinates(frame.normalized_coordinates))
        return fail(font_error::invalid_argument);
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
    if (frame.arithmetic_path == gpos_arithmetic_path::intrinsic_simd)
        return fail(font_error::invalid_argument);
#endif
    if (!std::equal(normalized_coordinates.begin(), normalized_coordinates.end(),
        frame.normalized_coordinates.begin())) return fail(font_error::invalid_argument);

    const auto fallback = get_space_fallback(glyph.code_point);
    if (fallback == space_fallback::none) {
        set_error(error, font_error::none);
        return true;
    }
    std::uint16_t original = 0U, space = 0U;
    if (!font.try_get_glyph_index(glyph.code_point, original) ||
        !font.try_get_glyph_index(0x20U, space)) return fail(font_error::invalid_face);
    if (original != 0U || space == 0U) {
        set_error(error, font_error::none);
        return true;
    }

    const bool vertical = direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
    std::int64_t advance = vertical ? glyph.advance_y : glyph.advance_x;
    if ((fallback >= space_fallback::em && fallback <= space_fallback::em6) ||
        fallback == space_fallback::em16 || fallback == space_fallback::four_em18) {
        sfnt_header_metrics header{};
        if (!font.try_get_header_metrics(header)) return fail(font_error::invalid_face);
        const auto divisor = static_cast<std::int64_t>(fallback);
        // Original ProGPU design-unit integer rounding precedes projection;
        // do not divide an already rounded device em or approximate the ratio.
        const auto design_advance = fallback == space_fallback::four_em18
            ? static_cast<std::int64_t>(header.units_per_em) * 4 / 18
            : (static_cast<std::int64_t>(header.units_per_em) + divisor / 2) / divisor;
        const auto signed_design = static_cast<std::int32_t>(vertical ?
            -design_advance : design_advance);
        const std::array<gpos_metric_vector, 2> design{{
            vertical ? gpos_metric_vector{0, signed_design} :
                gpos_metric_vector{signed_design, 0}, {}}};
        std::array<gpos_metric_vector, 2> device{};
        if (!frame.project_design(frame.owner, design, device))
            return fail(font_error::invalid_argument);
        advance = vertical ? device[0].y : device[0].x;
    } else if (fallback == space_fallback::figure ||
        fallback == space_fallback::punctuation) {
        std::uint16_t candidate = 0U;
        // Preserve original first-available mapping. A later unused digit or
        // comma must not introduce another mapping, capture or native failure.
        if (fallback == space_fallback::figure) {
            for (std::uint32_t code_point = 0x30U; code_point <= 0x39U; ++code_point) {
                if (!font.try_get_glyph_index(code_point, candidate))
                    return fail(font_error::invalid_face);
                if (candidate != 0U) break;
            }
        } else if (!font.try_get_glyph_index(0x2EU, candidate) ||
            (candidate == 0U && !font.try_get_glyph_index(0x2CU, candidate))) {
            return fail(font_error::invalid_face);
        }
        if (candidate != 0U) {
            if (advances.owner == nullptr || advances.get_advances == nullptr)
                return fail(font_error::invalid_argument);
            std::array<gpos_metric_vector, 1> device{};
            if (!advances.get_advances(advances.owner,
                fallback == space_fallback::figure ? device_space_advance_kind::figure :
                    device_space_advance_kind::punctuation,
                std::span<const std::uint16_t>(&candidate, 1U), device))
                return fail(font_error::invalid_argument);
            advance = vertical ? -static_cast<std::int64_t>(device[0].y) : device[0].x;
        }
    } else if (fallback == space_fallback::narrow) {
        advance /= 2;
    }
    if (advance < std::numeric_limits<std::int32_t>::min() ||
        advance > std::numeric_limits<std::int32_t>::max())
        return fail(font_error::invalid_argument);
    if (vertical) glyph.advance_y = static_cast<std::int32_t>(advance);
    else glyph.advance_x = static_cast<std::int32_t>(advance);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail
