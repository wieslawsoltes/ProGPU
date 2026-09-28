#pragma once

#include "progpu_native_draw_state.hpp"

#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>

namespace progpu::native {

// The caller has validated the complete outline and segment arena. Borrow only
// its selected segment bytes during this synchronous raster rebuild. The arena
// offset locates bytes; it is not coverage identity. No key survives the call.
struct glyph_raster_identity {
    std::array<std::uint32_t, 6U> raster_bits{};
    std::span<const std::byte> segments;

    bool operator==(const glyph_raster_identity& other) const noexcept {
        return raster_bits == other.raster_bits &&
            segments.size() == other.segments.size() &&
            (segments.empty() || std::memcmp(segments.data(), other.segments.data(),
                segments.size()) == 0);
    }
};

inline glyph_raster_identity make_glyph_raster_identity(
    const progpu_native_glyph_outline& outline,
    std::span<const progpu_native_path_segment> selected_segments) noexcept {
    return {{std::bit_cast<std::uint32_t>(outline.min_x),
        std::bit_cast<std::uint32_t>(outline.min_y),
        std::bit_cast<std::uint32_t>(outline.max_x),
        std::bit_cast<std::uint32_t>(outline.max_y),
        std::bit_cast<std::uint32_t>(outline.raster_scale),
        std::bit_cast<std::uint32_t>(outline.subpixel_x)},
        std::as_bytes(selected_segments)};
}

struct glyph_raster_identity_hash {
    std::size_t operator()(const glyph_raster_identity& value) const noexcept {
        auto hash = append_fnv1a64(14695981039346656037ULL,
            value.raster_bits.data(), sizeof(value.raster_bits));
        const std::uint64_t size = value.segments.size();
        hash = append_fnv1a64(hash, &size, sizeof(size));
        return static_cast<std::size_t>(append_fnv1a64(
            hash, value.segments.data(), value.segments.size()));
    }
};

} // namespace progpu::native
