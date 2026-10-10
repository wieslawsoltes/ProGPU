#include "progpu_native_text.hpp"

#include "progpu_native_gvar_payload_internal.hpp"
#include "progpu_native_font_bytes.hpp"

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
    float left = 0.0F, right = 0.0F;
    if (!try_get_glyph_horizontal_phantom_deltas(glyph_index,
            normalized_coordinates, item_count, left, right, scratch, error)) {
        return false;
    }
    result = right - left;
    return true;
}

namespace {

bool try_get_phantom_pair(
    const sfnt_font_view& font,
    bool vertical,
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint32_t item_count,
    float& left_result,
    float& right_result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    if (item_count < 4U) {
        left_result = 0.0F;
        right_result = 0.0F;
        return true;
    }
    sfnt_gvar_header gvar{};
    if (!font.try_get_gvar_header(gvar, error)) {
        return false;
    }
    if (normalized_coordinates.size() < gvar.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_glyph_phantom_variation_requirements requirements{};
    if (!font.try_get_glyph_phantom_variation_requirements(
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
    if (!font.try_get_glyph_variation_data(glyph_index, view, error)) {
        return false;
    }
    std::uint16_t headers_written = 0U;
    std::uint32_t coordinates_written = 0U;
    if (!font.try_decode_glyph_variation_tuple_headers(
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

    const auto left_phantom = item_count - (vertical ? 2U : 4U);
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
        const auto deltas = vertical ? scratch.y_deltas : scratch.x_deltas;
        if (payload.all_points) {
            if (right_phantom < payload.delta_count) {
                left_delta += deltas[left_phantom] * scalar;
                right_delta += deltas[right_phantom] * scalar;
            }
            continue;
        }
        for (std::size_t delta = 0U;
            delta < payload.point_numbers.size();
            ++delta) {
            const auto point = payload.point_numbers[delta];
            if (point == left_phantom) {
                left_delta += deltas[delta] * scalar;
            } else if (point == right_phantom) {
                right_delta += deltas[delta] * scalar;
            }
        }
    }
    left_result = left_delta;
    right_result = right_delta;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_glyph_horizontal_phantom_deltas(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint32_t item_count,
    float& left_result,
    float& right_result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) const noexcept {
    return try_get_phantom_pair(*this, false, glyph_index, normalized_coordinates,
        item_count, left_result, right_result, scratch, error);
}

bool sfnt_font_view::try_get_glyph_vertical_phantom_deltas(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint32_t item_count,
    float& top_result,
    float& bottom_result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) const noexcept {
    // The additive source query cannot interpret a malformed directory entry
    // as an absent variation table. Keep the legacy reader policy unchanged.
    constexpr auto glyf_tag = open_type_tag::from_chars('g', 'l', 'y', 'f');
    constexpr auto loca_tag = open_type_tag::from_chars('l', 'o', 'c', 'a');
    constexpr auto gvar_tag = open_type_tag::from_chars('g', 'v', 'a', 'r');
    unsigned glyf_count = 0U, loca_count = 0U, gvar_count = 0U;
    for (std::uint16_t index = 0U; index < table_count_; ++index) {
        const auto record = static_cast<std::size_t>(directory_offset_) + static_cast<std::size_t>(index) * 16U;
        const auto tag = detail::read_u32(data_, record);
        unsigned* count = tag == glyf_tag.value ? &glyf_count : tag == loca_tag.value ? &loca_count
            : tag == gvar_tag.value ? &gvar_count : nullptr;
        if (count == nullptr) continue;
        ++*count;
        if (*count != 1U || !detail::can_read(data_, detail::read_u32(data_, record + 8U),
                detail::read_u32(data_, record + 12U))) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }
    sfnt_table_view glyf{}, loca{}, gvar{};
    std::uint32_t actual_items = 0U;
    if (!try_get_table(glyf_tag, glyf) || !try_get_table(loca_tag, loca)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::uint16_t axis_count = 0U, glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count ||
        !try_get_variation_axis_count(axis_count, error)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (normalized_coordinates.size() != axis_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (const auto coordinate : normalized_coordinates) {
        if (coordinate < -16384 || coordinate > 16384) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    if (!try_get_glyph_variation_item_count(glyph_index, actual_items, error)) return false;
    if (actual_items != item_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (try_get_table(gvar_tag, gvar) &&
        (gvar.bytes.size() < 20U || detail::read_u32(gvar.bytes, 0U) != 0x00010000U ||
         (detail::read_u16(gvar.bytes, 14U) & ~1U) != 0U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    return try_get_phantom_pair(*this, true, glyph_index, normalized_coordinates,
        item_count, top_result, bottom_result, scratch, error);
}

} // namespace progpu::native::text
