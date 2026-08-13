#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::tests {

std::vector<std::byte> create_semantic_cubic_image_scene_stream(
    std::uint32_t width,
    std::uint32_t height);

} // namespace progpu::native::tests
