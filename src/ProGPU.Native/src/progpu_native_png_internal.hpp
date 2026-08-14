#ifndef PROGPU_NATIVE_PNG_INTERNAL_HPP
#define PROGPU_NATIVE_PNG_INTERNAL_HPP

#include "progpu_native_image.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::image::detail {

struct png_metadata final {
    png_decode_requirements requirements{};
    std::span<const std::byte> palette{};
    std::span<const std::byte> transparency{};
    std::array<std::byte, 6U> transparent_sample{};
    std::uint8_t transparent_sample_bytes = 0U;
};

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept;

std::uint32_t update_crc32(
    std::uint32_t crc,
    std::span<const std::byte> bytes) noexcept;

bool try_parse_png(
    std::span<const std::byte> input,
    png_metadata& metadata,
    std::span<std::byte> compressed_output,
    bool copy_compressed,
    image_error& error) noexcept;

bool try_unfilter_png(
    std::span<std::byte> filtered,
    std::uint32_t height,
    std::size_t row_bytes,
    std::size_t bytes_per_pixel) noexcept;

bool try_convert_png_to_rgba(
    const png_metadata& metadata,
    std::span<const std::byte> filtered,
    std::span<std::byte> rgba_output) noexcept;

} // namespace progpu::native::image::detail

#endif
