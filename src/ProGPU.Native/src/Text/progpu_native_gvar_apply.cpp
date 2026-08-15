#include "progpu_native_text.hpp"

#include "progpu_native_gvar_payload_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ParseGlyphVariation/ApplySimpleGlyphDeltas at
// checkpoint 83cc056e. The native port walks bounded payload slices twice and
// reuses only caller-owned scratch spans.

namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool validate_contours(
    std::span<const std::uint16_t> contour_end_points,
    std::size_t point_count) noexcept {
    std::size_t start = 0U;
    for (const auto end_value : contour_end_points) {
        const auto end = static_cast<std::size_t>(end_value);
        if (end < start || end >= point_count) {
            return false;
        }
        start = end + 1U;
    }
    return point_count == 0U
        ? contour_end_points.empty()
        : !contour_end_points.empty() && start == point_count;
}

} // namespace

bool sfnt_font_view::try_get_simple_glyph_variation_requirements(
    std::uint16_t glyph_index,
    std::uint32_t point_count,
    sfnt_simple_glyph_variation_requirements& result,
    font_error* error) const noexcept {
    result = {};
    if (point_count > std::numeric_limits<std::uint32_t>::max() - 4U) {
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
        result.point_number_count = point_count + 4U;
        result.delta_count = point_count + 4U;
        result.tuple_point_count = point_count;
    }
    set_error(error, font_error::none);
    return true;
}

bool sfnt_font_view::try_apply_simple_glyph_variations(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<const std::uint16_t> contour_end_points,
    std::span<const sfnt_outline_point> original_points,
    std::span<progpu_native_point> varied_points,
    sfnt_simple_glyph_variation_scratch scratch,
    font_error* error) const noexcept {
    set_error(error, font_error::none);
    if (original_points.size() >
            std::numeric_limits<std::uint32_t>::max() - 4U ||
        varied_points.size() < original_points.size() ||
        !validate_contours(contour_end_points, original_points.size())) {
        set_error(error, varied_points.size() < original_points.size()
            ? font_error::insufficient_buffer
            : font_error::invalid_glyph);
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
    sfnt_simple_glyph_variation_requirements requirements{};
    if (!try_get_simple_glyph_variation_requirements(
            glyph_index,
            static_cast<std::uint32_t>(original_points.size()),
            requirements,
            error)) {
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
        scratch.y_deltas.size() < requirements.delta_count ||
        scratch.tuple_x.size() < requirements.tuple_point_count ||
        scratch.tuple_y.size() < requirements.tuple_point_count ||
        scratch.touched.size() < requirements.tuple_point_count) {
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
    detail::gvar_point_set shared_points{};
    const auto item_count =
        static_cast<std::uint32_t>(original_points.size()) + 4U;
    if (!detail::try_preflight_gvar_payloads(
            view, headers, item_count, shared_points, error)) {
        return false;
    }

    if (!detail::try_decode_gvar_shared_points(
            view,
            scratch.shared_point_numbers,
            shared_points,
            error)) {
        return false;
    }
    for (std::size_t point = 0U; point < original_points.size(); ++point) {
        varied_points[point] = {
            static_cast<float>(original_points[point].x),
            static_cast<float>(original_points[point].y)};
    }
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
        std::fill_n(
            scratch.tuple_x.begin(), original_points.size(), 0.0F);
        std::fill_n(
            scratch.tuple_y.begin(), original_points.size(), 0.0F);
        std::fill_n(
            scratch.touched.begin(), original_points.size(), std::uint8_t{0U});
        if (payload.all_points) {
            const auto count = std::min<std::size_t>(
                original_points.size(), payload.delta_count);
            for (std::size_t point = 0U; point < count; ++point) {
                scratch.tuple_x[point] = scratch.x_deltas[point];
                scratch.tuple_y[point] = scratch.y_deltas[point];
                scratch.touched[point] = 1U;
            }
        } else {
            for (std::size_t delta = 0U;
                 delta < payload.point_numbers.size();
                 ++delta) {
                const auto point = payload.point_numbers[delta];
                if (point >= original_points.size()) {
                    continue;
                }
                scratch.tuple_x[point] += scratch.x_deltas[delta];
                scratch.tuple_y[point] += scratch.y_deltas[delta];
                scratch.touched[point] = 1U;
            }
            if (!sfnt_gvar_deltas::try_infer_untouched(
                    varied_points.first(original_points.size()),
                    contour_end_points,
                    scratch.tuple_x,
                    scratch.tuple_y,
                    scratch.touched,
                    error)) {
                return false;
            }
        }
        for (std::size_t point = 0U; point < original_points.size(); ++point) {
            varied_points[point].x += scratch.tuple_x[point] * scalar;
            varied_points[point].y += scratch.tuple_y[point] * scalar;
        }
    }
    return true;
}

} // namespace progpu::native::text
