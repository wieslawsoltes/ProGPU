#include "progpu_native_text.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned TtfFont.GetAdvanceWidth and
// TtfFont.Variations.GetVariationAdvanceDelta/ComputeGlyphVariationItemCount
// at repository checkpoints 90f67cccc and 5abc583db. Explicit normalized
// coordinates plus caller scratch replace managed instance/cache ownership.

namespace progpu::native::text {
namespace {

constexpr auto hmtx_tag = open_type_tag::from_chars('h', 'm', 't', 'x');
constexpr auto glyf_tag = open_type_tag::from_chars('g', 'l', 'y', 'f');
constexpr auto loca_tag = open_type_tag::from_chars('l', 'o', 'c', 'a');
constexpr auto gvar_tag = open_type_tag::from_chars('g', 'v', 'a', 'r');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

bool try_get_base_advance_width(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    float& result,
    font_error* error) noexcept {
    sfnt_horizontal_header_metrics horizontal{};
    sfnt_table_view hmtx{};
    if (!font.try_get_horizontal_header_metrics(horizontal) ||
        !font.try_get_table(hmtx_tag, hmtx) ||
        horizontal.number_of_horizontal_metrics == 0U) {
        sfnt_header_metrics header{};
        if (!font.try_get_header_metrics(header) || header.units_per_em == 0U) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        result = static_cast<float>(header.units_per_em) * 0.5F;
        return true;
    }

    sfnt_horizontal_glyph_metrics metrics{};
    if (!font.try_get_horizontal_glyph_metrics(glyph_index, metrics)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = static_cast<float>(metrics.advance_width);
    return true;
}

bool try_get_advance_variation_delta(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    const sfnt_horizontal_advance_variation_instance* variation,
    float& result,
    bool& uses_hvar,
    font_error* error) noexcept {
    result = 0.0F;
    uses_hvar = false;
    if (variation == nullptr) {
        return font.try_get_horizontal_advance_variation(
            glyph_index,
            normalized_coordinates,
            result,
            uses_hvar,
            error);
    }
    if (!variation->uses_hvar) return true;
    std::uint16_t outer_index = 0U;
    std::uint16_t inner_index = glyph_index;
    if (variation->has_advance_map) {
        sfnt_item_variation_data::get_delta_set_index(
            variation->advance_map,
            glyph_index,
            outer_index,
            inner_index);
    }
    if (!sfnt_item_variation_data::try_get_delta(
            variation->store,
            variation->region_scalars,
            outer_index,
            inner_index,
            result,
            error)) {
        return false;
    }
    uses_hvar = true;
    return true;
}

} // namespace

bool sfnt_font_view::try_get_design_advance_width(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    font_error* error) const noexcept {
    return try_get_design_advance_width(
        glyph_index,
        normalized_coordinates,
        nullptr,
        result,
        error);
}

bool sfnt_font_view::try_get_design_advance_width(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    const sfnt_horizontal_advance_variation_instance* variation,
    float& result,
    font_error* error) const noexcept {
    result = 0.0F;
    set_error(error, font_error::none);

    if (!try_get_base_advance_width(*this, glyph_index, result, error)) {
        return false;
    }
    if (normalized_coordinates.empty()) return true;

    float delta = 0.0F;
    bool uses_hvar = false;
    if (!try_get_advance_variation_delta(
            *this,
            glyph_index,
            normalized_coordinates,
            variation,
            delta,
            uses_hvar,
            error)) {
        result = 0.0F;
        return false;
    }
    result += delta;
    return true;
}

bool sfnt_font_view::try_get_glyph_variation_item_count(
    std::uint16_t glyph_index,
    std::uint32_t& result,
    font_error* error) const noexcept {
    result = 0U;
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    sfnt_table_view glyf{};
    sfnt_table_view loca{};
    if (!try_get_table(glyf_tag, glyf) || !try_get_table(loca_tag, loca)) {
        result = 4U;
        return true;
    }
    sfnt_glyph_decode_requirements glyph{};
    if (!try_get_glyph_decode_requirements(glyph_index, glyph, error)) {
        return false;
    }
    std::uint32_t body_count = 0U;
    if (glyph.kind == sfnt_glyph_kind::simple) {
        body_count = glyph.point_count;
    } else if (glyph.kind == sfnt_glyph_kind::composite) {
        sfnt_composite_glyph_decode_requirements composite{};
        if (!try_get_composite_glyph_decode_requirements(
                glyph_index, composite, error)) {
            return false;
        }
        body_count = composite.component_count;
    }
    if (body_count > std::numeric_limits<std::uint32_t>::max() - 4U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = body_count + 4U;
    return true;
}

bool sfnt_font_view::try_get_design_advance_width_requirements(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    sfnt_design_advance_width_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    if (normalized_coordinates.empty()) return true;
    float ignored_delta = 0.0F;
    bool uses_hvar = false;
    if (!try_get_horizontal_advance_variation(
            glyph_index,
            normalized_coordinates,
            ignored_delta,
            uses_hvar,
            error)) {
        return false;
    }
    if (uses_hvar) return true;
    sfnt_table_view gvar{};
    if (!try_get_table(gvar_tag, gvar)) return true;
    if (!try_get_glyph_variation_item_count(
            glyph_index, result.glyph_variation_item_count, error) ||
        !try_get_glyph_phantom_variation_requirements(
            glyph_index,
            result.glyph_variation_item_count,
            result.phantom,
            error)) {
        result = {};
        return false;
    }
    return true;
}

bool sfnt_font_view::try_get_design_advance_width(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) const noexcept {
    return try_get_design_advance_width(
        glyph_index,
        normalized_coordinates,
        nullptr,
        result,
        scratch,
        error);
}

bool sfnt_font_view::try_get_design_advance_width(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    const sfnt_horizontal_advance_variation_instance* variation,
    float& result,
    sfnt_glyph_phantom_variation_scratch scratch,
    font_error* error) const noexcept {
    result = 0.0F;
    set_error(error, font_error::none);
    float base = 0.0F;
    if (!try_get_base_advance_width(*this, glyph_index, base, error)) {
        return false;
    }
    if (normalized_coordinates.empty()) {
        result = base;
        return true;
    }
    float delta = 0.0F;
    bool uses_hvar = false;
    if (!try_get_advance_variation_delta(
            *this,
            glyph_index,
            normalized_coordinates,
            variation,
            delta,
            uses_hvar,
            error)) {
        return false;
    }
    if (uses_hvar) {
        result = base + delta;
        return true;
    }
    sfnt_table_view gvar{};
    if (!try_get_table(gvar_tag, gvar)) {
        result = base;
        return true;
    }
    std::uint32_t item_count = 0U;
    if (!try_get_glyph_variation_item_count(glyph_index, item_count, error) ||
        !try_get_glyph_phantom_advance_delta(
            glyph_index,
            normalized_coordinates,
            item_count,
            delta,
            scratch,
            error)) {
        return false;
    }
    result = base + delta;
    return true;
}

} // namespace progpu::native::text
