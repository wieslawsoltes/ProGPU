#pragma once

#include <cstddef>
#include <span>
#include <vector>

namespace progpu::native::browser {

// Private, versioned little-endian JS authoring transport. This adapter invokes
// the original semantic_scene_builder; it is not a second scene compiler.
// Failure preserves the caller's previous output. The public raw native stream
// bypasses this deliberately bounded typed authoring surface entirely.
bool compile_scene_packet(std::span<const std::byte> packet,
    std::vector<std::byte>& stream, const char*& error) noexcept;

} // namespace progpu::native::browser
