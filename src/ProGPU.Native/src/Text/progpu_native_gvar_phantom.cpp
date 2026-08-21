#include "progpu_native_text.hpp"

#include "progpu_native_gvar_payload_internal.hpp"

#include <cstddef>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.GetGlyphPhantomAdvanceDelta at checkpoint 3c00c363.
// The raw gvar fallback remains separate from the later HVAR precedence seam.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_glyph_phantom_variation_requirements(
    std::uint16_t glyph_index,
    std::uint32_t item_count,
    sfnt_glyph_phantom_variation_requirements& result,
    font_error* error) const noexcept {
    result = {};
    if (item_count < 4U) {
        set_error(error, font_error::none);
        return true;
    }
    sfnt_gvar_tuple_requirements tuples{};
    if (!try_get_glyph_variation_tuple_requirements(
            glyph_index, tuples, error)) {
        return false;
    }
    result.tuple_header_count = tuples.tuple_count;
    result.region_coordinate_count = tuples.region_coordinate_count;
    if (tuples.tuple_count != 0U) {
        result.point_number_count = item_count;
        result.delta_count = item_count;
    }
    set_error(error, font_error::none);
    return true;
}

bool sfnt_font_view::try_get_glyph_phantom_advance_delta(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint32_t item_count,
    float& result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) const noexcept {
    result = 0.0F;
    set_error(error, font_error::none);
    if (item_count < 4U) {
        return true;
    }
    sfnt_gvar_header gvar{};
    if (!try_get_gvar_header(gvar, error)) {
        return false;
    }
    if (normalized_coordinates.size() < gvar.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_glyph_phantom_variation_requirements requirements{};
    if (!try_get_glyph_phantom_variation_requirements(
            glyph_index, item_count, requirements, error)) {
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

    const auto left_phantom = item_count - 4U;
    const auto right_phantom = left_phantom + 1U;
    float left_delta = 0.0F;
    float right_delta = 0.0F;
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
            if (right_phantom < payload.delta_count) {
                left_delta += scratch.x_deltas[left_phantom] * scalar;
                right_delta += scratch.x_deltas[right_phantom] * scalar;
            }
            continue;
        }
        for (std::size_t delta = 0U;
            delta < payload.point_numbers.size();
            ++delta) {
            const auto point = payload.point_numbers[delta];
            if (point == left_phantom) {
                left_delta += scratch.x_deltas[delta] * scalar;
            } else if (point == right_phantom) {
                right_delta += scratch.x_deltas[delta] * scalar;
            }
        }
    }
    result = right_delta - left_delta;
    return true;
}

} // namespace progpu::native::text
