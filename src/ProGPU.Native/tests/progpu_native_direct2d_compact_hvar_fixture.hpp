#pragma once
#include "progpu_native_direct2d_variable_font_fixture.hpp"

namespace progpu::native::direct2d::tests {
// Additional independently authored fonts; the original four-font corpus stays
// byte-identical. Only hhea/hmtx advance storage and HVAR advance rows/maps vary.
// The literals are original Windows SDK GetDesignGlyphAdvances observations,
// separately checked against original GetGlyphRunOutline displacement.
struct compact_hvar_control final {
    unsigned metric_count, map_kind;
    float weight;
    std::array<std::int32_t, 3U> advances;
};
inline constexpr std::array<compact_hvar_control, 45U> compact_hvar_controls{{
    {1U, 0U, 100.0F, {380, 380, 380}},
    {1U, 0U, 250.0F, {390, 390, 390}},
    {1U, 0U, 400.0F, {400, 400, 400}},
    {1U, 0U, 650.0F, {430, 430, 430}},
    {1U, 0U, 900.0F, {440, 440, 440}},
    {1U, 1U, 100.0F, {380, 380, 380}},
    {1U, 1U, 250.0F, {390, 390, 390}},
    {1U, 1U, 400.0F, {400, 400, 400}},
    {1U, 1U, 650.0F, {430, 430, 430}},
    {1U, 1U, 900.0F, {440, 440, 440}},
    {1U, 2U, 100.0F, {340, 340, 340}},
    {1U, 2U, 250.0F, {370, 370, 370}},
    {1U, 2U, 400.0F, {400, 400, 400}},
    {1U, 2U, 650.0F, {490, 490, 490}},
    {1U, 2U, 900.0F, {520, 520, 520}},
    {2U, 0U, 100.0F, {380, 460, 460}},
    {2U, 0U, 250.0F, {390, 480, 480}},
    {2U, 0U, 400.0F, {400, 500, 500}},
    {2U, 0U, 650.0F, {430, 560, 560}},
    {2U, 0U, 900.0F, {440, 580, 580}},
    {2U, 1U, 100.0F, {380, 460, 460}},
    {2U, 1U, 250.0F, {390, 480, 480}},
    {2U, 1U, 400.0F, {400, 500, 500}},
    {2U, 1U, 650.0F, {430, 560, 560}},
    {2U, 1U, 900.0F, {440, 580, 580}},
    {2U, 2U, 100.0F, {340, 480, 480}},
    {2U, 2U, 250.0F, {370, 490, 490}},
    {2U, 2U, 400.0F, {400, 500, 500}},
    {2U, 2U, 650.0F, {490, 530, 530}},
    {2U, 2U, 900.0F, {520, 540, 540}},
    {3U, 0U, 100.0F, {380, 460, 540}},
    {3U, 0U, 250.0F, {390, 480, 570}},
    {3U, 0U, 400.0F, {400, 500, 600}},
    {3U, 0U, 650.0F, {430, 560, 690}},
    {3U, 0U, 900.0F, {440, 580, 720}},
    {3U, 1U, 100.0F, {380, 460, 540}},
    {3U, 1U, 250.0F, {390, 480, 570}},
    {3U, 1U, 400.0F, {400, 500, 600}},
    {3U, 1U, 650.0F, {430, 560, 690}},
    {3U, 1U, 900.0F, {440, 580, 720}},
    {3U, 2U, 100.0F, {340, 480, 560}},
    {3U, 2U, 250.0F, {370, 490, 580}},
    {3U, 2U, 400.0F, {400, 500, 600}},
    {3U, 2U, 650.0F, {490, 530, 660}},
    {3U, 2U, 900.0F, {520, 540, 680}},
}};

inline std::vector<std::byte> make_compact_hvar_control(unsigned metric_count, unsigned map_kind)
{
    using namespace variable_font_wire;
    if (metric_count < 1U || metric_count > 3U || map_kind > 2U)
        throw std::invalid_argument("compact HVAR control inventory");
    const auto original = make_variable_font({true, false, false});
    std::vector<table> tables;
    for (unsigned index = 0U; index < read16(original, 4U); ++index) {
        const auto record = 12U + index * 16U;
        const auto offset = read32(original, record + 8U), length = read32(original, record + 12U);
        table item{read32(original, record), bytes(original.begin() + offset, original.begin() + offset + length)};
        if (item.tag == 0x68656164U) put32(item.data, 8U, 0U);
        if (item.tag == 0x68686561U) put16(item.data, 34U, static_cast<std::uint16_t>(metric_count));
        if (item.tag == 0x686D7478U) {
            item.data.assign(metric_count * 4U + (3U - metric_count) * 2U, std::byte{0});
            for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
                if (glyph < metric_count)
                    put16(item.data, glyph * 4U, static_cast<std::uint16_t>(400U + glyph * 100U));
                const auto bearing = glyph < metric_count ? glyph * 4U + 2U : metric_count * 4U + (glyph - metric_count) * 2U;
                put16(item.data, bearing, glyph == 0U ? 0U : 13U);
            }
        }
        if (item.tag == 0x48564152U) {
            // Distinct advance rows distinguish every stored metric and map.
            for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
                put16(item.data, 58U + glyph * 4U, static_cast<std::uint16_t>(40U + 40U * glyph));
                put16(item.data, 60U + glyph * 4U, static_cast<std::uint16_t>(-20 - 20 * static_cast<int>(glyph)));
            }
            if (map_kind != 0U) {
                const auto map_offset = item.data.size(); item.data.resize(map_offset + 7U);
                put32(item.data, 8U, static_cast<std::uint32_t>(map_offset));
                item.data[map_offset + 1U] = std::byte{3}; put16(item.data, map_offset + 2U, 3U);
                constexpr unsigned reordered[]{2U, 0U, 1U};
                for (unsigned glyph = 0U; glyph < 3U; ++glyph)
                    item.data[map_offset + 4U + glyph] = static_cast<std::byte>(map_kind == 1U ? glyph : reordered[glyph]);
            }
        }
        tables.push_back(std::move(item));
    }
    bytes result(12U + tables.size() * 16U);
    std::copy_n(original.begin(), 12U, result.begin());
    std::size_t head = 0U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto& item = tables[index];
        const auto offset = result.size(), record = 12U + index * 16U;
        put32(result, record, item.tag); put32(result, record + 4U, checksum(item.data));
        put32(result, record + 8U, static_cast<std::uint32_t>(offset));
        put32(result, record + 12U, static_cast<std::uint32_t>(item.data.size()));
        result.insert(result.end(), item.data.begin(), item.data.end());
        while (result.size() % 4U != 0U) result.push_back(std::byte{0});
        if (item.tag == 0x68656164U) head = offset;
    }
    put32(result, head + 8U, 0xB1B0AFBAU - checksum(result));
    return result;
}
} // namespace progpu::native::direct2d::tests
