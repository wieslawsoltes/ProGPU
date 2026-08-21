#ifndef PROGPU_NATIVE_COMPRESSION_HPP
#define PROGPU_NATIVE_COMPRESSION_HPP

#include <cstddef>
#include <span>

namespace progpu::native::compression {

enum class compression_error {
    none = 0,
    invalid_argument,
    invalid_header,
    invalid_stream,
    insufficient_buffer,
    checksum_mismatch
};

bool try_inflate_zlib(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error* error = nullptr) noexcept;

bool try_inflate_gzip(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error* error = nullptr) noexcept;

bool try_get_gzip_uncompressed_size(
    std::span<const std::byte> input,
    std::size_t& result,
    compression_error* error = nullptr) noexcept;

} // namespace progpu::native::compression

#endif
