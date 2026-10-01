#include "progpu_native.h"
#include "progpu_native_edit_word_boundaries.hpp"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {
using namespace progpu::native::text;
constexpr std::uint32_t sentinel = 0xBAD0C0DEU;

void require(bool value, const char* contract) {
    if (!value) throw std::runtime_error(contract);
}

void inventory(std::u16string_view text, std::span<const std::uint32_t> expected, std::int8_t level) {
    std::vector<std::uint16_t> source(text.begin(), text.end());
    const auto original = source;
    edit_word_boundary_snapshot result{{91U}, 91U};
    edit_word_boundary_error error{};
    require(try_create_edit_word_boundary_snapshot(source, result, error, level) &&
        error == edit_word_boundary_error::none && result.leading_content_start == 0U &&
        std::ranges::equal(result.positions, expected), "Original Myanmar EDIT inventory differs");
    std::vector<std::uint32_t> output(text.size() + 4U, sentinel);
    const auto transported = progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(),
        static_cast<std::uint32_t>(source.size()), level, output.data(), static_cast<std::uint32_t>(output.size()));
    require(transported.status == PROGPU_NATIVE_STATUS_SUCCESS &&
        transported.error_code == PROGPU_NATIVE_EDIT_WORD_BOUNDARY_NONE &&
        transported.boundary_count == expected.size() && transported.leading_content_start == 0U &&
        std::equal(expected.begin(), expected.end(), output.begin()) &&
        std::all_of(output.begin() + transported.boundary_count, output.end(), [](auto value) { return value == sentinel; }),
        "Myanmar C transport changed metadata, inventory or caller tail");
    require(source == original, "Myanmar EDIT rewrote original UTF16");
    std::fill(source.begin(), source.end(), std::uint16_t{0});
    require(std::ranges::equal(result.positions, expected), "Myanmar snapshot retained mutable original storage");
}
}

void run_edit_myanmar_controls() {
    // Independent original Microsoft ScriptBreak/EDIT observations, NOT answers
    // generated from the syllable machine. Build 36900452884, source2dafdc178:
    // source-roles.json SHA256 858e7c8e34a694a1ff4e153b7a4c6946f451daaf6dc371cc0296519a468048b8.
    // Every row occurred bare/Latin-prefixed in both original paragraph directions.
    struct observed { std::u16string_view source; std::vector<std::uint32_t> starts; };
    const std::array<observed, 16> cases{{
        {u"\u1000\u1001\u1002", {0, 1, 2}},
        {u"\u1000\u102D\u1001", {0, 2}},
        {u"\u1000\u103B\u1001", {0, 2}},
        {u"\u1000\u103C\u1001", {0, 2}},
        {u"\u1000\u103D\u103E\u1001", {0, 3}},
        {u"\u1000\u103A\u1001", {0, 2}},
        {u"\u1000\u1039\u1001\u1002", {0, 3}},
        {u"\u1004\u103A\u1039\u1000\u1001", {0, 4}},
        {u"\u1000\u1037\u1001", {0, 2}},
        {u"\u1000\u1038\u1001", {0, 2}},
        {u"\u1000\u1031\u1001", {0, 2}},
        {u"\u1000\u102D\u103A\u1001", {0, 2, 3}},
        {u"\u102D\u1000\u1001", {0, 1, 2}},
        {u"\u1000\u1039\u200C\u1001", {0, 2, 3}},
        {u"\u1000\u1039\u200D\u1001", {0, 2, 3}},
        {u"\u1040\u1041\u1000\u1001", {0, 1, 2, 3}}
    }};
    for (const auto& observed : cases) {
        std::vector<std::uint8_t> categories;
        for (const auto cp : observed.source)
            categories.push_back(get_unicode_indic_shaping_properties(cp).category);
        std::vector<std::uint8_t> syllables(categories.size());
        require(try_assign_unicode_syllables(unicode_syllable_machine::myanmar, categories, {}, syllables),
            "Original Myanmar syllable machine rejected its source properties");
        std::vector<std::uint32_t> starts{0U};
        for (std::size_t index = 1; index < syllables.size(); ++index)
            if (syllables[index] != syllables[index - 1U]) starts.push_back(static_cast<std::uint32_t>(index));
        require(starts == observed.starts, "Myanmar shaping machine is not the observed EDIT syllable policy");
        for (const auto prefix : {u"", u"x"}) {
            const auto prefix_size = static_cast<std::uint32_t>(std::u16string_view(prefix).size());
            const auto text = std::u16string(prefix) + std::u16string(observed.source) + u"y ";
            std::vector<std::uint32_t> expected;
            if (prefix_size != 0U) expected.push_back(0U);
            for (const auto start : observed.starts) expected.push_back(prefix_size + start);
            expected.push_back(static_cast<std::uint32_t>(text.size()));
            for (const auto level : {std::int8_t{0}, std::int8_t{1}}) inventory(text, expected, level);
        }
    }
    // Retain the separate SIX original item-context requests. Their source was
    // not one of the sixteen new training sequences. Same-run receipt SHA256:
    // 248fa537f8f68fd0e748344c045b5bec89470173f81a4799b201b69ec63507ef.
    for (const auto level : {std::int8_t{0}, std::int8_t{1}}) {
        const std::array<std::uint32_t, 4> adjacent{0, 1, 2, 5};
        const std::array<std::uint32_t, 4> spaced{0, 2, 3, 6};
        inventory(u"x\u1000\u1001y ", adjacent, level);
        inventory(u"\u4E00\u1000\u1001y ", adjacent, level);
        inventory(u"x \u1000\u1001y ", spaced, level);
    }
    // New algorithm controls, not additional Microsoft oracle observations:
    // repeated syllables cross the machine's four-bit serial wrap while keeping
    // every source seam, and unchanged suffix/UTF16 ownership stays explicit.
    std::u16string repeated;
    std::vector<std::uint32_t> expected;
    for (std::uint32_t index = 0U; index < 40U; ++index) {
        expected.push_back(index * 2U);
        repeated += u"\u1000\u102D";
    }
    repeated += u"y ";
    expected.push_back(static_cast<std::uint32_t>(repeated.size()));
    inventory(repeated, expected, 0);
    inventory(repeated, expected, 1);
}
