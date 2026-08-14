#include "progpu_native_text.hpp"

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

struct point_set final {
    sfnt_packed_point_requirements requirements{};
    std::size_t data_offset = 0U;
};

bool try_preflight_payloads(
    sfnt_glyph_variation_data_view view,
    std::span<const sfnt_gvar_tuple_header> headers,
    std::uint32_t item_count,
    point_set& shared_points,
    font_error* error) noexcept {
    shared_points = {};
    std::size_t cursor = view.serialized_data_offset;
    if (view.has_shared_point_numbers) {
        shared_points.data_offset = cursor;
        if (!sfnt_packed_variation_data::try_get_point_requirements(
                view.bytes.subspan(cursor),
                shared_points.requirements,
                error)) {
            return false;
        }
        cursor += shared_points.requirements.bytes_consumed;
    } else {
        shared_points.requirements.all_points = true;
    }

    for (const auto& header : headers) {
        if (header.serialized_data_size > view.bytes.size() - cursor) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const auto tuple_end = cursor + header.serialized_data_size;
        sfnt_packed_point_requirements points = shared_points.requirements;
        if (header.has_private_point_numbers()) {
            if (!sfnt_packed_variation_data::try_get_point_requirements(
                    view.bytes.subspan(cursor, tuple_end - cursor),
                    points,
                    error)) {
                return false;
            }
            cursor += points.bytes_consumed;
        }
        const auto delta_count = points.all_points
            ? item_count
            : points.point_count;
        sfnt_packed_delta_requirements x{};
        if (!sfnt_packed_variation_data::try_get_delta_requirements(
                view.bytes.subspan(cursor, tuple_end - cursor),
                delta_count,
                x,
                error)) {
            return false;
        }
        cursor += x.bytes_consumed;
        sfnt_packed_delta_requirements y{};
        if (!sfnt_packed_variation_data::try_get_delta_requirements(
                view.bytes.subspan(cursor, tuple_end - cursor),
                delta_count,
                y,
                error)) {
            return false;
        }
        cursor = tuple_end;
    }
    return true;
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
    point_set shared_points{};
    const auto item_count =
        static_cast<std::uint32_t>(original_points.size()) + 4U;
    if (!try_preflight_payloads(
            view, headers, item_count, shared_points, error)) {
        return false;
    }

    std::uint32_t shared_written = 0U;
    std::size_t ignored_consumed = 0U;
    if (view.has_shared_point_numbers && !shared_points.requirements.all_points &&
        !sfnt_packed_variation_data::try_decode_points(
            view.bytes.subspan(shared_points.data_offset),
            scratch.shared_point_numbers,
            shared_written,
            ignored_consumed,
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
        const auto tuple_end = cursor + header.serialized_data_size;
        auto point_numbers = scratch.shared_point_numbers.first(shared_written);
        bool all_points = shared_points.requirements.all_points;
        if (header.has_private_point_numbers()) {
            sfnt_packed_point_requirements private_requirements{};
            if (!sfnt_packed_variation_data::try_get_point_requirements(
                    view.bytes.subspan(cursor, tuple_end - cursor),
                    private_requirements,
                    error)) {
                return false;
            }
            std::uint32_t private_written = 0U;
            std::size_t private_consumed = 0U;
            if (!sfnt_packed_variation_data::try_decode_points(
                    view.bytes.subspan(cursor, tuple_end - cursor),
                    scratch.private_point_numbers,
                    private_written,
                    private_consumed,
                    error)) {
                return false;
            }
            cursor += private_consumed;
            point_numbers =
                scratch.private_point_numbers.first(private_written);
            all_points = private_requirements.all_points;
        }
        const auto delta_count = all_points
            ? item_count
            : static_cast<std::uint32_t>(point_numbers.size());
        std::uint32_t x_written = 0U;
        std::size_t x_consumed = 0U;
        if (!sfnt_packed_variation_data::try_decode_deltas(
                view.bytes.subspan(cursor, tuple_end - cursor),
                scratch.x_deltas,
                delta_count,
                x_written,
                x_consumed,
                error)) {
            return false;
        }
        cursor += x_consumed;
        std::uint32_t y_written = 0U;
        std::size_t y_consumed = 0U;
        if (!sfnt_packed_variation_data::try_decode_deltas(
                view.bytes.subspan(cursor, tuple_end - cursor),
                scratch.y_deltas,
                delta_count,
                y_written,
                y_consumed,
                error)) {
            return false;
        }
        cursor = tuple_end;

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
        if (all_points) {
            const auto count = std::min<std::size_t>(
                original_points.size(), delta_count);
            for (std::size_t point = 0U; point < count; ++point) {
                scratch.tuple_x[point] = scratch.x_deltas[point];
                scratch.tuple_y[point] = scratch.y_deltas[point];
                scratch.touched[point] = 1U;
            }
        } else {
            for (std::size_t delta = 0U;
                 delta < point_numbers.size();
                 ++delta) {
                const auto point = point_numbers[delta];
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
