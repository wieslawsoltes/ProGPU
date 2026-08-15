#ifndef PROGPU_NATIVE_PNG_INTERNAL_HPP
#define PROGPU_NATIVE_PNG_INTERNAL_HPP

#include "progpu_native_image.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::image::detail {

struct png_pass_layout final {
    std::uint32_t start_x = 0U;
    std::uint32_t start_y = 0U;
    std::uint32_t step_x = 1U;
    std::uint32_t step_y = 1U;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::size_t row_bytes = 0U;
    std::size_t filtered_bytes = 0U;
};

struct png_layout final {
    std::array<png_pass_layout, 7U> passes{};
    std::size_t filtered_bytes = 0U;
    std::size_t filter_bytes_per_pixel = 1U;
    std::uint8_t pass_count = 0U;
};

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

bool try_build_png_layout(
    const png_decode_requirements& requirements,
    png_layout& layout) noexcept;

bool try_unfilter_png(
    const png_layout& layout,
    std::span<std::byte> filtered) noexcept;

bool try_convert_png_to_rgba(
    const png_metadata& metadata,
    std::span<const std::byte> filtered,
    std::span<std::byte> rgba_output) noexcept;

} // namespace progpu::native::image::detail

#endif
