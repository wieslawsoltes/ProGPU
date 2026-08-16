#include "progpu_native_text.hpp"

#include <cstddef>

// Direct native port provenance: ProGPU-owned TtfFont.GetAdvanceWidth at
// repository checkpoint 4e6ff74d. The explicit normalized-coordinate span
// replaces the managed font instance while retaining the same metric policy.

namespace progpu::native::text {
namespace {

constexpr auto hmtx_tag = open_type_tag::from_chars('h', 'm', 't', 'x');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

} // namespace

bool sfnt_font_view::try_get_design_advance_width(
    std::uint16_t glyph_index,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    font_error* error) const noexcept {
    result = 0.0F;
    set_error(error, font_error::none);

    sfnt_horizontal_header_metrics horizontal{};
    sfnt_table_view hmtx{};
    const bool has_horizontal_header =
        try_get_horizontal_header_metrics(horizontal);
    const bool has_horizontal_metrics = try_get_table(hmtx_tag, hmtx);
    if (!has_horizontal_header || !has_horizontal_metrics ||
        horizontal.number_of_horizontal_metrics == 0U) {
        sfnt_header_metrics header{};
        if (!try_get_header_metrics(header) || header.units_per_em == 0U) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        result = static_cast<float>(header.units_per_em) * 0.5F;
        return true;
    }

    sfnt_horizontal_glyph_metrics metrics{};
    if (!try_get_horizontal_glyph_metrics(glyph_index, metrics)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = static_cast<float>(metrics.advance_width);
    if (normalized_coordinates.empty()) return true;

    float delta = 0.0F;
    bool uses_hvar = false;
    if (!try_get_horizontal_advance_variation(
            glyph_index,
            normalized_coordinates,
            delta,
            uses_hvar,
            error)) {
        result = 0.0F;
        return false;
    }
    result += delta;
    return true;
}

} // namespace progpu::native::text
