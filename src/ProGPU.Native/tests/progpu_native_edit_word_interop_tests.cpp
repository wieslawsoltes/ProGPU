#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <iostream>
#include <source_location>
#include <span>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace {
constexpr std::uint32_t sentinel = 0xBAD0C0DEU;
static_assert(sizeof(progpu_native_edit_word_boundary_result) == 16U);
static_assert(offsetof(progpu_native_edit_word_boundary_result, leading_content_start) == 12U);

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

void check(std::u16string_view text, std::initializer_list<std::uint32_t> expected,
    std::uint32_t leading = 0U, std::int32_t level = 0) {
    std::vector<std::uint16_t> source(text.begin(), text.end());
    const auto original = source;
    std::vector<std::uint32_t> positions(text.size() + 4U, sentinel);
    const auto result = progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(),
        static_cast<std::uint32_t>(source.size()), level, positions.data(),
        static_cast<std::uint32_t>(positions.size()));
    require(result.status == PROGPU_NATIVE_STATUS_SUCCESS && result.error_code == PROGPU_NATIVE_EDIT_WORD_BOUNDARY_NONE,
        "C EDIT transport rejected an original reference inventory");
    require(result.boundary_count == expected.size() && result.leading_content_start == leading &&
        std::equal(expected.begin(), expected.end(), positions.begin()),
        "C EDIT transport changed original UTF16 boundary identity");
    require(std::all_of(positions.begin() + result.boundary_count, positions.end(),
        [](auto value) { return value == sentinel; }), "C EDIT transport changed unused output tail");
    require(source == original, "C EDIT transport rewrote source units");
    std::fill(source.begin(), source.end(), 0U);
    require(std::equal(expected.begin(), expected.end(), positions.begin()),
        "Published inventory retained mutable source storage");
}

void failed(progpu_native_edit_word_boundary_result result, std::uint32_t error,
    std::span<const std::uint32_t> output, progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
    std::source_location location = std::source_location::current()) {
    if (result.status != static_cast<std::uint32_t>(status) || result.error_code != error)
        std::cerr << "EDIT transport failure control line " << location.line() << ": status " << result.status
            << ", error " << result.error_code << "; expected " << status << ", " << error << '\n';
    require(result.status == static_cast<std::uint32_t>(status) && result.error_code == error &&
        result.boundary_count == 0U && result.leading_content_start == 0U,
        "C EDIT failure changed its exact error or published counts");
    require(std::all_of(output.begin(), output.end(), [](auto value) { return value == sentinel; }),
        "Rejected C EDIT request touched a caller output");
}
}

void run_edit_word_interop_controls() {
    // Literal independently observed source inventories, never regenerated from
    // the classifier under test. The joiner seam remains inside a modern cluster.
    check(u"", {0});
    check(u"  alpha  beta  ", {0, 2, 9, 15}, 2);
    check(u"\talpha ", {0, 1, 7});
    check(u"a\u202Fb", {0, 2, 3});
    check(u"\u00A0alpha ", {0, 7});
    check(u"x\U0001F469\u200D\U0001F4BBy ", {0, 1, 4, 6, 8});
    check(u"x\u0628\u062Ay ", {0, 1, 5}, 0, 0);
    check(u"x\u0628\u062Ay ", {0, 1, 5}, 0, 1);

    std::array<std::uint32_t, 16> output{};
    output.fill(sentinel);
    const std::array<std::uint16_t, 6> source{'a', 'l', 'p', 'h', 'a', ' '};
    for (auto level : {-1, 2, 256, std::numeric_limits<std::int32_t>::min(), std::numeric_limits<std::int32_t>::max()})
        failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 6U, level, output.data(), 16U),
            PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_PARAGRAPH_LEVEL, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 6U, 0, output.data(), 1U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_OUTPUT_TOO_SMALL, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(nullptr, 0U, 0, nullptr, 0U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_OUTPUT_TOO_SMALL, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(nullptr, 1U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 6U, 0, nullptr, 1U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    const auto* misaligned = reinterpret_cast<const std::uint16_t*>(reinterpret_cast<const std::uint8_t*>(source.data()) + 1U);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(misaligned, 1U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    auto* misaligned_output = reinterpret_cast<std::uint32_t*>(reinterpret_cast<std::uint8_t*>(output.data()) + 1U);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 6U, 0, misaligned_output, 1U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(
        reinterpret_cast<const std::uint16_t*>(maximum - 1U), 1U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 6U, 0,
        reinterpret_cast<std::uint32_t*>(maximum - 3U), 1U), PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, output);
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(source.data(), 0x80000000U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INPUT_TOO_LARGE, output);

    alignas(std::uint32_t) std::array<std::uint8_t, 32> alias{};
    std::memcpy(alias.data() + 12U, source.data(), sizeof(source));
    const auto unchanged = alias;
    auto result = progpu_native_text_resolve_edit_word_boundaries_utf16(
        reinterpret_cast<const std::uint16_t*>(alias.data() + 12U), 6U, 0,
        reinterpret_cast<std::uint32_t*>(alias.data()), 8U);
    failed(result, PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER, {});
    require(alias == unchanged, "C EDIT transport allowed input alias in unused output capacity");

    const std::array<std::uint16_t, 1> malformed{0xD800U};
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(malformed.data(), 1U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_ENCODING, output);
    const std::array<std::uint16_t, 4> unknown{'a', 0x3200U, 'b', ' '};
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(unknown.data(), 4U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_BMP_SYMBOL_POLICY, output, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    const std::array<std::uint16_t, 2> myanmar{0x1000U, 0x1001U};
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(myanmar.data(), 2U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_COMPLEX_SCRIPT_POLICY, output, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    const std::array<std::uint16_t, 4> unqualified{'x', 0x0711U, 'y', ' '};
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(unqualified.data(), 4U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_SCRIPT_ITEM_TRANSITION_POLICY, output, PROGPU_NATIVE_STATUS_UNSUPPORTED);
#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
    check(u"\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 ", {0, 4, 7, 11, 15});
#else
    const std::array<std::uint16_t, 7> thai{0x0E20U, 0x0E32U, 0x0E29U, 0x0E32U, 0x0E44U, 0x0E17U, 0x0E22U};
    failed(progpu_native_text_resolve_edit_word_boundaries_utf16(thai.data(), 7U, 0, output.data(), 16U),
        PROGPU_NATIVE_EDIT_WORD_BOUNDARY_DEPENDENCY_UNAVAILABLE, output, PROGPU_NATIVE_STATUS_UNSUPPORTED);
#endif
    check(u"alpha ", {0, 6}); // Failed requests never poison the next generation.
}
