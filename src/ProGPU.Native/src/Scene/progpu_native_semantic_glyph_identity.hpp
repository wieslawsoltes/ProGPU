#pragma once

#include "progpu_native_draw_state.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>

namespace progpu::native::semantic {

// Borrow only from the immutable, fully validated scene during one synchronous
// packed-page compilation. Resource ids/revisions and positioned glyph records
// are deliberately not raster identity; no borrowed key survives this call.
struct glyph_resource_identity {
    std::uint32_t flags{};
    std::span<const std::byte> outlines;
    std::span<const std::byte> segments;

    bool operator==(const glyph_resource_identity& other) const noexcept {
        const auto same_bytes = [](auto left, auto right) noexcept {
            return left.size() == right.size() &&
                (left.empty() ||
                    std::memcmp(left.data(), right.data(), left.size()) == 0);
        };
        return flags == other.flags && same_bytes(outlines, other.outlines) &&
            same_bytes(segments, other.segments);
    }
};

struct glyph_resource_identity_hash {
    std::size_t operator()(const glyph_resource_identity& value) const noexcept {
        const std::array<std::uint64_t, 3U> sizes{
            value.flags, value.outlines.size(), value.segments.size()};
        auto hash = append_fnv1a64(14695981039346656037ULL,
            sizes.data(), sizeof(sizes));
        hash = append_fnv1a64(hash, value.outlines.data(), value.outlines.size());
        return static_cast<std::size_t>(append_fnv1a64(
            hash, value.segments.data(), value.segments.size()));
    }
};

} // namespace progpu::native::semantic
