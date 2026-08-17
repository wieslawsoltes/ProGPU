#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"
#include "../Unicode/progpu_native_unicode_categories.generated.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned SfntFontFace.GetNames,
// DecodeName, and GetNameScore plus SfntFontMetadataReader.ReadNameTable at
// repository checkpoint 654fbd97. The native API preserves record selection,
// Unicode decoding, NUL removal, and trim behavior in caller-owned UTF-8.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;

constexpr auto name_tag = open_type_tag::from_chars('n', 'a', 'm', 'e');
constexpr std::uint32_t replacement_character = 0xFFFDU;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool is_continuation(std::uint8_t value) noexcept {
    return (value & 0xC0U) == 0x80U;
}

std::uint8_t byte_at(
    std::span<const std::byte> input,
    std::size_t index) noexcept {
    return std::to_integer<std::uint8_t>(input[index]);
}

void decode_utf8_scalar(
    std::span<const std::byte> input,
    std::size_t offset,
    std::uint32_t& code_point,
    std::size_t& consumed) noexcept {
    const auto first = byte_at(input, offset);
    code_point = replacement_character;
    consumed = 1U;
    if (first <= 0x7FU) {
        code_point = first;
        return;
    }
    if (first >= 0xC2U && first <= 0xDFU &&
        can_read(input, offset, 2U)) {
        const auto second = byte_at(input, offset + 1U);
        if (is_continuation(second)) {
            code_point = (static_cast<std::uint32_t>(first & 0x1FU) << 6U) |
                static_cast<std::uint32_t>(second & 0x3FU);
            consumed = 2U;
        }
        return;
    }
    if (first >= 0xE0U && first <= 0xEFU &&
        can_read(input, offset, 3U)) {
        const auto second = byte_at(input, offset + 1U);
        const auto third = byte_at(input, offset + 2U);
        if (is_continuation(second) && is_continuation(third) &&
            (first != 0xE0U || second >= 0xA0U) &&
            (first != 0xEDU || second <= 0x9FU)) {
            code_point =
                (static_cast<std::uint32_t>(first & 0x0FU) << 12U) |
                (static_cast<std::uint32_t>(second & 0x3FU) << 6U) |
                static_cast<std::uint32_t>(third & 0x3FU);
            consumed = 3U;
        }
        return;
    }
    if (first >= 0xF0U && first <= 0xF4U &&
        can_read(input, offset, 4U)) {
        const auto second = byte_at(input, offset + 1U);
        const auto third = byte_at(input, offset + 2U);
        const auto fourth = byte_at(input, offset + 3U);
        if (is_continuation(second) && is_continuation(third) &&
            is_continuation(fourth) &&
            (first != 0xF0U || second >= 0x90U) &&
            (first != 0xF4U || second <= 0x8FU)) {
            code_point =
                (static_cast<std::uint32_t>(first & 0x07U) << 18U) |
                (static_cast<std::uint32_t>(second & 0x3FU) << 12U) |
                (static_cast<std::uint32_t>(third & 0x3FU) << 6U) |
                static_cast<std::uint32_t>(fourth & 0x3FU);
            consumed = 4U;
        }
    }
}

void decode_scalar(
    std::span<const std::byte> input,
    std::uint16_t platform_id,
    std::uint16_t encoding_id,
    std::size_t offset,
    std::uint32_t& code_point,
    std::size_t& consumed) noexcept {
    if (platform_id == 0U || platform_id == 3U) {
        if (!can_read(input, offset, 2U)) {
            code_point = replacement_character;
            consumed = input.size() - offset;
            return;
        }
        const auto first = read_u16(input, offset);
        consumed = 2U;
        if (first >= 0xD800U && first <= 0xDBFFU &&
            can_read(input, offset, 4U)) {
            const auto second = read_u16(input, offset + 2U);
            if (second >= 0xDC00U && second <= 0xDFFFU) {
                code_point = 0x10000U +
                    (static_cast<std::uint32_t>(first - 0xD800U) << 10U) +
                    static_cast<std::uint32_t>(second - 0xDC00U);
                consumed = 4U;
                return;
            }
        }
        code_point = first >= 0xD800U && first <= 0xDFFFU
            ? replacement_character
            : first;
        return;
    }
    if (platform_id == 1U && encoding_id == 0U) {
        code_point = byte_at(input, offset);
        consumed = 1U;
        return;
    }
    decode_utf8_scalar(input, offset, code_point, consumed);
}

bool is_white_space(std::uint32_t code_point) noexcept {
    return (code_point >= 0x09U && code_point <= 0x0DU) ||
        code_point == 0x20U || code_point == 0x85U ||
        code_point == 0xA0U || code_point == 0x1680U ||
        (code_point >= 0x2000U && code_point <= 0x200AU) ||
        code_point == 0x2028U || code_point == 0x2029U ||
        code_point == 0x202FU || code_point == 0x205FU ||
        code_point == 0x3000U;
}

bool is_non_latin_bmp_letter(std::uint32_t code_point) noexcept {
    if (code_point <= 0x024FU || code_point > 0xFFFFU) return false;
    const auto ranges = std::span<const std::uint16_t>{
        detail::sfnt_name_letter_ranges};
    std::size_t low = 0U;
    std::size_t high = ranges.size() / 2U;
    while (low < high) {
        const auto middle = low + (high - low) / 2U;
        const auto start = ranges[middle * 2U];
        const auto end = ranges[middle * 2U + 1U];
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

std::size_t utf8_length(std::uint32_t code_point) noexcept {
    if (code_point <= 0x7FU) return 1U;
    if (code_point <= 0x7FFU) return 2U;
    if (code_point <= 0xFFFFU) return 3U;
    return 4U;
}

struct decoded_name final {
    std::size_t first = 0U;
    std::size_t last = 0U;
    std::size_t utf8_bytes = 0U;
    bool has_content = false;
    bool has_non_latin_letter = false;
};

decoded_name analyze_name(
    std::span<const std::byte> bytes,
    std::uint16_t platform_id,
    std::uint16_t encoding_id) noexcept {
    decoded_name result{};
    std::size_t filtered_index = 0U;
    for (std::size_t offset = 0U; offset < bytes.size();) {
        std::uint32_t code_point = 0U;
        std::size_t consumed = 0U;
        decode_scalar(bytes, platform_id, encoding_id, offset,
            code_point, consumed);
        if (consumed == 0U) break;
        offset += consumed;
        if (code_point == 0U) continue;
        if (!is_white_space(code_point)) {
            if (!result.has_content) result.first = filtered_index;
            result.last = filtered_index;
            result.has_content = true;
        }
        result.has_non_latin_letter |=
            is_non_latin_bmp_letter(code_point);
        ++filtered_index;
    }
    if (!result.has_content) return result;
    filtered_index = 0U;
    for (std::size_t offset = 0U; offset < bytes.size();) {
        std::uint32_t code_point = 0U;
        std::size_t consumed = 0U;
        decode_scalar(bytes, platform_id, encoding_id, offset,
            code_point, consumed);
        if (consumed == 0U) break;
        offset += consumed;
        if (code_point == 0U) continue;
        if (filtered_index >= result.first && filtered_index <= result.last) {
            result.utf8_bytes += utf8_length(code_point);
        }
        ++filtered_index;
    }
    return result;
}

std::int32_t name_score(
    std::uint16_t platform_id,
    std::uint16_t language_id,
    bool has_non_latin_letter) noexcept {
    std::int32_t result = platform_id == 3U && language_id == 0x0409U
        ? 4
        : platform_id == 3U ? 3 : platform_id == 0U ? 2 : 1;
    return has_non_latin_letter ? result : result + 10;
}

struct selected_name final {
    std::span<const std::byte> bytes{};
    sfnt_name_requirements requirements{};
    decoded_name decoded{};
};

bool select_name(
    const sfnt_font_view& font,
    std::uint16_t name_id,
    selected_name& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    sfnt_table_view table{};
    if (!font.try_get_table(name_tag, table)) return false;
    if (table.bytes.size() < 6U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto record_count = read_u16(table.bytes, 2U);
    const auto string_offset = read_u16(table.bytes, 4U);
    if (!can_read(table.bytes, 6U,
            static_cast<std::size_t>(record_count) * 12U) ||
        string_offset > table.bytes.size()) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    bool found = false;
    for (std::uint16_t index = 0U; index < record_count; ++index) {
        const auto record = 6U + static_cast<std::size_t>(index) * 12U;
        if (read_u16(table.bytes, record + 6U) != name_id) continue;
        const auto platform_id = read_u16(table.bytes, record);
        const auto encoding_id = read_u16(table.bytes, record + 2U);
        const auto language_id = read_u16(table.bytes, record + 4U);
        const auto length = read_u16(table.bytes, record + 8U);
        const auto relative = read_u16(table.bytes, record + 10U);
        const auto value_offset = static_cast<std::size_t>(string_offset) +
            relative;
        if (length == 0U || !can_read(table.bytes, value_offset, length)) {
            continue;
        }
        const auto bytes = table.bytes.subspan(value_offset, length);
        const auto decoded = analyze_name(bytes, platform_id, encoding_id);
        if (!decoded.has_content) continue;
        const auto score = name_score(
            platform_id, language_id, decoded.has_non_latin_letter);
        if (!found || score > result.requirements.score) {
            found = true;
            result.bytes = bytes;
            result.decoded = decoded;
            result.requirements = sfnt_name_requirements{
                decoded.utf8_bytes, score, platform_id, encoding_id,
                language_id};
        }
    }
    return found;
}

void write_utf8(
    std::uint32_t code_point,
    std::span<char> destination,
    std::size_t& offset) noexcept {
    if (code_point <= 0x7FU) {
        destination[offset++] = static_cast<char>(code_point);
    } else if (code_point <= 0x7FFU) {
        destination[offset++] = static_cast<char>(0xC0U | code_point >> 6U);
        destination[offset++] = static_cast<char>(
            0x80U | (code_point & 0x3FU));
    } else if (code_point <= 0xFFFFU) {
        destination[offset++] = static_cast<char>(0xE0U | code_point >> 12U);
        destination[offset++] = static_cast<char>(
            0x80U | ((code_point >> 6U) & 0x3FU));
        destination[offset++] = static_cast<char>(
            0x80U | (code_point & 0x3FU));
    } else {
        destination[offset++] = static_cast<char>(0xF0U | code_point >> 18U);
        destination[offset++] = static_cast<char>(
            0x80U | ((code_point >> 12U) & 0x3FU));
        destination[offset++] = static_cast<char>(
            0x80U | ((code_point >> 6U) & 0x3FU));
        destination[offset++] = static_cast<char>(
            0x80U | (code_point & 0x3FU));
    }
}

} // namespace

bool sfnt_font_view::try_get_name_requirements(
    std::uint16_t name_id,
    sfnt_name_requirements& result,
    font_error* error) const noexcept {
    result = {};
    selected_name selected{};
    if (!select_name(*this, name_id, selected, error)) return false;
    result = selected.requirements;
    return true;
}

bool sfnt_font_view::try_decode_name(
    std::uint16_t name_id,
    std::span<char> utf8,
    std::size_t& written,
    sfnt_name_requirements* requirements,
    font_error* error) const noexcept {
    written = 0U;
    if (requirements != nullptr) *requirements = {};
    selected_name selected{};
    if (!select_name(*this, name_id, selected, error)) return false;
    if (requirements != nullptr) *requirements = selected.requirements;
    if (utf8.size() < selected.requirements.utf8_bytes) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::size_t filtered_index = 0U;
    std::size_t output_offset = 0U;
    for (std::size_t offset = 0U; offset < selected.bytes.size();) {
        std::uint32_t code_point = 0U;
        std::size_t consumed = 0U;
        decode_scalar(selected.bytes, selected.requirements.platform_id,
            selected.requirements.encoding_id, offset, code_point, consumed);
        if (consumed == 0U) break;
        offset += consumed;
        if (code_point == 0U) continue;
        if (filtered_index >= selected.decoded.first &&
            filtered_index <= selected.decoded.last) {
            write_utf8(code_point, utf8, output_offset);
        }
        ++filtered_index;
    }
    written = output_offset;
    return true;
}

} // namespace progpu::native::text
