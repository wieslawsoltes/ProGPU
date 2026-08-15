#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

// Direct native port provenance: ProGPU-owned OpenTypeVariationData GDEF 1.3
// layout ItemVariationStore lookup at checkpoint df58d65c.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

constexpr auto gdef_tag = open_type_tag::from_chars('G', 'D', 'E', 'F');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_layout_variation(
    std::uint16_t outer_index,
    std::uint16_t inner_index,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    bool& uses_layout_store,
    font_error* error) const noexcept {
    result = 0.0F;
    uses_layout_store = false;
    set_error(error, font_error::none);
    sfnt_table_view gdef{};
    if (!try_get_table(gdef_tag, gdef)) {
        return true;
    }
    if (!can_read(gdef.bytes, 0U, 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto major = read_u16(gdef.bytes, 0U);
    const auto minor = read_u16(gdef.bytes, 2U);
    if (major != 1U || minor < 3U) {
        return true;
    }
    if (!can_read(gdef.bytes, 0U, 18U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto store_relative = read_u32(gdef.bytes, 14U);
    if (store_relative == 0U) {
        return true;
    }
    std::uint16_t axis_count = 0U;
    if (!try_get_variation_axis_count(axis_count, error)) {
        return false;
    }
    sfnt_item_variation_store_view store{};
    if (!sfnt_item_variation_data::try_get_store(
            gdef.bytes, store_relative, axis_count, store, error) ||
        !sfnt_item_variation_data::try_get_delta(
            store,
            normalized_coordinates,
            outer_index,
            inner_index,
            result,
            error)) {
        return false;
    }
    uses_layout_store = true;
    return true;
}

} // namespace progpu::native::text
