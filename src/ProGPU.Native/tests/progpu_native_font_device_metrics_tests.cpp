#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <source_location>
#include <vector>

namespace {
using namespace progpu::native::text;
using bytes = std::vector<std::byte>;
unsigned cases = 0U;

void require(bool condition, std::source_location location = std::source_location::current()) {
    if (!condition) {
        std::fprintf(stderr, "hdmx check failed at line %u\n", location.line());
        std::abort();
    }
}

void put16(bytes& data, std::size_t offset, std::uint16_t value) {
    data[offset] = static_cast<std::byte>(value >> 8U);
    data[offset + 1U] = static_cast<std::byte>(value & 255U);
}

void put32(bytes& data, std::size_t offset, std::uint32_t value) {
    put16(data, offset, static_cast<std::uint16_t>(value >> 16U));
    put16(data, offset + 2U, static_cast<std::uint16_t>(value & 65535U));
}

bytes device_table() {
    // Three glyphs, including glyph zero and a zero-width glyph. Deliberately
    // nonlinear widths make nearest-size or proportional substitutes observable.
    bytes table(24U);
    put16(table, 2U, 2U);
    put32(table, 4U, 8U);
    table[8U] = std::byte{12}; table[9U] = std::byte{255};
    table[10U] = std::byte{1}; table[11U] = std::byte{0}; table[12U] = std::byte{255};
    table[16U] = std::byte{16}; table[17U] = std::byte{4};
    table[18U] = std::byte{2}; table[19U] = std::byte{3}; table[20U] = std::byte{4};
    return table;
}

bytes font_bytes(std::span<const std::byte> table, std::uint16_t table_count = 2U) {
    const auto maxp_offset = 12U + static_cast<std::uint32_t>(table_count) * 16U;
    const auto hdmx_offset = maxp_offset + 32U;
    bytes font(hdmx_offset + table.size());
    put32(font, 0U, 0x00010000U);
    put16(font, 4U, table_count);
    put32(font, 12U, open_type_tag::from_chars('m', 'a', 'x', 'p').value);
    put32(font, 20U, maxp_offset);
    put32(font, 24U, 32U);
    put32(font, maxp_offset, 0x00010000U);
    put16(font, maxp_offset + 4U, 3U);
    for (std::size_t index = 1U; index < table_count; ++index) {
        const auto entry = 12U + index * 16U;
        put32(font, entry, open_type_tag::from_chars('h', 'd', 'm', 'x').value);
        put32(font, entry + 8U, hdmx_offset);
        put32(font, entry + 12U, static_cast<std::uint32_t>(table.size()));
    }
    std::copy(table.begin(), table.end(), font.begin() + hdmx_offset);
    return font;
}

void absent_or_invalid(const bytes& bytes_value, std::uint16_t size, bool expected_success,
    font_error expected_error = font_error::invalid_face) {
    ++cases;
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes_value, 0U, font));
    const std::array sentinel{std::byte{99}};
    sfnt_horizontal_device_metrics metrics{sentinel, 99U, 99U};
    bool available = true;
    auto error = font_error::verification_failed;
    require(font.try_get_horizontal_device_metrics(size, metrics, available, &error) == expected_success);
    require(!available);
    require(metrics.glyph_widths.empty());
    require(metrics.pixels_per_em == 0U && metrics.maximum_width == 0U);
    require(error == (expected_success ? font_error::none : expected_error));
}
} // namespace

int main() {
    const auto table = device_table();
    const auto data = font_bytes(table);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, 0U, font));
    for (const std::uint16_t size : {12U, 16U}) {
        ++cases;
        sfnt_horizontal_device_metrics metrics{};
        bool available = false;
        font_error error = font_error::verification_failed;
        require(font.try_get_horizontal_device_metrics(size, metrics, available, &error));
        require(available && error == font_error::none);
        require(metrics.pixels_per_em == size && metrics.glyph_widths.size() == 3U);
        const auto record_offset = size == 12U ? 8U : 16U;
        require(metrics.glyph_widths.data() == data.data() + 76U + record_offset + 2U);
        require(metrics.maximum_width == (size == 12U ? 255U : 4U));
        for (std::size_t index = 0U; index < 3U; ++index)
            require(metrics.glyph_widths[index] == table[record_offset + 2U + index]);
    }
    for (const std::uint16_t size : {1U, 11U, 13U, 17U, 255U, 256U, 65535U})
        absent_or_invalid(data, size, true);
    absent_or_invalid(data, 0U, false, font_error::invalid_argument);
    absent_or_invalid(font_bytes({}, 1U), 12U, true);

    auto empty = table;
    empty.resize(8U);
    put16(empty, 2U, 0U);
    absent_or_invalid(font_bytes(empty), 12U, true);
    for (std::size_t length = 0U; length < table.size(); ++length)
        absent_or_invalid(font_bytes(std::span{table}.first(length)), 12U, false);
    auto trailing = table;
    trailing.push_back(std::byte{0});
    absent_or_invalid(font_bytes(trailing), 12U, false);

    // Corrupt records after the requested record too: an early successful lookup
    // must not publish a view from a partially validated table.
    constexpr std::array<std::array<unsigned, 2>, 13> corruptions{{
        {1U, 1U}, {3U, 3U}, {7U, 0U}, {7U, 4U}, {7U, 7U}, {7U, 12U},
        {8U, 0U}, {16U, 12U}, {16U, 11U}, {9U, 254U}, {17U, 5U},
        {13U, 1U}, {21U, 1U}
    }};
    for (const auto& mutation : corruptions) {
        auto invalid = table;
        invalid[mutation[0U]] = static_cast<std::byte>(mutation[1U]);
        absent_or_invalid(font_bytes(invalid), 12U, false);
    }
    auto invalid = table;
    put32(invalid, 4U, 0xfffffffcU);
    absent_or_invalid(font_bytes(invalid), 12U, false);
    put16(invalid, 2U, 65535U);
    absent_or_invalid(font_bytes(invalid), 12U, false);
    absent_or_invalid(font_bytes(table, 3U), 12U, false); // Duplicate hdmx entry.
    auto invalid_font = data;
    put32(invalid_font, 36U, 0xfffffff0U);
    absent_or_invalid(invalid_font, 12U, false); // Declared table outside font.
    invalid_font = data;
    put32(invalid_font, 40U, 0xffffffffU);
    absent_or_invalid(invalid_font, 12U, false);
    invalid_font = data;
    put16(invalid_font, 48U, 0U); // maxp.numGlyphs.
    absent_or_invalid(invalid_font, 12U, false);

    sfnt_horizontal_device_metrics metrics{};
    bool available = true;
    require(!sfnt_font_view{}.try_get_horizontal_device_metrics(12U, metrics, available));
    require(!available && metrics.glyph_widths.empty());
    ++cases;
    std::printf("Native horizontal device metric contracts passed: %u cases.\n", cases);
}
