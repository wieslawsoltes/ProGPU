#include "progpu_native.h"
#include "../src/Text/progpu_native_text_cluster_breaks_internal.hpp"

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <iostream>
#include <limits>
#include <source_location>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <vector>

namespace {
using namespace progpu::native::text;
constexpr auto prohibited = text_line_break_kind::prohibited;
constexpr auto opportunity = text_line_break_kind::opportunity;
constexpr auto mandatory = text_line_break_kind::mandatory;
constexpr auto untouched = static_cast<text_line_break_kind>(0xA6U);

struct source_record final {
    std::uint32_t code_point;
    std::uint32_t input_index;
    std::uint16_t input_length;
};

void require(bool condition, std::string_view control,
    const std::source_location where = std::source_location::current()) {
    if (!condition)
        throw std::runtime_error(std::string(control) + " at line " + std::to_string(where.line()));
}

template<class T>
std::vector<std::byte> snapshot(std::span<const T> values) {
    std::vector<std::byte> result(values.size_bytes());
    if (!values.empty()) std::memcpy(result.data(), values.data(), values.size_bytes());
    return result;
}

template<class T>
bool unchanged(std::span<const T> values, const std::vector<std::byte>& before) {
    return before.size() == values.size_bytes() &&
        (values.empty() || std::memcmp(values.data(), before.data(), values.size_bytes()) == 0);
}

template<class Scalar>
void check(std::string_view name, std::initializer_list<source_record> source,
    std::initializer_list<text_line_break_kind> breaks,
    std::initializer_list<std::int32_t> clusters, bool success,
    std::initializer_list<text_line_break_kind> expected_used,
    std::size_t output_capacity = std::numeric_limits<std::size_t>::max()) {
    const auto control = std::string(std::is_same_v<Scalar, unicode_scalar> ? "native: " : "wire: ") +
        std::string(name);
    std::vector<Scalar> input;
    for (const auto value : source) {
        Scalar scalar{};
        scalar.code_point = value.code_point;
        scalar.input_index = value.input_index;
        scalar.input_length = value.input_length;
        input.push_back(scalar);
    }
    const std::vector<text_line_break_kind> scalar_breaks(breaks);
    std::vector<shaping_glyph> glyphs;
    for (const auto cluster : clusters) {
        shaping_glyph glyph{};
        glyph.glyph_id = 17U;
        glyph.cluster = cluster;
        glyphs.push_back(glyph);
    }
    if (output_capacity == std::numeric_limits<std::size_t>::max())
        output_capacity = glyphs.size() + 3U;
    std::vector<text_line_break_kind> output(output_capacity, untouched);
    std::vector<text_line_break_kind> expected(expected_used);
    require(expected.size() <= output.size(), control);
    expected.resize(output.size(), untouched);
    const auto input_before = snapshot(std::span<const Scalar>(input));
    const auto breaks_before = snapshot(std::span<const text_line_break_kind>(scalar_breaks));
    const auto glyphs_before = snapshot(std::span<const shaping_glyph>(glyphs));
    const auto result = detail::try_map_logical_cluster_breaks<Scalar>(
        input, scalar_breaks, glyphs, output);
    require(result == success && output == expected, control);
    require(unchanged(std::span<const Scalar>(input), input_before) &&
        unchanged(std::span<const text_line_break_kind>(scalar_breaks), breaks_before) &&
        unchanged(std::span<const shaping_glyph>(glyphs), glyphs_before), control);
}

template<class Scalar>
void controls() {
    // Independently authored UAX #14 boundaries: CR x LF, a mandatory boundary
    // after LF or standalone CR, and the final mandatory paragraph boundary.
    // No producer or second mapper computes the expected glyph output.
    check<Scalar>("LF", {{'A', 0U, 1U}, {'\n', 1U, 1U}, {'B', 2U, 1U}},
        {prohibited, mandatory, mandatory}, {0, 1, 2}, true,
        {prohibited, mandatory, mandatory});
    check<Scalar>("CR", {{'A', 0U, 1U}, {'\r', 1U, 1U}, {'B', 2U, 1U}},
        {prohibited, mandatory, mandatory}, {0, 1, 2}, true,
        {prohibited, mandatory, mandatory});
    check<Scalar>("CRLF", {{'A', 0U, 1U}, {'\r', 1U, 1U}, {'\n', 2U, 1U}, {'B', 3U, 1U}},
        {prohibited, prohibited, mandatory, mandatory}, {0, 1, 2, 3}, true,
        {prohibited, prohibited, mandatory, mandatory});
    check<Scalar>("CRLF shared cluster", {{'A', 0U, 1U}, {'\r', 1U, 1U}, {'\n', 2U, 1U}, {'B', 3U, 1U}},
        {prohibited, prohibited, mandatory, mandatory}, {0, 1, 1, 3}, true,
        {prohibited, prohibited, mandatory, mandatory});
    check<Scalar>("CRLF collapsed cluster", {{'A', 0U, 1U}, {'\r', 1U, 1U}, {'\n', 2U, 1U}, {'B', 3U, 1U}},
        {prohibited, prohibited, mandatory, mandatory}, {0, 1, 3}, true,
        {prohibited, mandatory, mandatory});
    check<Scalar>("ffi ligature", {{'f', 0U, 1U}, {'f', 1U, 1U}, {'i', 2U, 1U}, {' ', 3U, 1U}, {'X', 4U, 1U}},
        {prohibited, prohibited, prohibited, opportunity, mandatory}, {0, 3, 4}, true,
        {prohibited, opportunity, mandatory});
    check<Scalar>("repeated logical groups select their last source scalar",
        {{'a', 0U, 1U}, {'b', 1U, 1U}, {'c', 2U, 1U}, {'d', 3U, 1U}, {'e', 4U, 1U}},
        {opportunity, prohibited, mandatory, opportunity, mandatory}, {0, 0, 3, 3, 4}, true,
        {prohibited, mandatory, prohibited, opportunity, mandatory});
    check<Scalar>("source gaps do not require aligned cluster starts",
        {{0x1F600U, 10U, 2U}, {' ', 15U, 1U}, {'B', 22U, 1U}, {'C', 30U, 1U}},
        {prohibited, opportunity, prohibited, mandatory}, {9, 9, 14, 20, 20, 29}, true,
        {prohibited, prohibited, opportunity, prohibited, prohibited, mandatory});
    check<Scalar>("cluster inside a source scalar remains admitted",
        {{0x1F600U, 0U, 2U}, {' ', 2U, 1U}, {'B', 3U, 1U}},
        {prohibited, opportunity, mandatory}, {1, 3}, true, {opportunity, mandatory});
    check<Scalar>("clusters beyond source reuse the last consumed boundary",
        {{'A', 0U, 1U}, {'B', 1U, 1U}}, {prohibited, opportunity}, {3, 9}, true,
        {opportunity, opportunity});

    constexpr auto unknown = static_cast<text_line_break_kind>(0x73U);
    check<Scalar>("zero lengths and duplicate empty ranges do not add validation",
        {{'A', 0U, 0U}, {'B', 0U, 0U}, {'C', 5U, 0U}},
        {prohibited, opportunity, unknown}, {0, 1}, true, {opportunity, unknown});
    constexpr auto maximum = std::numeric_limits<std::uint32_t>::max();
    check<Scalar>("UINT_MAX final source index is excluded by the original sentinel",
        {{'A', 0U, 1U}, {'Z', maximum, 0U}}, {opportunity, mandatory}, {0}, true, {opportunity});
    check<Scalar>("only UINT_MAX source has no consumed scalar",
        {{'Z', maximum, 0U}}, {mandatory}, {0}, false, {});

    // The entire source is validated before any group writes, even when the
    // offending source record would otherwise be consumed by the final group.
    check<Scalar>("late source overlap publishes nothing",
        {{'A', 0U, 1U}, {'B', 2U, 2U}, {'C', 3U, 1U}},
        {opportunity, mandatory, mandatory}, {0, 2, 3}, false, {});
    check<Scalar>("late source end overflow publishes nothing",
        {{'A', 0U, 1U}, {'Z', maximum, 1U}}, {opportunity, mandatory}, {0, 1}, false, {});
    check<Scalar>("late source order failure publishes nothing",
        {{'A', 0U, 1U}, {'B', 4U, 0U}, {'C', 3U, 1U}},
        {opportunity, mandatory, mandatory}, {0, 1, 2}, false, {});

    // Glyph validation is deliberately group-local. A late bad following
    // cluster rejects its current group but preserves already-written groups.
    check<Scalar>("late descending glyph retains two written groups",
        {{'A', 0U, 1U}, {'B', 1U, 1U}, {'C', 2U, 1U}, {'D', 3U, 1U}, {'E', 4U, 1U}},
        {opportunity, opportunity, mandatory, mandatory, opportunity}, {0, 0, 2, 2, 4, 3}, false,
        {prohibited, opportunity, prohibited, mandatory, untouched, untouched});
    check<Scalar>("late negative glyph retains the earlier group",
        {{'A', 0U, 1U}, {'B', 1U, 1U}, {'C', 2U, 1U}},
        {prohibited, opportunity, mandatory}, {0, 0, 2, -1}, false,
        {prohibited, opportunity, untouched, untouched});
    check<Scalar>("noncontiguous repeated cluster fails after the first write",
        {{'A', 0U, 1U}, {'B', 1U, 1U}, {'C', 2U, 1U}},
        {prohibited, opportunity, mandatory}, {0, 2, 0}, false,
        {opportunity, untouched, untouched});
    check<Scalar>("first negative cluster publishes nothing",
        {{'A', 0U, 1U}}, {mandatory}, {-1, 0}, false, {});
    check<Scalar>("first descending group publishes nothing",
        {{'A', 0U, 1U}, {'B', 1U, 1U}, {'C', 2U, 1U}},
        {opportunity, mandatory, mandatory}, {2, 1, 3}, false, {});
    check<Scalar>("next cluster before every source index publishes nothing",
        {{'A', 10U, 1U}, {'B', 20U, 1U}}, {opportunity, mandatory}, {0, 5}, false, {});

    check<Scalar>("short output publishes nothing",
        {{'A', 0U, 1U}, {'B', 1U, 1U}, {'C', 2U, 1U}},
        {opportunity, mandatory, mandatory}, {0, 1, 2}, false, {}, 2U);
    check<Scalar>("short scalar breaks publish nothing",
        {{'A', 0U, 1U}, {'B', 1U, 1U}}, {mandatory}, {0, 1}, false, {});
    check<Scalar>("extra scalar breaks publish nothing",
        {{'A', 0U, 1U}}, {prohibited, mandatory}, {0}, false, {});
    check<Scalar>("nonempty glyphs require source", {}, {}, {0}, false, {});
    check<Scalar>("empty input and glyphs preserve every caller slot", {}, {}, {}, true, {});
    check<Scalar>("empty input and zero output remain admitted", {}, {}, {}, true, {}, 0U);
    check<Scalar>("empty glyphs bypass malformed source validation",
        {{'A', 0U, 1U}, {'B', 0U, 1U}, {'Z', maximum, 1U}},
        {prohibited, opportunity, mandatory}, {}, true, {});
    check<Scalar>("empty glyphs still require equal scalar break count",
        {{'A', 0U, 1U}}, {}, {}, false, {});
    check<Scalar>("empty source still rejects an extra scalar break", {}, {mandatory}, {}, false, {});
}
} // namespace

int main() {
    try {
        controls<progpu_native_text_scalar>();
        controls<unicode_scalar>();
        std::cout << "{\"wireScalarBreaks\":true,\"nativeScalarBreaks\":true,\"hardBreakSequences\":true,\"partialGlyphFailure\":true,\"sourcePreflight\":true,\"unusedSlots\":true,\"legacyAdmission\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
