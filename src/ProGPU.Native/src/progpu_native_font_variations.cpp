#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ParseAxes at repository checkpoint e94630ef. This
// translation unit preserves its bounded fvar range and fixed-coordinate
// contracts without porting foreign font-engine implementation source.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_i32;
using detail::read_u16;
using detail::read_u32;

constexpr auto fvar_tag = open_type_tag::from_chars('f', 'v', 'a', 'r');
constexpr auto avar_tag = open_type_tag::from_chars('a', 'v', 'a', 'r');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_axis_layout(
    const sfnt_font_view& font,
    sfnt_table_view& table,
    std::uint16_t& count,
    std::uint16_t& size,
    std::size_t& axes_offset,
    font_error* error) noexcept {
    table = {};
    count = 0U;
    size = 0U;
    axes_offset = 0U;
    set_error(error, font_error::none);
    if (!font.try_get_table(fvar_tag, table)) {
        return true;
    }
    if (!can_read(table.bytes, 0U, 16U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    axes_offset = read_u16(table.bytes, 4U);
    count = read_u16(table.bytes, 8U);
    size = read_u16(table.bytes, 10U);
    if (count == 0U || size < 20U) {
        count = 0U;
        return true;
    }
    if (axes_offset > table.bytes.size() ||
        count > (table.bytes.size() - axes_offset) / size) {
        count = 0U;
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

std::int16_t normalize_fvar_coordinate(
    const sfnt_variation_axis& axis,
    std::int32_t user_fixed) noexcept {
    user_fixed = std::clamp(
        user_fixed,
        axis.minimum_fixed,
        axis.maximum_fixed);
    double normalized = 0.0;
    if (user_fixed < axis.default_fixed) {
        const auto range = static_cast<std::int64_t>(axis.default_fixed) -
            axis.minimum_fixed;
        normalized = range == 0
            ? 0.0
            : -static_cast<double>(
                static_cast<std::int64_t>(axis.default_fixed) - user_fixed) /
                static_cast<double>(range);
    } else if (user_fixed > axis.default_fixed) {
        const auto range = static_cast<std::int64_t>(axis.maximum_fixed) -
            axis.default_fixed;
        normalized = range == 0
            ? 0.0
            : static_cast<double>(
                static_cast<std::int64_t>(user_fixed) - axis.default_fixed) /
                static_cast<double>(range);
    }
    const auto rounded = std::round(normalized * 16384.0);
    return static_cast<std::int16_t>(std::clamp(
        rounded,
        -16384.0,
        16384.0));
}

std::int16_t round_mapped_coordinate(float value) noexcept {
    const auto lower = std::floor(value);
    const auto fraction = value - lower;
    const auto rounded = fraction < 0.5F
        ? lower
        : fraction > 0.5F
            ? lower + 1.0F
            : std::fmod(lower, 2.0F) == 0.0F
                ? lower
                : lower + 1.0F;
    return static_cast<std::int16_t>(std::clamp(
        rounded,
        -16384.0F,
        16384.0F));
}

} // namespace

float sfnt_variation_axis::minimum() const noexcept {
    return static_cast<float>(minimum_fixed) / 65536.0F;
}

float sfnt_variation_axis::default_value() const noexcept {
    return static_cast<float>(default_fixed) / 65536.0F;
}

float sfnt_variation_axis::maximum() const noexcept {
    return static_cast<float>(maximum_fixed) / 65536.0F;
}

bool sfnt_variation_axis::hidden() const noexcept {
    return (flags & 1U) != 0U;
}

bool sfnt_font_view::try_get_variation_axis_count(
    std::uint16_t& result,
    font_error* error) const noexcept {
    sfnt_table_view table{};
    std::uint16_t size = 0U;
    std::size_t offset = 0U;
    return try_get_axis_layout(
        *this,
        table,
        result,
        size,
        offset,
        error);
}

bool sfnt_font_view::try_decode_variation_axes(
    std::span<sfnt_variation_axis> axes,
    std::uint16_t& written,
    font_error* error) const noexcept {
    written = 0U;
    sfnt_table_view table{};
    std::uint16_t count = 0U;
    std::uint16_t size = 0U;
    std::size_t axes_offset = 0U;
    if (!try_get_axis_layout(
            *this,
            table,
            count,
            size,
            axes_offset,
            error)) {
        return false;
    }
    if (axes.size() < count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (std::uint16_t index = 0U; index < count; ++index) {
        const auto offset = axes_offset +
            static_cast<std::size_t>(index) * size;
        axes[index] = {
            {read_u32(table.bytes, offset)},
            read_i32(table.bytes, offset + 4U),
            read_i32(table.bytes, offset + 8U),
            read_i32(table.bytes, offset + 12U),
            read_u16(table.bytes, offset + 16U),
            read_u16(table.bytes, offset + 18U)};
    }
    written = count;
    set_error(error, font_error::none);
    return true;
}

bool sfnt_font_view::try_normalize_variation_coordinate(
    std::uint16_t axis_index,
    std::int32_t user_fixed,
    std::int16_t& result,
    font_error* error) const noexcept {
    result = 0;
    std::uint16_t axis_count = 0U;
    if (!try_get_variation_axis_count(axis_count, error)) {
        return false;
    }
    if (axis_index >= axis_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    sfnt_table_view fvar{};
    std::uint16_t record_size = 0U;
    std::size_t axes_offset = 0U;
    if (!try_get_axis_layout(
            *this,
            fvar,
            axis_count,
            record_size,
            axes_offset,
            error)) {
        return false;
    }
    const auto axis_offset = axes_offset +
        static_cast<std::size_t>(axis_index) * record_size;
    const sfnt_variation_axis axis{
        {read_u32(fvar.bytes, axis_offset)},
        read_i32(fvar.bytes, axis_offset + 4U),
        read_i32(fvar.bytes, axis_offset + 8U),
        read_i32(fvar.bytes, axis_offset + 12U),
        read_u16(fvar.bytes, axis_offset + 16U),
        read_u16(fvar.bytes, axis_offset + 18U)};
    if (axis.minimum_fixed > axis.default_fixed ||
        axis.default_fixed > axis.maximum_fixed) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto coordinate = normalize_fvar_coordinate(axis, user_fixed);

    sfnt_table_view avar{};
    if (!try_get_table(avar_tag, avar)) {
        result = coordinate;
        set_error(error, font_error::none);
        return true;
    }
    if (!can_read(avar.bytes, 0U, 8U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto avar_axis_count = read_u16(avar.bytes, 6U);
    if (avar_axis_count != axis_count) {
        result = coordinate;
        set_error(error, font_error::none);
        return true;
    }

    std::size_t offset = 8U;
    std::int16_t mapped = coordinate;
    for (std::uint16_t current_axis = 0U;
         current_axis < avar_axis_count;
         ++current_axis) {
        if (!can_read(avar.bytes, offset, 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto map_count = read_u16(avar.bytes, offset);
        offset += 2U;
        if (!can_read(
                avar.bytes,
                offset,
                static_cast<std::size_t>(map_count) * 4U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (current_axis == axis_index && map_count >= 2U) {
            auto previous_from = read_i16(avar.bytes, offset);
            auto previous_to = read_i16(avar.bytes, offset + 2U);
            mapped = previous_to;
            for (std::uint16_t map_index = 0U;
                 map_index < map_count;
                 ++map_index) {
                const auto pair = offset +
                    static_cast<std::size_t>(map_index) * 4U;
                const auto current_from = read_i16(avar.bytes, pair);
                const auto current_to = read_i16(avar.bytes, pair + 2U);
                if (coordinate <= current_from) {
                    if (coordinate == current_from || map_index == 0U) {
                        mapped = current_to;
                    } else {
                        const auto denominator = static_cast<float>(
                            current_from - previous_from);
                        if (denominator == 0.0F) {
                            set_error(error, font_error::invalid_face);
                            return false;
                        }
                        const auto ratio = static_cast<float>(
                            coordinate - previous_from) / denominator;
                        mapped = round_mapped_coordinate(
                            previous_to + ratio *
                                static_cast<float>(current_to - previous_to));
                    }
                    break;
                }
                previous_from = current_from;
                previous_to = current_to;
                mapped = current_to;
            }
        }
        offset += static_cast<std::size_t>(map_count) * 4U;
    }
    result = mapped;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
