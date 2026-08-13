#pragma once

#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_draw_state.hpp"
#include "progpu_native_effect_plan.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_frame_execution.hpp"
#include "progpu_native_geometry.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_replay_execution.hpp"
#include "progpu_native_scene.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_effect_cache.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_native_semantic_validation.hpp"
#include "progpu_native_webgpu_resources.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace progpu::native::execution {

using semantic_scissor = semantic::scissor;
using semantic_compilation_budget = semantic::compilation_budget;
using semantic_layer_budget = semantic::layer_budget;
using semantic::apply_semantic_state;
using semantic::intersect_semantic_scissors;
using semantic::is_valid_semantic_analytic;
using semantic::is_valid_semantic_glyph_outline;
using semantic::is_valid_semantic_image;
using semantic::is_valid_semantic_path;
using semantic::is_valid_semantic_positioned_glyph;
using semantic::is_valid_semantic_segment;
using semantic::localize_semantic_state;
using semantic::resolve_semantic_layer_scissor;
using semantic::resolve_semantic_scissor;
using semantic::resolve_semantic_target_scissor;
using semantic::semantic_default_layer;
using semantic::semantic_layer_target_cursor;
using semantic::semantic_state_cursor;
inline constexpr std::uint32_t semantic_max_draw_passes =
    semantic::max_draw_passes;
inline constexpr std::uint32_t semantic_max_effect_passes =
    semantic::max_effect_passes;
inline constexpr std::uint32_t semantic_effect_uniform_alignment =
    semantic::effect_uniform_alignment;
inline constexpr std::uint64_t semantic_max_total_compiled_bytes =
    semantic::max_total_compiled_bytes;
inline constexpr std::uint64_t semantic_max_coverage_bytes =
    semantic::max_coverage_bytes;

inline void multiply_vertex_alpha(
    std::vector<vector_vertex>& vertices,
    float opacity) noexcept {
    if (opacity == 1.0F) {
        return;
    }
    for (auto& vertex : vertices) {
        vertex.color[3] *= opacity;
    }
}

inline void set_brush_opacity(
    std::vector<std::byte>& brushes,
    float opacity) noexcept {
    for (std::size_t offset = 4U; offset < brushes.size();
         offset += gpu_brush_size) {
        std::memcpy(brushes.data() + offset, &opacity, sizeof(opacity));
    }
}

template<typename TMetrics>
inline void clear_metrics(TMetrics* metrics) noexcept {
    if (metrics == nullptr || metrics->struct_size < sizeof(TMetrics)) {
        return;
    }
    const std::uint32_t struct_size = metrics->struct_size;
    *metrics = {};
    metrics->struct_size = struct_size;
}

} // namespace progpu::native::execution
