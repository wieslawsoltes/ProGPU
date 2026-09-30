#pragma once

#include "progpu_native_hint_fault_fixture.hpp"

namespace progpu::native::tests {

// Original authored font: B initially maps to the instruction-fault glyph, but
// liga substitutes the valid glyph before capture. kern then positions that
// same retained descriptor. Unused later digit/comma mappings remain faulty.
// Public wire contracts only; no external font or implementation is copied.
inline std::vector<std::byte> make_hinted_fixture_font(bool include_layout) {
    using bytes = std::vector<std::byte>;
    const auto original = make_hint_fault_font();
    const auto read16 = [&](std::size_t at) {
        return static_cast<std::uint16_t>(
            (std::to_integer<std::uint32_t>(original[at]) << 8U) |
            std::to_integer<std::uint32_t>(original[at + 1U]));
    };
    const auto read32 = [&](std::size_t at) {
        return (static_cast<std::uint32_t>(read16(at)) << 16U) |
            static_cast<std::uint32_t>(read16(at + 2U));
    };
    const auto put16 = [](bytes& data, std::size_t at, std::uint16_t value) {
        data[at] = static_cast<std::byte>(value >> 8U);
        data[at + 1U] = static_cast<std::byte>(value);
    };
    const auto put32 = [&](bytes& data, std::size_t at, std::uint32_t value) {
        put16(data, at, static_cast<std::uint16_t>(value >> 16U));
        put16(data, at + 2U, static_cast<std::uint16_t>(value));
    };
    const auto checksum = [](std::span<const std::byte> data) {
        std::uint32_t sum = 0U;
        for (std::size_t at = 0U; at < data.size(); at += 4U) {
            std::uint32_t word = 0U;
            for (std::size_t lane = 0U; lane < 4U; ++lane) {
                word <<= 8U;
                if (at + lane < data.size()) word |=
                    std::to_integer<std::uint32_t>(data[at + lane]);
            }
            sum += word;
        }
        return sum;
    };
    struct table final { std::uint32_t tag; bytes data; };
    std::vector<table> tables{};
    tables.reserve(10U);
    for (std::size_t index = 0U; index < read16(4U); ++index) {
        const auto record = 12U + index * 16U;
        const auto offset = read32(record + 8U);
        const auto length = read32(record + 12U);
        table value{read32(record), bytes(original.begin() + static_cast<std::ptrdiff_t>(offset),
            original.begin() + static_cast<std::ptrdiff_t>(offset + length))};
        if (value.tag == 0x68656164U) put32(value.data, 8U, 0U);
        if (value.tag == 0x636D6170U) {
            // The capture-only fixture's Macintosh format zero is accepted by
            // FreeType, but native shaping needs a supported Unicode cmap.
            // Keep every authored source/auxiliary mapping and fault identity.
            struct segment final { std::uint16_t first, last, first_glyph; };
            constexpr std::array segments{
                segment{0x20U, 0x20U, 1U}, segment{0x2CU, 0x2CU, 2U},
                segment{0x2EU, 0x2EU, 1U}, segment{0x30U, 0x31U, 1U},
                segment{0x41U, 0x42U, 1U}, segment{0xFFFFU, 0xFFFFU, 0U}};
            value.data.assign(76U, std::byte{0});
            put16(value.data, 2U, 1U);
            put16(value.data, 4U, 3U); // Windows Unicode BMP
            put16(value.data, 6U, 1U);
            put32(value.data, 8U, 12U);
            put16(value.data, 12U, 4U);
            put16(value.data, 14U, 64U);
            put16(value.data, 18U, 12U); // six segments
            put16(value.data, 20U, 8U);
            put16(value.data, 22U, 2U);
            put16(value.data, 24U, 4U);
            for (std::size_t segment_index = 0U; segment_index < segments.size(); ++segment_index) {
                const auto& mapping = segments[segment_index];
                put16(value.data, 26U + segment_index * 2U, mapping.last);
                put16(value.data, 40U + segment_index * 2U, mapping.first);
                put16(value.data, 52U + segment_index * 2U, static_cast<std::uint16_t>(
                    static_cast<std::int32_t>(mapping.first_glyph) - mapping.first));
            }
        }
        tables.push_back(std::move(value));
    }
    const auto layout = [&](bool substitution) {
        bytes data(substitution ? 70U : 72U);
        put16(data, 0U, 1U);
        put16(data, 4U, 10U); // ScriptList
        put16(data, 6U, 30U); // FeatureList
        put16(data, 8U, 44U); // LookupList
        put16(data, 10U, 1U);
        put32(data, 12U, 0x6C61746EU); // latn
        put16(data, 16U, 8U);
        put16(data, 18U, 4U); // default LangSys
        put16(data, 24U, 0xFFFFU);
        put16(data, 26U, 1U); // one optional feature, index zero
        put16(data, 30U, 1U);
        put32(data, 32U, substitution ? 0x6C696761U : 0x6B65726EU);
        put16(data, 36U, 8U);
        put16(data, 40U, 1U); // one lookup, index zero
        put16(data, 44U, 1U);
        put16(data, 46U, 4U);
        put16(data, 48U, 1U); // SingleSubst / SinglePos
        put16(data, 52U, 1U);
        put16(data, 54U, 8U);
        if (substitution) {
            put16(data, 56U, 2U);
            put16(data, 58U, 8U);
            put16(data, 60U, 1U);
            put16(data, 62U, 1U); // substitute valid glyph one
            put16(data, 64U, 1U);
            put16(data, 66U, 1U);
            put16(data, 68U, 2U); // cover fault glyph two
        } else {
            put16(data, 56U, 1U);
            put16(data, 58U, 10U);
            put16(data, 60U, 5U); // xPlacement + xAdvance
            put16(data, 62U, 3U);
            put16(data, 64U, 12U);
            put16(data, 66U, 1U);
            put16(data, 68U, 1U);
            put16(data, 70U, 1U); // same final glyph one
        }
        return data;
    };
    if (include_layout) {
        tables.push_back({0x47535542U, layout(true)});
        tables.push_back({0x47504F53U, layout(false)});
    }
    std::sort(tables.begin(), tables.end(), [](const table& left, const table& right) {
        return left.tag < right.tag;
    });
    std::size_t length = 12U + tables.size() * 16U;
    for (const auto& value : tables) length += (value.data.size() + 3U) & ~std::size_t{3U};
    bytes result(length);
    put32(result, 0U, 0x00010000U);
    put16(result, 4U, static_cast<std::uint16_t>(tables.size()));
    put16(result, 6U, 128U);
    put16(result, 8U, 3U);
    put16(result, 10U, static_cast<std::uint16_t>(tables.size() * 16U - 128U));
    std::size_t cursor = 12U + tables.size() * 16U;
    std::size_t head_offset = 0U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto& value = tables[index];
        const auto record = 12U + index * 16U;
        put32(result, record, value.tag);
        put32(result, record + 4U, checksum(value.data));
        put32(result, record + 8U, static_cast<std::uint32_t>(cursor));
        put32(result, record + 12U, static_cast<std::uint32_t>(value.data.size()));
        std::copy(value.data.begin(), value.data.end(), result.begin() +
            static_cast<std::ptrdiff_t>(cursor));
        if (value.tag == 0x68656164U) head_offset = cursor;
        cursor += (value.data.size() + 3U) & ~std::size_t{3U};
    }
    put32(result, head_offset + 8U, 0xB1B0AFBAU - checksum(result));
    return result;
}

inline std::vector<std::byte> make_hinted_mapping_font() {
    return make_hinted_fixture_font(false);
}

inline std::vector<std::byte> make_hinted_shape_font() {
    return make_hinted_fixture_font(true);
}
} // namespace progpu::native::tests
