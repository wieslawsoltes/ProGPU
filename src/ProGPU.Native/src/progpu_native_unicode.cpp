#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of the ProGPU-owned Unicode value-record and strict
// decoder contract in ShapingContracts.cs/OpenTypeTextShaper.cs. Unicode 17
// script and canonical-combining-class tables are generated from the same
// managed packed data; no foreign implementation source is present here.

namespace progpu::native::text {
namespace {

constexpr open_type_tag default_script =
    open_type_tag::from_chars('D', 'F', 'L', 'T');
constexpr open_type_tag hiragana_script =
    open_type_tag::from_chars('h', 'i', 'r', 'a');
constexpr open_type_tag kana_script =
    open_type_tag::from_chars('k', 'a', 'n', 'a');
constexpr open_type_tag lao_script =
    open_type_tag::from_chars('l', 'a', 'o', 'o');
constexpr open_type_tag lao_layout_script =
    open_type_tag::from_chars('l', 'a', 'o', ' ');

void set_error(unicode_error* error, unicode_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool is_scalar(std::uint32_t value) noexcept {
    return value <= 0x10FFFFU &&
        (value < 0xD800U || value > 0xDFFFU);
}

std::uint8_t byte_at(
    std::span<const std::byte> input,
    std::size_t index) noexcept {
    return std::to_integer<std::uint8_t>(input[index]);
}

bool is_continuation(std::uint8_t value) noexcept {
    return (value & 0xC0U) == 0x80U;
}

bool try_decode_utf8_scalar(
    std::span<const std::byte> input,
    std::size_t offset,
    std::uint32_t& code_point,
    std::uint8_t& length) noexcept {
    const auto first = byte_at(input, offset);
    if (first <= 0x7FU) {
        code_point = first;
        length = 1U;
        return true;
    }
    if (first >= 0xC2U && first <= 0xDFU) {
        if (offset + 2U > input.size()) {
            return false;
        }
        const auto second = byte_at(input, offset + 1U);
        if (!is_continuation(second)) {
            return false;
        }
        code_point = (static_cast<std::uint32_t>(first & 0x1FU) << 6U) |
            static_cast<std::uint32_t>(second & 0x3FU);
        length = 2U;
        return true;
    }
    if (first >= 0xE0U && first <= 0xEFU) {
        if (offset + 3U > input.size()) {
            return false;
        }
        const auto second = byte_at(input, offset + 1U);
        const auto third = byte_at(input, offset + 2U);
        const bool legal_second = is_continuation(second) &&
            (first != 0xE0U || second >= 0xA0U) &&
            (first != 0xEDU || second <= 0x9FU);
        if (!legal_second || !is_continuation(third)) {
            return false;
        }
        code_point = (static_cast<std::uint32_t>(first & 0x0FU) << 12U) |
            (static_cast<std::uint32_t>(second & 0x3FU) << 6U) |
            static_cast<std::uint32_t>(third & 0x3FU);
        length = 3U;
        return true;
    }
    if (first >= 0xF0U && first <= 0xF4U) {
        if (offset + 4U > input.size()) {
            return false;
        }
        const auto second = byte_at(input, offset + 1U);
        const auto third = byte_at(input, offset + 2U);
        const auto fourth = byte_at(input, offset + 3U);
        const bool legal_second = is_continuation(second) &&
            (first != 0xF0U || second >= 0x90U) &&
            (first != 0xF4U || second <= 0x8FU);
        if (!legal_second || !is_continuation(third) ||
            !is_continuation(fourth)) {
            return false;
        }
        code_point = (static_cast<std::uint32_t>(first & 0x07U) << 18U) |
            (static_cast<std::uint32_t>(second & 0x3FU) << 12U) |
            (static_cast<std::uint32_t>(third & 0x3FU) << 6U) |
            static_cast<std::uint32_t>(fourth & 0x3FU);
        length = 4U;
        return is_scalar(code_point);
    }
    return false;
}

bool try_decode_utf16_scalar(
    std::span<const std::uint16_t> input,
    std::size_t offset,
    std::uint32_t& code_point,
    std::uint8_t& length) noexcept {
    const std::uint16_t first = input[offset];
    if (first < 0xD800U || first > 0xDFFFU) {
        code_point = first;
        length = 1U;
        return true;
    }
    if (first > 0xDBFFU || offset + 2U > input.size()) {
        return false;
    }
    const std::uint16_t second = input[offset + 1U];
    if (second < 0xDC00U || second > 0xDFFFU) {
        return false;
    }
    code_point = 0x10000U +
        (static_cast<std::uint32_t>(first - 0xD800U) << 10U) +
        static_cast<std::uint32_t>(second - 0xDC00U);
    length = 2U;
    return true;
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

unicode_scalar make_scalar(
    std::uint32_t code_point,
    std::uint32_t input_index,
    std::uint8_t input_length) noexcept {
    return unicode_scalar{
        code_point,
        input_index,
        input_length,
        get_unicode_canonical_combining_class(code_point),
        0U,
        get_unicode_script(code_point)};
}

} // namespace

open_type_tag get_unicode_script(std::uint32_t code_point) noexcept {
    if (!is_scalar(code_point)) {
        return default_script;
    }
    const std::uint32_t index = find_range_value(
        code_point,
        std::span<const std::uint32_t>{detail::unicode_script_ranges},
        0U);
    if (index >= detail::unicode_script_tags.size()) {
        return default_script;
    }
    open_type_tag result{detail::unicode_script_tags[index]};
    if (result.value == 0U) {
        return default_script;
    }
    if (result == hiragana_script) {
        return kana_script;
    }
    if (result == lao_script) {
        return lao_layout_script;
    }
    return result;
}

std::uint8_t get_unicode_canonical_combining_class(
    std::uint32_t code_point) noexcept {
    if (!is_scalar(code_point)) {
        return 0U;
    }
    return static_cast<std::uint8_t>(find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_combining_class_ranges},
        0U));
}

bool try_get_utf8_decode_requirements(
    std::span<const std::byte> input,
    unicode_decode_requirements& result,
    unicode_error* error) noexcept {
    result = {};
    if (input.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    std::size_t offset = 0U;
    std::uint32_t count = 0U;
    while (offset < input.size()) {
        std::uint32_t code_point = 0U;
        std::uint8_t length = 0U;
        if (!try_decode_utf8_scalar(input, offset, code_point, length)) {
            set_error(error, unicode_error::invalid_encoding);
            return false;
        }
        offset += length;
        ++count;
    }
    result.scalar_count = count;
    set_error(error, unicode_error::none);
    return true;
}

bool try_decode_utf8(
    std::span<const std::byte> input,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    unicode_decode_requirements requirements{};
    if (!try_get_utf8_decode_requirements(input, requirements, error)) {
        return false;
    }
    if (output.size() < requirements.scalar_count) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    std::size_t offset = 0U;
    std::uint32_t index = 0U;
    while (offset < input.size()) {
        std::uint32_t code_point = 0U;
        std::uint8_t length = 0U;
        if (!try_decode_utf8_scalar(input, offset, code_point, length)) {
            set_error(error, unicode_error::invalid_encoding);
            return false;
        }
        output[index++] = make_scalar(
            code_point,
            static_cast<std::uint32_t>(offset),
            length);
        offset += length;
    }
    written = index;
    set_error(error, unicode_error::none);
    return true;
}

bool try_get_utf16_decode_requirements(
    std::span<const std::uint16_t> input,
    unicode_decode_requirements& result,
    unicode_error* error) noexcept {
    result = {};
    if (input.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    std::size_t offset = 0U;
    std::uint32_t count = 0U;
    while (offset < input.size()) {
        std::uint32_t code_point = 0U;
        std::uint8_t length = 0U;
        if (!try_decode_utf16_scalar(input, offset, code_point, length)) {
            set_error(error, unicode_error::invalid_encoding);
            return false;
        }
        offset += length;
        ++count;
    }
    result.scalar_count = count;
    set_error(error, unicode_error::none);
    return true;
}

bool try_decode_utf16(
    std::span<const std::uint16_t> input,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    unicode_decode_requirements requirements{};
    if (!try_get_utf16_decode_requirements(input, requirements, error)) {
        return false;
    }
    if (output.size() < requirements.scalar_count) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    std::size_t offset = 0U;
    std::uint32_t index = 0U;
    while (offset < input.size()) {
        std::uint32_t code_point = 0U;
        std::uint8_t length = 0U;
        if (!try_decode_utf16_scalar(input, offset, code_point, length)) {
            set_error(error, unicode_error::invalid_encoding);
            return false;
        }
        output[index++] = make_scalar(
            code_point,
            static_cast<std::uint32_t>(offset),
            length);
        offset += length;
    }
    written = index;
    set_error(error, unicode_error::none);
    return true;
}

} // namespace progpu::native::text
