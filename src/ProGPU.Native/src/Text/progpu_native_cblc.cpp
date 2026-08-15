#include "progpu_native_text.hpp"

#include "progpu_native_cbdt_internal.hpp"
#include "progpu_native_font_bytes.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont CBLC/CBDT strike and
// subtable selection at checkpoint 873593a7. Lookup is O(S + R + N) for S
// strikes, R index-subtable records, and N sparse records; storage is O(1), and
// the selected encoded image remains borrowed from the source font.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

constexpr auto cblc_tag = open_type_tag::from_chars('C', 'B', 'L', 'C');
constexpr auto cbdt_tag = open_type_tag::from_chars('C', 'B', 'D', 'T');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_get_from_strike(
    std::span<const std::byte> cblc,
    std::span<const std::byte> cbdt,
    std::size_t strike_offset,
    std::uint16_t glyph_index,
    std::uint16_t pixels_per_em,
    sfnt_bitmap_glyph_data_view& result) noexcept {
    result = {};
    const auto index_list_offset =
        static_cast<std::size_t>(read_u32(cblc, strike_offset));
    const auto index_list_size =
        static_cast<std::size_t>(read_u32(cblc, strike_offset + 4U));
    const auto subtable_count = read_u32(cblc, strike_offset + 8U);
    if (index_list_size < 8U || index_list_offset > cblc.size() ||
        index_list_size > cblc.size() - index_list_offset ||
        subtable_count == 0U || subtable_count > index_list_size / 8U) {
        return false;
    }
    const auto index_list_end = index_list_offset + index_list_size;
    for (std::uint32_t record = 0U; record < subtable_count; ++record) {
        const auto record_offset = index_list_offset +
            static_cast<std::size_t>(record) * 8U;
        const auto first_glyph = read_u16(cblc, record_offset);
        const auto last_glyph = read_u16(cblc, record_offset + 2U);
        if (glyph_index < first_glyph || glyph_index > last_glyph) {
            continue;
        }
        const auto relative = read_u32(cblc, record_offset + 4U);
        if (relative > index_list_size ||
            index_list_size - relative < 8U) {
            return false;
        }
        const auto subtable_offset = index_list_offset + relative;
        if (!can_read(cblc, subtable_offset, 8U) ||
            subtable_offset + 8U > index_list_end) {
            return false;
        }
        detail::cbdt_image_range range{};
        if (!detail::try_resolve_cbdt_image_range(
                cblc,
                subtable_offset,
                index_list_end,
                first_glyph,
                last_glyph,
                glyph_index,
                range) ||
            !detail::try_read_cbdt_image(
                cbdt,
                range,
                read_u16(cblc, subtable_offset + 2U),
                pixels_per_em,
                std::to_integer<std::uint8_t>(cblc[strike_offset + 47U]),
                result)) {
            return false;
        }
        return true;
    }
    return false;
}

} // namespace

bool sfnt_font_view::try_get_cbdt_glyph(
    std::uint16_t glyph_index,
    float target_pixels_per_em,
    sfnt_bitmap_glyph_data_view& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    sfnt_table_view cblc_table{};
    sfnt_table_view cbdt_table{};
    if (!try_get_table(cblc_tag, cblc_table) ||
        !try_get_table(cbdt_tag, cbdt_table) ||
        cblc_table.bytes.size() < 8U || cbdt_table.bytes.size() < 4U ||
        read_u16(cblc_table.bytes, 0U) != 3U ||
        read_u16(cbdt_table.bytes, 0U) != 3U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    const auto strike_count = read_u32(cblc_table.bytes, 4U);
    if (strike_count == 0U ||
        strike_count > (cblc_table.bytes.size() - 8U) / 48U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    auto found = false;
    auto best_distance = 0.0F;
    sfnt_bitmap_glyph_data_view best{};
    for (std::uint32_t strike = 0U; strike < strike_count; ++strike) {
        const auto strike_offset = 8U +
            static_cast<std::size_t>(strike) * 48U;
        const auto first_glyph = read_u16(cblc_table.bytes, strike_offset + 40U);
        const auto last_glyph = read_u16(cblc_table.bytes, strike_offset + 42U);
        if (glyph_index < first_glyph || glyph_index > last_glyph) {
            continue;
        }
        const auto y_ppem = std::to_integer<std::uint8_t>(
            cblc_table.bytes[strike_offset + 45U]);
        const auto pixels_per_em = y_ppem != 0U
            ? y_ppem
            : std::to_integer<std::uint8_t>(
                cblc_table.bytes[strike_offset + 44U]);
        sfnt_bitmap_glyph_data_view candidate{};
        if (pixels_per_em == 0U ||
            !try_get_from_strike(
                cblc_table.bytes,
                cbdt_table.bytes,
                strike_offset,
                glyph_index,
                pixels_per_em,
                candidate)) {
            continue;
        }
        const auto distance = std::abs(
            static_cast<float>(candidate.pixels_per_em) -
            target_pixels_per_em);
        if (!found || distance < best_distance ||
            (distance == best_distance &&
                candidate.pixels_per_em > best.pixels_per_em)) {
            found = true;
            best_distance = distance;
            best = candidate;
        }
    }
    if (!found) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = best;
    return true;
}

} // namespace progpu::native::text
