#pragma once

#include "progpu_native_gpu_records.hpp"

#include <array>
#include <cmath>
#include <cstdint>

namespace progpu::native {

// Private GPU-uniform certificate, not a public scene/frame ABI or a glyph
// admission policy. Call only for the owner's actual unshifted full-target pass.
// All other create_uniforms users retain zero, including translated layers.
// Negative -1 is distinct from the original positive bounded Texture/ROP tags
// sharing offset204; its consumers test >0.5/>1.5 and remain disabled at root.
// O(1) work/storage; no allocation, GPU work or shader/pipeline acquisition.
inline bool certify_root_glyph_coverage_frame(
    gpu_uniforms& uniforms,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float viewport_x,
    float viewport_y,
    float viewport_width,
    float viewport_height) noexcept {
    uniforms.pad0 = 0.0F;
    const float dpi = uniforms.dpi_scale;
    const float physical_width = static_cast<float>(target_width);
    const float physical_height = static_cast<float>(target_height);
    if (target_width == 0U || target_height == 0U ||
        static_cast<double>(physical_width) != static_cast<double>(target_width) ||
        static_cast<double>(physical_height) != static_cast<double>(target_height) ||
        !std::isfinite(dpi) || dpi <= 0.0F ||
        viewport_x != 0.0F || viewport_y != 0.0F ||
        viewport_width != physical_width ||
        viewport_height != physical_height ||
        uniforms.render_origin[0] != 0.0F ||
        uniforms.render_origin[1] != 0.0F) {
        return false;
    }
    const float logical_width = physical_width / dpi;
    const float logical_height = physical_height / dpi;
    if (!std::isfinite(logical_width) || !std::isfinite(logical_height) ||
        logical_width <= 0.0F || logical_height <= 0.0F ||
        logical_width * dpi != physical_width ||
        logical_height * dpi != physical_height ||
        uniforms.canvas_size[0] != logical_width ||
        uniforms.canvas_size[1] != logical_height) {
        return false;
    }
    std::array<float, 16U> projection{};
    projection[0] = 2.0F / logical_width;
    projection[5] = -2.0F / logical_height;
    projection[10] = -1.0F;
    projection[12] = -1.0F;
    projection[13] = 1.0F;
    projection[15] = 1.0F;
    if (!std::isfinite(projection[0]) || !std::isfinite(projection[5]) ||
        projection[0] <= 0.0F || projection[5] >= 0.0F) {
        return false;
    }
    for (std::uint32_t i = 0U; i < 16U; ++i) {
        const float identity = i % 5U == 0U ? 1.0F : 0.0F;
        if (uniforms.projection[i] != projection[i] ||
            uniforms.model_view_projection[i] != identity ||
            uniforms.view[i] != identity) {
            return false;
        }
    }
    uniforms.pad0 = -1.0F;
    return true;
}

static_assert(offsetof(gpu_uniforms, pad0) == 204U);
static_assert(sizeof(gpu_uniforms) == 224U);

} // namespace progpu::native
