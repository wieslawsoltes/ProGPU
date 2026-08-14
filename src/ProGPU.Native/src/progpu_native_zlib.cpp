#include "progpu_native_compression.hpp"

#include "progpu_native_deflate_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <span>

// Algorithm: RFC 1950 header/trailer validation around the bounded RFC 1951
// decoder, including Adler-32 verification over caller-owned output.
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

std::uint32_t read_u32_be(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(
                std::to_integer<std::uint8_t>(bytes[offset])) << 24U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 1U])) << 16U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 2U])) << 8U) |
        std::to_integer<std::uint8_t>(bytes[offset + 3U]);
}

std::uint32_t adler32(std::span<const std::byte> bytes) noexcept {
    constexpr std::uint32_t modulus = 65521U;
    std::uint32_t first = 1U;
    std::uint32_t second = 0U;
    std::size_t offset = 0U;
    while (offset < bytes.size()) {
        const auto block = std::min<std::size_t>(5552U, bytes.size() - offset);
        for (std::size_t index = 0U; index < block; ++index) {
            first += std::to_integer<std::uint8_t>(bytes[offset + index]);
            second += first;
        }
        first %= modulus;
        second %= modulus;
        offset += block;
    }
    return (second << 16U) | first;
}

} // namespace

bool try_inflate_zlib(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error* error) noexcept {
    written = 0U;
    set_error(error, compression_error::none);
    if (input.size() < 6U) {
        set_error(error, compression_error::invalid_argument);
        return false;
    }
    const auto cmf = std::to_integer<std::uint8_t>(input[0U]);
    const auto flags = std::to_integer<std::uint8_t>(input[1U]);
    if ((cmf & 0x0FU) != 8U || (cmf >> 4U) > 7U ||
        ((static_cast<std::uint16_t>(cmf) << 8U) | flags) % 31U != 0U ||
        (flags & 0x20U) != 0U) {
        set_error(error, compression_error::invalid_header);
        return false;
    }
    compression_error inflate_error = compression_error::none;
    if (!detail::try_inflate_deflate(
            input.subspan(2U, input.size() - 6U),
            output,
            written,
            inflate_error)) {
        set_error(error, inflate_error);
        written = 0U;
        return false;
    }
    const auto expected = read_u32_be(input, input.size() - 4U);
    if (adler32(output.first(written)) != expected) {
        set_error(error, compression_error::checksum_mismatch);
        written = 0U;
        return false;
    }
    return true;
}

} // namespace progpu::native::compression
