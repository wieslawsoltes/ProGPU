#include "progpu_native_text.hpp"
#include "progpu_native_true_type_composite_internal.hpp"

#include <algorithm>
#include <array>
#include <limits>

// Direct native port provenance: ProGPU-owned recursive composite expansion
// and variation scratch contracts at checkpoint 6ca97ba7. This pass measures
// exact caller storage without retaining or allocating an outline graph.
namespace progpu::native::text {
namespace {

using detail::read_composite_component;

constexpr std::size_t maximum_composite_depth = 32U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool is_ancestor(
    std::span<const std::uint16_t> ancestors,
    std::uint16_t glyph_index) noexcept {
    return std::find(ancestors.begin(), ancestors.end(), glyph_index) !=
        ancestors.end();
}

void merge_simple_requirements(
    sfnt_simple_glyph_variation_requirements& destination,
    const sfnt_simple_glyph_variation_requirements& source) noexcept {
    destination.tuple_header_count = std::max(
        destination.tuple_header_count, source.tuple_header_count);
    destination.region_coordinate_count = std::max(
        destination.region_coordinate_count, source.region_coordinate_count);
    destination.point_number_count = std::max(
        destination.point_number_count, source.point_number_count);
    destination.delta_count = std::max(
        destination.delta_count, source.delta_count);
    destination.tuple_point_count = std::max(
        destination.tuple_point_count, source.tuple_point_count);
}

void merge_composite_requirements(
    sfnt_composite_glyph_variation_requirements& destination,
    const sfnt_composite_glyph_variation_requirements& source) noexcept {
    destination.tuple_header_count = std::max(
        destination.tuple_header_count, source.tuple_header_count);
    destination.region_coordinate_count = std::max(
        destination.region_coordinate_count, source.region_coordinate_count);
    destination.point_number_count = std::max(
        destination.point_number_count, source.point_number_count);
    destination.delta_count = std::max(
        destination.delta_count, source.delta_count);
}

bool measure_variation_scratch(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    std::span<std::uint16_t> ancestors,
    std::size_t depth,
    std::uint32_t component_offset_base,
    sfnt_varied_glyph_requirements& result) noexcept {
    std::uint16_t glyph_count = 0U;
    if (!font.try_get_glyph_count(glyph_count)) {
        return false;
    }
    if (glyph_index >= glyph_count || depth > maximum_composite_depth ||
        is_ancestor(ancestors.first(depth), glyph_index)) {
        return true;
    }
    ancestors[depth] = glyph_index;

    sfnt_glyph_decode_requirements glyph_requirements{};
    if (!font.try_get_glyph_decode_requirements(
            glyph_index, glyph_requirements)) {
        return false;
    }
    if (glyph_requirements.kind == sfnt_glyph_kind::empty) {
        return true;
    }
    if (glyph_requirements.kind == sfnt_glyph_kind::simple) {
        sfnt_simple_glyph_variation_requirements variation{};
        if (!font.try_get_simple_glyph_variation_requirements(
                glyph_index, glyph_requirements.point_count, variation)) {
            return false;
        }
        merge_simple_requirements(result.simple_variation, variation);
        result.varied_simple_point_count = std::max(
            result.varied_simple_point_count, glyph_requirements.point_count);
        return true;
    }

    sfnt_composite_glyph_decode_requirements composite{};
    sfnt_glyph_data_view glyph{};
    if (!font.try_get_composite_glyph_decode_requirements(
            glyph_index, composite) ||
        !font.try_get_glyph_data(glyph_index, glyph)) {
        return false;
    }
    sfnt_composite_glyph_variation_requirements variation{};
    if (!font.try_get_composite_glyph_variation_requirements(
            glyph_index, composite.component_count, variation)) {
        return false;
    }
    merge_composite_requirements(result.composite_variation, variation);
    if (composite.component_count >
        std::numeric_limits<std::uint32_t>::max() - component_offset_base) {
        return false;
    }
    const auto child_offset_base =
        component_offset_base + composite.component_count;
    result.component_offset_count = std::max(
        result.component_offset_count, child_offset_base);

    auto cursor = static_cast<std::size_t>(10U);
    for (std::uint32_t index = 0U;
        index < composite.component_count;
        ++index) {
        sfnt_composite_component component{};
        if (!read_composite_component(glyph.bytes, cursor, &component) ||
            !measure_variation_scratch(
                font,
                component.glyph_index,
                ancestors,
                depth + 1U,
                child_offset_base,
                result)) {
            return false;
        }
    }
    return true;
}

} // namespace

bool sfnt_font_view::try_get_varied_glyph_requirements(
    std::uint16_t glyph_index,
    sfnt_varied_glyph_requirements& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    if (!try_get_expanded_glyph_requirements(
            glyph_index, result.outline, error)) {
        return false;
    }
    std::array<std::uint16_t, maximum_composite_depth + 1U> ancestors{};
    if (!measure_variation_scratch(
            *this, glyph_index, ancestors, 0U, 0U, result)) {
        result = {};
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    return true;
}

} // namespace progpu::native::text
