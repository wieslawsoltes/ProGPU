#include "progpu_native_text.hpp"

#include "progpu_native_compression.hpp"
#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>

// Direct native port provenance: ProGPU-owned SfntFontContainer.cs at
// checkpoint c51ce8ec. The WOFF1 wire contract follows the W3C WOFF 1.0
// recommendation. Requirements are O(T); bounded preflight plus normalization
// are O(I + O) time and O(M) caller scratch for maximum table size M.
namespace progpu::native::text {
namespace {

using detail::read_u16;
using detail::read_u32;

constexpr std::uint32_t woff1_signature = 0x774F4646U;
constexpr std::uint32_t woff2_signature = 0x774F4632U;
constexpr std::size_t woff_header_size = 44U;
constexpr std::size_t woff_entry_size = 20U;
constexpr std::size_t sfnt_header_size = 12U;
constexpr std::size_t sfnt_entry_size = 16U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool checked_add(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

bool checked_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U &&
        right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

bool try_align4(std::size_t value, std::size_t& result) noexcept {
    return value <= std::numeric_limits<std::size_t>::max() - 3U
        ? (result = (value + 3U) & ~std::size_t{3U}, true)
        : false;
}

bool overlaps(
    std::span<const std::byte> left,
    std::span<const std::byte> right) noexcept {
    if (left.empty() || right.empty()) {
        return false;
    }
    const auto left_begin = reinterpret_cast<std::uintptr_t>(left.data());
    const auto right_begin = reinterpret_cast<std::uintptr_t>(right.data());
    if (left.size() > std::numeric_limits<std::uintptr_t>::max() - left_begin ||
        right.size() >
            std::numeric_limits<std::uintptr_t>::max() - right_begin) {
        return true;
    }
    const auto left_end = left_begin + left.size();
    const auto right_end = right_begin + right.size();
    return left_begin < right_end && right_begin < left_end;
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

bool try_write_search_parameters(
    std::span<std::byte> output,
    std::uint16_t table_count) noexcept {
    std::uint32_t power_of_two = 1U;
    std::uint16_t entry_selector = 0U;
    while (power_of_two <= table_count / 2U) {
        power_of_two *= 2U;
        ++entry_selector;
    }
    const auto search_range = power_of_two * 16U;
    const auto full_range = static_cast<std::uint32_t>(table_count) * 16U;
    if (search_range > std::numeric_limits<std::uint16_t>::max() ||
        full_range - search_range >
            std::numeric_limits<std::uint16_t>::max()) {
        return false;
    }
    write_u16(output, 6U, static_cast<std::uint16_t>(search_range));
    write_u16(output, 8U, entry_selector);
    write_u16(output, 10U,
        static_cast<std::uint16_t>(full_range - search_range));
    return true;
}

bool try_parse_requirements(
    std::span<const std::byte> input,
    sfnt_container_requirements& result,
    font_error& error) noexcept {
    result = {};
    error = font_error::none;
    if (input.size() < 4U) {
        result.normalized_bytes = input.size();
        return true;
    }
    const auto signature = read_u32(input, 0U);
    if (signature == woff2_signature) {
        error = font_error::unsupported_container;
        return false;
    }
    if (signature != woff1_signature) {
        result.normalized_bytes = input.size();
        return true;
    }
    if (input.size() < woff_header_size) {
        error = font_error::invalid_container;
        return false;
    }
    const auto declared_length = read_u32(input, 8U);
    const auto table_count = read_u16(input, 12U);
    const auto reserved = read_u16(input, 14U);
    const auto declared_sfnt_size = read_u32(input, 16U);
    std::size_t directory_bytes = 0U;
    std::size_t directory_end = 0U;
    std::size_t sfnt_directory_bytes = 0U;
    std::size_t target_offset = 0U;
    if (declared_length < woff_header_size ||
        declared_length > input.size() || table_count == 0U ||
        reserved != 0U ||
        !checked_multiply(table_count, woff_entry_size, directory_bytes) ||
        !checked_add(woff_header_size, directory_bytes, directory_end) ||
        directory_end > declared_length ||
        !checked_multiply(table_count, sfnt_entry_size,
            sfnt_directory_bytes) ||
        !checked_add(sfnt_header_size, sfnt_directory_bytes,
            target_offset)) {
        error = font_error::invalid_container;
        return false;
    }
    std::size_t scratch_bytes = 0U;
    for (std::uint16_t index = 0U; index < table_count; ++index) {
        const auto record_offset =
            woff_header_size + static_cast<std::size_t>(index) *
                woff_entry_size;
        const auto source_offset = read_u32(input, record_offset + 4U);
        const auto compressed_length = read_u32(input, record_offset + 8U);
        const auto original_length = read_u32(input, record_offset + 12U);
        if (compressed_length > original_length ||
            source_offset > declared_length ||
            compressed_length > declared_length - source_offset) {
            error = font_error::invalid_container;
            return false;
        }
        if (compressed_length < original_length) {
            scratch_bytes = std::max(
                scratch_bytes, static_cast<std::size_t>(original_length));
        }
        std::size_t table_end = 0U;
        if (!checked_add(target_offset, original_length, table_end) ||
            !try_align4(table_end, target_offset)) {
            error = font_error::invalid_container;
            return false;
        }
    }
    if (target_offset != declared_sfnt_size ||
        target_offset > std::numeric_limits<std::uint32_t>::max()) {
        error = font_error::invalid_container;
        return false;
    }
    std::array<std::byte, sfnt_header_size> header{};
    if (!try_write_search_parameters(header, table_count)) {
        error = font_error::invalid_container;
        return false;
    }
    result = {target_offset, scratch_bytes, table_count, true};
    return true;
}

bool preflight_compressed_tables(
    std::span<const std::byte> input,
    const sfnt_container_requirements& requirements,
    std::span<std::byte> scratch) noexcept {
    for (std::uint16_t index = 0U;
         index < requirements.table_count;
         ++index) {
        const auto record_offset =
            woff_header_size + static_cast<std::size_t>(index) *
                woff_entry_size;
        const auto source_offset = read_u32(input, record_offset + 4U);
        const auto compressed_length = read_u32(input, record_offset + 8U);
        const auto original_length = read_u32(input, record_offset + 12U);
        if (compressed_length == original_length) {
            continue;
        }
        std::size_t written = 0U;
        if (!compression::try_inflate_zlib(
                input.subspan(source_offset, compressed_length),
                scratch.first(original_length),
                written) ||
            written != original_length) {
            return false;
        }
    }
    return true;
}

} // namespace

bool try_get_sfnt_container_requirements(
    std::span<const std::byte> input,
    sfnt_container_requirements& result,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    font_error parse_error = font_error::none;
    if (!try_parse_requirements(input, result, parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    return true;
}

bool try_normalize_sfnt_container(
    std::span<const std::byte> input,
    std::span<std::byte> table_scratch,
    std::span<std::byte> output,
    sfnt_container_requirements& result,
    font_error* error) noexcept {
    set_error(error, font_error::none);
    sfnt_container_requirements requirements{};
    font_error parse_error = font_error::none;
    if (!try_parse_requirements(input, requirements, parse_error)) {
        result = {};
        set_error(error, parse_error);
        return false;
    }
    if (output.size() < requirements.normalized_bytes ||
        table_scratch.size() < requirements.table_scratch_bytes) {
        result = {};
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (!requirements.requires_normalization) {
        if (!input.empty()) {
            std::memmove(output.data(), input.data(), input.size());
        }
        result = requirements;
        return true;
    }
    if (overlaps(input, table_scratch) || overlaps(input, output) ||
        overlaps(table_scratch, output)) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!preflight_compressed_tables(input, requirements, table_scratch)) {
        result = {};
        set_error(error, font_error::invalid_compressed_data);
        return false;
    }
    auto normalized = output.first(requirements.normalized_bytes);
    std::fill(normalized.begin(), normalized.end(), std::byte{0U});
    write_u32(normalized, 0U, read_u32(input, 4U));
    write_u16(normalized, 4U, requirements.table_count);
    if (!try_write_search_parameters(normalized, requirements.table_count)) {
        result = {};
        set_error(error, font_error::invalid_container);
        return false;
    }
    std::size_t target_offset = sfnt_header_size +
        static_cast<std::size_t>(requirements.table_count) * sfnt_entry_size;
    for (std::uint16_t index = 0U;
         index < requirements.table_count;
         ++index) {
        const auto source_record =
            woff_header_size + static_cast<std::size_t>(index) *
                woff_entry_size;
        const auto target_record =
            sfnt_header_size + static_cast<std::size_t>(index) *
                sfnt_entry_size;
        const auto source_offset = read_u32(input, source_record + 4U);
        const auto compressed_length = read_u32(input, source_record + 8U);
        const auto original_length = read_u32(input, source_record + 12U);
        write_u32(normalized, target_record, read_u32(input, source_record));
        write_u32(normalized, target_record + 4U,
            read_u32(input, source_record + 16U));
        write_u32(normalized, target_record + 8U,
            static_cast<std::uint32_t>(target_offset));
        write_u32(normalized, target_record + 12U, original_length);
        auto target = normalized.subspan(target_offset, original_length);
        if (compressed_length == original_length) {
            std::copy_n(input.begin() + source_offset,
                original_length, target.begin());
        } else {
            std::size_t written = 0U;
            if (!compression::try_inflate_zlib(
                    input.subspan(source_offset, compressed_length),
                    target,
                    written) ||
                written != original_length) {
                result = {};
                set_error(error, font_error::invalid_compressed_data);
                return false;
            }
        }
        std::size_t table_end = target_offset + original_length;
        (void)try_align4(table_end, target_offset);
    }
    result = requirements;
    return true;
}

} // namespace progpu::native::text
