#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>

// Direct allocation-free port of ProGPU-owned
// FontManager.GetFallbackFamilyPreferences. Platform adapters retain catalog
// and byte ownership; this file owns only the cross-platform ordering policy.

namespace progpu::native::text {
namespace {

constexpr std::array arabic{
    std::string_view{"Geeza Pro"},
    std::string_view{"Noto Naskh Arabic"},
    std::string_view{"Noto Sans Arabic"},
    std::string_view{"Segoe UI"},
    std::string_view{"Traditional Arabic"},
    std::string_view{"Arabic Typesetting"},
    std::string_view{"Tahoma"},
    std::string_view{"Arial"},
    std::string_view{"DejaVu Sans"}};
constexpr std::array japanese{
    std::string_view{"Hiragino Sans"},
    std::string_view{"Hiragino Kaku Gothic ProN"},
    std::string_view{"Yu Gothic"},
    std::string_view{"Meiryo"},
    std::string_view{"Noto Sans CJK JP"},
    std::string_view{"Noto Sans JP"},
    std::string_view{".Aqua Kana"}};
constexpr std::array korean{
    std::string_view{"Apple SD Gothic Neo"},
    std::string_view{"AppleGothic"},
    std::string_view{"Malgun Gothic"},
    std::string_view{"Noto Sans CJK KR"},
    std::string_view{"Noto Sans KR"}};
constexpr std::array chinese_traditional{
    std::string_view{"PingFang TC"},
    std::string_view{"Heiti TC"},
    std::string_view{"Noto Sans CJK TC"},
    std::string_view{"Noto Sans TC"},
    std::string_view{"Microsoft JhengHei"},
    std::string_view{"Songti TC"}};
constexpr std::array chinese_simplified{
    std::string_view{"PingFang SC"},
    std::string_view{"Hiragino Sans GB"},
    std::string_view{"Heiti SC"},
    std::string_view{"Noto Sans CJK SC"},
    std::string_view{"Noto Sans SC"},
    std::string_view{"Microsoft YaHei"},
    std::string_view{"SimHei"},
    std::string_view{"Songti SC"},
    std::string_view{"Noto Sans CJK JP"},
    std::string_view{"Noto Sans JP"}};
constexpr std::array hebrew{
    std::string_view{"Arial Hebrew"},
    std::string_view{"Lucida Grande"},
    std::string_view{"Noto Sans Hebrew"},
    std::string_view{"Segoe UI"},
    std::string_view{"Arial"},
    std::string_view{"DejaVu Sans"}};
constexpr std::array latin{
    std::string_view{"Helvetica"},
    std::string_view{"Arial"},
    std::string_view{"Segoe UI"},
    std::string_view{"Noto Sans"},
    std::string_view{"DejaVu Sans"},
    std::string_view{"Liberation Sans"}};
constexpr std::array symbols{
    std::string_view{"Apple Symbols"},
    std::string_view{"Zapf Dingbats"},
    std::string_view{"Segoe UI Symbol"},
    std::string_view{"Noto Sans Symbols 2"},
    std::string_view{"Noto Sans Symbols"},
    std::string_view{"Apple Color Emoji"},
    std::string_view{"Segoe UI Emoji"},
    std::string_view{"Noto Color Emoji"},
    std::string_view{"Symbola"},
    std::string_view{"DejaVu Sans"}};
constexpr std::array emoji{
    std::string_view{"Apple Color Emoji"},
    std::string_view{"Segoe UI Emoji"},
    std::string_view{"Noto Color Emoji"},
    std::string_view{"Noto Emoji"},
    std::string_view{"Segoe UI Symbol"}};

static_assert(
    arabic.size() + japanese.size() + korean.size() +
        chinese_traditional.size() + chinese_simplified.size() +
        hebrew.size() + latin.size() + symbols.size() + emoji.size() <= 64U,
    "The fixed fallback preference buffer must contain every policy family.");

constexpr char normalized(char value) noexcept {
    if (value == '_') return '-';
    return value >= 'A' && value <= 'Z'
        ? static_cast<char>(value + ('a' - 'A'))
        : value;
}

constexpr bool ascii_space(char value) noexcept {
    return value == ' ' || value == '\t' || value == '\r' || value == '\n' ||
        value == '\f' || value == '\v';
}

std::string_view trim(std::string_view value) noexcept {
    while (!value.empty() && ascii_space(value.front())) value.remove_prefix(1U);
    while (!value.empty() && ascii_space(value.back())) value.remove_suffix(1U);
    return value;
}

bool equals_at(
    std::string_view value,
    std::size_t offset,
    std::string_view expected) noexcept {
    if (offset > value.size() || expected.size() > value.size() - offset) {
        return false;
    }
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        if (normalized(value[offset + index]) != expected[index]) return false;
    }
    return true;
}

bool language_is(std::string_view value, std::string_view primary) noexcept {
    value = trim(value);
    return value.size() == primary.size()
        ? equals_at(value, 0U, primary)
        : value.size() > primary.size() &&
            equals_at(value, 0U, primary) &&
            normalized(value[primary.size()]) == '-';
}

bool language_contains(
    std::string_view value,
    std::string_view part) noexcept {
    value = trim(value);
    if (part.size() > value.size()) return false;
    for (std::size_t index = 0U; index <= value.size() - part.size(); ++index) {
        if (equals_at(value, index, part)) return true;
    }
    return false;
}

bool language_ends_with(
    std::string_view value,
    std::string_view suffix) noexcept {
    value = trim(value);
    return value.size() >= suffix.size() &&
        equals_at(value, value.size() - suffix.size(), suffix);
}

bool equal_family(std::string_view left, std::string_view right) noexcept {
    if (left.size() != right.size()) return false;
    for (std::size_t index = 0U; index < left.size(); ++index) {
        if (normalized(left[index]) != normalized(right[index])) return false;
    }
    return true;
}

struct preference_buffer final {
    std::array<std::string_view, 64U> values{};
    std::uint32_t count = 0U;

    template<std::size_t Size>
    void add(const std::array<std::string_view, Size>& source) noexcept {
        for (const auto family : source) {
            const auto present = std::any_of(
                values.begin(),
                values.begin() + count,
                [family](std::string_view candidate) {
                    return equal_family(candidate, family);
                });
            if (!present) values[count++] = family;
        }
    }
};

void add_language(
    preference_buffer& result,
    std::string_view language) noexcept {
    if (language_is(language, "ar")) {
        result.add(arabic);
    } else if (language_is(language, "ja")) {
        result.add(japanese);
    } else if (language_is(language, "ko")) {
        result.add(korean);
    } else if (language_is(language, "zh")) {
        const bool traditional = language_contains(language, "-hant") ||
            language_ends_with(language, "-tw") ||
            language_ends_with(language, "-hk") ||
            language_ends_with(language, "-mo");
        if (traditional) {
            result.add(chinese_traditional);
        } else {
            result.add(chinese_simplified);
        }
    }
}

bool is_arabic(std::uint32_t code_point) noexcept {
    return (code_point >= 0x0600U && code_point <= 0x06FFU) ||
        (code_point >= 0x0750U && code_point <= 0x077FU) ||
        (code_point >= 0x0870U && code_point <= 0x089FU) ||
        (code_point >= 0x08A0U && code_point <= 0x08FFU) ||
        (code_point >= 0xFB50U && code_point <= 0xFDFFU) ||
        (code_point >= 0xFE70U && code_point <= 0xFEFFU) ||
        (code_point >= 0x1EE00U && code_point <= 0x1EEFFU);
}

bool is_hebrew(std::uint32_t code_point) noexcept {
    return (code_point >= 0x0590U && code_point <= 0x05FFU) ||
        (code_point >= 0xFB1DU && code_point <= 0xFB4FU);
}

bool is_symbol(std::uint32_t code_point) noexcept {
    return code_point >= 0x2000U && code_point <= 0x2BFFU;
}

bool is_emoji(std::uint32_t code_point) noexcept {
    return code_point >= 0x1F000U && code_point <= 0x1FAFFU;
}

bool try_build_preferences(
    std::span<const std::string_view> language_tags,
    std::uint32_t code_point,
    preference_buffer& result,
    font_error* error) noexcept {
    result = {};
    if (code_point > 0x10FFFFU) {
        if (error != nullptr) *error = font_error::invalid_argument;
        return false;
    }
    for (const auto language : language_tags) {
        if (!trim(language).empty()) add_language(result, language);
    }
    if (is_arabic(code_point)) {
        result.add(arabic);
    } else if (is_hebrew(code_point)) {
        result.add(hebrew);
    } else if (is_emoji(code_point)) {
        result.add(emoji);
    } else if (is_symbol(code_point)) {
        result.add(symbols);
    } else if (code_point >= 0x3040U && code_point <= 0x30FFU) {
        result.add(japanese);
    } else if ((code_point >= 0xAC00U && code_point <= 0xD7AFU) ||
        (code_point >= 0x1100U && code_point <= 0x11FFU)) {
        result.add(korean);
    } else if ((code_point >= 0x3400U && code_point <= 0x9FFFU) ||
        (code_point >= 0xF900U && code_point <= 0xFAFFU) ||
        (code_point >= 0x20000U && code_point <= 0x323AFU)) {
        result.add(chinese_simplified);
    } else if ((code_point >= 0x0020U && code_point <= 0x024FU) ||
        (code_point >= 0x0370U && code_point <= 0x052FU) ||
        (code_point >= 0x1E00U && code_point <= 0x1FFFU) ||
        (code_point >= 0x2DE0U && code_point <= 0x2DFFU) ||
        (code_point >= 0xA640U && code_point <= 0xA69FU)) {
        result.add(latin);
    }
    if (error != nullptr) *error = font_error::none;
    return true;
}

} // namespace

bool try_get_font_fallback_family_preference_count(
    std::span<const std::string_view> language_tags,
    std::uint32_t code_point,
    std::uint32_t& result,
    font_error* error) noexcept {
    result = 0U;
    preference_buffer preferences{};
    if (!try_build_preferences(
            language_tags, code_point, preferences, error)) {
        return false;
    }
    result = preferences.count;
    return true;
}

bool try_get_font_fallback_family_preferences(
    std::span<const std::string_view> language_tags,
    std::uint32_t code_point,
    std::span<std::string_view> output,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    preference_buffer preferences{};
    if (!try_build_preferences(
            language_tags, code_point, preferences, error)) {
        return false;
    }
    if (output.size() < preferences.count) {
        if (error != nullptr) *error = font_error::insufficient_buffer;
        return false;
    }
    std::copy_n(
        preferences.values.begin(), preferences.count, output.begin());
    written = preferences.count;
    if (error != nullptr) *error = font_error::none;
    return true;
}

} // namespace progpu::native::text
