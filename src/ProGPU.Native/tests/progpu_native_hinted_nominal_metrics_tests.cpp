#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"

#include <array>
#include <algorithm>
#include <cstring>
#include <iostream>
#include <source_location>
#include <stdexcept>

// Original authored SFNT/TTC hmtx records, no font driver or GPU. This unit
// isolates exact metadata capture; original producer controls live separately
// in progpu_native_hinted_glyph_resource_transport_tests.cpp.
namespace {
using namespace progpu::native::text;
using bytes = std::vector<std::byte>;
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("nominal metric control at " + std::to_string(at.line()));
}
void put16(bytes& data, std::size_t at, std::uint16_t value) {
    data[at] = static_cast<std::byte>(value >> 8U); data[at + 1U] = static_cast<std::byte>(value);
}
void put32(bytes& data, std::size_t at, std::uint32_t value) {
    put16(data, at, static_cast<std::uint16_t>(value >> 16U)); put16(data, at + 2U, static_cast<std::uint16_t>(value));
}
bytes font(std::uint16_t first, std::uint16_t last, bool metrics = true) {
    bytes data(186U);
    put32(data, 0U, 0x00010000U); put16(data, 4U, 4U);
    constexpr std::array<std::uint32_t, 4U> tags{0x68656164U, 0x68686561U, 0x686D7478U, 0x6D617870U};
    constexpr std::array<std::uint32_t, 4U> offsets{132U, 76U, 112U, 124U}, lengths{54U, 36U, 10U, 6U};
    for (std::size_t i = 0U; i < tags.size(); ++i) {
        put32(data, 12U + i * 16U, tags[i]); put32(data, 20U + i * 16U, offsets[i]);
        put32(data, 24U + i * 16U, !metrics && i == 2U ? 0U : lengths[i]);
    }
    put16(data, 150U, 1000U); // Original UPM, never a replacement width.
    put16(data, 110U, 2U); // Glyph two shares the last long horizontal advance.
    put16(data, 112U, first); put16(data, 116U, last);
    put32(data, 124U, 0x00010000U); put16(data, 128U, 3U);
    return data;
}
bytes collection() {
    const std::array faces{font(401U, 613U), font(867U, 0U)};
    bytes data(20U + faces[0].size() + faces[1].size());
    put32(data, 0U, 0x74746366U); put32(data, 4U, 0x00010000U); put32(data, 8U, 2U);
    std::uint32_t base = 20U;
    for (std::size_t i = 0U; i < faces.size(); ++i) {
        put32(data, 12U + i * 4U, base);
        std::copy(faces[i].begin(), faces[i].end(), data.begin() + base);
        constexpr std::array<std::uint32_t, 4U> offsets{132U, 76U, 112U, 124U};
        for (std::size_t table = 0U; table < offsets.size(); ++table)
            put32(data, base + 20U + table * 16U, base + offsets[table]);
        base += static_cast<std::uint32_t>(faces[i].size());
    }
    return data;
}
void controls() {
    hinted_paragraph_generation paragraph;
    paragraph.font_sources.push_back(std::make_shared<const owned_font_source>(collection(), 1U));
    paragraph.font_sources.push_back(std::make_shared<const owned_font_source>(collection(), 0U));
    paragraph.runs.resize(2U); paragraph.runs[1U].font_index = 1U;
    paragraph.device_styles.resize(1U);
    paragraph.logical_font_indices = {0U, 1U, 0U, 1U};
    paragraph.glyphs.resize(4U); paragraph.positioned_owners.resize(4U);
    // Repeat logical glyph zero after reordered font/glyph two, retain zero
    // advances and never infer width from arbitrary positioned draw values.
    constexpr std::array<std::uint32_t, 4U> logical{1U, 0U, 2U, 0U}, ids{2U, 0U, 1U, 0U};
    for (std::size_t i = 0U; i < ids.size(); ++i) {
        paragraph.glyphs[i].glyph_index = logical[i]; paragraph.glyphs[i].glyph_id = ids[i];
        paragraph.glyphs[i].advance_x = 17.25F + static_cast<float>(i);
        paragraph.positioned_owners[i].run_index = paragraph.logical_font_indices[logical[i]];
    }
    std::vector<progpu_native_hinted_glyph_nominal_metrics> output{{999U, 998U, 997U, 996U}};
    require(capture_hinted_nominal_metrics(paragraph, output) == PROGPU_NATIVE_STATUS_SUCCESS && output.size() == 4U);
    constexpr std::array<std::uint32_t, 4U> expected{613U, 867U, 0U, 867U};
    for (std::size_t i = 0U; i < output.size(); ++i)
        require(output[i].positioned_index == i && output[i].glyph_id == ids[i] &&
            output[i].font_index == paragraph.logical_font_indices[logical[i]] && output[i].advance_width_design_units == expected[i]);
    const auto before = output;
    const auto rejected = [&](progpu_native_status status) {
        require(capture_hinted_nominal_metrics(paragraph, output) == status && output.size() == before.size() &&
            std::memcmp(output.data(), before.data(), output.size() * sizeof(output[0])) == 0);
    };
    paragraph.font_sources[0U] = std::make_shared<const owned_font_source>(font(401U, 613U, false), 0U);
    rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); // A later selected face fails after an earlier valid record.
    paragraph.font_sources[0U] = std::make_shared<const owned_font_source>(collection(), 1U);
    paragraph.glyphs.back().glyph_id = 3U; rejected(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); paragraph.glyphs.back().glyph_id = 0U;
    paragraph.normalized_coordinates = {0}; rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); paragraph.normalized_coordinates.clear();
    paragraph.device_styles[0U].variation_coordinates_16_16 = {0}; rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED);
    paragraph.device_styles[0U].variation_coordinates_16_16.clear();
    paragraph.font_sources[0U] = std::make_shared<const owned_font_source>(collection(), 2U);
    rejected(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
}
} // namespace
int main() {
    try { controls(); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
