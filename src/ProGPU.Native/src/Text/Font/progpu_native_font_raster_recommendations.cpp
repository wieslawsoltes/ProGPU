#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

// Paired original ProGPU implementation: SfntRasterizationRecommendations.cs
// and TtfFont.RasterizationRecommendations.cs in the same change. Public format:
// https://learn.microsoft.com/en-us/typography/opentype/spec/gasp
namespace progpu::native::text {

bool try_read_sfnt_rasterization_recommendations(
    std::span<const std::byte> table, std::uint16_t pixels_per_em,
    sfnt_rasterization_range& result, bool& available) noexcept {
    result = {};
    available = false;
    if (pixels_per_em == 0U || table.size() < 4U) return false;
    const auto version = detail::read_u16(table, 0U);
    const auto count = detail::read_u16(table, 2U);
    if (version > 1U || table.size() != 4U + static_cast<std::size_t>(count) * 4U) return false;
    const unsigned allowed = version == 0U ? 3U : 15U;
    int previous = -1;
    sfnt_rasterization_range candidate{};
    bool selected = false;
    // Dependent first-match and ordered validation; O(R), constant storage.
    for (std::size_t index = 0U; index < count; ++index) {
        const auto maximum = detail::read_u16(table, 4U + index * 4U);
        const auto flags = detail::read_u16(table, 6U + index * 4U);
        if (maximum <= previous || (flags & ~allowed) != 0U) return false;
        if (!selected && pixels_per_em <= maximum) {
            candidate = {version, pixels_per_em, maximum,
                static_cast<font_rasterization_recommendations>(flags)};
            selected = true;
        }
        previous = maximum;
    }
    result = candidate;
    available = selected;
    return true;
}

bool sfnt_font_view::try_get_static_rasterization_recommendations(
    std::uint16_t pixels_per_em, sfnt_rasterization_range& result,
    bool& available, font_error* error) const noexcept {
    result = {};
    available = false;
    if (error != nullptr) *error = font_error::none;
    const auto fail = [error](font_error reason = font_error::invalid_face) {
        if (error != nullptr) *error = reason;
        return false;
    };
    if (pixels_per_em == 0U) return fail(font_error::invalid_argument);
    if (data_.empty()) return fail();
    std::span<const std::byte> table{};
    bool found = false, variable = false;
    for (std::size_t index = 0U; index < table_count_; ++index) {
        const auto entry = directory_offset_ + index * 16U;
        const auto tag = detail::read_u32(data_, entry);
        variable |= tag == open_type_tag::from_chars('f', 'v', 'a', 'r').value;
        if (tag != open_type_tag::from_chars('g', 'a', 's', 'p').value) continue;
        const auto offset = detail::read_u32(data_, entry + 8U);
        const auto length = detail::read_u32(data_, entry + 12U);
        if (found || !detail::can_read(data_, offset, length)) return fail();
        found = true;
        table = data_.subspan(offset, length);
    }
    if (!found) return true;
    sfnt_rasterization_range candidate{};
    bool selected = false;
    if (!try_read_sfnt_rasterization_recommendations(table, pixels_per_em, candidate, selected)) return fail();
    sfnt_table_view glyf{}, loca{};
    if (variable || !selected ||
        !try_get_table(open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf) ||
        !try_get_table(open_type_tag::from_chars('l', 'o', 'c', 'a'), loca)) return true;
    result = candidate;
    available = true;
    return true;
}
} // namespace progpu::native::text
