#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <span>

// Direct native port provenance: ProGPU-owned
// OpenTypeTextShaper.GetFeatureTags/AddRawFeatureTags at repository checkpoint
// 064260fe. Tables remain borrowed and output ownership stays with the caller.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

constexpr auto gsub_tag = open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr auto gpos_tag = open_type_tag::from_chars('G', 'P', 'O', 'S');

template<typename Visitor>
void visit_feature_tags(
    const sfnt_font_view& font,
    Visitor&& visitor) noexcept {
    constexpr open_type_tag tables[]{gsub_tag, gpos_tag};
    std::uint32_t ordinal = 0U;
    for (const auto table_tag : tables) {
        sfnt_table_view table{};
        if (!font.try_get_table(table_tag, table) ||
            !can_read(table.bytes, 6U, 2U)) {
            continue;
        }
        const auto feature_list = read_u16(table.bytes, 6U);
        if (!can_read(table.bytes, feature_list, 2U)) continue;
        const auto count = read_u16(table.bytes, feature_list);
        for (std::uint16_t index = 0U; index < count; ++index) {
            const auto record = static_cast<std::size_t>(feature_list) + 2U +
                static_cast<std::size_t>(index) * 6U;
            if (!can_read(table.bytes, record, 6U)) break;
            visitor(open_type_tag{read_u32(table.bytes, record)}, ordinal++);
        }
    }
}

bool appeared_before(
    const sfnt_font_view& font,
    open_type_tag tag,
    std::uint32_t ordinal) noexcept {
    bool found = false;
    visit_feature_tags(
        font,
        [&](open_type_tag candidate, std::uint32_t candidate_ordinal) {
            found |= candidate_ordinal < ordinal && candidate == tag;
        });
    return found;
}

} // namespace

bool try_parse_open_type_tag(
    std::string_view value,
    open_type_tag& result) noexcept {
    result = {};
    if (value.size() != 4U) return false;
    std::uint32_t packed = 0U;
    for (const char character : value) {
        const auto byte = static_cast<unsigned char>(character);
        if (byte < 0x20U || byte > 0x7EU) return false;
        packed = (packed << 8U) | static_cast<std::uint32_t>(byte);
    }
    result = open_type_tag{packed};
    return true;
}

bool try_write_open_type_tag(
    open_type_tag value,
    std::span<char> output) noexcept {
    if (output.size() < 4U) return false;
    output[0U] = static_cast<char>((value.value >> 24U) & 0xFFU);
    output[1U] = static_cast<char>((value.value >> 16U) & 0xFFU);
    output[2U] = static_cast<char>((value.value >> 8U) & 0xFFU);
    output[3U] = static_cast<char>(value.value & 0xFFU);
    return true;
}

bool try_get_open_type_feature_tag_requirements(
    const sfnt_font_view& font,
    open_type_feature_tag_requirements& result,
    font_error* error) noexcept {
    result = {};
    visit_feature_tags(
        font,
        [&](open_type_tag tag, std::uint32_t ordinal) {
            if (!appeared_before(font, tag, ordinal)) {
                ++result.tag_capacity;
            }
        });
    if (error != nullptr) *error = font_error::none;
    return true;
}

bool try_decode_open_type_feature_tags(
    const sfnt_font_view& font,
    std::span<open_type_tag> output,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    open_type_feature_tag_requirements requirements{};
    if (!try_get_open_type_feature_tag_requirements(
            font, requirements, error)) {
        return false;
    }
    if (output.size() < requirements.tag_capacity) {
        if (error != nullptr) *error = font_error::insufficient_buffer;
        return false;
    }
    visit_feature_tags(
        font,
        [&](open_type_tag tag, std::uint32_t) {
            const auto populated = output.first(written);
            if (std::find(populated.begin(), populated.end(), tag) ==
                populated.end()) {
                output[written++] = tag;
            }
        });
    std::sort(
        output.begin(),
        output.begin() + static_cast<std::ptrdiff_t>(written),
        [](open_type_tag left, open_type_tag right) {
            return left.value < right.value;
        });
    if (error != nullptr) *error = font_error::none;
    return true;
}

} // namespace progpu::native::text
