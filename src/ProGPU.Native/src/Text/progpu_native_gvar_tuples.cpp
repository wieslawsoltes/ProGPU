#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ParseGlyphVariation/CalculateScalar at checkpoint
// 55e1bf4e. Headers and F2Dot14 regions write to exact caller-owned spans.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;

constexpr std::uint16_t embedded_peak_tuple = 0x8000U;
constexpr std::uint16_t intermediate_region = 0x4000U;
constexpr std::uint16_t tuple_index_mask = 0x0FFFU;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_preflight_tuple_headers(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    sfnt_glyph_variation_data_view& view,
    sfnt_gvar_header& gvar,
    sfnt_gvar_tuple_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (!font.try_get_gvar_header(gvar, error) ||
        !font.try_get_glyph_variation_data(glyph_index, view, error)) {
        return false;
    }
    if (view.empty()) {
        return true;
    }
    const auto coordinate_count =
        static_cast<std::size_t>(view.tuple_count) * gvar.axis_count * 3U;
    if (coordinate_count > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    std::size_t cursor = 4U;
    std::size_t serialized_size = 0U;
    const auto tuple_bytes = static_cast<std::size_t>(gvar.axis_count) * 2U;
    for (std::uint16_t index = 0U; index < view.tuple_count; ++index) {
        if (!can_read(view.bytes, cursor, 4U)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const auto size = read_u16(view.bytes, cursor);
        const auto flags = read_u16(view.bytes, cursor + 2U);
        cursor += 4U;
        if ((flags & embedded_peak_tuple) != 0U) {
            if (!can_read(view.bytes, cursor, tuple_bytes)) {
                set_error(error, font_error::invalid_glyph);
                return false;
            }
            cursor += tuple_bytes;
        } else if ((flags & tuple_index_mask) >= gvar.shared_tuple_count) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        if ((flags & intermediate_region) != 0U) {
            if (!can_read(view.bytes, cursor, tuple_bytes * 2U)) {
                set_error(error, font_error::invalid_glyph);
                return false;
            }
            cursor += tuple_bytes * 2U;
        }
        if (serialized_size >
            std::numeric_limits<std::size_t>::max() - size) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        serialized_size += size;
    }
    if (cursor > view.serialized_data_offset ||
        view.serialized_data_offset > view.bytes.size() ||
        serialized_size > view.bytes.size() - view.serialized_data_offset) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result.tuple_count = view.tuple_count;
    result.region_coordinate_count =
        static_cast<std::uint32_t>(coordinate_count);
    set_error(error, font_error::none);
    return true;
}

float calculate_axis_scalar(
    float coordinate,
    float start,
    float peak,
    float end) noexcept {
    if (start > peak || peak > end ||
        (start < 0.0F && end > 0.0F && peak != 0.0F) || peak == 0.0F) {
        return 1.0F;
    }
    if (coordinate < start || coordinate > end) {
        return 0.0F;
    }
    if (coordinate == peak) {
        return 1.0F;
    }
    if (coordinate < peak) {
        return peak == start
            ? 1.0F
            : (coordinate - start) / (peak - start);
    }
    return end == peak ? 1.0F : (end - coordinate) / (end - peak);
}

} // namespace

float sfnt_gvar_tuple_data::calculate_scalar(
    std::span<const std::int16_t> normalized_coordinates,
    std::span<const std::int16_t> region_coordinates) noexcept {
    if (region_coordinates.size() % 3U != 0U) {
        return 0.0F;
    }
    const auto axis_count = region_coordinates.size() / 3U;
    const auto count = std::min(normalized_coordinates.size(), axis_count);
    float scalar = 1.0F;
    for (std::size_t axis = 0U; axis < count; ++axis) {
        constexpr float scale = 1.0F / 16384.0F;
        scalar *= calculate_axis_scalar(
            normalized_coordinates[axis] * scale,
            region_coordinates[axis] * scale,
            region_coordinates[axis_count + axis] * scale,
            region_coordinates[axis_count * 2U + axis] * scale);
        if (scalar == 0.0F) {
            break;
        }
    }
    return scalar;
}

bool sfnt_font_view::try_get_glyph_variation_tuple_requirements(
    std::uint16_t glyph_index,
    sfnt_gvar_tuple_requirements& result,
    font_error* error) const noexcept {
    sfnt_glyph_variation_data_view view{};
    sfnt_gvar_header gvar{};
    return try_preflight_tuple_headers(
        *this, glyph_index, view, gvar, result, error);
}

bool sfnt_font_view::try_decode_glyph_variation_tuple_headers(
    std::uint16_t glyph_index,
    std::span<sfnt_gvar_tuple_header> headers,
    std::span<std::int16_t> region_coordinates,
    std::uint16_t& headers_written,
    std::uint32_t& coordinates_written,
    font_error* error) const noexcept {
    headers_written = 0U;
    coordinates_written = 0U;
    sfnt_glyph_variation_data_view view{};
    sfnt_gvar_header gvar{};
    sfnt_gvar_tuple_requirements requirements{};
    if (!try_preflight_tuple_headers(
            *this, glyph_index, view, gvar, requirements, error)) {
        return false;
    }
    if (headers.size() < requirements.tuple_count ||
        region_coordinates.size() < requirements.region_coordinate_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::size_t cursor = 4U;
    const auto axis_count = static_cast<std::size_t>(gvar.axis_count);
    const auto tuple_bytes = axis_count * 2U;
    for (std::uint16_t index = 0U;
         index < requirements.tuple_count;
         ++index) {
        const auto size = read_u16(view.bytes, cursor);
        const auto flags = read_u16(view.bytes, cursor + 2U);
        cursor += 4U;
        const auto coordinate_offset =
            static_cast<std::size_t>(index) * axis_count * 3U;
        auto coordinates = region_coordinates.subspan(
            coordinate_offset, axis_count * 3U);
        auto start = coordinates.first(axis_count);
        auto peak = coordinates.subspan(axis_count, axis_count);
        auto end = coordinates.last(axis_count);
        if ((flags & embedded_peak_tuple) != 0U) {
            for (std::size_t axis = 0U; axis < axis_count; ++axis) {
                peak[axis] = read_i16(view.bytes, cursor + axis * 2U);
            }
            cursor += tuple_bytes;
        } else {
            std::uint16_t peak_written = 0U;
            if (!try_decode_gvar_shared_tuple(
                    flags & tuple_index_mask,
                    peak,
                    peak_written,
                    error) || peak_written != gvar.axis_count) {
                return false;
            }
        }
        if ((flags & intermediate_region) != 0U) {
            for (std::size_t axis = 0U; axis < axis_count; ++axis) {
                start[axis] = read_i16(view.bytes, cursor + axis * 2U);
                end[axis] = read_i16(
                    view.bytes,
                    cursor + tuple_bytes + axis * 2U);
            }
            cursor += tuple_bytes * 2U;
        } else {
            for (std::size_t axis = 0U; axis < axis_count; ++axis) {
                start[axis] = std::min<std::int16_t>(peak[axis], 0);
                end[axis] = std::max<std::int16_t>(peak[axis], 0);
            }
        }
        headers[index] = {
            static_cast<std::uint32_t>(coordinate_offset), size, flags};
    }
    headers_written = requirements.tuple_count;
    coordinates_written = requirements.region_coordinate_count;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
