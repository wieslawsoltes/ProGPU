#include "progpu_native_png_internal.hpp"

// Direct native port provenance: ProGPU-owned managed PNG codec contracts.
// CRC-32 follows W3C PNG / ISO 3309 with O(N) time and O(1) storage.
namespace progpu::native::image::detail {

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return (static_cast<std::uint32_t>(
                std::to_integer<std::uint8_t>(bytes[offset])) << 24U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 1U])) << 16U) |
        (static_cast<std::uint32_t>(
             std::to_integer<std::uint8_t>(bytes[offset + 2U])) << 8U) |
        static_cast<std::uint32_t>(
            std::to_integer<std::uint8_t>(bytes[offset + 3U]));
}

std::uint32_t update_crc32(
    std::uint32_t crc,
    std::span<const std::byte> bytes) noexcept {
    auto current = crc;
    for (const auto value : bytes) {
        current ^= std::to_integer<std::uint8_t>(value);
        for (std::uint32_t bit = 0U; bit < 8U; ++bit) {
            const auto mask = 0U - (current & 1U);
            current = (current >> 1U) ^ (0xEDB88320U & mask);
        }
    }
    return current;
}

} // namespace progpu::native::image::detail
