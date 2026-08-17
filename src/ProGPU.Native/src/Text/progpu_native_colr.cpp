#include "progpu_native_text.hpp"

#include "progpu_native_colr_internal.hpp"
#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont COLR v0-prefix base and
// layer lookup at checkpoint 30e9ebe5. Base lookup is O(log B), layer decode is
// O(L), caller output is transactional, and internal storage is O(1).
namespace progpu::native::text {
namespace detail {

bool try_find_colr_layers(
    std::span<const std::byte> colr,
    std::uint16_t glyph_index,
    colr_layer_range& result) noexcept {
    result = {};
    if (colr.size() < 14U || read_u16(colr, 0U) > 1U) {
        return false;
    }
    const auto base_count = read_u16(colr, 2U);
    const auto base_offset =
        static_cast<std::size_t>(read_u32(colr, 4U));
    const auto layer_offset =
        static_cast<std::size_t>(read_u32(colr, 8U));
    const auto layer_count = read_u16(colr, 12U);
    if (base_offset > colr.size() ||
        base_count > (colr.size() - base_offset) / 6U ||
        layer_offset > colr.size() ||
        layer_count > (colr.size() - layer_offset) / 4U) {
        return false;
    }
    std::uint16_t low = 0U;
    std::uint16_t high = base_count;
    while (low < high) {
        const auto middle = static_cast<std::uint16_t>(
            low + static_cast<std::uint16_t>((high - low) / 2U));
        const auto record = base_offset +
            static_cast<std::size_t>(middle) * 6U;
        const auto candidate = read_u16(colr, record);
        if (glyph_index < candidate) {
            high = middle;
        } else if (glyph_index > candidate) {
            low = static_cast<std::uint16_t>(middle + 1U);
        } else {
            const auto first = read_u16(colr, record + 2U);
            const auto count = read_u16(colr, record + 4U);
            if (first > layer_count || count > layer_count - first) {
                return false;
            }
            result = {colr, layer_offset, first, count};
            return true;
        }
    }
    return true;
}

} // namespace detail
namespace {

constexpr auto colr_tag = open_type_tag::from_chars('C', 'O', 'L', 'R');
constexpr auto cpal_tag = open_type_tag::from_chars('C', 'P', 'A', 'L');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_range(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    detail::colr_layer_range& result,
    font_error* error) noexcept {
    result = {};
    std::uint16_t glyph_count = 0U;
    if (!font.try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    sfnt_table_view table{};
    if (!font.try_get_table(colr_tag, table)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    if (!detail::try_find_colr_layers(table.bytes, glyph_index, result)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (result.layer_count == 0U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    return true;
}

} // namespace

bool sfnt_font_view::try_get_colr_layer_count(
    std::uint16_t glyph_index,
    std::uint16_t& result,
    font_error* error) const noexcept {
    result = 0U;
    set_error(error, font_error::none);
    detail::colr_layer_range range{};
    if (!try_get_range(*this, glyph_index, range, error)) {
        return false;
    }
    result = range.layer_count;
    return true;
}

bool sfnt_font_view::try_decode_colr_layers(
    std::uint16_t glyph_index,
    std::uint16_t palette_index,
    std::span<sfnt_color_glyph_layer> layers,
    std::uint16_t& written,
    font_error* error) const noexcept {
    return try_decode_colr_layers(
        glyph_index,
        palette_index,
        std::span<const sfnt_color_palette_override>{},
        layers,
        written,
        error);
}

bool sfnt_font_view::try_decode_colr_layers(
    std::uint16_t glyph_index,
    std::uint16_t palette_index,
    std::span<const sfnt_color_palette_override> palette_overrides,
    std::span<sfnt_color_glyph_layer> layers,
    std::uint16_t& written,
    font_error* error) const noexcept {
    written = 0U;
    set_error(error, font_error::none);
    detail::colr_layer_range range{};
    if (!try_get_range(*this, glyph_index, range, error)) {
        return false;
    }
    if (layers.size() < range.layer_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    sfnt_table_view cpal_table{};
    detail::cpal_view palette{};
    const auto has_palette = try_get_table(cpal_tag, cpal_table);
    if (has_palette && !detail::try_parse_cpal(cpal_table.bytes, palette)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    for (std::uint16_t index = 0U; index < range.layer_count; ++index) {
        const auto layer = range.layer_records_offset +
            static_cast<std::size_t>(range.first_layer + index) * 4U;
        const auto entry = detail::read_u16(range.colr, layer + 2U);
        sfnt_color_rgba8 color{};
        auto foreground = entry == 0xFFFFU;
        if (has_palette) {
            detail::resolve_cpal_color(
                palette, palette_index, entry, color, foreground);
            if (!foreground && entry < palette.entries_per_palette) {
                for (auto override_index = palette_overrides.size();
                    override_index > 0U;
                    --override_index) {
                    const auto& palette_override =
                        palette_overrides[override_index - 1U];
                    if (palette_override.palette_entry_index != entry) {
                        continue;
                    }
                    const auto argb = palette_override.argb;
                    color = {
                        static_cast<std::uint8_t>((argb >> 16U) & 0xFFU),
                        static_cast<std::uint8_t>((argb >> 8U) & 0xFFU),
                        static_cast<std::uint8_t>(argb & 0xFFU),
                        static_cast<std::uint8_t>((argb >> 24U) & 0xFFU)};
                    break;
                }
            }
        }
        layers[index] = {
            detail::read_u16(range.colr, layer), entry, color, foreground};
    }
    written = range.layer_count;
    return true;
}

} // namespace progpu::native::text
