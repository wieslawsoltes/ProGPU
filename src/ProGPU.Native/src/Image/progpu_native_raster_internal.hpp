#ifndef PROGPU_NATIVE_RASTER_INTERNAL_HPP
#define PROGPU_NATIVE_RASTER_INTERNAL_HPP

#include "progpu_native_image.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::image::detail {

// The most bytes the pixels of a decoded image may take.
inline constexpr std::uint64_t maximum_raster_bytes = 0x7FFFFFFFULL;

inline bool raster_bytes(
    std::uint32_t width,
    std::uint32_t height,
    std::size_t& bytes) noexcept {
    if (width == 0U || height == 0U) {
        return false;
    }
    const std::uint64_t total = static_cast<std::uint64_t>(width) * height * 4U;
    if (total > maximum_raster_bytes) {
        return false;
    }
    bytes = static_cast<std::size_t>(total);
    return true;
}

inline std::uint8_t byte_at(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint8_t>(bytes[offset]);
}

inline std::uint16_t read_u16_le(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        byte_at(bytes, offset) |
        (static_cast<std::uint32_t>(byte_at(bytes, offset + 1U)) << 8U));
}

inline std::uint32_t read_u32_le(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint32_t>(byte_at(bytes, offset)) |
        (static_cast<std::uint32_t>(byte_at(bytes, offset + 1U)) << 8U) |
        (static_cast<std::uint32_t>(byte_at(bytes, offset + 2U)) << 16U) |
        (static_cast<std::uint32_t>(byte_at(bytes, offset + 3U)) << 24U);
}

inline std::uint16_t read_u16_be(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint32_t>(byte_at(bytes, offset)) << 8U) |
        byte_at(bytes, offset + 1U));
}

inline void write_rgba(
    std::span<std::byte> rgba,
    std::size_t pixel,
    std::uint8_t red,
    std::uint8_t green,
    std::uint8_t blue,
    std::uint8_t alpha) noexcept {
    const std::size_t at = pixel * 4U;
    rgba[at] = static_cast<std::byte>(red);
    rgba[at + 1U] = static_cast<std::byte>(green);
    rgba[at + 2U] = static_cast<std::byte>(blue);
    rgba[at + 3U] = static_cast<std::byte>(alpha);
}

bool read_bmp_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept;
bool decode_bmp(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error);

bool read_gif_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept;
bool decode_gif(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error);

bool read_jpeg_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept;
bool decode_jpeg(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error);

} // namespace progpu::native::image::detail

#endif
