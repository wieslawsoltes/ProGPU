#include "progpu_native_cff_type2_internal.hpp"

#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

// Direct native port provenance: ProGPU-owned bounded SFNT/font views and CFF
// evaluator architecture at checkpoint 83c4f6e2, specialized against the
// OpenType 1.9.1 CFF2 contract. Opening is O(F + V), for FontDICT count F and
// variation subtables V, with borrowed storage and fixed parser stacks.
namespace progpu::native::text {
namespace {

constexpr auto cff2_tag = open_type_tag::from_chars('C', 'F', 'F', '2');

using detail::can_read;
using detail::read_u16;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_to_offset(double value, std::uint32_t& result) noexcept {
    if (!std::isfinite(value) || value < 0.0 ||
        value > static_cast<double>(
            std::numeric_limits<std::int32_t>::max()) ||
        value != std::trunc(value)) {
        result = 0U;
        return false;
    }
    result = static_cast<std::uint32_t>(value);
    return true;
}

bool try_to_u16(double value, std::uint16_t& result) noexcept {
    if (!std::isfinite(value) || value < 0.0 ||
        value > static_cast<double>(
            std::numeric_limits<std::uint16_t>::max()) ||
        value != std::trunc(value)) {
        result = 0U;
        return false;
    }
    result = static_cast<std::uint16_t>(value);
    return true;
}

bool try_get_private_range(
    sfnt_cff_index_view font_dictionaries,
    std::uint32_t dictionary_index,
    std::uint32_t& size,
    std::uint32_t& offset) noexcept {
    size = 0U;
    offset = 0U;
    std::span<const std::byte> dictionary{};
    if (!sfnt_cff_data::try_get_index_item(
            font_dictionaries, dictionary_index, dictionary)) {
        return false;
    }
    std::array<double, 2U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    bool saw_private = false;
    while (cursor < dictionary.size()) {
        const auto value =
            std::to_integer<std::uint8_t>(dictionary[cursor++]);
        double number = 0.0;
        if (sfnt_cff_data::try_read_dictionary_number(
                dictionary, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor >= dictionary.size()) {
                return false;
            }
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(dictionary[cursor++]));
        }
        if (operation != 18U || operand_count != 2U || saw_private ||
            !try_to_offset(operands[0U], size) ||
            !try_to_offset(operands[1U], offset)) {
            return false;
        }
        saw_private = true;
        operand_count = 0U;
    }
    return saw_private && operand_count == 0U &&
        ((size == 0U && offset == 0U) ||
            (size != 0U && offset != 0U));
}

bool try_read_private_metadata(
    sfnt_cff2_font_view font,
    std::uint32_t private_size,
    std::uint32_t private_offset,
    sfnt_cff_index_view& local_subroutines,
    std::uint16_t& initial_vsindex) noexcept {
    local_subroutines = {};
    initial_vsindex = 0U;
    if (private_size == 0U) {
        return private_offset == 0U;
    }
    const auto offset = static_cast<std::size_t>(private_offset);
    const auto size = static_cast<std::size_t>(private_size);
    if (!can_read(font.bytes, offset, size)) {
        return false;
    }
    const auto dictionary = font.bytes.subspan(offset, size);
    std::array<double, 513U> operands{};
    std::size_t operand_count = 0U;
    std::size_t cursor = 0U;
    std::uint32_t operation_count = 0U;
    bool saw_vsindex = false;
    bool saw_subroutines = false;
    std::uint32_t subroutine_offset = 0U;
    while (cursor < dictionary.size()) {
        const auto value =
            std::to_integer<std::uint8_t>(dictionary[cursor++]);
        double number = 0.0;
        if (sfnt_cff_data::try_read_dictionary_number(
                dictionary, cursor, value, number)) {
            if (operand_count >= operands.size()) {
                return false;
            }
            operands[operand_count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor >= dictionary.size()) {
                return false;
            }
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(dictionary[cursor++]));
        }
        if (operation == 22U) {
            if (operation_count != 0U || operand_count != 1U ||
                saw_vsindex || font.variation_store.bytes.empty() ||
                !try_to_u16(operands[0U], initial_vsindex)) {
                return false;
            }
            std::uint16_t ignored = 0U;
            if (!sfnt_item_variation_data::try_get_region_scalar_count(
                    font.variation_store, initial_vsindex, ignored)) {
                return false;
            }
            saw_vsindex = true;
        } else if (operation == 19U) {
            if (operand_count != 1U || saw_subroutines ||
                !try_to_offset(operands[0U], subroutine_offset)) {
                return false;
            }
            saw_subroutines = true;
        }
        operand_count = 0U;
        ++operation_count;
    }
    if (operand_count != 0U) {
        return false;
    }
    if (!saw_subroutines) {
        return true;
    }
    if (subroutine_offset > font.bytes.size() - offset) {
        return false;
    }
    auto subroutine_cursor = offset + subroutine_offset;
    return sfnt_cff_data::try_read_cff2_index(
            font.bytes, subroutine_cursor, local_subroutines) &&
        local_subroutines.count <= 65536U;
}

} // namespace

bool sfnt_font_view::try_get_cff2_font(
    std::uint16_t expected_glyph_count,
    sfnt_cff2_font_view& result,
    font_error* error) const noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_table_view table{};
    if (!try_get_table(cff2_tag, table) ||
        !can_read(table.bytes, 0U, 5U) ||
        std::to_integer<std::uint8_t>(table.bytes[0U]) != 2U ||
        std::to_integer<std::uint8_t>(table.bytes[1U]) != 0U ||
        std::to_integer<std::uint8_t>(table.bytes[2U]) != 5U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto top_size = static_cast<std::size_t>(read_u16(table.bytes, 3U));
    if (!can_read(table.bytes, 5U, top_size)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    sfnt_cff2_top_dictionary top{};
    if (!sfnt_cff_data::try_get_cff2_top_dictionary(
            table.bytes.subspan(5U, top_size), top, error) ||
        top.char_strings_offset >= table.bytes.size() ||
        top.font_dictionary_offset >= table.bytes.size()) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    auto global_cursor = 5U + top_size;
    sfnt_cff_index_view global_subroutines{};
    if (!sfnt_cff_data::try_read_cff2_index(
            table.bytes, global_cursor, global_subroutines, error) ||
        global_subroutines.count > 65536U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    auto char_strings_cursor =
        static_cast<std::size_t>(top.char_strings_offset);
    sfnt_cff_index_view char_strings{};
    if (!sfnt_cff_data::try_read_cff2_index(
            table.bytes, char_strings_cursor, char_strings, error) ||
        char_strings.count == 0U ||
        (expected_glyph_count != 0U &&
            char_strings.count != expected_glyph_count)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    auto font_dictionary_cursor =
        static_cast<std::size_t>(top.font_dictionary_offset);
    sfnt_cff_index_view font_dictionaries{};
    if (!sfnt_cff_data::try_read_cff2_index(
            table.bytes,
            font_dictionary_cursor,
            font_dictionaries,
            error) ||
        font_dictionaries.count == 0U ||
        font_dictionaries.count >
            std::numeric_limits<std::uint16_t>::max()) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    sfnt_cff_fd_select_view fd_select{};
    if ((font_dictionaries.count == 1U && top.fd_select_offset != 0U) ||
        (font_dictionaries.count > 1U && top.fd_select_offset == 0U) ||
        (top.fd_select_offset != 0U &&
            !sfnt_cff_data::try_read_fd_select(
                table.bytes,
                top.fd_select_offset,
                char_strings.count,
                font_dictionaries.count,
                fd_select,
                error))) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    for (std::uint32_t dictionary = 0U;
        dictionary < font_dictionaries.count;
        ++dictionary) {
        std::uint32_t private_size = 0U;
        std::uint32_t private_offset = 0U;
        if (!try_get_private_range(
                font_dictionaries,
                dictionary,
                private_size,
                private_offset) ||
            (private_size != 0U &&
                !can_read(table.bytes, private_offset, private_size))) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }

    std::uint16_t axis_count = 0U;
    sfnt_item_variation_store_view variation_store{};
    if (top.variation_store_offset != 0U) {
        if (!try_get_variation_axis_count(axis_count, error) ||
            !can_read(table.bytes, top.variation_store_offset, 2U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto store_length = static_cast<std::size_t>(
            read_u16(table.bytes, top.variation_store_offset));
        const auto store_offset =
            static_cast<std::size_t>(top.variation_store_offset) + 2U;
        if (store_length == 0U ||
            !can_read(table.bytes, store_offset, store_length)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto store_bytes =
            table.bytes.subspan(store_offset, store_length);
        if (!sfnt_item_variation_data::try_get_store(
                store_bytes, 0U, axis_count, variation_store, error) ||
            variation_store.subtable_count == 0U) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        for (std::uint16_t index = 0U;
            index < variation_store.subtable_count;
            ++index) {
            std::uint16_t ignored = 0U;
            if (!sfnt_item_variation_data::try_get_region_scalar_count(
                    variation_store, index, ignored, error)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
        }
    }

    sfnt_header_metrics header{};
    if (!try_get_header_metrics(header) || header.units_per_em == 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto expected_scale = 1.0 /
        static_cast<double>(header.units_per_em);
    const auto scale_error =
        std::abs(top.font_matrix_scale - expected_scale);
    if ((header.units_per_em != 1000U && !top.has_font_matrix) ||
        scale_error > std::max(1.0, expected_scale) * 1.0e-12) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    result = {
        table.bytes,
        char_strings,
        global_subroutines,
        font_dictionaries,
        fd_select,
        variation_store,
        top,
        axis_count};
    return true;
}

namespace detail {

bool try_get_cff2_glyph_private(
    sfnt_cff2_font_view font,
    std::uint32_t glyph_index,
    sfnt_cff_index_view& local_subroutines,
    std::uint16_t& initial_vsindex,
    font_error* error) noexcept {
    local_subroutines = {};
    initial_vsindex = 0U;
    set_error(error, font_error::none);
    if (glyph_index >= font.char_strings.count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t dictionary_index = 0U;
    if (!font.fd_select.bytes.empty() &&
        !sfnt_cff_data::try_get_font_dictionary(
            font.fd_select, glyph_index, dictionary_index, error)) {
        return false;
    }
    std::uint32_t private_size = 0U;
    std::uint32_t private_offset = 0U;
    if (!try_get_private_range(
            font.font_dictionaries,
            dictionary_index,
            private_size,
            private_offset) ||
        !try_read_private_metadata(
            font,
            private_size,
            private_offset,
            local_subroutines,
            initial_vsindex)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

} // namespace detail
} // namespace progpu::native::text
