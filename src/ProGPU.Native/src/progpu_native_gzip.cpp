#include "progpu_native_compression.hpp"

#include "progpu_native_deflate_internal.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

// Algorithm: RFC 1952 single-member gzip header/trailer validation around the
// bounded RFC 1951 decoder, including optional-field and CRC verification.
// Time complexity: O(I + O); space complexity: O(1) beyond DEFLATE scratch.
namespace progpu::native::compression {
namespace {

void set_error(
    compression_error* destination,
    compression_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

std::uint16_t read_u16_le(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        std::to_integer<std::uint8_t>(bytes[offset]) |
        (static_cast<std::uint16_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 1U])) << 8U));
}

std::uint32_t read_u32_le(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return std::to_integer<std::uint8_t>(bytes[offset]) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 1U])) << 8U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 2U])) << 16U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 3U])) << 24U);
}

std::uint32_t crc32(std::span<const std::byte> bytes) noexcept {
    auto crc = 0xFFFFFFFFU;
    for (const auto value : bytes) {
        crc ^= std::to_integer<std::uint8_t>(value);
        for (std::uint32_t bit = 0U; bit < 8U; ++bit) {
            const auto mask = 0U - (crc & 1U);
            crc = (crc >> 1U) ^ (0xEDB88320U & mask);
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

bool try_skip_zero_terminated(
    std::span<const std::byte> input,
    std::size_t trailer_offset,
    std::size_t& offset) noexcept {
    while (offset < trailer_offset) {
        if (input[offset++] == std::byte{0U}) {
            return true;
        }
    }
    return false;
}

bool try_get_member_bounds(
    std::span<const std::byte> input,
    std::size_t& payload_offset,
    std::size_t& trailer_offset,
    compression_error& error) noexcept {
    payload_offset = 0U;
    trailer_offset = 0U;
    error = compression_error::none;
    if (input.size() < 18U) {
        error = compression_error::invalid_argument;
        return false;
    }
    const auto flags = std::to_integer<std::uint8_t>(input[3U]);
    if (input[0U] != std::byte{0x1FU} ||
        input[1U] != std::byte{0x8BU} || input[2U] != std::byte{8U} ||
        (flags & 0xE0U) != 0U) {
        error = compression_error::invalid_header;
        return false;
    }
    trailer_offset = input.size() - 8U;
    std::size_t offset = 10U;
    if ((flags & 0x04U) != 0U) {
        if (offset + 2U > trailer_offset) {
            error = compression_error::invalid_header;
            return false;
        }
        const auto extra_length = read_u16_le(input, offset);
        offset += 2U;
        if (extra_length > trailer_offset - offset) {
            error = compression_error::invalid_header;
            return false;
        }
        offset += extra_length;
    }
    if ((flags & 0x08U) != 0U &&
        !try_skip_zero_terminated(input, trailer_offset, offset)) {
        error = compression_error::invalid_header;
        return false;
    }
    if ((flags & 0x10U) != 0U &&
        !try_skip_zero_terminated(input, trailer_offset, offset)) {
        error = compression_error::invalid_header;
        return false;
    }
    if ((flags & 0x02U) != 0U) {
        if (offset + 2U > trailer_offset ||
            static_cast<std::uint16_t>(crc32(input.first(offset))) !=
                read_u16_le(input, offset)) {
            error = compression_error::checksum_mismatch;
            return false;
        }
        offset += 2U;
    }
    if (offset >= trailer_offset) {
        error = compression_error::invalid_stream;
        return false;
    }
    payload_offset = offset;
    return true;
}

} // namespace

bool try_get_gzip_uncompressed_size(
    std::span<const std::byte> input,
    std::size_t& result,
    compression_error* error) noexcept {
    result = 0U;
    set_error(error, compression_error::none);
    std::size_t payload_offset = 0U;
    std::size_t trailer_offset = 0U;
    compression_error parse_error = compression_error::none;
    if (!try_get_member_bounds(
            input, payload_offset, trailer_offset, parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    (void)payload_offset;
    result = read_u32_le(input, trailer_offset + 4U);
    return true;
}

bool try_inflate_gzip(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error* error) noexcept {
    written = 0U;
    set_error(error, compression_error::none);
    std::size_t payload_offset = 0U;
    std::size_t trailer_offset = 0U;
    compression_error parse_error = compression_error::none;
    if (!try_get_member_bounds(
            input, payload_offset, trailer_offset, parse_error)) {
        set_error(error, parse_error);
        return false;
    }
    compression_error inflate_error = compression_error::none;
    if (!detail::try_inflate_deflate(
            input.subspan(
                payload_offset, trailer_offset - payload_offset),
            output,
            written,
            inflate_error)) {
        set_error(error, inflate_error);
        written = 0U;
        return false;
    }
    if (crc32(output.first(written)) != read_u32_le(input, trailer_offset) ||
        static_cast<std::uint32_t>(written) !=
            read_u32_le(input, trailer_offset + 4U)) {
        set_error(error, compression_error::checksum_mismatch);
        written = 0U;
        return false;
    }
    return true;
}

} // namespace progpu::native::compression
