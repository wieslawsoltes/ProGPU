#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <string_view>

namespace progpu::native::text::svg_number_detail {

// Locale-independent SVG number parser. Floating-point std::from_chars is not
// available in the Apple libc++ used by the supported Intel macOS runner.
// Parsing is O(B) for B token bytes with O(1) storage.
inline bool try_parse(
    std::string_view text,
    std::size_t& index,
    float& result) noexcept {
    const std::size_t start = index;
    bool negative = false;
    if (index < text.size() &&
        (text[index] == '+' || text[index] == '-')) {
        negative = text[index] == '-';
        ++index;
    }

    std::uint64_t significand = 0U;
    std::size_t collected_digits = 0U;
    std::size_t combined_digits = 0U;
    std::size_t first_significant_digit = 0U;
    std::size_t integer_digits = 0U;
    bool has_digit = false;
    bool has_significant_digit = false;

    const auto consume_digit = [&](char character) noexcept {
        has_digit = true;
        const auto digit = static_cast<std::uint32_t>(character - '0');
        if (!has_significant_digit && digit != 0U) {
            has_significant_digit = true;
            first_significant_digit = combined_digits;
        }
        if (has_significant_digit && collected_digits < 19U) {
            significand = significand * 10U + digit;
            ++collected_digits;
        }
        ++combined_digits;
    };

    while (index < text.size() && text[index] >= '0' && text[index] <= '9') {
        consume_digit(text[index++]);
        ++integer_digits;
    }
    if (index < text.size() && text[index] == '.') {
        ++index;
        while (index < text.size() &&
            text[index] >= '0' && text[index] <= '9') {
            consume_digit(text[index++]);
        }
    }
    if (!has_digit) {
        index = start;
        return false;
    }

    std::int64_t explicit_exponent = 0;
    if (index < text.size() &&
        (text[index] == 'e' || text[index] == 'E')) {
        const std::size_t exponent_start = index++;
        bool exponent_negative = false;
        if (index < text.size() &&
            (text[index] == '+' || text[index] == '-')) {
            exponent_negative = text[index] == '-';
            ++index;
        }
        const std::size_t exponent_digits = index;
        while (index < text.size() &&
            text[index] >= '0' && text[index] <= '9') {
            const std::int64_t digit = text[index++] - '0';
            if (explicit_exponent < 1000000) {
                explicit_exponent = explicit_exponent * 10 + digit;
                if (explicit_exponent > 1000000) {
                    explicit_exponent = 1000000;
                }
            }
        }
        if (index == exponent_digits) {
            index = exponent_start;
            explicit_exponent = 0;
        } else if (exponent_negative) {
            explicit_exponent = -explicit_exponent;
        }
    }

    if (!has_significant_digit) {
        result = negative ? -0.0F : 0.0F;
        return true;
    }

    const auto decimal_exponent =
        static_cast<std::int64_t>(integer_digits) -
        static_cast<std::int64_t>(first_significant_digit) -
        static_cast<std::int64_t>(collected_digits) + explicit_exponent;
    if (decimal_exponent > 10000) {
        index = start;
        return false;
    }
    long double value = static_cast<long double>(significand);
    if (decimal_exponent < -10000) {
        value = 0.0L;
    } else {
        value *= std::pow(10.0L, static_cast<long double>(decimal_exponent));
    }
    if (negative) {
        value = -value;
    }
    const float converted = static_cast<float>(value);
    if (!std::isfinite(value) || !std::isfinite(converted)) {
        index = start;
        return false;
    }
    result = converted;
    return true;
}

inline bool try_parse_exact(std::string_view text, float& result) noexcept {
    std::size_t index = 0U;
    return try_parse(text, index, result) && index == text.size();
}

} // namespace progpu::native::text::svg_number_detail
