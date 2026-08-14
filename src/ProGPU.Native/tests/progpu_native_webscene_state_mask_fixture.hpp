#pragma once

#include <IOSurface/IOSurface.h>

namespace progpu::native::tests {

void verify_semantic_state_mask_scene(
    IOSurfaceRef surface,
    const char* output_path);

} // namespace progpu::native::tests
