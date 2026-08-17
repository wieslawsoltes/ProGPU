#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Exact allocation-free port of ProGPU-owned CreateShapingPlan route
// selection, ResolveDirection, ResolveLayoutScript, IsIndicShaperScript, and
// UsesArabicJoiningScript from OpenTypeTextShaper.cs at checkpoint 2a47eec0.
// This unit reads only the font's borrowed GSUB ScriptList and owns no feature
// or shaping state.

namespace progpu::native::text {
namespace {

constexpr auto tag(char a, char b, char c, char d) noexcept {
    return open_type_tag::from_chars(a, b, c, d);
}

constexpr open_type_tag gsub_tag = tag('G', 'S', 'U', 'B');
constexpr open_type_tag gpos_tag = tag('G', 'P', 'O', 'S');

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool can_read(
    std::span<const std::byte> bytes,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= bytes.size() && length <= bytes.size() - offset;
}

std::uint16_t read_u16(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(bytes[offset]) << 8U) |
        std::to_integer<std::uint16_t>(bytes[offset + 1U]));
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(read_u16(bytes, offset)) << 16U) |
        read_u16(bytes, offset + 2U);
}

bool has_open_type_script(
    const sfnt_font_view& font,
    open_type_tag requested) noexcept {
    sfnt_table_view table{};
    if (!font.try_get_table(gsub_tag, table) ||
        !can_read(table.bytes, 4U, 2U)) {
        return false;
    }
    const std::size_t script_list = read_u16(table.bytes, 4U);
    if (!can_read(table.bytes, script_list, 2U)) return false;
    const std::uint16_t count = read_u16(table.bytes, script_list);
    for (std::uint16_t index = 0U; index < count; ++index) {
        const std::size_t record = script_list + 2U + index * 6U;
        if (!can_read(table.bytes, record, 6U)) break;
        if (read_u32(table.bytes, record) == requested.value) return true;
    }
    return false;
}

bool has_open_type_feature(
    const sfnt_font_view& font,
    open_type_tag requested) noexcept {
    constexpr std::array tables{gsub_tag, gpos_tag};
    for (const auto table_tag : tables) {
        sfnt_table_view table{};
        if (!font.try_get_table(table_tag, table) ||
            !can_read(table.bytes, 6U, 2U)) continue;
        const std::size_t feature_list = read_u16(table.bytes, 6U);
        if (!can_read(table.bytes, feature_list, 2U)) continue;
        const std::uint16_t count = read_u16(table.bytes, feature_list);
        for (std::uint16_t index = 0U; index < count; ++index) {
            const std::size_t record = feature_list + 2U + index * 6U;
            if (!can_read(table.bytes, record, 6U)) break;
            if (read_u32(table.bytes, record) == requested.value) return true;
        }
    }
    return false;
}

char normalized_language_character(char value) noexcept {
    if (value == '_') return '-';
    if (value >= 'A' && value <= 'Z') {
        return static_cast<char>(value + ('a' - 'A'));
    }
    return value;
}

bool language_equals(std::string_view value, std::string_view expected) noexcept {
    if (value.size() != expected.size()) return false;
    for (std::size_t index = 0U; index < value.size(); ++index) {
        if (normalized_language_character(value[index]) != expected[index]) {
            return false;
        }
    }
    return true;
}

struct script_generation final {
    open_type_tag unicode{};
    open_type_tag layout{};
};

constexpr std::array third_generation{
    script_generation{tag('b', 'e', 'n', 'g'), tag('b', 'n', 'g', '3')},
    script_generation{tag('d', 'e', 'v', 'a'), tag('d', 'e', 'v', '3')},
    script_generation{tag('g', 'u', 'j', 'r'), tag('g', 'j', 'r', '3')},
    script_generation{tag('g', 'u', 'r', 'u'), tag('g', 'u', 'r', '3')},
    script_generation{tag('k', 'n', 'd', 'a'), tag('k', 'n', 'd', '3')},
    script_generation{tag('m', 'l', 'y', 'm'), tag('m', 'l', 'm', '3')},
    script_generation{tag('o', 'r', 'y', 'a'), tag('o', 'r', 'y', '3')},
    script_generation{tag('t', 'a', 'm', 'l'), tag('t', 'm', 'l', '3')},
    script_generation{tag('t', 'e', 'l', 'u'), tag('t', 'e', 'l', '3')}};

constexpr std::array second_generation{
    script_generation{tag('b', 'e', 'n', 'g'), tag('b', 'n', 'g', '2')},
    script_generation{tag('d', 'e', 'v', 'a'), tag('d', 'e', 'v', '2')},
    script_generation{tag('g', 'u', 'j', 'r'), tag('g', 'j', 'r', '2')},
    script_generation{tag('g', 'u', 'r', 'u'), tag('g', 'u', 'r', '2')},
    script_generation{tag('k', 'n', 'd', 'a'), tag('k', 'n', 'd', '2')},
    script_generation{tag('m', 'l', 'y', 'm'), tag('m', 'l', 'm', '2')},
    script_generation{tag('m', 'y', 'm', 'r'), tag('m', 'y', 'm', '2')},
    script_generation{tag('o', 'r', 'y', 'a'), tag('o', 'r', 'y', '2')},
    script_generation{tag('t', 'a', 'm', 'l'), tag('t', 'm', 'l', '2')},
    script_generation{tag('t', 'e', 'l', 'u'), tag('t', 'e', 'l', '2')}};

template <std::size_t Size>
open_type_tag generation_tag(
    open_type_tag script,
    const std::array<script_generation, Size>& generations) noexcept {
    const auto found = std::find_if(
        generations.begin(),
        generations.end(),
        [script](const script_generation& candidate) {
            return candidate.unicode == script;
        });
    return found == generations.end() ? open_type_tag{} : found->layout;
}

bool is_indic_script(open_type_tag script) noexcept {
    constexpr std::array scripts{
        tag('b', 'e', 'n', 'g'), tag('d', 'e', 'v', 'a'),
        tag('g', 'u', 'j', 'r'), tag('g', 'u', 'r', 'u'),
        tag('k', 'n', 'd', 'a'), tag('m', 'l', 'y', 'm'),
        tag('o', 'r', 'y', 'a'), tag('t', 'a', 'm', 'l'),
        tag('t', 'e', 'l', 'u')};
    return std::find(scripts.begin(), scripts.end(), script) != scripts.end();
}

bool is_use_script(open_type_tag script) noexcept {
    constexpr std::array scripts{
        tag('t', 'i', 'b', 't'), tag('m', 'o', 'n', 'g'),
        tag('s', 'i', 'n', 'h'), tag('j', 'a', 'v', 'a'),
        tag('m', 'a', 'r', 'c'), tag('l', 'i', 'm', 'b'),
        tag('t', 'a', 'l', 'e'), tag('b', 'u', 'g', 'i'),
        tag('k', 'h', 'a', 'r'), tag('s', 'y', 'l', 'o'),
        tag('t', 'f', 'n', 'g'), tag('b', 'a', 'l', 'i'),
        tag('n', 'k', 'o', 'o'), tag('p', 'h', 'a', 'g'),
        tag('c', 'h', 'a', 'm'), tag('k', 'a', 'l', 'i'),
        tag('l', 'e', 'p', 'c'), tag('r', 'j', 'n', 'g'),
        tag('s', 'a', 'u', 'r'), tag('s', 'u', 'n', 'd'),
        tag('e', 'g', 'y', 'p'), tag('k', 't', 'h', 'i'),
        tag('m', 't', 'e', 'i'), tag('l', 'a', 'n', 'a'),
        tag('t', 'a', 'v', 't'), tag('b', 'a', 't', 'k'),
        tag('b', 'r', 'a', 'h'), tag('m', 'a', 'n', 'd'),
        tag('c', 'a', 'k', 'm'), tag('p', 'l', 'r', 'd'),
        tag('s', 'h', 'r', 'd'), tag('t', 'a', 'k', 'r'),
        tag('d', 'u', 'p', 'l'), tag('g', 'r', 'a', 'n'),
        tag('k', 'h', 'o', 'j'), tag('s', 'i', 'n', 'd'),
        tag('m', 'a', 'h', 'j'), tag('m', 'a', 'n', 'i'),
        tag('m', 'o', 'd', 'i'), tag('h', 'm', 'n', 'g'),
        tag('p', 'h', 'l', 'p'), tag('s', 'i', 'd', 'd'),
        tag('t', 'i', 'r', 'h'), tag('a', 'h', 'o', 'm'),
        tag('m', 'u', 'l', 't'), tag('a', 'd', 'l', 'm'),
        tag('b', 'h', 'k', 's'), tag('n', 'e', 'w', 'a'),
        tag('g', 'o', 'n', 'm'), tag('s', 'o', 'y', 'o'),
        tag('z', 'a', 'n', 'b'), tag('d', 'o', 'g', 'r'),
        tag('g', 'o', 'n', 'g'), tag('r', 'o', 'h', 'g'),
        tag('m', 'a', 'k', 'a'), tag('m', 'e', 'd', 'f'),
        tag('s', 'o', 'g', 'o'), tag('s', 'o', 'g', 'd'),
        tag('e', 'l', 'y', 'm'), tag('n', 'a', 'n', 'd'),
        tag('h', 'm', 'n', 'p'), tag('w', 'c', 'h', 'o'),
        tag('c', 'h', 'r', 's'), tag('d', 'i', 'a', 'k'),
        tag('k', 'i', 't', 's'), tag('y', 'e', 'z', 'i'),
        tag('c', 'p', 'm', 'n'), tag('o', 'u', 'g', 'r'),
        tag('t', 'n', 's', 'a'), tag('t', 'o', 't', 'o'),
        tag('v', 'i', 't', 'h'), tag('k', 'a', 'w', 'i'),
        tag('n', 'a', 'g', 'm')};
    return std::find(scripts.begin(), scripts.end(), script) != scripts.end();
}

bool is_arabic_joining_script(open_type_tag script) noexcept {
    constexpr std::array scripts{
        tag('a', 'd', 'l', 'm'), tag('a', 'r', 'a', 'b'),
        tag('c', 'h', 'r', 's'), tag('r', 'o', 'h', 'g'),
        tag('m', 'a', 'n', 'd'), tag('m', 'a', 'n', 'i'),
        tag('m', 'o', 'n', 'g'), tag('n', 'k', 'o', 'o'),
        tag('o', 'u', 'g', 'r'), tag('p', 'h', 'a', 'g'),
        tag('p', 'h', 'l', 'p'), tag('s', 'o', 'g', 'd'),
        tag('s', 'y', 'r', 'c')};
    return std::find(scripts.begin(), scripts.end(), script) != scripts.end();
}

shaping_direction resolve_direction(
    shaping_direction requested,
    open_type_tag script) noexcept {
    if (requested != shaping_direction::unspecified) return requested;
    constexpr std::array rtl_scripts{
        tag('a', 'r', 'a', 'b'), tag('h', 'e', 'b', 'r'),
        tag('s', 'y', 'r', 'c'), tag('t', 'h', 'a', 'a'),
        tag('n', 'k', 'o', 'o'), tag('a', 'd', 'l', 'm'),
        tag('r', 'o', 'h', 'g')};
    return std::find(rtl_scripts.begin(), rtl_scripts.end(), script) !=
            rtl_scripts.end()
        ? shaping_direction::right_to_left
        : shaping_direction::left_to_right;
}

} // namespace

open_type_tag resolve_open_type_language_tag(std::string_view language) noexcept {
    if (language_equals(language, "az") ||
        language_equals(language, "az-latn")) return tag('A', 'Z', 'E', ' ');
    if (language_equals(language, "de")) return tag('D', 'E', 'U', ' ');
    if (language_equals(language, "dv")) return tag('D', 'H', 'V', ' ');
    if (language_equals(language, "fa")) return tag('F', 'A', 'R', ' ');
    if (language_equals(language, "ja")) return tag('J', 'A', 'N', ' ');
    if (language_equals(language, "nl")) return tag('N', 'L', 'D', ' ');
    if (language_equals(language, "pl")) return tag('P', 'L', 'K', ' ');
    if (language_equals(language, "ro")) return tag('R', 'O', 'M', ' ');
    if (language_equals(language, "tr")) return tag('T', 'R', 'K', ' ');
    if (language_equals(language, "zh") ||
        language_equals(language, "zh-cn") ||
        language_equals(language, "zh-sg") ||
        language_equals(language, "zh-hans")) return tag('Z', 'H', 'S', ' ');
    if (language_equals(language, "zh-tw") ||
        language_equals(language, "zh-hant")) return tag('Z', 'H', 'T', ' ');
    if (language_equals(language, "zh-hk") ||
        language_equals(language, "zh-mo") ||
        language_equals(language, "zh-hant-hk") ||
        language_equals(language, "zh-hant-mo")) return tag('Z', 'H', 'H', ' ');
    return tag('d', 'f', 'l', 't');
}

bool try_resolve_open_type_shaping_route(
    const sfnt_font_view& font,
    open_type_tag unicode_script,
    shaping_direction requested_direction,
    open_type_shaping_route& result,
    font_error* error) noexcept {
    result = {};
    if (static_cast<std::uint8_t>(requested_direction) >
        static_cast<std::uint8_t>(shaping_direction::bottom_to_top)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (unicode_script == tag('h', 'i', 'r', 'a')) {
        unicode_script = tag('k', 'a', 'n', 'a');
    } else if (unicode_script == tag('l', 'a', 'o', 'o')) {
        unicode_script = tag('l', 'a', 'o', ' ');
    }

    open_type_tag layout_script = unicode_script;
    bool use_shaper = false;
    const auto third = generation_tag(unicode_script, third_generation);
    const auto second = generation_tag(unicode_script, second_generation);
    if (third.value != 0U && has_open_type_script(font, third)) {
        layout_script = third;
        use_shaper = true;
    } else if (second.value != 0U && has_open_type_script(font, second)) {
        layout_script = second;
    } else {
        use_shaper = is_use_script(unicode_script);
    }

    const bool indic_shaper = !use_shaper && is_indic_script(unicode_script);
    const bool khmer_shaper = layout_script == tag('k', 'h', 'm', 'r');
    const bool myanmar_shaper = unicode_script == tag('m', 'y', 'm', 'r');
    const bool arabic_shaper = is_arabic_joining_script(unicode_script);
    const bool compose_hebrew_presentation_forms =
        unicode_script != tag('h', 'e', 'b', 'r') ||
        !has_open_type_feature(font, tag('m', 'a', 'r', 'k'));
    const auto complex_script = use_shaper
        ? open_type_complex_script::use
        : indic_shaper
            ? open_type_complex_script::indic
            : khmer_shaper
                ? open_type_complex_script::khmer
                : myanmar_shaper
                    ? open_type_complex_script::myanmar
                    : open_type_complex_script::none;
    result = open_type_shaping_route{
        unicode_script,
        layout_script,
        resolve_direction(requested_direction, unicode_script),
        complex_script,
        use_shaper,
        indic_shaper,
        khmer_shaper,
        myanmar_shaper,
        arabic_shaper,
        compose_hebrew_presentation_forms};
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
