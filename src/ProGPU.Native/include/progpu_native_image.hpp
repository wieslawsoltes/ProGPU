#ifndef PROGPU_NATIVE_IMAGE_HPP
#define PROGPU_NATIVE_IMAGE_HPP

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::image {

enum class image_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    invalid_signature,
    invalid_chunk,
    checksum_mismatch,
    unsupported_format,
    insufficient_buffer,
    invalid_compressed_data
};

struct png_decode_requirements final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::size_t compressed_bytes = 0U;
    std::size_t filtered_bytes = 0U;
    std::size_t rgba_bytes = 0U;
    std::uint8_t bit_depth = 0U;
    std::uint8_t color_type = 0U;
    std::uint8_t channel_count = 0U;
    std::uint8_t interlace_method = 0U;
};

bool try_get_png_decode_requirements(
    std::span<const std::byte> input,
    png_decode_requirements& result,
    image_error* error = nullptr) noexcept;

bool try_decode_png_rgba(
    std::span<const std::byte> input,
    std::span<std::byte> compressed_scratch,
    std::span<std::byte> filtered_scratch,
    std::span<std::byte> rgba_output,
    png_decode_requirements& result,
    image_error* error = nullptr) noexcept;

} // namespace progpu::native::image

#endif
