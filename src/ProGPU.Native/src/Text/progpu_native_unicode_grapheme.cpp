#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"
#include "Unicode/progpu_native_unicode_grapheme_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Original UAX #29 revision-47 implementation using Unicode 17 property data.
// The property source and rule provenance are recorded in the native text plan.

namespace progpu::native::text {
namespace {

void set_error(unicode_error* error, unicode_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

template <typename Value>
std::uint32_t find_range_value(
    std::uint32_t code_point,
    std::span<const Value> ranges,
    std::uint32_t fallback) noexcept {
    std::size_t low = 0U;
    std::size_t high = ranges.size() / 3U;
    while (low < high) {
        const std::size_t middle = low + (high - low) / 2U;
        const std::size_t offset = middle * 3U;
        if (code_point < static_cast<std::uint32_t>(ranges[offset])) {
            high = middle;
        } else if (code_point >
            static_cast<std::uint32_t>(ranges[offset + 1U])) {
            low = middle + 1U;
        } else {
            return static_cast<std::uint32_t>(ranges[offset + 2U]);
        }
    }
    return fallback;
}

bool is_control(unicode_grapheme_break_class value) noexcept {
    return value == unicode_grapheme_break_class::carriage_return ||
        value == unicode_grapheme_break_class::line_feed ||
        value == unicode_grapheme_break_class::control;
}

struct boundary_state final {
    std::uint32_t regional_indicator_count = 0U;
    bool emoji_extended_pictographic = false;
    bool emoji_zwj_ready = false;
    bool indic_consonant = false;
    bool indic_linker = false;
};

void advance_state(
    std::uint32_t code_point,
    unicode_grapheme_break_class grapheme,
    boundary_state& state) noexcept {
    if (grapheme == unicode_grapheme_break_class::regional_indicator) {
        ++state.regional_indicator_count;
    } else {
        state.regional_indicator_count = 0U;
    }

    if (is_unicode_extended_pictographic(code_point)) {
        state.emoji_extended_pictographic = true;
        state.emoji_zwj_ready = false;
    } else if (grapheme == unicode_grapheme_break_class::extend &&
        state.emoji_extended_pictographic) {
        state.emoji_zwj_ready = false;
    } else if (grapheme == unicode_grapheme_break_class::zero_width_joiner) {
        state.emoji_zwj_ready = state.emoji_extended_pictographic;
        state.emoji_extended_pictographic = false;
    } else {
        state.emoji_extended_pictographic = false;
        state.emoji_zwj_ready = false;
    }

    switch (get_unicode_indic_conjunct_class(code_point)) {
        case unicode_indic_conjunct_class::consonant:
            state.indic_consonant = true;
            state.indic_linker = false;
            break;
        case unicode_indic_conjunct_class::extend:
            break;
        case unicode_indic_conjunct_class::linker:
            state.indic_linker |= state.indic_consonant;
            break;
        default:
            state.indic_consonant = false;
            state.indic_linker = false;
            break;
    }
}

bool has_boundary(
    std::uint32_t right_code_point,
    unicode_grapheme_break_class left,
    unicode_grapheme_break_class right,
    const boundary_state& state,
    bool join_indic_conjuncts) noexcept {
    using value = unicode_grapheme_break_class;
    if (left == value::carriage_return && right == value::line_feed) {
        return false;
    }
    if (is_control(left) || is_control(right)) {
        return true;
    }
    if (left == value::hangul_l &&
        (right == value::hangul_l || right == value::hangul_v ||
            right == value::hangul_lv || right == value::hangul_lvt)) {
        return false;
    }
    if ((left == value::hangul_lv || left == value::hangul_v) &&
        (right == value::hangul_v || right == value::hangul_t)) {
        return false;
    }
    if ((left == value::hangul_lvt || left == value::hangul_t) &&
        right == value::hangul_t) {
        return false;
    }
    if (right == value::extend || right == value::zero_width_joiner ||
        right == value::spacing_mark || left == value::prepend) {
        return false;
    }
    if (join_indic_conjuncts &&
        get_unicode_indic_conjunct_class(right_code_point) ==
            unicode_indic_conjunct_class::consonant &&
        state.indic_consonant && state.indic_linker) {
        return false;
    }
    if (is_unicode_extended_pictographic(right_code_point) &&
        state.emoji_zwj_ready) {
        return false;
    }
    if (left == value::regional_indicator &&
        right == value::regional_indicator &&
        (state.regional_indicator_count & 1U) != 0U) {
        return false;
    }
    return true;
}

std::uint32_t scalar_source_end(const unicode_scalar& scalar) noexcept {
    const std::uint64_t end = static_cast<std::uint64_t>(scalar.input_index) +
        scalar.input_length;
    return static_cast<std::uint32_t>(std::min<std::uint64_t>(
        end, std::numeric_limits<std::uint32_t>::max()));
}

bool segment(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output,
    bool write,
    bool join_indic_conjuncts,
    std::uint32_t& count) noexcept {
    count = 0U;
    if (input.empty()) {
        return true;
    }
    std::size_t cluster_start = 0U;
    std::uint32_t source_start = input[0U].input_index;
    std::uint32_t source_end = scalar_source_end(input[0U]);
    auto left = get_unicode_grapheme_break_class(input[0U].code_point);
    boundary_state state{};
    advance_state(input[0U].code_point, left, state);
    for (std::size_t index = 1U; index < input.size(); ++index) {
        const auto right =
            get_unicode_grapheme_break_class(input[index].code_point);
        if (has_boundary(input[index].code_point, left, right, state,
                join_indic_conjuncts)) {
            if (write) {
                output[count] = unicode_grapheme_cluster{
                    source_start,
                    source_end - source_start,
                    static_cast<std::uint32_t>(cluster_start),
                    static_cast<std::uint32_t>(index - cluster_start)};
            }
            ++count;
            cluster_start = index;
            source_start = input[index].input_index;
            source_end = scalar_source_end(input[index]);
        } else {
            source_start = std::min(source_start, input[index].input_index);
            source_end = std::max(source_end, scalar_source_end(input[index]));
        }
        advance_state(input[index].code_point, right, state);
        left = right;
    }
    if (write) {
        output[count] = unicode_grapheme_cluster{
            source_start,
            source_end - source_start,
            static_cast<std::uint32_t>(cluster_start),
            static_cast<std::uint32_t>(input.size() - cluster_start)};
    }
    ++count;
    return true;
}

} // namespace

unicode_grapheme_break_class get_unicode_grapheme_break_class(
    std::uint32_t code_point) noexcept {
    return static_cast<unicode_grapheme_break_class>(find_range_value(
        code_point,
        std::span<const std::uint32_t>{detail::unicode_grapheme_ranges},
        0U));
}

unicode_indic_conjunct_class get_unicode_indic_conjunct_class(
    std::uint32_t code_point) noexcept {
    return static_cast<unicode_indic_conjunct_class>(find_range_value(
        code_point,
        std::span<const std::uint32_t>{detail::unicode_indic_conjunct_ranges},
        0U));
}

bool is_unicode_extended_pictographic(std::uint32_t code_point) noexcept {
    return find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_extended_pictographic_ranges},
        0U) != 0U;
}

bool try_get_unicode_grapheme_cluster_count(
    std::span<const unicode_scalar> input,
    std::uint32_t& result,
    unicode_error* error) noexcept {
    result = 0U;
    if (input.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    segment(input, {}, false, true, result);
    set_error(error, unicode_error::none);
    return true;
}

bool try_segment_unicode_graphemes(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    std::uint32_t required = 0U;
    if (!try_get_unicode_grapheme_cluster_count(input, required, error)) {
        return false;
    }
    if (output.size() < required) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    segment(input, output, true, true, written);
    set_error(error, unicode_error::none);
    return true;
}

namespace detail {

bool try_segment_managed_compatible_graphemes(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    if (input.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    std::uint32_t required = 0U;
    segment(input, {}, false, false, required);
    if (output.size() < required) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    segment(input, output, true, false, written);
    set_error(error, unicode_error::none);
    return true;
}

} // namespace detail

} // namespace progpu::native::text
