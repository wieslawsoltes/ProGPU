#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::semantic {

struct semantic_brush_draw final {
    std::uint32_t first_index = 0U;
    std::uint32_t index_count = 0U;
};

struct semantic_brush_page final {
    std::uint64_t scene_hash = 0U;
    bool cache_valid = false;
    std::vector<progpu_native_scene_brush> brushes;
    std::vector<progpu_native_scene_gradient_stop> gradient_stops;
    std::vector<std::uint32_t> remapped_indices;
    std::vector<semantic_brush_draw> command_draws;
};

std::uint32_t semantic_brush_stored_stop_count(
    const progpu_native_scene_brush& brush) noexcept;

bool is_valid_semantic_brush(
    const progpu_native_scene_brush& brush,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept;

bool validate_brush_table(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset) noexcept;

bool validate_draw_brushes(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,
    std::uint32_t expected_count,
    std::uint32_t& error_offset) noexcept;

bool compile_brush_page(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint64_t scene_hash,
    semantic_brush_page& page) noexcept;

bool try_get_draw_brush_index(
    const semantic_brush_page& page,
    std::uint32_t command_index,
    std::uint32_t record_index,
    std::uint32_t& brush_index) noexcept;

const progpu_native_scene_brush* try_get_packed_brush(
    const semantic_brush_page& page,
    std::uint32_t brush_index) noexcept;

} // namespace progpu::native::semantic
