#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

struct progpu_native_engine;
struct semantic_render_bundle_span;

namespace progpu::native {
namespace semantic {
struct scissor;
}
namespace execution {

bool create_semantic_coverage_mask_binding(
    progpu_native_engine& engine,
    const progpu_native_scene_layer_coverage_mask& source,
    const std::byte* coverage,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation,
    std::uint64_t& texture_upload_bytes);

} // namespace execution
} // namespace progpu::native
