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

bool try_get_hvar_instance_views(
    const sfnt_font_view& font,
    std::span<const std::int16_t> normalized_coordinates,
    sfnt_item_variation_store_view& store,
    sfnt_delta_set_index_map_view& advance_map,
    bool& uses_hvar,
    bool& has_advance_map,
    font_error* error) noexcept {
    store = {};
    advance_map = {};
    uses_hvar = false;
    has_advance_map = false;
    sfnt_table_view hvar{};
    if (!font.try_get_table(hvar_tag, hvar)) return true;
    if (!can_read(hvar.bytes, 0U, 12U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto store_relative = read_u32(hvar.bytes, 4U);
    if (store_relative == 0U) return true;
    std::uint16_t axis_count = 0U;
    if (!font.try_get_variation_axis_count(axis_count, error)) return false;
    if (normalized_coordinates.size() < axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (!sfnt_item_variation_data::try_get_store(
            hvar.bytes, store_relative, axis_count, store, error)) {
        return false;
    }
    const auto map_relative = read_u32(hvar.bytes, 8U);
    if (map_relative != 0U) {
        if (!sfnt_item_variation_data::try_get_delta_set_index_map(
                hvar.bytes, map_relative, advance_map, error)) {
            return false;
        }
        has_advance_map = true;
    }
    uses_hvar = true;
    return true;
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
    sfnt_item_variation_store_view store{};
    sfnt_delta_set_index_map_view map{};
    bool has_map = false;
    if (!try_get_hvar_instance_views(
            *this,
            normalized_coordinates,
            store,
            map,
            uses_hvar,
            has_map,
            error)) {
        return false;
    }
    if (!uses_hvar) return true;
    std::uint16_t outer_index = 0U;
    std::uint16_t inner_index = glyph_index;
    if (has_map) {
        sfnt_item_variation_data::get_delta_set_index(
            map, glyph_index, outer_index, inner_index);
    }
    if (!sfnt_item_variation_data::try_get_delta(
            store,
            normalized_coordinates.first(store.axis_count),
            outer_index,
            inner_index,
            result,
            error)) {
        return false;
    }
    return true;
}

bool sfnt_font_view::try_get_horizontal_advance_variation_region_count(
    std::span<const std::int16_t> normalized_coordinates,
    std::uint16_t& result,
    bool& uses_hvar,
    font_error* error) const noexcept {
    result = 0U;
    uses_hvar = false;
    set_error(error, font_error::none);
    sfnt_item_variation_store_view store{};
    sfnt_delta_set_index_map_view map{};
    bool has_map = false;
    if (!try_get_hvar_instance_views(
            *this,
            normalized_coordinates,
            store,
            map,
            uses_hvar,
            has_map,
            error)) {
        return false;
    }
    if (uses_hvar) result = store.region_count;
    return true;
}

bool sfnt_font_view::try_prepare_horizontal_advance_variation(
    std::span<const std::int16_t> normalized_coordinates,
    std::span<float> region_scalars,
    sfnt_horizontal_advance_variation_instance& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_item_variation_store_view store{};
    sfnt_delta_set_index_map_view map{};
    bool uses_hvar = false;
    bool has_map = false;
    if (!try_get_hvar_instance_views(
            *this,
            normalized_coordinates,
            store,
            map,
            uses_hvar,
            has_map,
            error)) {
        return false;
    }
    if (!uses_hvar) return true;
    if (region_scalars.size() < store.region_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (std::uint16_t region = 0U; region < store.region_count; ++region) {
        if (!sfnt_item_variation_data::try_get_region_scalar(
                store,
                normalized_coordinates,
                region,
                region_scalars[region],
                error)) {
            return false;
        }
    }
    result.store = store;
    result.advance_map = map;
    result.region_scalars = region_scalars.first(store.region_count);
    result.uses_hvar = true;
    result.has_advance_map = has_map;
    return true;
}

} // namespace progpu::native::text
