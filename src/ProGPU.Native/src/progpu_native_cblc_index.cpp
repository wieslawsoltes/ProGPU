#include "progpu_native_cbdt_internal.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned TtfFont CBLC index formats 1-5
// at checkpoint 873593a7. Resolution is O(1) for dense formats and O(N) for N
// sparse glyph records, retains O(1) storage, and performs all offsets in a
// 64-bit checked domain before a borrowed span is formed.
namespace progpu::native::text::detail {

cbdt_glyph_metrics read_small_cbdt_metrics(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return {
        std::to_integer<std::uint8_t>(bytes[offset + 1U]),
        std::to_integer<std::uint8_t>(bytes[offset]),
        static_cast<std::int8_t>(
            std::to_integer<std::uint8_t>(bytes[offset + 2U])),
        static_cast<std::int8_t>(
            std::to_integer<std::uint8_t>(bytes[offset + 3U]))};
}

cbdt_glyph_metrics read_big_cbdt_metrics(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return read_small_cbdt_metrics(bytes, offset);
}

bool try_resolve_cbdt_image_range(
    std::span<const std::byte> cblc,
    std::size_t subtable_offset,
    std::size_t subtable_limit,
    std::uint16_t first_glyph,
    std::uint16_t last_glyph,
    std::uint16_t glyph_index,
    cbdt_image_range& result) noexcept {
    result = {};
    if (first_glyph > last_glyph || glyph_index < first_glyph ||
        glyph_index > last_glyph || subtable_limit > cblc.size() ||
        subtable_offset > subtable_limit ||
        subtable_limit - subtable_offset < 8U) {
        return false;
    }
    const auto format = read_u16(cblc, subtable_offset);
    const auto image_data_offset = read_u32(cblc, subtable_offset + 4U);
    const auto glyph_offset =
        static_cast<std::uint32_t>(glyph_index - first_glyph);
    const auto glyph_count =
        static_cast<std::uint32_t>(last_glyph - first_glyph) + 1U;
    std::uint64_t relative_start = 0U;
    std::uint64_t relative_end = 0U;
    cbdt_glyph_metrics metrics{};
    switch (format) {
    case 1U: {
        const auto offset_count = static_cast<std::size_t>(glyph_count) + 1U;
        if (offset_count > (subtable_limit - subtable_offset - 8U) / 4U) {
            return false;
        }
        const auto offset = subtable_offset + 8U +
            static_cast<std::size_t>(glyph_offset) * 4U;
        relative_start = read_u32(cblc, offset);
        relative_end = read_u32(cblc, offset + 4U);
        break;
    }
    case 2U: {
        if (subtable_limit - subtable_offset < 20U) {
            return false;
        }
        const auto image_size = read_u32(cblc, subtable_offset + 8U);
        if (image_size == 0U) {
            return false;
        }
        metrics = read_big_cbdt_metrics(cblc, subtable_offset + 12U);
        relative_start =
            static_cast<std::uint64_t>(glyph_offset) * image_size;
        relative_end = relative_start + image_size;
        break;
    }
    case 3U: {
        const auto offset_count = static_cast<std::size_t>(glyph_count) + 1U;
        if (offset_count > (subtable_limit - subtable_offset - 8U) / 2U) {
            return false;
        }
        const auto offset = subtable_offset + 8U +
            static_cast<std::size_t>(glyph_offset) * 2U;
        relative_start = read_u16(cblc, offset);
        relative_end = read_u16(cblc, offset + 2U);
        break;
    }
    case 4U: {
        if (subtable_limit - subtable_offset < 12U) {
            return false;
        }
        const auto sparse_count = read_u32(cblc, subtable_offset + 8U);
        if (sparse_count == 0U ||
            static_cast<std::uint64_t>(sparse_count) + 1U >
                (subtable_limit - subtable_offset - 12U) / 4U) {
            return false;
        }
        const auto pairs = subtable_offset + 12U;
        auto found = false;
        for (std::uint32_t index = 0U; index < sparse_count; ++index) {
            const auto pair = pairs + static_cast<std::size_t>(index) * 4U;
            if (read_u16(cblc, pair) == glyph_index) {
                relative_start = read_u16(cblc, pair + 2U);
                relative_end = read_u16(cblc, pair + 6U);
                found = true;
                break;
            }
        }
        if (!found) {
            return false;
        }
        break;
    }
    case 5U: {
        if (subtable_limit - subtable_offset < 24U) {
            return false;
        }
        const auto image_size = read_u32(cblc, subtable_offset + 8U);
        const auto sparse_count = read_u32(cblc, subtable_offset + 20U);
        if (image_size == 0U || sparse_count == 0U ||
            sparse_count > (subtable_limit - subtable_offset - 24U) / 2U) {
            return false;
        }
        const auto glyphs = subtable_offset + 24U;
        auto sparse_index = std::numeric_limits<std::uint32_t>::max();
        for (std::uint32_t index = 0U; index < sparse_count; ++index) {
            if (read_u16(
                    cblc,
                    glyphs + static_cast<std::size_t>(index) * 2U) ==
                glyph_index) {
                sparse_index = index;
                break;
            }
        }
        if (sparse_index == std::numeric_limits<std::uint32_t>::max()) {
            return false;
        }
        metrics = read_big_cbdt_metrics(cblc, subtable_offset + 12U);
        relative_start =
            static_cast<std::uint64_t>(sparse_index) * image_size;
        relative_end = relative_start + image_size;
        break;
    }
    default:
        return false;
    }
    if (relative_start >= relative_end ||
        relative_start >
            std::numeric_limits<std::uint64_t>::max() - image_data_offset ||
        relative_end >
            std::numeric_limits<std::uint64_t>::max() - image_data_offset) {
        return false;
    }
    result = {
        static_cast<std::uint64_t>(image_data_offset) + relative_start,
        static_cast<std::uint64_t>(image_data_offset) + relative_end,
        metrics};
    return true;
}

} // namespace progpu::native::text::detail
