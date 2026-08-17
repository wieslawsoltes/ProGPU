#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Exact native port of ProGPU-owned ArabicJoiningData.Generated.cs. Both
// managed and native fallbacks use the same checked-in Unicode 17 categories.

namespace progpu::native::text {
namespace {

struct arabic_state_entry final {
    open_type_arabic_action previous;
    open_type_arabic_action current;
    std::uint8_t next_state;
};

using enum open_type_arabic_action;

constexpr std::array<arabic_state_entry, 42U> arabic_state_table{{
    {none, none, 0U}, {none, isolated, 2U}, {none, isolated, 1U},
    {none, isolated, 2U}, {none, isolated, 1U}, {none, isolated, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {none, isolated, 1U},
    {none, isolated, 2U}, {none, final2, 5U}, {none, isolated, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {initial, final, 1U},
    {initial, final, 3U}, {initial, final, 4U}, {initial, final, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {medial, final, 1U},
    {medial, final, 3U}, {medial, final, 4U}, {medial, final, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {medial2, isolated, 1U},
    {medial2, isolated, 2U}, {medial2, final2, 5U},
    {medial2, isolated, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {isolated, isolated, 1U},
    {isolated, isolated, 2U}, {isolated, final2, 5U},
    {isolated, isolated, 6U},
    {none, none, 0U}, {none, isolated, 2U}, {none, isolated, 1U},
    {none, isolated, 2U}, {none, final3, 5U}, {none, isolated, 6U}}};

void set_error(unicode_error* error, unicode_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

std::uint8_t nibble(
    std::span<const std::uint8_t> data,
    std::uint32_t index) noexcept {
    return static_cast<std::uint8_t>(
        (data[index >> 1U] >> ((index & 1U) << 2U)) & 15U);
}

bool is_fallback_transparent(std::uint32_t code_point) noexcept {
    const auto& ranges = detail::unicode_joining_fallback_ranges;
    std::size_t low = 0U;
    std::size_t high = ranges.size() / 2U;
    while (low < high) {
        const std::size_t middle = low + (high - low) / 2U;
        const std::uint32_t start = ranges[middle * 2U];
        const std::uint32_t end = ranges[middle * 2U + 1U];
        if (code_point < start) {
            high = middle;
        } else if (code_point > end) {
            low = middle + 1U;
        } else {
            return true;
        }
    }
    return false;
}

} // namespace

unicode_arabic_joining_type get_unicode_arabic_joining_type(
    std::uint32_t code_point) noexcept {
    std::uint8_t value = 7U;
    if (code_point < 125260U) {
        const auto& data = detail::unicode_arabic_joining_packed;
        const std::uint32_t level0 = nibble(data, code_point >> 9U);
        const std::uint32_t level1 = data[
            123U + level0 * 8U + ((code_point >> 6U) & 7U)];
        const std::uint32_t level2 = data[
            209U + level1 + ((code_point >> 3U) & 7U)];
        value = nibble(
            std::span<const std::uint8_t>{data}.subspan(441U),
            level2 * 8U + (code_point & 7U));
    }
    if (value != 7U) {
        return static_cast<unicode_arabic_joining_type>(value);
    }
    return is_fallback_transparent(code_point)
        ? unicode_arabic_joining_type::transparent
        : unicode_arabic_joining_type::non_joining;
}

bool try_assign_open_type_arabic_actions(
    std::span<const unicode_scalar> input,
    std::span<open_type_arabic_action> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    return try_assign_open_type_arabic_actions(
        input, {}, {}, output, written, error);
}

bool try_assign_open_type_arabic_actions(
    std::span<const unicode_scalar> input,
    std::span<const unicode_scalar> pre_context,
    std::span<const unicode_scalar> post_context,
    std::span<open_type_arabic_action> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    if (output.size() < input.size()) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    const auto valid = [](const unicode_scalar& scalar) noexcept {
        return scalar.code_point <= 0x10FFFFU &&
            (scalar.code_point < 0xD800U || scalar.code_point > 0xDFFFU);
    };
    for (const auto& scalar : input) {
        if (!valid(scalar)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
    }
    for (const auto& scalar : pre_context) {
        if (!valid(scalar)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
    }
    for (const auto& scalar : post_context) {
        if (!valid(scalar)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
    }
    std::fill_n(output.begin(), input.size(), open_type_arabic_action::none);
    std::size_t previous = input.size();
    std::uint8_t state = 0U;
    std::size_t inspected = 0U;
    for (std::size_t index = pre_context.size();
         index != 0U && inspected < 5U;
         ++inspected) {
        const auto joining = get_unicode_arabic_joining_type(
            pre_context[--index].code_point);
        if (joining == unicode_arabic_joining_type::transparent) continue;
        state = arabic_state_table[static_cast<std::size_t>(joining)].next_state;
        break;
    }
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const auto joining = get_unicode_arabic_joining_type(
            input[index].code_point);
        if (joining == unicode_arabic_joining_type::transparent) {
            continue;
        }
        const arabic_state_entry entry = arabic_state_table[
            static_cast<std::size_t>(state) * 6U +
            static_cast<std::size_t>(joining)];
        if (entry.previous != open_type_arabic_action::none &&
            previous < input.size()) {
            output[previous] = entry.previous;
        }
        output[index] = entry.current;
        previous = index;
        state = entry.next_state;
    }
    inspected = 0U;
    for (std::size_t index = 0U;
         index < post_context.size() && inspected < 5U;
         ++index, ++inspected) {
        const auto joining = get_unicode_arabic_joining_type(
            post_context[index].code_point);
        if (joining == unicode_arabic_joining_type::transparent) continue;
        const arabic_state_entry entry = arabic_state_table[
            static_cast<std::size_t>(state) * 6U +
            static_cast<std::size_t>(joining)];
        if (entry.previous != open_type_arabic_action::none &&
            previous < input.size()) {
            output[previous] = entry.previous;
        }
        break;
    }
    written = static_cast<std::uint32_t>(input.size());
    set_error(error, unicode_error::none);
    return true;
}

} // namespace progpu::native::text
