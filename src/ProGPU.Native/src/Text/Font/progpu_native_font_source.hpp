#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::text {

// One original immutable memory-font owner, shared by native text contexts,
// selected hint configurations and snapshots. Collection index is source state,
// not a table offset or the context's fallback-palette index.
struct owned_font_source final {
    const std::vector<std::byte> bytes;
    const std::uint32_t face_index;

    owned_font_source(std::span<const std::byte> original, std::uint32_t index)
        : bytes(original.begin(), original.end()), face_index(index) {}
};

} // namespace progpu::native::text
