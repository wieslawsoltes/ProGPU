#ifndef PROGPU_NATIVE_FONT_BYTES_HPP
#define PROGPU_NATIVE_FONT_BYTES_HPP

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

inline bool can_read(
    std::span<const std::byte> data,
    std::size_t offset,
    std::size_t length) noexcept {
    return offset <= data.size() && length <= data.size() - offset;
}

inline std::uint16_t read_u16(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(
        (std::to_integer<std::uint16_t>(data[offset]) << 8U) |
        std::to_integer<std::uint16_t>(data[offset + 1U]));
}

inline std::int16_t read_i16(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return static_cast<std::int16_t>(read_u16(data, offset));
}

inline std::uint32_t read_u32(
    std::span<const std::byte> data,
    std::size_t offset) noexcept {
    return
        (std::to_integer<std::uint32_t>(data[offset]) << 24U) |
        (std::to_integer<std::uint32_t>(data[offset + 1U]) << 16U) |
        (std::to_integer<std::uint32_t>(data[offset + 2U]) << 8U) |
        std::to_integer<std::uint32_t>(data[offset + 3U]);
}

} // namespace progpu::native::text::detail

#endif
