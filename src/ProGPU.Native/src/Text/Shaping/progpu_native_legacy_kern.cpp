#include "progpu_native_legacy_kern_internal.hpp"
#include "progpu_native_open_type_gpos_internal.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// OpenTypeTextShaper.GlyphPositionBuffer.ApplyLegacyKern at repository
// checkpoint 34b76eeb. Parsing is bounded and all glyph storage is borrowed.

namespace progpu::native::text::detail {
namespace {

constexpr auto kern_tag = open_type_tag::from_chars('k', 'e', 'r', 'n');

std::int32_t add_i16_clamped(
    std::int32_t value,
    std::int32_t adjustment) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        static_cast<std::int64_t>(value) + adjustment,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

std::int32_t floor_half(std::int32_t value) noexcept {
    return value >= 0 ? value / 2 : -((-value + 1) / 2);
}

std::size_t next_kern_glyph(
    std::span<const shaping_glyph> glyphs,
    std::size_t index,
    const open_type_gdef_view* gdef) noexcept {
    if (gdef == nullptr) return index;
    while (index < glyphs.size() &&
        gdef->glyph_class(static_cast<std::uint16_t>(
            glyphs[index].glyph_id)) == open_type_glyph_class::mark) {
        ++index;
    }
    return index;
}

void apply_adjustment(
    std::span<shaping_glyph> glyphs,
    std::size_t left_index,
    std::size_t right_index,
    std::int32_t kerning,
    bool cross_stream) noexcept {
    if (kerning == 0) return;
    mark_gpos_dependency(glyphs, left_index, right_index);
    if (cross_stream) {
        glyphs[right_index].offset_y = add_i16_clamped(
            glyphs[right_index].offset_y, kerning);
        return;
    }
    const auto first = floor_half(kerning);
    const auto second = kerning - first;
    glyphs[left_index].advance_x = add_i16_clamped(
        glyphs[left_index].advance_x, first);
    glyphs[right_index].advance_x = add_i16_clamped(
        glyphs[right_index].advance_x, second);
    glyphs[right_index].offset_x = add_i16_clamped(
        glyphs[right_index].offset_x, second);
}

std::int32_t find_pair(
    std::span<const std::byte> data,
    std::size_t records,
    std::uint16_t pair_count,
    std::uint16_t left,
    std::uint16_t right) noexcept {
    const auto key = (static_cast<std::uint32_t>(left) << 16U) | right;
    std::uint16_t low = 0U;
    std::uint16_t high = pair_count;
    while (low < high) {
        const auto middle = static_cast<std::uint16_t>(
            low + static_cast<std::uint16_t>((high - low) / 2U));
        const auto record = records + static_cast<std::size_t>(middle) * 6U;
        const auto candidate = read_u32(data, record);
        if (key < candidate) {
            high = middle;
        } else if (key > candidate) {
            low = static_cast<std::uint16_t>(middle + 1U);
        } else {
            return read_i16(data, record + 4U);
        }
    }
    return 0;
}

std::uint16_t get_class(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t length,
    std::uint16_t relative_offset,
    std::uint16_t glyph) noexcept {
    if (relative_offset > length || length - relative_offset < 4U) return 0U;
    const auto table = subtable + relative_offset;
    const auto first_glyph = read_u16(data, table);
    const auto glyph_count = read_u16(data, table + 2U);
    if (glyph < first_glyph) return 0U;
    const auto index = static_cast<std::uint32_t>(glyph - first_glyph);
    const auto value = table + 4U + static_cast<std::size_t>(index) * 2U;
    return index < glyph_count && can_read(data, value, 2U)
        ? read_u16(data, value)
        : 0U;
}

void apply_format_zero(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t header_size,
    std::size_t length,
    bool cross_stream,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept {
    const auto body = subtable + header_size;
    if (!can_read(data, body, 8U)) return;
    const auto pair_count = read_u16(data, body);
    const auto records = body + 8U;
    if (records > subtable + length ||
        static_cast<std::size_t>(pair_count) >
            (subtable + length - records) / 6U) {
        return;
    }
    for (std::size_t left = 0U; left + 1U < glyphs.size(); ++left) {
        const auto right = next_kern_glyph(glyphs, left + 1U, gdef);
        if (right >= glyphs.size()) break;
        const auto kerning = find_pair(
            data,
            records,
            pair_count,
            static_cast<std::uint16_t>(glyphs[left].glyph_id),
            static_cast<std::uint16_t>(glyphs[right].glyph_id));
        apply_adjustment(glyphs, left, right, kerning, cross_stream);
    }
}

void apply_format_two(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t header_size,
    std::size_t length,
    bool cross_stream,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept {
    const auto body = subtable + header_size;
    if (!can_read(data, body, 8U)) return;
    const auto left_table = read_u16(data, body + 2U);
    const auto right_table = read_u16(data, body + 4U);
    const auto array = read_u16(data, body + 6U);
    for (std::size_t left = 0U; left + 1U < glyphs.size(); ++left) {
        const auto right = next_kern_glyph(glyphs, left + 1U, gdef);
        if (right >= glyphs.size()) break;
        const auto left_offset = get_class(
            data, subtable, length, left_table,
            static_cast<std::uint16_t>(glyphs[left].glyph_id));
        const auto right_offset = get_class(
            data, subtable, length, right_table,
            static_cast<std::uint16_t>(glyphs[right].glyph_id));
        const auto value_offset =
            static_cast<std::size_t>(left_offset) + right_offset;
        const auto kerning = value_offset < array ||
            value_offset > length || length - value_offset < 2U
            ? 0
            : read_i16(data, subtable + value_offset);
        apply_adjustment(glyphs, left, right, kerning, cross_stream);
    }
}

} // namespace

void apply_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept {
    sfnt_table_view table{};
    if (!font.try_get_table(kern_tag, table)) return;
    const auto data = table.bytes;
    const bool apple = can_read(data, 0U, 8U) &&
        read_u32(data, 0U) == 0x00010000U;
    std::uint32_t subtable_count = 0U;
    std::size_t subtable = 0U;
    if (apple) {
        subtable_count = read_u32(data, 4U);
        subtable = 8U;
    } else {
        if (!can_read(data, 0U, 4U) || read_u16(data, 0U) != 0U) return;
        subtable_count = read_u16(data, 2U);
        subtable = 4U;
    }
    for (std::uint32_t index = 0U; index < subtable_count; ++index) {
        const std::size_t header_size = apple ? 8U : 6U;
        if (!can_read(data, subtable, header_size)) break;
        const auto raw_length = apple
            ? static_cast<std::uint64_t>(read_u32(data, subtable))
            : static_cast<std::uint64_t>(read_u16(data, subtable + 2U));
        if (raw_length < header_size ||
            raw_length > std::numeric_limits<std::size_t>::max() ||
            !can_read(data, subtable, static_cast<std::size_t>(raw_length))) {
            break;
        }
        const auto length = static_cast<std::size_t>(raw_length);
        const auto format = std::to_integer<std::uint8_t>(
            data[subtable + (apple ? 5U : 4U)]);
        const auto coverage = std::to_integer<std::uint8_t>(
            data[subtable + (apple ? 4U : 5U)]);
        const bool horizontal = apple
            ? (coverage & 0x80U) == 0U
            : (coverage & 0x01U) != 0U;
        const bool cross_stream = apple
            ? (coverage & 0x40U) != 0U
            : (coverage & 0x04U) != 0U;
        if (horizontal && format == 0U) {
            apply_format_zero(
                data, subtable, header_size, length, cross_stream, glyphs,
                gdef);
        } else if (horizontal && format == 2U) {
            apply_format_two(
                data, subtable, header_size, length, cross_stream, glyphs,
                gdef);
        }
        subtable += length;
    }
}

} // namespace progpu::native::text::detail
