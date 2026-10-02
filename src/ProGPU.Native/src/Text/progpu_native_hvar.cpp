#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cmath>

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

bool sfnt_font_view::try_prepare_horizontal_metrics_variation(
    std::span<const std::int16_t> normalized_coordinates,
    std::span<float> region_scalars,
    sfnt_horizontal_metrics_variation_instance& result,
    font_error* error) const noexcept {
    sfnt_horizontal_metrics_variation_instance candidate{};
    sfnt_table_view hvar{};
    if (try_get_table(hvar_tag, hvar)) {
        // The new source-bearing contract needs the complete HVAR 1.0 header.
        // Preserve the legacy advance-only reader's existing admission.
        if (!can_read(hvar.bytes, 0U, 20U) || read_u32(hvar.bytes, 0U) != 0x00010000U ||
            read_u32(hvar.bytes, 4U) == 0U) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto offset = read_u32(hvar.bytes, 12U);
        if (offset != 0U) {
            if (!sfnt_item_variation_data::try_get_delta_set_index_map(
                    hvar.bytes, offset, candidate.left_side_bearing_map, error)) return false;
            candidate.has_left_side_bearing_map = true;
        }
    }
    if (!try_prepare_horizontal_advance_variation(normalized_coordinates,
            region_scalars, candidate.advance, error)) return false;
    result = candidate;
    return true;
}

bool sfnt_font_view::try_get_horizontal_left_side_bearing_variation(
    std::uint16_t glyph_index,
    const sfnt_horizontal_metrics_variation_instance& variation,
    float& result,
    bool& has_mapping,
    font_error* error) const noexcept {
    float candidate = 0.0F;
    if (variation.has_left_side_bearing_map) {
        if (!variation.advance.uses_hvar ||
            variation.advance.region_scalars.size() != variation.advance.store.region_count) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        std::uint16_t outer = 0U, inner = 0U;
        sfnt_item_variation_data::get_delta_set_index(
            variation.left_side_bearing_map, glyph_index, outer, inner);
        if (!sfnt_item_variation_data::try_get_delta(variation.advance.store,
                variation.advance.region_scalars, outer, inner, candidate, error)) return false;
        if (!std::isfinite(candidate)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }
    result = candidate;
    has_mapping = variation.has_left_side_bearing_map;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
