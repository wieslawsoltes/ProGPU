#pragma once
#include <cstdint>

namespace progpu::native {
// Paint-only source policy. Clip sample-grid admission remains independent.
constexpr bool valid_glyph_path_sampling(std::uint32_t value) noexcept {
    const auto grid = value & 0xFFU;
    const auto transfer = value >> 8U;
    return (grid == 1U || grid == 4U || grid == 8U) && transfer <= 13U &&
        (transfer == 0U || grid != 1U);
}
constexpr std::uint32_t glyph_path_sample_grid(std::uint32_t value) noexcept {
    return value & 0xFFU;
}
constexpr float glyph_path_transfer(std::uint32_t value) noexcept {
    return value >> 8U ? -static_cast<float>(value >> 8U) : 1.0F;
}
}
