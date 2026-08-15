#ifndef PROGPU_NATIVE_GVAR_PAYLOAD_INTERNAL_HPP
#define PROGPU_NATIVE_GVAR_PAYLOAD_INTERNAL_HPP

#include "progpu_native_text.hpp"

#include <cstddef>

namespace progpu::native::text::detail {

struct gvar_point_set final {
    sfnt_packed_point_requirements requirements{};
    std::size_t data_offset = 0U;
    std::uint32_t written = 0U;
};

struct gvar_tuple_payload final {
    std::span<const std::uint32_t> point_numbers{};
    std::uint32_t delta_count = 0U;
    bool all_points = false;
};

bool try_preflight_gvar_payloads(
    sfnt_glyph_variation_data_view view,
    std::span<const sfnt_gvar_tuple_header> headers,
    std::uint32_t item_count,
    gvar_point_set& shared_points,
    font_error* error) noexcept;

bool try_decode_gvar_shared_points(
    sfnt_glyph_variation_data_view view,
    std::span<std::uint32_t> output,
    gvar_point_set& shared_points,
    font_error* error) noexcept;

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
    font_error* error) noexcept;

} // namespace progpu::native::text::detail

#endif
