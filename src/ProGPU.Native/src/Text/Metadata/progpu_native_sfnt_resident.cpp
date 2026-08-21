#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// SfntFontMetadataReader.TryBuildGlyphSbix, TryReadSbixGlyphRecord, and
// TryCreateGlyphResidentFont at repository checkpoint 83018287. Construction
// remains cold font-cache work and writes one caller-owned immutable snapshot.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

constexpr auto sbix_tag = open_type_tag::from_chars('s', 'b', 'i', 'x');
constexpr auto dupe_tag = open_type_tag::from_chars('d', 'u', 'p', 'e');
constexpr auto png_tag = open_type_tag::from_chars('p', 'n', 'g', ' ');
constexpr auto jpeg_tag = open_type_tag::from_chars('j', 'p', 'g', ' ');
constexpr auto tiff_tag = open_type_tag::from_chars('t', 'i', 'f', 'f');
constexpr std::uint32_t maximum_strike_count = 4096U;
constexpr std::uint16_t maximum_table_count = 4096U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

bool try_align4(std::size_t value, std::size_t& result) noexcept {
    if (value > std::numeric_limits<std::size_t>::max() - 3U) return false;
    result = (value + 3U) & ~std::size_t{3U};
    return true;
}

void write_u16(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint16_t value) noexcept {
    destination[offset] = static_cast<std::byte>(value >> 8U);
    destination[offset + 1U] = static_cast<std::byte>(value);
}

void write_u32(
    std::span<std::byte> destination,
    std::size_t offset,
    std::uint32_t value) noexcept {
    destination[offset] = static_cast<std::byte>(value >> 24U);
    destination[offset + 1U] = static_cast<std::byte>(value >> 16U);
    destination[offset + 2U] = static_cast<std::byte>(value >> 8U);
    destination[offset + 3U] = static_cast<std::byte>(value);
}

struct resolved_record final {
    std::span<const std::byte> bytes{};
    std::int16_t origin_x = 0;
    std::int16_t origin_y = 0;
};

bool try_resolve_record(
    std::span<const std::byte> sbix,
    std::size_t strike_offset,
    std::size_t strike_end,
    std::uint16_t glyph_count,
    std::uint16_t glyph_index,
    std::uint16_t original_glyph_index,
    std::uint32_t depth,
    resolved_record& result) noexcept {
    result = {};
    const auto offsets_bytes =
        (static_cast<std::size_t>(glyph_count) + 1U) * 4U;
    if (depth > 16U || glyph_index >= glyph_count ||
        strike_offset >= strike_end || strike_end > sbix.size() ||
        !can_read(sbix, strike_offset, 4U + offsets_bytes)) {
        return false;
    }
    const auto offset_entry = strike_offset + 4U +
        static_cast<std::size_t>(glyph_index) * 4U;
    const auto start_relative = read_u32(sbix, offset_entry);
    const auto end_relative = read_u32(sbix, offset_entry + 4U);
    const auto strike_length = strike_end - strike_offset;
    if (start_relative >= end_relative || start_relative > strike_length ||
        end_relative > strike_length || end_relative - start_relative < 8U) {
        return false;
    }
    const auto start = strike_offset + start_relative;
    const auto end = strike_offset + end_relative;
    if (start < strike_offset + 4U + offsets_bytes || end > strike_end) {
        return false;
    }
    const auto record = sbix.subspan(start, end - start);
    const auto origin_x = read_i16(record, 0U);
    const auto origin_y = read_i16(record, 2U);
    const open_type_tag graphic_type{read_u32(record, 4U)};
    if (graphic_type == dupe_tag) {
        if (record.size() < 10U) return false;
        const auto duplicate_glyph = read_u16(record, 8U);
        resolved_record duplicate{};
        if (duplicate_glyph == original_glyph_index ||
            !try_resolve_record(sbix, strike_offset, strike_end, glyph_count,
                duplicate_glyph, original_glyph_index, depth + 1U,
                duplicate)) {
            return false;
        }
        result = {duplicate.bytes, origin_x, origin_y};
        return true;
    }
    if (graphic_type != png_tag && graphic_type != jpeg_tag &&
        graphic_type != tiff_tag) {
        return false;
    }
    result = {record, origin_x, origin_y};
    return true;
}

struct resident_source final {
    std::span<const std::byte> sbix{};
    std::uint16_t glyph_count = 0U;
    std::uint16_t glyph_index = 0U;
    std::uint32_t strike_count = 0U;
    std::size_t sbix_bytes = 0U;
};

bool inspect_resident_source(
    const sfnt_font_view& font,
    std::uint16_t glyph_index,
    resident_source& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    std::uint16_t glyph_count = 0U;
    sfnt_table_view table{};
    if (!font.try_get_glyph_count(glyph_count) || glyph_index >= glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!font.try_get_table(sbix_tag, table) || table.bytes.size() < 8U ||
        read_u16(table.bytes, 0U) != 1U) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    const auto strike_count = read_u32(table.bytes, 4U);
    if (strike_count == 0U || strike_count > maximum_strike_count ||
        !can_read(table.bytes, 8U,
            static_cast<std::size_t>(strike_count) * 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::size_t output_bytes = 8U +
        static_cast<std::size_t>(strike_count) * 4U;
    const auto glyph_offsets_bytes =
        (static_cast<std::size_t>(glyph_count) + 1U) * 4U;
    for (std::uint32_t strike = 0U; strike < strike_count; ++strike) {
        const auto strike_offset = read_u32(
            table.bytes, 8U + static_cast<std::size_t>(strike) * 4U);
        const auto strike_end = strike + 1U < strike_count
            ? read_u32(table.bytes,
                8U + static_cast<std::size_t>(strike + 1U) * 4U)
            : static_cast<std::uint32_t>(table.bytes.size());
        if (strike_offset >= strike_end || strike_end > table.bytes.size() ||
            !can_read(table.bytes, strike_offset, 4U + glyph_offsets_bytes)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        resolved_record record{};
        (void)try_resolve_record(table.bytes, strike_offset, strike_end,
            glyph_count, glyph_index, glyph_index, 0U, record);
        const auto strike_bytes = 4U + glyph_offsets_bytes +
            record.bytes.size();
        if (output_bytes >
            std::numeric_limits<std::size_t>::max() - strike_bytes) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        output_bytes += strike_bytes;
    }
    if (output_bytes > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = {table.bytes, glyph_count, glyph_index, strike_count, output_bytes};
    return true;
}

void write_resident_sbix(
    const resident_source& source,
    std::span<std::byte> output) noexcept {
    std::copy_n(source.sbix.begin(), 8U, output.begin());
    std::size_t target = 8U +
        static_cast<std::size_t>(source.strike_count) * 4U;
    const auto glyph_offsets_bytes =
        (static_cast<std::size_t>(source.glyph_count) + 1U) * 4U;
    for (std::uint32_t strike = 0U; strike < source.strike_count; ++strike) {
        write_u32(output, 8U + static_cast<std::size_t>(strike) * 4U,
            static_cast<std::uint32_t>(target));
        const auto strike_offset = read_u32(
            source.sbix, 8U + static_cast<std::size_t>(strike) * 4U);
        const auto strike_end = strike + 1U < source.strike_count
            ? read_u32(source.sbix,
                8U + static_cast<std::size_t>(strike + 1U) * 4U)
            : static_cast<std::uint32_t>(source.sbix.size());
        resolved_record record{};
        const auto has_record = try_resolve_record(source.sbix,
            strike_offset, strike_end, source.glyph_count,
            source.glyph_index, source.glyph_index, 0U, record);
        std::copy_n(source.sbix.begin() + strike_offset, 4U,
            output.begin() + static_cast<std::ptrdiff_t>(target));
        const auto record_offset = 4U + glyph_offsets_bytes;
        const auto strike_size = record_offset +
            (has_record ? record.bytes.size() : 0U);
        for (std::uint32_t offset_index = 0U;
             offset_index <= source.glyph_count; ++offset_index) {
            const auto value = offset_index <= source.glyph_index
                ? record_offset
                : strike_size;
            write_u32(output,
                target + 4U + static_cast<std::size_t>(offset_index) * 4U,
                static_cast<std::uint32_t>(value));
        }
        if (has_record) {
            const auto record_target = target + record_offset;
            std::copy(record.bytes.begin(), record.bytes.end(),
                output.begin() + static_cast<std::ptrdiff_t>(record_target));
            write_u16(output, record_target,
                static_cast<std::uint16_t>(record.origin_x));
            write_u16(output, record_target + 2U,
                static_cast<std::uint16_t>(record.origin_y));
        }
        target += strike_size;
    }
}

std::uint32_t checksum(std::span<const std::byte> bytes) noexcept {
    std::uint32_t result = 0U;
    for (std::size_t offset = 0U; offset < bytes.size(); offset += 4U) {
        std::uint32_t value =
            std::to_integer<std::uint32_t>(bytes[offset]) << 24U;
        if (offset + 1U < bytes.size()) {
            value |= std::to_integer<std::uint32_t>(bytes[offset + 1U]) << 16U;
        }
        if (offset + 2U < bytes.size()) {
            value |= std::to_integer<std::uint32_t>(bytes[offset + 2U]) << 8U;
        }
        if (offset + 3U < bytes.size()) {
            value |= std::to_integer<std::uint32_t>(bytes[offset + 3U]);
        }
        result += value;
    }
    return result;
}

void write_search_parameters(
    std::span<std::byte> output,
    std::uint16_t table_count) noexcept {
    std::uint16_t maximum_power = 1U;
    std::uint16_t selector = 0U;
    while (maximum_power <= table_count / 2U) {
        maximum_power = static_cast<std::uint16_t>(maximum_power * 2U);
        ++selector;
    }
    write_u16(output, 6U,
        static_cast<std::uint16_t>(maximum_power * 16U));
    write_u16(output, 8U, selector);
    write_u16(output, 10U, static_cast<std::uint16_t>(
        table_count * 16U - maximum_power * 16U));
}

bool get_font_size(
    const sfnt_font_view& font,
    std::size_t resident_sbix_bytes,
    std::size_t& result) noexcept {
    const auto data = font.data();
    const auto directory = static_cast<std::size_t>(font.face_offset()) + 12U;
    const auto table_count = font.table_count();
    if (table_count == 0U || table_count > maximum_table_count ||
        !try_align4(12U + static_cast<std::size_t>(table_count) * 16U,
            result)) {
        return false;
    }
    bool replaced_sbix = false;
    for (std::uint16_t index = 0U; index < table_count; ++index) {
        const auto record = directory + static_cast<std::size_t>(index) * 16U;
        if (!can_read(data, record, 16U)) return false;
        const auto offset = read_u32(data, record + 8U);
        const auto length = read_u32(data, record + 12U);
        if (!can_read(data, offset, length)) return false;
        auto target_length = static_cast<std::size_t>(length);
        if (!replaced_sbix && read_u32(data, record) == sbix_tag.value) {
            target_length = resident_sbix_bytes;
            replaced_sbix = true;
        }
        std::size_t padded = 0U;
        if (!try_align4(target_length, padded)) return false;
        if (result > std::numeric_limits<std::size_t>::max() - padded) {
            return false;
        }
        result += padded;
    }
    return replaced_sbix &&
        result <= std::numeric_limits<std::uint32_t>::max();
}

} // namespace

bool sfnt_font_view::try_get_glyph_resident_requirements(
    std::uint16_t glyph_index,
    sfnt_glyph_resident_requirements& result,
    font_error* error) const noexcept {
    result = {};
    resident_source source{};
    if (!inspect_resident_source(*this, glyph_index, source, error)) {
        return false;
    }
    std::size_t font_bytes = 0U;
    if (!get_font_size(*this, source.sbix_bytes, font_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = {source.sbix_bytes, font_bytes, source.strike_count};
    return true;
}

bool sfnt_font_view::try_create_glyph_resident_sbix(
    std::uint16_t glyph_index,
    std::span<std::byte> output,
    std::size_t& written,
    sfnt_glyph_resident_requirements* requirements,
    font_error* error) const noexcept {
    written = 0U;
    if (requirements != nullptr) *requirements = {};
    resident_source source{};
    if (!inspect_resident_source(*this, glyph_index, source, error)) {
        return false;
    }
    std::size_t font_bytes = 0U;
    if (!get_font_size(*this, source.sbix_bytes, font_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const sfnt_glyph_resident_requirements resolved{
        source.sbix_bytes, font_bytes, source.strike_count};
    if (requirements != nullptr) *requirements = resolved;
    if (output.size() < source.sbix_bytes) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::fill_n(output.begin(), source.sbix_bytes, std::byte{});
    write_resident_sbix(source, output.first(source.sbix_bytes));
    written = source.sbix_bytes;
    return true;
}

bool sfnt_font_view::try_create_glyph_resident_font(
    std::uint16_t glyph_index,
    std::span<std::byte> output,
    std::size_t& written,
    sfnt_glyph_resident_requirements* requirements,
    font_error* error) const noexcept {
    written = 0U;
    if (requirements != nullptr) *requirements = {};
    resident_source source{};
    if (!inspect_resident_source(*this, glyph_index, source, error)) {
        return false;
    }
    std::size_t font_bytes = 0U;
    if (!get_font_size(*this, source.sbix_bytes, font_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const sfnt_glyph_resident_requirements resolved{
        source.sbix_bytes, font_bytes, source.strike_count};
    if (requirements != nullptr) *requirements = resolved;
    if (output.size() < font_bytes) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::fill_n(output.begin(), font_bytes, std::byte{});
    const auto data = this->data();
    const auto face = static_cast<std::size_t>(face_offset());
    const auto directory = face + 12U;
    const auto count = table_count();
    std::copy_n(data.begin() + static_cast<std::ptrdiff_t>(face), 4U,
        output.begin());
    write_u16(output, 4U, count);
    write_search_parameters(output, count);
    std::size_t target = 0U;
    (void)try_align4(
        12U + static_cast<std::size_t>(count) * 16U, target);
    bool replaced_sbix = false;
    for (std::uint16_t index = 0U; index < count; ++index) {
        const auto source_record =
            directory + static_cast<std::size_t>(index) * 16U;
        const auto target_record = 12U + static_cast<std::size_t>(index) * 16U;
        const auto tag = read_u32(data, source_record);
        const auto source_offset = read_u32(data, source_record + 8U);
        const auto source_length = read_u32(data, source_record + 12U);
        const auto is_resident_sbix = !replaced_sbix && tag == sbix_tag.value;
        const auto target_length = is_resident_sbix
            ? source.sbix_bytes
            : static_cast<std::size_t>(source_length);
        write_u32(output, target_record, tag);
        write_u32(output, target_record + 8U,
            static_cast<std::uint32_t>(target));
        write_u32(output, target_record + 12U,
            static_cast<std::uint32_t>(target_length));
        if (is_resident_sbix) {
            write_resident_sbix(source,
                output.subspan(target, source.sbix_bytes));
            write_u32(output, target_record + 4U,
                checksum(output.subspan(target, source.sbix_bytes)));
            replaced_sbix = true;
        } else {
            write_u32(output, target_record + 4U,
                read_u32(data, source_record + 4U));
            std::copy_n(data.begin() + source_offset, source_length,
                output.begin() + static_cast<std::ptrdiff_t>(target));
        }
        std::size_t padded = 0U;
        (void)try_align4(target_length, padded);
        target += padded;
    }
    written = font_bytes;
    return true;
}

} // namespace progpu::native::text
