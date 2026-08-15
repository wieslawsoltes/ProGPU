#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource DICT number
// and top-dictionary readers at checkpoint 2f152ddd. Parsing uses fixed stack
// storage and locale-independent numeric conversion.
namespace progpu::native::text {
namespace {

using detail::read_i16;
using detail::read_u32;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool append_real_nibble(
    std::uint8_t nibble,
    std::span<char> destination,
    std::size_t& length,
    bool& ended) noexcept {
    ended = false;
    if (nibble == 15U) {
        ended = true;
        return true;
    }
    std::array<char, 2U> encoded{};
    std::size_t count = 0U;
    if (nibble <= 9U) {
        encoded[0] = static_cast<char>('0' + nibble);
        count = 1U;
    } else if (nibble == 10U) {
        encoded[0] = '.';
        count = 1U;
    } else if (nibble == 11U) {
        encoded[0] = 'E';
        count = 1U;
    } else if (nibble == 12U) {
        encoded[0] = 'E';
        encoded[1] = '-';
        count = 2U;
    } else if (nibble == 14U) {
        encoded[0] = '-';
        count = 1U;
    } else if (nibble == 13U) {
        return false;
    }
    if (count > destination.size() - length) {
        return false;
    }
    for (std::size_t index = 0U; index < count; ++index) {
        destination[length++] = encoded[index];
    }
    return true;
}

bool try_parse_real(
    std::span<const char> encoded,
    double& result) noexcept {
    result = 0.0;
    if (encoded.empty()) {
        return false;
    }
    std::size_t cursor = 0U;
    auto negative = false;
    if (encoded[cursor] == '-') {
        negative = true;
        if (++cursor == encoded.size()) {
            return false;
        }
    }
    double significand = 0.0;
    std::int32_t fractional_digits = 0;
    auto saw_digit = false;
    auto saw_decimal = false;
    while (cursor < encoded.size() && encoded[cursor] != 'E') {
        const auto value = encoded[cursor++];
        if (value == '.') {
            if (saw_decimal) {
                return false;
            }
            saw_decimal = true;
            continue;
        }
        if (value < '0' || value > '9') {
            return false;
        }
        saw_digit = true;
        significand = significand * 10.0 +
            static_cast<double>(value - '0');
        if (saw_decimal) {
            ++fractional_digits;
        }
    }
    if (!saw_digit) {
        return false;
    }
    std::int32_t exponent = 0;
    if (cursor < encoded.size()) {
        ++cursor;
        auto exponent_negative = false;
        if (cursor < encoded.size() && encoded[cursor] == '-') {
            exponent_negative = true;
            ++cursor;
        }
        const auto first_exponent_digit = cursor;
        while (cursor < encoded.size()) {
            const auto value = encoded[cursor++];
            if (value < '0' || value > '9' || exponent > 4096) {
                return false;
            }
            exponent = exponent * 10 + (value - '0');
        }
        if (cursor == first_exponent_digit) {
            return false;
        }
        if (exponent_negative) {
            exponent = -exponent;
        }
    }
    const auto scale = exponent - fractional_digits;
    result = significand * std::pow(10.0, static_cast<double>(scale));
    if (negative) {
        result = -result;
    }
    return std::isfinite(result);
}

std::uint32_t to_offset(double value) noexcept {
    return std::isfinite(value) && value >= 0.0 &&
        value <= std::numeric_limits<std::int32_t>::max()
        ? static_cast<std::uint32_t>(value)
        : std::numeric_limits<std::uint32_t>::max();
}

} // namespace

bool sfnt_cff_data::try_read_dictionary_number(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::uint8_t first,
    double& result) noexcept {
    result = 0.0;
    if (first >= 32U && first <= 246U) {
        result = static_cast<double>(first) - 139.0;
        return true;
    }
    if (first >= 247U && first <= 250U) {
        if (cursor >= bytes.size()) {
            return false;
        }
        result = static_cast<double>((first - 247U) * 256U +
            std::to_integer<std::uint8_t>(bytes[cursor++]) + 108U);
        return true;
    }
    if (first >= 251U && first <= 254U) {
        if (cursor >= bytes.size()) {
            return false;
        }
        result = -static_cast<double>((first - 251U) * 256U +
            std::to_integer<std::uint8_t>(bytes[cursor++]) + 108U);
        return true;
    }
    if (first == 28U) {
        if (cursor > bytes.size() || bytes.size() - cursor < 2U) {
            return false;
        }
        result = read_i16(bytes, cursor);
        cursor += 2U;
        return true;
    }
    if (first == 29U) {
        if (cursor > bytes.size() || bytes.size() - cursor < 4U) {
            return false;
        }
        result = std::bit_cast<std::int32_t>(read_u32(bytes, cursor));
        cursor += 4U;
        return true;
    }
    if (first != 30U) {
        return false;
    }

    std::array<char, 96U> encoded{};
    std::size_t length = 0U;
    bool ended = false;
    while (cursor < bytes.size() && !ended) {
        const auto pair = std::to_integer<std::uint8_t>(bytes[cursor++]);
        if (!append_real_nibble(
                pair >> 4U, encoded, length, ended)) {
            return false;
        }
        if (!ended && !append_real_nibble(
                pair & 0x0FU, encoded, length, ended)) {
            return false;
        }
    }
    if (!ended || length == 0U) {
        return false;
    }
    return try_parse_real(
        std::span<const char>{encoded}.first(length), result);
}

bool sfnt_cff_data::try_get_top_dictionary(
    std::span<const std::byte> bytes,
    sfnt_cff1_top_dictionary& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    std::array<double, 48U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    while (cursor < bytes.size()) {
        const auto value = std::to_integer<std::uint8_t>(bytes[cursor++]);
        double number = 0.0;
        if (try_read_dictionary_number(bytes, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor >= bytes.size()) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(bytes[cursor++]));
        }
        if (operation == 17U && operand_count >= 1U) {
            result.char_strings_offset = to_offset(
                operands[operand_count - 1U]);
        } else if (operation == 18U && operand_count >= 2U) {
            result.private_size = to_offset(
                operands[operand_count - 2U]);
            result.private_offset = to_offset(
                operands[operand_count - 1U]);
        } else if (operation == 0x0C24U && operand_count >= 1U) {
            result.font_dictionary_offset = to_offset(
                operands[operand_count - 1U]);
        } else if (operation == 0x0C25U && operand_count >= 1U) {
            result.fd_select_offset = to_offset(
                operands[operand_count - 1U]);
        }
        operand_count = 0U;
    }
    if (result.char_strings_offset == 0U ||
        result.char_strings_offset ==
            std::numeric_limits<std::uint32_t>::max() ||
        result.private_size == std::numeric_limits<std::uint32_t>::max() ||
        result.private_offset == std::numeric_limits<std::uint32_t>::max() ||
        result.font_dictionary_offset ==
            std::numeric_limits<std::uint32_t>::max() ||
        result.fd_select_offset ==
            std::numeric_limits<std::uint32_t>::max()) {
        result = {};
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

bool sfnt_cff_data::try_get_cff2_top_dictionary(
    std::span<const std::byte> bytes,
    sfnt_cff2_top_dictionary& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    std::array<double, 48U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    bool saw_char_strings = false;
    bool saw_font_dictionaries = false;
    bool saw_fd_select = false;
    bool saw_variation_store = false;
    bool saw_font_matrix = false;
    while (cursor < bytes.size()) {
        const auto value = std::to_integer<std::uint8_t>(bytes[cursor++]);
        double number = 0.0;
        if (try_read_dictionary_number(bytes, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor >= bytes.size()) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(bytes[cursor++]));
        }
        if (operation == 17U && operand_count == 1U &&
            !saw_char_strings) {
            result.char_strings_offset = to_offset(operands[0U]);
            saw_char_strings = true;
        } else if (operation == 24U && operand_count == 1U &&
            !saw_variation_store) {
            result.variation_store_offset = to_offset(operands[0U]);
            saw_variation_store = true;
        } else if (operation == 0x0C24U && operand_count == 1U &&
            !saw_font_dictionaries) {
            result.font_dictionary_offset = to_offset(operands[0U]);
            saw_font_dictionaries = true;
        } else if (operation == 0x0C25U && operand_count == 1U &&
            !saw_fd_select) {
            result.fd_select_offset = to_offset(operands[0U]);
            saw_fd_select = true;
        } else if (operation == 0x0C07U && operand_count == 6U &&
            !saw_font_matrix && operands[0U] == operands[3U] &&
            operands[1U] == 0.0 && operands[2U] == 0.0 &&
            operands[4U] == 0.0 && operands[5U] == 0.0 &&
            operands[0U] > 0.0 && std::isfinite(operands[0U])) {
            result.font_matrix_scale = operands[0U];
            result.has_font_matrix = true;
            saw_font_matrix = true;
        } else {
            result = {};
            set_error(error, font_error::invalid_face);
            return false;
        }
        operand_count = 0U;
    }
    if (operand_count != 0U || !saw_char_strings ||
        !saw_font_dictionaries || result.char_strings_offset == 0U ||
        result.font_dictionary_offset == 0U ||
        result.char_strings_offset ==
            std::numeric_limits<std::uint32_t>::max() ||
        result.font_dictionary_offset ==
            std::numeric_limits<std::uint32_t>::max() ||
        result.fd_select_offset ==
            std::numeric_limits<std::uint32_t>::max() ||
        result.variation_store_offset ==
            std::numeric_limits<std::uint32_t>::max()) {
        result = {};
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

} // namespace progpu::native::text
