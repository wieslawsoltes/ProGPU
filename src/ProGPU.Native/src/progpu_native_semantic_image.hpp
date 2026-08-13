#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

struct semantic_image_options final {
    float cubic_b = 0.0F;
    float cubic_c = 0.5F;
    bool has_color_matrix = false;
    progpu_native_scene_image_color_matrix color_matrix{};
};

bool validate_image_draw_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes,
    semantic_image_options& options) noexcept;

} // namespace progpu::native::semantic
