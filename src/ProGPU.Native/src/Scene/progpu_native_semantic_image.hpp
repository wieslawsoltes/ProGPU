#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

struct semantic_image_options final {
    float cubic_b = 0.0F;
    float cubic_c = 0.5F;
    bool has_color_matrix = false;
    bool luminance_to_alpha = false;
    bool has_effect = false;
    const std::byte* patch_bytes = nullptr;
    std::uint32_t patch_count = 0U;
    progpu_native_scene_image_color_matrix color_matrix{};
    progpu_native_scene_image_effect effect{};
};

bool validate_image_draw_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes,
    semantic_image_options& options, std::uint32_t bytes_per_pixel = 4U) noexcept;

void resolve_image_vertex_color(
    const progpu_native_scene_image_draw& image,
    bool has_effect,
    float (&color)[4]) noexcept;

void resolve_image_patch_vertex_attributes(
    const progpu_native_scene_image_draw& image,
    const progpu_native_scene_image_patch& patch,
    bool has_effect,
    float (&color)[4],
    float& patch_kind,
    float& color_blend_mode,
    float& patch_opacity) noexcept;

} // namespace progpu::native::semantic
