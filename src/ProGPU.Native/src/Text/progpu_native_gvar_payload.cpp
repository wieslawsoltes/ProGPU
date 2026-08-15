#include "progpu_native_gvar_payload_internal.hpp"

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ParseGlyphVariation at checkpoint 173e0d24. This
// granular walker validates every tuple before any public result is mutated.

namespace progpu::native::text::detail {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool try_preflight_gvar_payloads(
    sfnt_glyph_variation_data_view view,
    std::span<const sfnt_gvar_tuple_header> headers,
    std::uint32_t item_count,
    gvar_point_set& shared_points,
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
        if (cursor > view.bytes.size() ||
            header.serialized_data_size > view.bytes.size() - cursor) {
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

bool try_decode_gvar_shared_points(
    sfnt_glyph_variation_data_view view,
    std::span<std::uint32_t> output,
    gvar_point_set& shared_points,
    font_error* error) noexcept {
    shared_points.written = 0U;
    if (!view.has_shared_point_numbers ||
        shared_points.requirements.all_points) {
        return true;
    }
    std::size_t consumed = 0U;
    return sfnt_packed_variation_data::try_decode_points(
        view.bytes.subspan(shared_points.data_offset),
        output,
        shared_points.written,
        consumed,
        error);
}

bool try_decode_gvar_tuple_payload(
    sfnt_glyph_variation_data_view view,
    const sfnt_gvar_tuple_header& header,
    std::uint32_t item_count,
    const gvar_point_set& shared_points,
    std::span<const std::uint32_t> shared_point_numbers,
    std::span<std::uint32_t> private_point_numbers,
    std::span<std::int16_t> x_deltas,
    std::span<std::int16_t> y_deltas,
    std::size_t& cursor,
    gvar_tuple_payload& result,
    font_error* error) noexcept {
    result = {};
    const auto tuple_end = cursor + header.serialized_data_size;
    auto point_numbers = shared_point_numbers.first(shared_points.written);
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
                private_point_numbers,
                private_written,
                private_consumed,
                error)) {
            return false;
        }
        cursor += private_consumed;
        point_numbers = private_point_numbers.first(private_written);
        all_points = private_requirements.all_points;
    }
    const auto delta_count = all_points
        ? item_count
        : static_cast<std::uint32_t>(point_numbers.size());
    std::uint32_t x_written = 0U;
    std::size_t x_consumed = 0U;
    if (!sfnt_packed_variation_data::try_decode_deltas(
            view.bytes.subspan(cursor, tuple_end - cursor),
            x_deltas,
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
            y_deltas,
            delta_count,
            y_written,
            y_consumed,
            error)) {
        return false;
    }
    cursor = tuple_end;
    result.point_numbers = point_numbers;
    result.delta_count = delta_count;
    result.all_points = all_points;
    return true;
}

} // namespace progpu::native::text::detail
