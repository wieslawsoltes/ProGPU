#pragma once

#include <IOSurface/IOSurface.h>

#include <cstddef>
#include <vector>

namespace progpu::native::tests {

std::vector<std::byte> create_semantic_advanced_blend_scene_stream();

void verify_semantic_advanced_blend_scene(
    IOSurfaceRef surface,
    const char* output_path);

} // namespace progpu::native::tests
