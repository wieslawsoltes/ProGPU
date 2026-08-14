#include "progpu_native_colr_internal.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont CPAL palette loading and
// WithColorPalette selection at checkpoint 30e9ebe5. Parsing is O(1), color
// lookup is O(1), and all table data remains borrowed.
namespace progpu::native::text::detail {

bool try_parse_cpal(
    std::span<const std::byte> cpal,
    cpal_view& result) noexcept {
    result = {};
    if (cpal.size() < 12U || read_u16(cpal, 0U) > 1U) {
        return false;
    }
    const auto entries = read_u16(cpal, 2U);
    const auto palette_count = read_u16(cpal, 4U);
    const auto color_count = read_u16(cpal, 6U);
    const auto records_offset =
        static_cast<std::size_t>(read_u32(cpal, 8U));
    if (entries == 0U || palette_count == 0U ||
        palette_count > (cpal.size() - 12U) / 2U ||
        records_offset > cpal.size() ||
        color_count > (cpal.size() - records_offset) / 4U) {
        return false;
    }
    for (std::uint16_t palette = 0U; palette < palette_count; ++palette) {
        const auto first = read_u16(
            cpal, 12U + static_cast<std::size_t>(palette) * 2U);
        if (first > color_count || entries > color_count - first) {
            return false;
        }
    }
    result = {
        cpal,
        records_offset,
        12U,
        entries,
        palette_count};
    return true;
}

void resolve_cpal_color(
    cpal_view palette,
    std::uint16_t palette_index,
    std::uint16_t entry_index,
    sfnt_color_rgba8& result,
    bool& uses_foreground) noexcept {
    result = {};
    uses_foreground = entry_index == 0xFFFFU;
    if (uses_foreground || entry_index >= palette.entries_per_palette) {
        return;
    }
    const auto selected = palette_index < palette.palette_count
        ? palette_index
        : 0U;
    const auto first = read_u16(
        palette.cpal,
        palette.palette_indices_offset +
            static_cast<std::size_t>(selected) * 2U);
    const auto record = static_cast<std::size_t>(first) + entry_index;
    const auto offset = palette.color_records_offset + record * 4U;
    result = {
        std::to_integer<std::uint8_t>(palette.cpal[offset + 2U]),
        std::to_integer<std::uint8_t>(palette.cpal[offset + 1U]),
        std::to_integer<std::uint8_t>(palette.cpal[offset]),
        std::to_integer<std::uint8_t>(palette.cpal[offset + 3U])};
}

} // namespace progpu::native::text::detail
