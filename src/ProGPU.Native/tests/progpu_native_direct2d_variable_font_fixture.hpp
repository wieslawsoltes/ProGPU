#pragma once

#include "progpu_native_hint_fault_fixture.hpp"
#include <stdexcept>
#include <string_view>

namespace progpu::native::direct2d::tests {

struct variable_font_options final {
    bool hvar = false;
    bool side_bearing_maps = false;
    bool compact_metrics = false;
};

struct variable_font_case final { float weight; float scalar; };
inline constexpr std::array<variable_font_case, 5U> variable_font_cases{{
    {400, 0}, {650, 0.75F}, {900, 1}, {250, -0.5F}, {100, -1}}};
// Three binary32 ULPs above 100: not representable by a signed 16.16 input.
// Retain this source value, rather than manufacturing a rounded font instance.
inline constexpr float variable_precision_weight = 100.00002288818359375F;

struct variable_glyph_expectation final {
    float x_min, y_min, x_max, y_max, horizontal_origin, advance;
    bool has_ink;
};

inline variable_glyph_expectation expected_variable_glyph(std::uint16_t glyph, std::size_t case_index)
{
    // Independent authored design-space observations, not a font decode or
    // product variation calculation. Entries are glyph0(empty), glyph1, glyph2.
    // The positive tuple expands width by64, glyph1 height by32 and glyph2 by32.
    // Its phantom advances are +80/+80/+48; negative tuple deltas are -half.
    static constexpr std::array<std::array<variable_glyph_expectation, 3U>, 5U> expected{{
        {{{0,0,0,0,0,500,false}, {13,13,313,413,0,500,true}, {13,13,313,413,0,500,true}}},
        {{{0,0,0,0,6,560,false}, {25,19,373,443,6,560,true}, {1,7,349,431,-6,536,true}}},
        {{{0,0,0,0,8,580,false}, {29,21,393,453,8,580,true}, {-3,5,361,437,-8,548,true}}},
        {{{0,0,0,0,-2,480,false}, {9,11,293,403,-2,480,true}, {17,15,301,407,2,488,true}}},
        {{{0,0,0,0,-4,460,false}, {5,9,273,393,-4,460,true}, {21,17,289,401,4,476,true}}}
    }};
    return expected.at(case_index).at(glyph);
}

namespace variable_font_wire {
using bytes = std::vector<std::byte>;
inline void put16(bytes& data, std::size_t at, std::uint16_t value)
{
    data.at(at) = static_cast<std::byte>(value >> 8U);
    data.at(at + 1U) = static_cast<std::byte>(value & 255U);
}
inline void put32(bytes& data, std::size_t at, std::uint32_t value)
{
    put16(data, at, static_cast<std::uint16_t>(value >> 16U));
    put16(data, at + 2U, static_cast<std::uint16_t>(value));
}
inline std::uint16_t read16(const bytes& data, std::size_t at)
{
    return static_cast<std::uint16_t>((std::to_integer<std::uint32_t>(data.at(at)) << 8U) |
        std::to_integer<std::uint32_t>(data.at(at + 1U)));
}
inline std::uint32_t read32(const bytes& data, std::size_t at)
{
    return (static_cast<std::uint32_t>(read16(data, at)) << 16U) | read16(data, at + 2U);
}
inline std::uint32_t checksum(std::span<const std::byte> data)
{
    std::uint32_t sum = 0U;
    for (std::size_t at = 0U; at < data.size(); at += 4U) {
        std::uint32_t word = 0U;
        for (std::size_t lane = 0U; lane < 4U; ++lane) {
            word <<= 8U;
            if (at + lane < data.size()) word |= std::to_integer<std::uint32_t>(data[at + lane]);
        }
        sum += word;
    }
    return sum;
}
struct table final { std::uint32_t tag; bytes data; };

inline bytes names()
{
    struct name final { std::uint16_t id; std::u16string_view text; };
    constexpr std::array records{
        name{1, u"ProGPU Variable Fixture"}, name{2, u"Regular"},
        name{4, u"ProGPU Variable Fixture Regular"}, name{6, u"ProGPUVariableFixture-Regular"},
        name{16, u"ProGPU Variable Fixture"}, name{17, u"Regular"}, name{256, u"Weight"}};
    const auto strings = 6U + records.size() * 12U;
    bytes result(strings);
    put16(result, 2U, static_cast<std::uint16_t>(records.size()));
    put16(result, 4U, static_cast<std::uint16_t>(strings));
    for (std::size_t index = 0U; index < records.size(); ++index) {
        const auto at = 6U + index * 12U;
        put16(result, at, 3U); put16(result, at + 2U, 1U); put16(result, at + 4U, 0x0409U);
        put16(result, at + 6U, records[index].id);
        put16(result, at + 8U, static_cast<std::uint16_t>(records[index].text.size() * 2U));
        put16(result, at + 10U, static_cast<std::uint16_t>(result.size() - strings));
        for (const auto character : records[index].text) {
            const auto offset = result.size(); result.resize(offset + 2U);
            put16(result, offset, static_cast<std::uint16_t>(character));
        }
    }
    return result;
}

inline bytes glyph_variations(unsigned glyph)
{
    constexpr std::array<std::array<std::int16_t, 8U>, 3U> x{{
        {{8,88,0,0,0,0,0,0}}, {{16,80,80,16,8,88,0,0}}, {{-16,48,48,-16,-8,40,0,0}}}};
    constexpr std::array<std::array<std::int16_t, 8U>, 3U> y{{
        {{0,0,0,0,0,0,0,0}}, {{8,8,40,40,0,0,0,0}}, {{-8,-8,24,24,0,0,0,0}}}};
    const std::size_t count = glyph == 0U ? 4U : 8U;
    // Two embedded peak tuples, each with its own all-points marker. The four
    // phantom points follow contour points even for the empty glyph.
    bytes result(16U);
    put16(result, 0U, 2U); put16(result, 2U, 16U);
    for (unsigned tuple = 0U; tuple < 2U; ++tuple) {
        const auto header = 4U + tuple * 6U;
        const auto start = result.size(); result.push_back(std::byte{0});
        for (const auto* coordinates : {&x[glyph], &y[glyph]}) {
            result.push_back(static_cast<std::byte>(count - 1U)); // int8 run, including explicit zeros.
            for (std::size_t point = 0U; point < count; ++point) {
                const auto delta = tuple == 0U ? (*coordinates)[point] : -(*coordinates)[point] / 2;
                result.push_back(static_cast<std::byte>(static_cast<std::uint8_t>(delta)));
            }
        }
        put16(result, header, static_cast<std::uint16_t>(result.size() - start));
        put16(result, header + 2U, 0xA000U); // embedded peak + private point numbers
        put16(result, header + 4U, tuple == 0U ? 0x4000U : 0xC000U);
    }
    return result;
}

inline bytes horizontal_variations(bool side_bearings)
{
    // One store, two regions (+1 and -1), and independent rows for advances,
    // left bearings and right bearings. Optional maps use explicit row indices.
    constexpr std::array<std::array<std::int16_t, 2U>, 9U> deltas{{
        {{80,-40}}, {{80,-40}}, {{48,-24}},
        {{-8,4}}, {{8,-4}}, {{-8,4}},
        {{88,-44}}, {{8,-4}}, {{-8,4}}}};
    const std::uint16_t count = side_bearings ? 9U : 3U;
    bytes result(20U + 28U + 10U + static_cast<std::size_t>(count) * 4U);
    put16(result, 0U, 1U); put32(result, 4U, 20U);
    put16(result, 20U, 1U); put32(result, 22U, 12U); put16(result, 26U, 1U); put32(result, 28U, 28U);
    put16(result, 32U, 1U); put16(result, 34U, 2U);
    put16(result, 36U, 0U); put16(result, 38U, 0x4000U); put16(result, 40U, 0x4000U);
    put16(result, 42U, 0xC000U); put16(result, 44U, 0xC000U); put16(result, 46U, 0U);
    put16(result, 48U, count); put16(result, 50U, 2U); put16(result, 52U, 2U);
    put16(result, 54U, 0U); put16(result, 56U, 1U);
    for (std::size_t index = 0U; index < count; ++index) {
        put16(result, 58U + index * 4U, static_cast<std::uint16_t>(deltas[index][0]));
        put16(result, 60U + index * 4U, static_cast<std::uint16_t>(deltas[index][1]));
    }
    if (side_bearings) {
        for (unsigned map = 0U; map < 2U; ++map) {
            const auto at = result.size(); result.resize(at + 7U);
            put32(result, 12U + map * 4U, static_cast<std::uint32_t>(at));
            result[at + 1U] = std::byte{3}; // one-byte entry, four inner-index bits
            put16(result, at + 2U, 3U);
            for (unsigned glyph = 0U; glyph < 3U; ++glyph)
                result[at + 4U + glyph] = static_cast<std::byte>(3U + map * 3U + glyph);
        }
    }
    return result;
}
} // namespace variable_font_wire

// Original ProGPU byte construction from public OpenType table formats only:
// https://learn.microsoft.com/en-us/typography/opentype/spec/otvaroverview
// and its fvar/avar/gvar/HVAR/STAT/common-format links. No foreign font data,
// builder implementation or product decoder supplies these bytes/expectations.
// Fixed tiny inventories: O(F) time/storage in emitted font bytes.
inline std::vector<std::byte> make_variable_font(variable_font_options options = {})
{
    using namespace variable_font_wire;
    if (options.side_bearing_maps && !options.hvar)
        throw std::invalid_argument("side-bearing maps require the authored HVAR alternative");
    // Do not change the established original fixture. Variable TrueType default
    // LSBs equal xMin, as required by the OpenType variation contract.
    const auto original = progpu::native::tests::make_hint_fault_font(13, 13, options.compact_metrics);
    std::vector<table> tables;
    for (std::size_t index = 0U; index < read16(original, 4U); ++index) {
        const auto record = 12U + index * 16U;
        const auto offset = read32(original, record + 8U), length = read32(original, record + 12U);
        table value{read32(original, record), bytes(original.begin() + static_cast<std::ptrdiff_t>(offset),
            original.begin() + static_cast<std::ptrdiff_t>(offset + length))};
        if (value.tag == 0x68656164U) put32(value.data, 8U, 0U);
        if (value.tag == 0x636D6170U) {
            // Windows Unicode format4: A/B map to glyph1/2; final sentinel maps0.
            value.data.assign(44U, std::byte{0});
            put16(value.data, 2U, 1U); put16(value.data, 4U, 3U); put16(value.data, 6U, 1U);
            put32(value.data, 8U, 12U); put16(value.data, 12U, 4U); put16(value.data, 14U, 32U);
            put16(value.data, 18U, 4U); put16(value.data, 20U, 4U); put16(value.data, 22U, 1U);
            put16(value.data, 26U, 66U); put16(value.data, 28U, 0xFFFFU);
            put16(value.data, 32U, 65U); put16(value.data, 34U, 0xFFFFU);
            put16(value.data, 36U, static_cast<std::uint16_t>(1 - 65)); put16(value.data, 38U, 1U);
        }
        tables.push_back(std::move(value));
    }
    bytes fvar(36U);
    put16(fvar, 0U, 1U); put16(fvar, 4U, 16U); put16(fvar, 6U, 2U);
    put16(fvar, 8U, 1U); put16(fvar, 10U, 20U); put16(fvar, 14U, 8U);
    put32(fvar, 16U, 0x77676874U); put32(fvar, 20U, 100U << 16U);
    put32(fvar, 24U, 400U << 16U); put32(fvar, 28U, 900U << 16U); put16(fvar, 34U, 256U);
    tables.push_back({0x66766172U, std::move(fvar)});
    bytes avar(26U);
    put16(avar, 0U, 1U); put16(avar, 6U, 1U); put16(avar, 8U, 4U);
    put16(avar, 10U, 0xC000U); put16(avar, 12U, 0xC000U);
    put16(avar, 18U, 0x2000U); put16(avar, 20U, 0x3000U);
    put16(avar, 22U, 0x4000U); put16(avar, 24U, 0x4000U);
    tables.push_back({0x61766172U, std::move(avar)});
    bytes gvar(36U);
    put16(gvar, 0U, 1U); put16(gvar, 4U, 1U); put32(gvar, 8U, 36U);
    put16(gvar, 12U, 3U); put16(gvar, 14U, 1U); put32(gvar, 16U, 36U);
    for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
        put32(gvar, 20U + glyph * 4U, static_cast<std::uint32_t>(gvar.size() - 36U));
        const auto data = glyph_variations(glyph); gvar.insert(gvar.end(), data.begin(), data.end());
    }
    put32(gvar, 32U, static_cast<std::uint32_t>(gvar.size() - 36U));
    tables.push_back({0x67766172U, std::move(gvar)});
    if (options.hvar) tables.push_back({0x48564152U, horizontal_variations(options.side_bearing_maps)});
    tables.push_back({0x6E616D65U, names()});
    bytes stat(42U);
    put16(stat, 0U, 1U); put16(stat, 2U, 1U); put16(stat, 4U, 8U); put16(stat, 6U, 1U);
    put32(stat, 8U, 20U); put16(stat, 12U, 1U); put32(stat, 14U, 28U); put16(stat, 18U, 2U);
    put32(stat, 20U, 0x77676874U); put16(stat, 24U, 256U); put16(stat, 28U, 2U);
    put16(stat, 30U, 1U); put16(stat, 34U, 2U); put16(stat, 36U, 2U); put32(stat, 38U, 400U << 16U);
    tables.push_back({0x53544154U, std::move(stat)});
    bytes os2(96U);
    put16(os2, 0U, 4U); put16(os2, 2U, 500U); put16(os2, 4U, 400U); put16(os2, 6U, 5U);
    put32(os2, 42U, 1U); put32(os2, 58U, 0x50475055U); put16(os2, 62U, 0x40U);
    put16(os2, 64U, 65U); put16(os2, 66U, 66U); put16(os2, 68U, 800U);
    put16(os2, 70U, static_cast<std::uint16_t>(-200)); put16(os2, 74U, 800U); put16(os2, 76U, 200U);
    put32(os2, 78U, 1U); put16(os2, 86U, 413U); put16(os2, 88U, 413U);
    put16(os2, 92U, 32U); put16(os2, 94U, 1U);
    tables.push_back({0x4F532F32U, std::move(os2)});
    std::sort(tables.begin(), tables.end(), [](const table& first, const table& second) { return first.tag < second.tag; });
    std::size_t length = 12U + tables.size() * 16U;
    for (const auto& value : tables) length += (value.data.size() + 3U) & ~std::size_t{3U};
    bytes result(length);
    put32(result, 0U, 0x00010000U); put16(result, 4U, static_cast<std::uint16_t>(tables.size()));
    // This authored inventory has fourteen or fifteen tables.
    put16(result, 6U, 128U); put16(result, 8U, 3U);
    put16(result, 10U, static_cast<std::uint16_t>(tables.size() * 16U - 128U));
    std::size_t cursor = 12U + tables.size() * 16U, head_offset = 0U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto& value = tables[index]; const auto record = 12U + index * 16U;
        put32(result, record, value.tag); put32(result, record + 4U, checksum(value.data));
        put32(result, record + 8U, static_cast<std::uint32_t>(cursor));
        put32(result, record + 12U, static_cast<std::uint32_t>(value.data.size()));
        std::copy(value.data.begin(), value.data.end(), result.begin() + static_cast<std::ptrdiff_t>(cursor));
        if (value.tag == 0x68656164U) head_offset = cursor;
        cursor += (value.data.size() + 3U) & ~std::size_t{3U};
    }
    put32(result, head_offset + 8U, 0xB1B0AFBAU - checksum(result));
    return result;
}
} // namespace progpu::native::direct2d::tests
