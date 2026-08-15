#include "progpu_native_text.hpp"

#include "progpu_native_gvar_payload_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.GetCompositeGlyphDeltas at checkpoint 173e0d24. The
// output and all parser scratch remain caller-owned and allocation-free.

namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_composite_glyph_variation_requirements(
    std::uint16_t glyph_index,
    std::uint32_t component_count,
    sfnt_composite_glyph_variation_requirements& result,
    font_error* error) const noexcept {
    result = {};
    if (component_count > std::numeric_limits<std::uint32_t>::max() - 4U) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    sfnt_gvar_tuple_requirements tuples{};
    if (!try_get_glyph_variation_tuple_requirements(
            glyph_index, tuples, error)) {
        return false;
    }
    result.tuple_header_count = tuples.tuple_count;
    result.region_coordinate_count = tuples.region_coordinate_count;
    if (tuples.tuple_count != 0U) {
        result.point_number_count = component_count + 4U;
        result.delta_count = component_count + 4U;
    }
    set_error(error, font_error::none);
    return true;
}

bool sfnt_font_view::try_get_composite_glyph_variation_offsets(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint32_t component_count,
    std::span<progpu_native_point> offsets,
    sfnt_composite_glyph_variation_scratch scratch,
    font_error* error) const noexcept {
    set_error(error, font_error::none);
    if (offsets.size() < component_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_gvar_header gvar{};
    if (!try_get_gvar_header(gvar, error)) {
        return false;
    }
    if (normalized_coordinates.size() < gvar.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_composite_glyph_variation_requirements requirements{};
    if (!try_get_composite_glyph_variation_requirements(
            glyph_index, component_count, requirements, error)) {
        return false;
    }
    if (scratch.tuple_headers.size() < requirements.tuple_header_count ||
        scratch.region_coordinates.size() <
            requirements.region_coordinate_count ||
        scratch.shared_point_numbers.size() <
            requirements.point_number_count ||
        scratch.private_point_numbers.size() <
            requirements.point_number_count ||
        scratch.x_deltas.size() < requirements.delta_count ||
        scratch.y_deltas.size() < requirements.delta_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_glyph_variation_data_view view{};
    if (!try_get_glyph_variation_data(glyph_index, view, error)) {
        return false;
    }
    std::uint16_t headers_written = 0U;
    std::uint32_t coordinates_written = 0U;
    if (!try_decode_glyph_variation_tuple_headers(
            glyph_index,
            scratch.tuple_headers,
            scratch.region_coordinates,
            headers_written,
            coordinates_written,
            error)) {
        return false;
    }
    const auto headers = scratch.tuple_headers.first(headers_written);
    const auto item_count = component_count + 4U;
    detail::gvar_point_set shared_points{};
    if (!detail::try_preflight_gvar_payloads(
            view, headers, item_count, shared_points, error) ||
        !detail::try_decode_gvar_shared_points(
            view,
            scratch.shared_point_numbers,
            shared_points,
            error)) {
        return false;
    }

    std::fill_n(offsets.begin(), component_count, progpu_native_point{});
    std::size_t cursor = view.serialized_data_offset +
        (view.has_shared_point_numbers
            ? shared_points.requirements.bytes_consumed
            : 0U);
    const auto axis_count = static_cast<std::size_t>(gvar.axis_count);
    for (const auto& header : headers) {
        detail::gvar_tuple_payload payload{};
        if (!detail::try_decode_gvar_tuple_payload(
                view,
                header,
                item_count,
                shared_points,
                scratch.shared_point_numbers,
                scratch.private_point_numbers,
                scratch.x_deltas,
                scratch.y_deltas,
                cursor,
                payload,
                error)) {
            return false;
        }
        const auto region = scratch.region_coordinates.subspan(
            header.region_coordinate_offset, axis_count * 3U);
        const auto scalar = sfnt_gvar_tuple_data::calculate_scalar(
            normalized_coordinates.first(gvar.axis_count), region);
        if (scalar == 0.0F) {
            continue;
        }
        if (payload.all_points) {
            const auto count = std::min<std::uint32_t>(
                component_count, payload.delta_count);
            for (std::uint32_t component = 0U;
                 component < count;
                 ++component) {
                offsets[component].x += scratch.x_deltas[component] * scalar;
                offsets[component].y += scratch.y_deltas[component] * scalar;
            }
            continue;
        }
        for (std::size_t delta = 0U;
             delta < payload.point_numbers.size();
             ++delta) {
            const auto component = payload.point_numbers[delta];
            if (component >= component_count) {
                continue;
            }
            offsets[component].x += scratch.x_deltas[delta] * scalar;
            offsets[component].y += scratch.y_deltas[delta] * scalar;
        }
    }
    return true;
}

} // namespace progpu::native::text
