#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::tests {

std::vector<std::byte> create_semantic_advanced_blend_scene_stream(
    std::uint32_t width = 64U,
    std::uint32_t height = 48U,
    std::uint32_t blend_mode = PROGPU_NATIVE_BLEND_MULTIPLY);

} // namespace progpu::native::tests
