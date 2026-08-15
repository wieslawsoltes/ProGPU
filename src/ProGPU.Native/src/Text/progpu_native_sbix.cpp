#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cmath>
#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned TtfFont sbix strike selection
// and duplicate-glyph resolution at checkpoint fdb47fb7. The selected encoded
// image remains a borrowed table slice. Lookup is O(S + D) for S strikes and
// bounded duplicate depth D <= 16, with O(1) internal storage.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

constexpr auto sbix_tag = open_type_tag::from_chars('s', 'b', 'i', 'x');
constexpr auto dupe_tag = open_type_tag::from_chars('d', 'u', 'p', 'e');
constexpr auto png_tag = open_type_tag::from_chars('p', 'n', 'g', ' ');
constexpr auto jpeg_tag = open_type_tag::from_chars('j', 'p', 'g', ' ');
constexpr auto tiff_tag = open_type_tag::from_chars('t', 'i', 'f', 'f');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_resolve_glyph(
    std::span<const std::byte> sbix,
    std::size_t strike_offset,
    std::size_t strike_end,
    std::uint16_t pixels_per_em,
    std::uint16_t pixels_per_inch,
    std::uint16_t glyph_count,
    std::uint16_t glyph_index,
    std::uint16_t original_glyph_index,
    std::uint32_t depth,
    sfnt_bitmap_glyph_data_view& result) noexcept {
    result = {};
    if (depth > 16U || glyph_index >= glyph_count) {
        return false;
    }
    const auto offsets = strike_offset + 4U;
    const auto glyph_offset = offsets +
        static_cast<std::size_t>(glyph_index) * 4U;
    if (!can_read(sbix, glyph_offset, 8U)) {
        return false;
    }
    const auto start_relative = read_u32(sbix, glyph_offset);
    const auto end_relative = read_u32(sbix, glyph_offset + 4U);
    if (start_relative >= end_relative ||
        start_relative > strike_end - strike_offset ||
        end_relative > strike_end - strike_offset) {
        return false;
    }
    const auto start = strike_offset + start_relative;
    const auto end = strike_offset + end_relative;
    if (start < offsets || end > strike_end || end - start < 8U) {
        return false;
    }
    const auto origin_x = read_i16(sbix, start);
    const auto origin_y = read_i16(sbix, start + 2U);
    const open_type_tag graphic_type{read_u32(sbix, start + 4U)};
    if (graphic_type == dupe_tag) {
        if (end - start < 10U) {
            return false;
        }
        const auto duplicate_glyph = read_u16(sbix, start + 8U);
        sfnt_bitmap_glyph_data_view duplicate{};
        if (duplicate_glyph == original_glyph_index ||
            !try_resolve_glyph(
                sbix,
                strike_offset,
                strike_end,
                pixels_per_em,
                pixels_per_inch,
                glyph_count,
                duplicate_glyph,
                original_glyph_index,
                depth + 1U,
                duplicate)) {
            return false;
        }
        result = {
            duplicate.bytes,
            duplicate.graphic_type,
            pixels_per_em,
            pixels_per_inch,
            origin_x,
            origin_y};
        return true;
    }
    if (graphic_type != png_tag && graphic_type != jpeg_tag &&
        graphic_type != tiff_tag) {
        return false;
    }
    result = {
        sbix.subspan(start + 8U, end - start - 8U),
        graphic_type,
        pixels_per_em,
        pixels_per_inch,
        origin_x,
        origin_y};
    return true;
}

bool try_get_from_strike(
    std::span<const std::byte> sbix,
    std::uint32_t encoded_strike_offset,
    std::uint32_t encoded_strike_end,
    std::uint16_t glyph_count,
    std::uint16_t glyph_index,
    sfnt_bitmap_glyph_data_view& result) noexcept {
    result = {};
    const auto strike_offset =
        static_cast<std::size_t>(encoded_strike_offset);
    const auto strike_end = static_cast<std::size_t>(encoded_strike_end);
    const auto offset_table_size =
        (static_cast<std::size_t>(glyph_count) + 1U) * 4U;
    if (strike_offset >= strike_end || strike_end > sbix.size() ||
        !can_read(sbix, strike_offset, 4U + offset_table_size)) {
        return false;
    }
    const auto pixels_per_em = read_u16(sbix, strike_offset);
    const auto pixels_per_inch = read_u16(sbix, strike_offset + 2U);
    return try_resolve_glyph(
        sbix,
        strike_offset,
        strike_end,
        pixels_per_em,
        pixels_per_inch,
        glyph_count,
        glyph_index,
        glyph_index,
        0U,
        result);
}

} // namespace

bool sfnt_font_view::try_get_sbix_glyph(
    std::uint16_t glyph_index,
    float target_pixels_per_em,
    sfnt_bitmap_glyph_data_view& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    sfnt_table_view table{};
    if (!try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!try_get_table(sbix_tag, table) || table.bytes.size() < 12U ||
        read_u16(table.bytes, 0U) != 1U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    const auto strike_count = read_u32(table.bytes, 4U);
    if (strike_count == 0U ||
        static_cast<std::size_t>(strike_count) >
            (table.bytes.size() - 8U) / 4U) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    auto best_distance = 0.0F;
    auto found = false;
    sfnt_bitmap_glyph_data_view best{};
    for (std::uint32_t strike = 0U; strike < strike_count; ++strike) {
        const auto strike_offset = read_u32(
            table.bytes, 8U + static_cast<std::size_t>(strike) * 4U);
        const auto strike_end = strike + 1U < strike_count
            ? read_u32(
                table.bytes,
                8U + static_cast<std::size_t>(strike + 1U) * 4U)
            : static_cast<std::uint32_t>(table.bytes.size());
        sfnt_bitmap_glyph_data_view candidate{};
        if (!try_get_from_strike(
                table.bytes,
                strike_offset,
                strike_end,
                glyph_count,
                glyph_index,
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
