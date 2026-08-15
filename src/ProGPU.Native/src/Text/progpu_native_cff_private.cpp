#include "progpu_native_text.hpp"

#include <array>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource private-DICT
// and local-subroutine resolution at checkpoint 006069ab. The encoded INDEX
// remains borrowed and the parser uses a fixed 48-value operand stack.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

std::uint32_t to_offset(double value) noexcept {
    return value >= 0.0 &&
        value <= static_cast<double>(
            std::numeric_limits<std::int32_t>::max())
        ? static_cast<std::uint32_t>(value)
        : std::numeric_limits<std::uint32_t>::max();
}

bool try_read_private_dictionary(
    std::span<const std::byte> bytes,
    std::uint32_t& private_size,
    std::uint32_t& private_offset) noexcept {
    private_size = 0U;
    private_offset = 0U;
    std::array<double, 48U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    while (cursor < bytes.size()) {
        const auto value = std::to_integer<std::uint8_t>(bytes[cursor++]);
        double number = 0.0;
        if (sfnt_cff_data::try_read_dictionary_number(
                bytes, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor >= bytes.size()) {
                return false;
            }
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(bytes[cursor++]));
        }
        if (operation == 18U && operand_count >= 2U) {
            private_size = to_offset(operands[operand_count - 2U]);
            private_offset = to_offset(operands[operand_count - 1U]);
        }
        operand_count = 0U;
    }
    return private_size != std::numeric_limits<std::uint32_t>::max() &&
        private_offset != std::numeric_limits<std::uint32_t>::max();
}

bool try_read_private_subroutine_offset(
    std::span<const std::byte> bytes,
    std::uint32_t& result) noexcept {
    result = 0U;
    std::array<double, 48U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    while (cursor < bytes.size()) {
        const auto value = std::to_integer<std::uint8_t>(bytes[cursor++]);
        double number = 0.0;
        if (sfnt_cff_data::try_read_dictionary_number(
                bytes, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        if (value == 12U) {
            if (cursor >= bytes.size()) {
                return false;
            }
            ++cursor;
        } else if (value == 19U && operand_count >= 1U) {
            result = to_offset(operands[operand_count - 1U]);
            return result != std::numeric_limits<std::uint32_t>::max();
        }
        operand_count = 0U;
    }
    return false;
}

} // namespace

bool sfnt_cff_data::try_read_local_subroutines(
    std::span<const std::byte> bytes,
    std::uint32_t private_offset,
    std::uint32_t private_size,
    sfnt_cff_index_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    const auto offset = static_cast<std::size_t>(private_offset);
    const auto size = static_cast<std::size_t>(private_size);
    if (offset > bytes.size() || size > bytes.size() - offset) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (size == 0U) {
        return true;
    }
    std::uint32_t relative_offset = 0U;
    if (!try_read_private_subroutine_offset(
            bytes.subspan(offset, size), relative_offset)) {
        return true;
    }
    if (relative_offset > bytes.size() - offset) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    auto cursor = offset + relative_offset;
    return try_read_index(bytes, cursor, result, error);
}

bool sfnt_cff_data::try_get_local_subroutines(
    sfnt_cff1_font_view font,
    std::uint32_t glyph_index,
    sfnt_cff_index_view& result,
    font_error* error) noexcept {
    result = font.default_local_subroutines;
    set_error(error, font_error::none);
    if (glyph_index >= font.char_strings.count) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (font.fd_select.bytes.empty()) {
        return true;
    }

    std::uint32_t dictionary_index = 0U;
    if (!try_get_font_dictionary(
            font.fd_select, glyph_index, dictionary_index, error)) {
        result = {};
        return false;
    }
    std::span<const std::byte> dictionary{};
    if (!try_get_index_item(
            font.font_dictionaries, dictionary_index, dictionary, error)) {
        result = {};
        return false;
    }
    std::uint32_t private_size = 0U;
    std::uint32_t private_offset = 0U;
    if (!try_read_private_dictionary(
            dictionary, private_size, private_offset) ||
        private_size == 0U) {
        result = {};
        set_error(error, font_error::none);
        return true;
    }
    return try_read_local_subroutines(
        font.bytes, private_offset, private_size, result, error);
}

} // namespace progpu::native::text
