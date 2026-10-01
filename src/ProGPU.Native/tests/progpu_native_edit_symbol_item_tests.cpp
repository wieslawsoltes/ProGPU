#include "progpu_native.h"
#include "progpu_native_edit_word_boundaries.hpp"
#include "progpu_native_edit_item_policy.hpp"

#include <algorithm>
#include <array>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {
using namespace progpu::native::text;
constexpr std::uint32_t sentinel = 0xBAD0C0DEU;

void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

void check(std::u16string_view text, std::initializer_list<std::uint32_t> expected,
    std::int8_t level = 0)
{
    std::vector<std::uint16_t> source(text.begin(), text.end());
    const auto original = source;
    edit_word_boundary_snapshot snapshot{{73U, 91U}, 73U};
    edit_word_boundary_error error{};
    require(try_create_edit_word_boundary_snapshot(source, snapshot, error, level) &&
        error == edit_word_boundary_error::none && snapshot.leading_content_start == 0U &&
        snapshot.positions == std::vector<std::uint32_t>(expected),
        "Typed symbol item differs from original literal inventory");
    std::vector<std::uint32_t> output(text.size() + 4U, sentinel);
    const auto result = progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(),
        static_cast<std::uint32_t>(source.size()), level, output.data(),
        static_cast<std::uint32_t>(output.size()));
    require(result.status == PROGPU_NATIVE_STATUS_SUCCESS &&
        result.error_code == PROGPU_NATIVE_EDIT_WORD_BOUNDARY_NONE &&
        result.boundary_count == expected.size() && result.leading_content_start == 0U &&
        std::equal(expected.begin(), expected.end(), output.begin()) &&
        std::all_of(output.begin() + result.boundary_count, output.end(),
            [](auto value) { return value == sentinel; }),
        "Symbol C export lost exact source endpoints or changed its output tail");
    require(source == original, "Symbol policy changed original UTF16 units");
    std::fill(source.begin(), source.end(), 0U);
    require(snapshot.positions == std::vector<std::uint32_t>(expected) &&
        std::equal(expected.begin(), expected.end(), output.begin()),
        "Symbol outputs retained mutable source storage");
}

void rejected(std::u16string_view text, edit_word_boundary_error expected, std::int8_t level)
{
    const std::vector<std::uint16_t> source(text.begin(), text.end());
    edit_word_boundary_snapshot snapshot{{73U, 91U}, 73U};
    edit_word_boundary_error error{};
    require(!try_create_edit_word_boundary_snapshot(source, snapshot, error, level) &&
        error == expected && snapshot.positions == std::vector<std::uint32_t>{73U, 91U} &&
        snapshot.leading_content_start == 73U, "Unsupported symbol role published a partial snapshot");
    std::array<std::uint32_t, 32> output{};
    output.fill(sentinel);
    const auto result = progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(),
        static_cast<std::uint32_t>(source.size()), level, output.data(),
        static_cast<std::uint32_t>(output.size()));
    require(result.status == PROGPU_NATIVE_STATUS_UNSUPPORTED &&
        result.error_code == static_cast<std::uint32_t>(expected) && result.boundary_count == 0U &&
        result.leading_content_start == 0U && std::all_of(output.begin(), output.end(),
            [](auto value) { return value == sentinel; }),
        "Unsupported symbol C export lost exact error or touched caller output");
}
} // namespace

void run_edit_symbol_item_controls()
{
    // Original full sweep repeated in run36900452884, symbol-attributes.json
    // SHA256 5bd22eeada1b79878346fdd7c911e70f24427d7ce64746530c1c00cad54e313c.
    // The prior run36867054042 symbol table and its pin remain unchanged.
    // Literal measured members and inventories, NOT expected values generated
    // by the policy under test. The three contexts were observed LTR only.
    constexpr std::u16string_view balinese =
        u"\u1B61\u1B62\u1B63\u1B64\u1B65\u1B66\u1B67\u1B68\u1B69\u1B6A"
        u"\u1B74\u1B75\u1B76\u1B77\u1B78\u1B79\u1B7A\u1B7B\u1B7C";
    constexpr std::u16string_view hangul =
        u"\u3200\u3201\u3202\u3203\u3204\u3205\u3206\u3207\u3208\u3209\u320A\u320B\u320C\u320D\u320E\u320F"
        u"\u3210\u3211\u3212\u3213\u3214\u3215\u3216\u3217\u3218\u3219\u321A\u321B\u321C\u321D\u321E"
        u"\u3260\u3261\u3262\u3263\u3264\u3265\u3266\u3267\u3268\u3269\u326A\u326B\u326C\u326D\u326E\u326F"
        u"\u3270\u3271\u3272\u3273\u3274\u3275\u3276\u3277\u3278\u3279\u327A\u327B\u327C\u327D\u327E";
    static_assert(balinese.size() == 19U && hangul.size() == 62U);
    using profile = detail::edit_item_profile;
    for (std::uint32_t cp = 0U; cp <= 0xFFFFU; ++cp) {
        const unicode_scalar scalar{cp, 47U, 1U, 0U, 0U, get_unicode_script(cp)};
        const auto expected = balinese.find(static_cast<char16_t>(cp)) != std::u16string_view::npos
            ? profile::balinese_symbols : hangul.find(static_cast<char16_t>(cp)) != std::u16string_view::npos
                ? profile::hangul_symbols : profile::paragraph_bridge;
        require(detail::get_edit_symbol_item_profile(scalar) == expected,
            "Owned symbol properties do not match the complete original measured domain");
        if (expected == profile::paragraph_bridge) continue;
        detail::edit_item_properties properties{};
        require(detail::try_classify_edit_item_properties(scalar, properties) &&
            properties.profile == expected && properties.flags == detail::get_edit_profile_flags(expected) &&
            properties.role == detail::edit_item_source_role::ordinary && properties.has_attachment_owner,
            "Symbol property classification lost its original ordinary role");
    }
    for (const auto inventory : {balinese, hangul}) {
        for (const auto symbol : inventory) {
            const std::u16string latin{u'a', symbol, u'b', u' '};
            const std::u16string variation{u'a', symbol, 0xFE0FU, u'b', u' '};
            const std::u16string cjk{0x4E00U, symbol, 0x4E8CU, u' '};
            if (inventory == balinese) {
                check(latin, {0, 1, 4});
                check(variation, {0, 1, 5});
                check(cjk, {0, 1, 4});
            } else {
                check(latin, {0, 4});
                check(variation, {0, 5});
                check(cjk, {0, 4});
            }
            // Every member remains raw ID and breakable on each side under
            // the ordinary UAX14 worker. No generated Unicode table changes.
            std::array<unicode_scalar, 4> scalars{};
            const std::vector<std::uint16_t> source(latin.begin(), latin.end());
            std::uint32_t written = 0U;
            std::array<unicode_line_break_class, 4> classes{};
            std::array<text_line_break_kind, 4> breaks{};
            require(try_decode_utf16(source, scalars, written) && written == scalars.size() &&
                try_resolve_unicode_line_breaks(scalars, classes, breaks) &&
                get_unicode_line_break_class(symbol) == unicode_line_break_class::ideographic &&
                classes[1] == unicode_line_break_class::ideographic &&
                breaks[0] != text_line_break_kind::prohibited && breaks[1] != text_line_break_kind::prohibited,
                "Typed EDIT symbol policy changed the ordinary UAX14 worker");
        }
    }
    // Independent original repeated-item source roles, run36900452884,
    // source-roles.json SHA256
    // 858e7c8e34a694a1ff4e153b7a4c6946f451daaf6dc371cc0296519a468048b8.
    // These eight literal inventories include both actual paragraph directions.
    for (const auto level : {std::int8_t{0}, std::int8_t{1}}) {
        check(u"\u3200\u3201y ", {0, 4}, level);
        check(u"x\u3200\u3201y ", {0, 5}, level);
        check(u"\u1B61\u1B62y ", {0, 4}, level);
        check(u"x\u1B61\u1B62y ", {0, 1, 5}, level);
        for (const auto source : {u"a\u327Fb ", u"a\u327F\uFE0Fb ", u"\u4E00\u327F\u4E8C "})
            rejected(source, edit_word_boundary_error::unqualified_bmp_symbol_policy, level);
        for (const auto source : {u"\u3200\uAC00", u"\uAC00\u3200", u"\u3200\uFE0F\uAC00",
                u"\u3200\u0301y ", u"\u1B61\u0301y ", u"\u3200\u200Dy ", u"\u1B61\u200Cy "})
            rejected(source, edit_word_boundary_error::unqualified_script_item_transition_policy, level);
#if !defined(PROGPU_NATIVE_EDIT_WORD_ICU)
        // Symbol admission never bypasses the original complete-source Thai
        // dictionary dependency, even after an otherwise qualified symbol item.
        rejected(u"x\u1B61\u1B62\u0E01\u0E02y ",
            edit_word_boundary_error::dependency_unavailable, level);
#endif
    }
}
