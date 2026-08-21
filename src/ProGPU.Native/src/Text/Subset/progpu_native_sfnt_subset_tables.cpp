#include "progpu_native_sfnt_subset_internal.hpp"

#include <algorithm>
#include <limits>
#include <map>

namespace progpu::native::text::sfnt_subset_detail {
namespace {

constexpr std::uint32_t checksum_adjustment = 0xB1B0AFBAU;
constexpr std::size_t maximum_table_count = 4096U;

std::uint32_t byte_value(std::byte value) noexcept {
    return std::to_integer<std::uint32_t>(value);
}

std::uint16_t search_range(std::uint16_t table_count) noexcept {
    std::uint16_t power = 1U;
    while (power <= table_count / 2U) {
        power = static_cast<std::uint16_t>(power * 2U);
    }
    return static_cast<std::uint16_t>(power * 16U);
}

std::uint16_t entry_selector(std::uint16_t range) noexcept {
    std::uint16_t power = static_cast<std::uint16_t>(range / 16U);
    std::uint16_t result = 0U;
    while (power > 1U) {
        power = static_cast<std::uint16_t>(power / 2U);
        ++result;
    }
    return result;
}

std::uint32_t checksum(std::span<const std::byte> data) {
    const auto padded = align4(data.size());
    std::uint32_t sum = 0U;
    for (std::size_t offset = 0U; offset < padded; offset += 4U) {
        std::uint32_t value = 0U;
        for (std::size_t index = 0U; index < 4U; ++index) {
            const auto source = offset + index;
            value = (value << 8U) |
                (source < data.size() ? byte_value(data[source]) : 0U);
        }
        sum += value;
    }
    return sum;
}

} // namespace

bool can_read(
    std::span<const std::byte> data,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= data.size() && length <= data.size() - offset;
}

std::uint16_t read_u16(
    std::span<const std::byte> data,
    std::size_t offset) {
    if (!can_read(data, offset, 2U)) {
        throw subset_failure{};
    }
    return static_cast<std::uint16_t>(
        (byte_value(data[offset]) << 8U) | byte_value(data[offset + 1U]));
}

std::int16_t read_i16(
    std::span<const std::byte> data,
    std::size_t offset) {
    return static_cast<std::int16_t>(read_u16(data, offset));
}

std::uint32_t read_u32(
    std::span<const std::byte> data,
    std::size_t offset) {
    if (!can_read(data, offset, 4U)) {
        throw subset_failure{};
    }
    return (byte_value(data[offset]) << 24U) |
        (byte_value(data[offset + 1U]) << 16U) |
        (byte_value(data[offset + 2U]) << 8U) |
        byte_value(data[offset + 3U]);
}

void write_u16(
    std::span<std::byte> data,
    std::size_t offset,
    std::uint16_t value) {
    if (!can_read(data, offset, 2U)) {
        throw subset_failure{};
    }
    data[offset] = static_cast<std::byte>(value >> 8U);
    data[offset + 1U] = static_cast<std::byte>(value & 0xFFU);
}

void write_i16(
    std::span<std::byte> data,
    std::size_t offset,
    std::int16_t value) {
    write_u16(data, offset, static_cast<std::uint16_t>(value));
}

void write_u32(
    std::span<std::byte> data,
    std::size_t offset,
    std::uint32_t value) {
    if (!can_read(data, offset, 4U)) {
        throw subset_failure{};
    }
    data[offset] = static_cast<std::byte>(value >> 24U);
    data[offset + 1U] = static_cast<std::byte>((value >> 16U) & 0xFFU);
    data[offset + 2U] = static_cast<std::byte>((value >> 8U) & 0xFFU);
    data[offset + 3U] = static_cast<std::byte>(value & 0xFFU);
}

std::size_t align4(std::size_t value) {
    if (value > std::numeric_limits<std::size_t>::max() - 3U) {
        throw subset_failure{};
    }
    return (value + 3U) & ~std::size_t{3U};
}

face_data parse_face(
    std::span<const std::byte> font_data,
    std::size_t directory_offset) {
    if (!can_read(font_data, directory_offset, 12U)) {
        throw subset_failure{};
    }
    face_data result{};
    result.sfnt_version = read_u32(font_data, directory_offset);
    const auto table_count = read_u16(font_data, directory_offset + 4U);
    if (table_count > maximum_table_count ||
        !can_read(font_data, directory_offset + 12U,
            static_cast<std::size_t>(table_count) * 16U)) {
        throw subset_failure{};
    }
    result.tables.reserve(table_count);
    for (std::size_t index = 0U; index < table_count; ++index) {
        const auto record = directory_offset + 12U + index * 16U;
        const auto table_tag = read_u32(font_data, record);
        const auto offset = static_cast<std::size_t>(
            read_u32(font_data, record + 8U));
        const auto length = static_cast<std::size_t>(
            read_u32(font_data, record + 12U));
        if (!can_read(font_data, offset, length)) {
            throw subset_failure{};
        }
        result.tables.push_back(table_data{
            table_tag,
            std::vector<std::byte>(
                font_data.begin() + static_cast<std::ptrdiff_t>(offset),
                font_data.begin() +
                    static_cast<std::ptrdiff_t>(offset + length))});
    }
    return result;
}

const table_data* find_table(
    const face_data& face,
    std::uint32_t table_tag) noexcept {
    const table_data* result = nullptr;
    for (const auto& table : face.tables) {
        if (table.tag == table_tag) {
            result = &table;
        }
    }
    return result;
}

std::vector<std::byte> build_sfnt(
    std::uint32_t sfnt_version,
    std::span<const table_data> tables) {
    std::map<std::uint32_t, std::vector<std::byte>> sorted;
    for (const auto& table : tables) {
        sorted[table.tag] = table.bytes;
    }
    if (sorted.empty() ||
        sorted.size() > std::numeric_limits<std::uint16_t>::max()) {
        throw subset_failure{};
    }
    const auto table_count = static_cast<std::uint16_t>(sorted.size());
    const std::size_t directory_bytes =
        12U + static_cast<std::size_t>(table_count) * 16U;
    std::size_t table_offset = align4(directory_bytes);
    for (const auto& [table_tag, bytes] : sorted) {
        static_cast<void>(table_tag);
        const auto padded = align4(bytes.size());
        if (padded > std::numeric_limits<std::uint32_t>::max() ||
            table_offset >
                std::numeric_limits<std::uint32_t>::max() - padded) {
            throw subset_failure{};
        }
        table_offset += padded;
    }
    std::vector<std::byte> output(table_offset);
    write_u32(output, 0U, sfnt_version);
    write_u16(output, 4U, table_count);
    const auto range = search_range(table_count);
    write_u16(output, 6U, range);
    write_u16(output, 8U, entry_selector(range));
    write_u16(output, 10U, static_cast<std::uint16_t>(
        static_cast<std::uint32_t>(table_count) * 16U - range));

    table_offset = align4(directory_bytes);
    std::size_t record_index = 0U;
    std::size_t head_offset = std::numeric_limits<std::size_t>::max();
    for (const auto& [table_tag, bytes] : sorted) {
        const auto record = 12U + record_index++ * 16U;
        write_u32(output, record, table_tag);
        write_u32(output, record + 4U, checksum(bytes));
        write_u32(output, record + 8U,
            static_cast<std::uint32_t>(table_offset));
        write_u32(output, record + 12U,
            static_cast<std::uint32_t>(bytes.size()));
        std::copy(bytes.begin(), bytes.end(),
            output.begin() + static_cast<std::ptrdiff_t>(table_offset));
        if (table_tag == tag('h', 'e', 'a', 'd') && bytes.size() >= 12U) {
            head_offset = table_offset;
        }
        table_offset += align4(bytes.size());
    }
    if (head_offset != std::numeric_limits<std::size_t>::max()) {
        write_u32(output, head_offset + 8U, 0U);
        write_u32(output, head_offset + 8U,
            checksum_adjustment - checksum(output));
    }
    return output;
}

} // namespace progpu::native::text::sfnt_subset_detail
