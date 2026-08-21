#pragma once

#include "progpu_native_semantic_advanced_blend_scene.hpp"

#include <IOSurface/IOSurface.h>

namespace progpu::native::tests {

void verify_semantic_advanced_blend_scene(
    IOSurfaceRef surface,
    const char* output_path);

} // namespace progpu::native::tests
