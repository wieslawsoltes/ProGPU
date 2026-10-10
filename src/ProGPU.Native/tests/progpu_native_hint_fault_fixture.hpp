#ifndef PROGPU_NATIVE_HINT_FAULT_FIXTURE_HPP
#define PROGPU_NATIVE_HINT_FAULT_FIXTURE_HPP

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <utility>
#include <vector>

namespace progpu::native::tests {

// Original test-only SFNT construction from the public OpenType wire tables and
// Apple's documented instructions; no foreign font or implementation is copied.
// Glyphs 1/2 have identical valid contours. Only glyph 2's program is faulty.
// Algorithm: fixed three-glyph table assembly and independent table/file checksums.
// Time/space: O(F), F = small authored fixture bytes; not a product font builder.
inline std::vector<std::byte> make_hint_fault_font(std::int16_t first_bearing = 13,
    std::int16_t second_bearing = 13, bool compact_metrics = false, bool reverse_contours = false)
{
    using bytes = std::vector<std::byte>;
    const auto put16 = [](bytes& data, std::size_t offset, std::uint16_t value) {
        data[offset] = static_cast<std::byte>(value >> 8U);
        data[offset + 1U] = static_cast<std::byte>(value & 255U);
    };
    const auto put32 = [&](bytes& data, std::size_t offset, std::uint32_t value) {
        put16(data, offset, static_cast<std::uint16_t>(value >> 16U));
        put16(data, offset + 2U, static_cast<std::uint16_t>(value & 65535U));
    };
    const auto checksum = [](std::span<const std::byte> data) {
        std::uint32_t sum = 0U;
        for (std::size_t offset = 0U; offset < data.size(); offset += 4U) {
            std::uint32_t word = 0U;
            for (std::size_t byte = 0U; byte < 4U; ++byte) {
                word <<= 8U;
                if (offset + byte < data.size()) word |= std::to_integer<std::uint32_t>(data[offset + byte]);
            }
            sum += word;
        }
        return sum;
    };
    const auto glyph = [&](std::span<const std::uint8_t> program) {
        bytes result(34U + program.size());
        put16(result, 0U, 1U); // one closed contour
        put16(result, 2U, 13U);
        put16(result, 4U, 13U);
        put16(result, 6U, 313U);
        put16(result, 8U, 413U);
        put16(result, 10U, 3U); // last of four on-curve points
        put16(result, 12U, static_cast<std::uint16_t>(program.size()));
        std::size_t cursor = 14U;
        for (const auto instruction : program) result[cursor++] = static_cast<std::byte>(instruction);
        for (std::size_t point = 0U; point < 4U; ++point) result[cursor++] = std::byte{1};
        // Additional winding control only; every existing call preserves its
        // exact original byte inventory and faulty/valid instruction programs.
        const std::array<std::int16_t, 4> x_deltas = reverse_contours
            ? std::array<std::int16_t, 4>{13, 0, 300, 0} : std::array<std::int16_t, 4>{13, 300, 0, -300};
        const std::array<std::int16_t, 4> y_deltas = reverse_contours
            ? std::array<std::int16_t, 4>{13, 400, 0, -400} : std::array<std::int16_t, 4>{13, 0, 400, 0};
        for (const auto delta : x_deltas) {
            put16(result, cursor, static_cast<std::uint16_t>(delta));
            cursor += 2U;
        }
        for (const auto delta : y_deltas) {
            put16(result, cursor, static_cast<std::uint16_t>(delta));
            cursor += 2U;
        }
        result.resize((result.size() + 3U) & ~std::size_t{3U});
        return result;
    };
    // SVTCA[y], PUSHB[1] point zero, MDAP[1]: an explicit grid-fitted y move.
    const std::array<std::uint8_t, 4> valid_program{0x00U, 0xB0U, 0x00U, 0x2FU};
    // SVTCA[y], POP on an empty glyph stack. Pedantic native execution must fail.
    const std::array<std::uint8_t, 2> invalid_program{0x00U, 0x21U};
    auto valid_glyph = glyph(valid_program);
    auto invalid_glyph = glyph(invalid_program);
    bytes glyf = valid_glyph;
    glyf.insert(glyf.end(), invalid_glyph.begin(), invalid_glyph.end());
    bytes loca(16U);
    // Glyph zero is a legitimate empty .notdef, with equal offsets 0/1.
    put32(loca, 8U, static_cast<std::uint32_t>(valid_glyph.size()));
    put32(loca, 12U, static_cast<std::uint32_t>(glyf.size()));
    bytes head(54U);
    put32(head, 0U, 0x00010000U);
    put32(head, 4U, 0x00010000U);
    put32(head, 12U, 0x5F0F3CF5U);
    put16(head, 16U, first_bearing == 13 && second_bearing == 13 ? 2U : 0U);
    put16(head, 18U, 1000U);
    put16(head, 36U, 13U);
    put16(head, 38U, 13U);
    put16(head, 40U, 313U);
    put16(head, 42U, 413U);
    put16(head, 46U, 8U);
    put16(head, 48U, 2U);
    put16(head, 50U, 1U); // long loca offsets
    bytes maxp(32U);
    put32(maxp, 0U, 0x00010000U);
    put16(maxp, 4U, 3U);
    put16(maxp, 6U, 4U);
    put16(maxp, 8U, 1U);
    put16(maxp, 14U, 2U);
    put16(maxp, 24U, 16U);
    put16(maxp, 26U, 4U);
    bytes hhea(36U);
    put32(hhea, 0U, 0x00010000U);
    put16(hhea, 4U, 800U);
    put16(hhea, 6U, static_cast<std::uint16_t>(-200));
    put16(hhea, 10U, 500U);
    put16(hhea, 12U, static_cast<std::uint16_t>(std::min({0, static_cast<int>(first_bearing), static_cast<int>(second_bearing)})));
    put16(hhea, 14U, static_cast<std::uint16_t>(std::min({500, 200 - first_bearing, 200 - second_bearing})));
    put16(hhea, 16U, static_cast<std::uint16_t>(std::max({0, 300 + first_bearing, 300 + second_bearing})));
    put16(hhea, 18U, 1U);
    put16(hhea, 34U, compact_metrics ? 1U : 3U);
    bytes hmtx(compact_metrics ? 8U : 12U);
    const std::array<std::int16_t, 3U> bearings{0, first_bearing, second_bearing};
    for (std::size_t index = 0U; index < 3U; ++index) {
        if (!compact_metrics || index == 0U) put16(hmtx, index * 4U, 500U);
        const auto bearing_offset = compact_metrics && index != 0U ? 2U + index * 2U : index * 4U + 2U;
        put16(hmtx, bearing_offset, static_cast<std::uint16_t>(bearings[index]));
    }
    bytes cmap(274U);
    put16(cmap, 2U, 1U);
    put16(cmap, 4U, 1U); // Macintosh Roman format-zero mapping
    put32(cmap, 8U, 12U);
    put16(cmap, 14U, 262U);
    cmap[18U + 65U] = std::byte{1};
    cmap[18U + 66U] = std::byte{2};
    bytes post(32U);
    put32(post, 0U, 0x00030000U);
    struct table final { std::uint32_t tag; bytes data; };
    std::array tables{
        table{0x636D6170U, std::move(cmap)}, table{0x676C7966U, std::move(glyf)},
        table{0x68656164U, std::move(head)}, table{0x68686561U, std::move(hhea)},
        table{0x686D7478U, std::move(hmtx)}, table{0x6C6F6361U, std::move(loca)},
        table{0x6D617870U, std::move(maxp)}, table{0x706F7374U, std::move(post)}};
    std::size_t length = 12U + tables.size() * 16U;
    for (const auto& value : tables) length += (value.data.size() + 3U) & ~std::size_t{3U};
    bytes result(length);
    put32(result, 0U, 0x00010000U);
    put16(result, 4U, 8U);
    put16(result, 6U, 128U);
    put16(result, 8U, 3U);
    std::size_t cursor = 12U + tables.size() * 16U;
    std::size_t head_offset = 0U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto& value = tables[index];
        const auto record = 12U + index * 16U;
        put32(result, record, value.tag);
        put32(result, record + 4U, checksum(value.data));
        put32(result, record + 8U, static_cast<std::uint32_t>(cursor));
        put32(result, record + 12U, static_cast<std::uint32_t>(value.data.size()));
        std::copy(value.data.begin(), value.data.end(), result.begin() + static_cast<std::ptrdiff_t>(cursor));
        if (value.tag == 0x68656164U) head_offset = cursor;
        cursor += (value.data.size() + 3U) & ~std::size_t{3U};
    }
    put32(result, head_offset + 8U, 0xB1B0AFBAU - checksum(result));
    return result;
}

} // namespace progpu::native::tests

#endif
