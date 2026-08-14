#pragma once

#include <IOSurface/IOSurface.h>

#include <cstddef>
#include <vector>

namespace progpu::native::tests {

std::vector<std::byte> create_semantic_masked_effect_layer_scene_stream();

std::vector<std::byte> create_semantic_root_effect_layer_scene_stream();

void verify_semantic_masked_effect_layer_scene(
    IOSurfaceRef surface,
    const char* output_path);

} // namespace progpu::native::tests
