#include "progpu_native_text.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// SfntFontFace.CreateStandaloneFontData at repository checkpoint f21d5cbf.
// Native callers provide the sorted-directory scratch and immutable output.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u32;

constexpr std::uint16_t maximum_table_count = 4096U;
constexpr auto cff1_tag = open_type_tag::from_chars('C', 'F', 'F', ' ');
constexpr auto cff2_tag = open_type_tag::from_chars('C', 'F', 'F', '2');
constexpr auto otto_tag = open_type_tag::from_chars('O', 'T', 'T', 'O');

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

bool try_get_effective_record(
    std::span<const std::byte> data,
    std::size_t directory,
    std::uint16_t source_count,
    std::uint16_t index,
    sfnt_directory_record& result) noexcept {
    result = {};
    const auto record = directory + static_cast<std::size_t>(index) * 16U;
    if (!can_read(data, record, 16U)) return false;
    const auto tag = read_u32(data, record);
    const auto offset = read_u32(data, record + 8U);
    const auto length = read_u32(data, record + 12U);
    if (!can_read(data, offset, length)) return false;
    for (std::uint16_t later = static_cast<std::uint16_t>(index + 1U);
         later < source_count;
         ++later) {
        const auto candidate =
            directory + static_cast<std::size_t>(later) * 16U;
        if (read_u32(data, candidate) != tag) continue;
        const auto candidate_offset = read_u32(data, candidate + 8U);
        const auto candidate_length = read_u32(data, candidate + 12U);
        if (can_read(data, candidate_offset, candidate_length)) return false;
    }
    result = sfnt_directory_record{
        open_type_tag{tag},
        read_u32(data, record + 4U),
        offset,
        length};
    return true;
}

bool inspect(
    const sfnt_font_view& font,
    sfnt_standalone_requirements& result) noexcept {
    result = {};
    const auto source_count = font.table_count();
    if (source_count == 0U || source_count > maximum_table_count) return false;
    const auto data = font.data();
    const auto directory = static_cast<std::size_t>(font.face_offset()) + 12U;
    std::uint16_t retained_count = 0U;
    std::size_t table_bytes = 0U;
    for (std::uint16_t index = 0U; index < source_count; ++index) {
        sfnt_directory_record record{};
        if (!try_get_effective_record(
                data, directory, source_count, index, record)) {
            continue;
        }
        std::size_t padded = 0U;
        if (!try_align4(record.length, padded) ||
            table_bytes > std::numeric_limits<std::size_t>::max() - padded) {
            return false;
        }
        table_bytes += padded;
        ++retained_count;
    }
    if (retained_count == 0U) return false;
    std::size_t output_size = 0U;
    if (!try_align4(
            12U + static_cast<std::size_t>(retained_count) * 16U,
            output_size) ||
        output_size > std::numeric_limits<std::size_t>::max() - table_bytes) {
        return false;
    }
    output_size += table_bytes;
    if (output_size > std::numeric_limits<std::uint32_t>::max()) return false;
    result = {output_size, retained_count};
    return true;
}

} // namespace

bool sfnt_font_view::try_get_standalone_requirements(
    sfnt_standalone_requirements& result,
    font_error* error) const noexcept {
    set_error(error, font_error::none);
    if (!inspect(*this, result)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    return true;
}

bool sfnt_font_view::try_create_standalone_font(
    std::span<std::byte> output,
    std::span<sfnt_directory_record> table_scratch,
    std::size_t& written,
    sfnt_standalone_requirements* requirements,
    font_error* error) const noexcept {
    written = 0U;
    if (requirements != nullptr) *requirements = {};
    sfnt_standalone_requirements resolved{};
    set_error(error, font_error::none);
    if (!inspect(*this, resolved)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (requirements != nullptr) *requirements = resolved;
    if (output.size() < resolved.font_bytes ||
        table_scratch.size() < resolved.table_scratch_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    const auto data = this->data();
    const auto directory = static_cast<std::size_t>(face_offset()) + 12U;
    auto records = table_scratch.first(resolved.table_scratch_count);
    std::uint16_t retained_index = 0U;
    for (std::uint16_t index = 0U; index < table_count(); ++index) {
        sfnt_directory_record record{};
        if (!try_get_effective_record(
                data, directory, table_count(), index, record)) {
            continue;
        }
        records[retained_index++] = record;
    }
    if (retained_index != resolved.table_scratch_count) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::sort(records.begin(), records.end(),
        [](const sfnt_directory_record& left,
           const sfnt_directory_record& right) {
            return left.tag.value < right.tag.value;
        });
    std::fill_n(output.begin(), resolved.font_bytes, std::byte{});
    auto signature = open_type_tag{read_u32(data, face_offset())};
    const auto has_cff = std::any_of(records.begin(), records.end(),
        [](const sfnt_directory_record& record) {
            return record.tag == cff1_tag || record.tag == cff2_tag;
        });
    if (has_cff) signature = otto_tag;
    write_u32(output, 0U, signature.value);
    write_u16(output, 4U, resolved.table_scratch_count);
    write_search_parameters(output, resolved.table_scratch_count);
    std::size_t target = 0U;
    (void)try_align4(
        12U + static_cast<std::size_t>(resolved.table_scratch_count) * 16U,
        target);
    for (std::size_t index = 0U; index < records.size(); ++index) {
        const auto record = 12U + index * 16U;
        write_u32(output, record, records[index].tag.value);
        write_u32(output, record + 4U, records[index].checksum);
        write_u32(output, record + 8U,
            static_cast<std::uint32_t>(target));
        write_u32(output, record + 12U, records[index].length);
        std::copy_n(data.begin() + records[index].offset,
            records[index].length,
            output.begin() + static_cast<std::ptrdiff_t>(target));
        std::size_t padded = 0U;
        (void)try_align4(records[index].length, padded);
        target += padded;
    }
    written = resolved.font_bytes;
    return true;
}

} // namespace progpu::native::text
