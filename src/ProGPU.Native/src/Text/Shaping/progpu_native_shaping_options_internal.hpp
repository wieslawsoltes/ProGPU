#pragma once

#include "progpu_native_text.hpp"

#include <cstdint>

// Shared validation for the ProGPU-owned ShapingRequest/TextShapingOptions
// contract port. Kept internal so the public header remains the stable native
// text surface and all shaping entry points reject the same invalid states.

namespace progpu::native::text::detail {

inline bool valid_shaping_options(
    shaping_direction direction,
    shaping_cluster_level cluster_level,
    shaping_buffer_flags buffer_flags,
    bool allow_unspecified_direction) noexcept {
    const auto direction_value = static_cast<std::uint8_t>(direction);
    const auto cluster_value = static_cast<std::uint8_t>(cluster_level);
    const auto flags = static_cast<std::uint8_t>(buffer_flags);
    const auto preserve = static_cast<std::uint8_t>(
        shaping_buffer_flags::preserve_default_ignorables);
    const auto remove = static_cast<std::uint8_t>(
        shaping_buffer_flags::remove_default_ignorables);
    return direction_value <=
            static_cast<std::uint8_t>(shaping_direction::bottom_to_top) &&
        (allow_unspecified_direction ||
            direction != shaping_direction::unspecified) &&
        cluster_value <=
            static_cast<std::uint8_t>(shaping_cluster_level::graphemes) &&
        !((flags & preserve) != 0U && (flags & remove) != 0U);
}

} // namespace progpu::native::text::detail
