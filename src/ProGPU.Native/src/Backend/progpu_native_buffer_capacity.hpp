#pragma once

#include <algorithm>
#include <cstdint>

namespace progpu::native {

inline constexpr std::uint64_t portable_max_buffer_size =
    256ULL * 1024ULL * 1024ULL;

inline bool try_calculate_buffer_capacity(
    std::uint64_t current,
    std::uint64_t required,
    std::uint64_t initial,
    std::uint64_t maximum,
    std::uint64_t& capacity) noexcept {
    if (maximum == 0U || required > maximum || current > maximum) {
        return false;
    }

    capacity = std::max(
        std::min(std::max<std::uint64_t>(initial, 1U), maximum),
        current);
    while (capacity < required) {
        capacity = capacity > maximum / 2U
            ? maximum
            : capacity * 2U;
    }
    return true;
}

} // namespace progpu::native
