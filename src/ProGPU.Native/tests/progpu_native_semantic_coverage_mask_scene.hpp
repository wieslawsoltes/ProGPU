#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::tests {

std::vector<std::byte> create_semantic_coverage_mask_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height);

} // namespace progpu::native::tests
