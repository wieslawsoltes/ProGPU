#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

struct progpu_native_engine;
struct semantic_render_bundle_span;

namespace progpu::native {
namespace semantic {
struct scissor;
struct semantic_layer_mask;
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

bool create_semantic_vector_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const progpu_native_scene_resource& resource,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation);

bool create_semantic_brush_mask_binding(
    progpu_native_engine& engine,
    const semantic::semantic_layer_mask& parsed,
    const semantic::scissor& target_extent,
    float dpi_scale,
    semantic_render_bundle_span& operation);

} // namespace execution
} // namespace progpu::native
