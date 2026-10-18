#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <source_location>
#include <span>
#include <utility>
#include <vector>

#ifdef PROGPU_TEST_TEXT_MODULE
import progpu.native.text;
#else
#include "progpu_native_text.hpp"
#endif

namespace {
using namespace progpu::native::text;
using bytes = std::vector<std::byte>;
unsigned checks = 0;
void require(bool value, std::source_location location = std::source_location::current()) {
    ++checks;
    if (!value) {
        std::fprintf(stderr, "gasp check failed at line %u\n", location.line());
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
bytes original_table(bool segoe) {
    bytes result(16U);
    put16(result, 0U, segoe ? 1U : 0U);
    put16(result, 2U, 3U);
    put16(result, 4U, 8U); put16(result, 6U, segoe ? 10U : 2U);
    put16(result, 8U, segoe ? 19U : 18U); put16(result, 10U, segoe ? 7U : 1U);
    put16(result, 12U, 65535U); put16(result, 14U, segoe ? 15U : 3U);
    return result;
}
bool empty(const sfnt_rasterization_range& value) {
    return value.version == 0U && value.pixels_per_em == 0U &&
        value.maximum_pixels_per_em == 0U && value.recommendations == font_rasterization_recommendations::none;
}
void rejected(std::span<const std::byte> table, std::uint16_t size = 1U) {
    sfnt_rasterization_range range{99U, 99U, 99U, font_rasterization_recommendations::grayscale};
    bool available = true;
    require(!try_read_sfnt_rasterization_recommendations(table, size, range, available));
    require(!available && empty(range));
}
bytes font_bytes(const bytes& table, bool duplicate = false, bool variable = false, bool outlines = true) {
    const std::size_t count = 1U + (outlines ? 2U : 0U) + (duplicate ? 1U : 0U) + (variable ? 1U : 0U);
    const auto payload = 12U + count * 16U;
    bytes result(payload + table.size());
    put32(result, 0U, 0x00010000U);
    put16(result, 4U, static_cast<std::uint16_t>(count));
    std::size_t record = 12U;
    const auto add = [&](open_type_tag tag, std::size_t length) {
        put32(result, record, tag.value);
        put32(result, record + 8U, static_cast<std::uint32_t>(payload));
        put32(result, record + 12U, static_cast<std::uint32_t>(length));
        record += 16U;
    };
    add(open_type_tag::from_chars('g', 'a', 's', 'p'), table.size());
    if (outlines) {
        add(open_type_tag::from_chars('g', 'l', 'y', 'f'), 0U);
        add(open_type_tag::from_chars('l', 'o', 'c', 'a'), 0U);
    }
    if (duplicate) add(open_type_tag::from_chars('g', 'a', 's', 'p'), table.size());
    if (variable) add(open_type_tag::from_chars('f', 'v', 'a', 'r'), 0U);
    std::copy(table.begin(), table.end(), result.begin() + static_cast<std::ptrdiff_t>(payload));
    return result;
}
void query(const bytes& data, bool success, bool expected_available, std::uint16_t expected_flags = 0U,
    std::uint32_t face = 0U) {
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(data, face, font));
    sfnt_rasterization_range range{99U, 99U, 99U, font_rasterization_recommendations::grayscale};
    bool available = true;
    font_error error = font_error::verification_failed;
    require(font.try_get_static_rasterization_recommendations(12U, range, available, &error) == success);
    require(error == (success ? font_error::none : font_error::invalid_face));
    require(available == expected_available);
    if (!available) require(empty(range));
    else require(static_cast<std::uint16_t>(range.recommendations) == expected_flags && range.pixels_per_em == 12U);
}
} // namespace

int main() {
    for (const bool segoe : {false, true}) {
        const auto table = original_table(segoe);
        for (unsigned size = 1U; size <= 65535U; ++size) {
            sfnt_rasterization_range range{};
            bool available = false;
            require(try_read_sfnt_rasterization_recommendations(table, static_cast<std::uint16_t>(size), range, available));
            const unsigned middle = segoe ? 19U : 18U;
            const unsigned maximum = size <= 8U ? 8U : size <= middle ? middle : 65535U;
            const unsigned flags = size <= 8U ? (segoe ? 10U : 2U) : size <= middle ? (segoe ? 7U : 1U) : (segoe ? 15U : 3U);
            require(available && range.version == (segoe ? 1U : 0U) && range.pixels_per_em == size &&
                range.maximum_pixels_per_em == maximum && static_cast<unsigned>(range.recommendations) == flags);
        }
        for (std::size_t length = 0U; length < table.size(); ++length) rejected(std::span(table).first(length));
        auto tail = table; tail.push_back(std::byte{}); rejected(tail);
        rejected(table, 0U);
    }
    for (const auto [offset, value] : std::array<std::pair<unsigned, std::uint16_t>, 7>{
        {{0U, 2U}, {2U, 2U}, {2U, 65535U}, {8U, 8U}, {12U, 18U}, {12U, 19U}, {14U, 16U}}}) {
        auto table = original_table(true); put16(table, offset, value); rejected(table);
    }
    auto symmetric = original_table(true); put16(symmetric, 6U, 12U);
    sfnt_rasterization_range range{};
    bool available = false;
    require(try_read_sfnt_rasterization_recommendations(symmetric, 1U, range, available));
    require(available && static_cast<unsigned>(range.recommendations) == 12U);
    put16(symmetric, 0U, 0U); rejected(symmetric);
    bytes finite(8U); put16(finite, 0U, 1U); put16(finite, 2U, 1U); put16(finite, 4U, 12U);
    require(try_read_sfnt_rasterization_recommendations(finite, 12U, range, available));
    require(available && range.recommendations == font_rasterization_recommendations::none);
    require(try_read_sfnt_rasterization_recommendations(finite, 13U, range, available));
    require(!available && empty(range));
    require(try_read_sfnt_rasterization_recommendations(bytes(4U), 12U, range, available));
    require(!available && empty(range));
    const auto table = original_table(true);
    query(font_bytes(table), true, true, 7U);
    query(font_bytes(table, true), false, false);
    query(font_bytes(bytes(3U)), false, false);
    auto invalid = font_bytes(table); put32(invalid, 20U, 0xFFFFFFFFU); query(invalid, false, false);
    query(font_bytes(table, false, true), true, false);
    query(font_bytes(bytes(3U), false, true), false, false);
    query(font_bytes(table, false, false, false), true, false);
    auto missing = font_bytes(table); put32(missing, 12U, 0U); query(missing, true, false);
    auto ignored_fvar = font_bytes(table, false, true); put32(ignored_fvar, 68U, 0xFFFFFFFFU);
    query(ignored_fvar, true, false);
    // Original TTC offsets are collection-relative, with independent face tables.
    const std::array faces{font_bytes(original_table(false)), font_bytes(table)};
    bytes collection(20U + faces[0].size() + faces[1].size());
    put32(collection, 0U, open_type_tag::from_chars('t', 't', 'c', 'f').value);
    put32(collection, 4U, 0x00010000U); put32(collection, 8U, 2U);
    std::size_t start = 20U;
    for (std::size_t face = 0U; face < 2U; ++face) {
        put32(collection, 12U + 4U * face, static_cast<std::uint32_t>(start));
        std::copy(faces[face].begin(), faces[face].end(), collection.begin() + static_cast<std::ptrdiff_t>(start));
        for (std::size_t record = 0U; record < 3U; ++record)
            put32(collection, start + 20U + record * 16U, static_cast<std::uint32_t>(start + 60U));
        start += faces[face].size();
    }
    query(collection, true, true, 1U, 0U); query(collection, true, true, 7U, 1U);
    sfnt_font_view font{};
    auto data = font_bytes(table);
    require(sfnt_font_view::try_create(data, 0U, font));
    font_error error{};
    require(!font.try_get_static_rasterization_recommendations(0U, range, available, &error));
    require(!available && empty(range) && error == font_error::invalid_argument);
    std::printf("gasp checks: %u passed\n", checks);
}
