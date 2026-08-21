#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData HVAR advance lookup at checkpoint 38b2b05f.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u32;

constexpr auto hvar_tag = open_type_tag::from_chars('H', 'V', 'A', 'R');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_horizontal_advance_variation(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    bool& uses_hvar,
    font_error* error) const noexcept {
    result = 0.0F;
    uses_hvar = false;
    set_error(error, font_error::none);
    sfnt_table_view hvar{};
    if (!try_get_table(hvar_tag, hvar)) {
        return true;
    }
    if (!can_read(hvar.bytes, 0U, 12U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto store_relative = read_u32(hvar.bytes, 4U);
    if (store_relative == 0U) {
        return true;
    }
    std::uint16_t axis_count = 0U;
    if (!try_get_variation_axis_count(axis_count, error)) {
        return false;
    }
    sfnt_item_variation_store_view store{};
    if (!sfnt_item_variation_data::try_get_store(
            hvar.bytes, store_relative, axis_count, store, error)) {
        return false;
    }
    if (normalized_coordinates.size() < axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::uint16_t outer_index = 0U;
    std::uint16_t inner_index = glyph_index;
    const auto map_relative = read_u32(hvar.bytes, 8U);
    if (map_relative != 0U) {
        sfnt_delta_set_index_map_view map{};
        if (!sfnt_item_variation_data::try_get_delta_set_index_map(
                hvar.bytes, map_relative, map, error)) {
            return false;
        }
        sfnt_item_variation_data::get_delta_set_index(
            map, glyph_index, outer_index, inner_index);
    }
    if (!sfnt_item_variation_data::try_get_delta(
            store,
            normalized_coordinates.first(axis_count),
            outer_index,
            inner_index,
            result,
            error)) {
        return false;
    }
    uses_hvar = true;
    return true;
}

} // namespace progpu::native::text
