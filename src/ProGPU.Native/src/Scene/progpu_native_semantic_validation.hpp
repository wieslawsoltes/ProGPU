#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

struct semantic_image_sampler_options final {
    bool mag_linear = false;
    bool min_linear = false;
    bool mip_linear = false;
    std::uint16_t max_anisotropy = 1U;
};

bool resolve_semantic_image_sampler_options(
    std::uint32_t sampling,
    std::uint32_t max_anisotropy,
    semantic_image_sampler_options& options) noexcept;

bool is_valid_semantic_analytic(
    const progpu_native_analytic_primitive& primitive) noexcept;

bool is_valid_semantic_segment(
    const progpu_native_path_segment& segment,
    bool allow_arc) noexcept;

bool is_valid_semantic_path(
    const progpu_native_scene_path_fill& path,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes = nullptr) noexcept;

bool is_valid_semantic_glyph_outline(
    const progpu_native_scene_glyph_outline& outline,
    std::uint64_t segment_count,
    std::uint64_t* coverage_bytes = nullptr) noexcept;

bool is_valid_semantic_positioned_glyph(
    const progpu_native_positioned_glyph& glyph,
    std::uint64_t outline_count) noexcept;
bool is_valid_semantic_color_glyph_bitmap(
    const progpu_native_scene_color_glyph_bitmap& bitmap,
    std::size_t pixel_bytes) noexcept;

bool is_valid_semantic_text_style(
    const progpu_native_scene_text_style& style) noexcept;

bool is_valid_semantic_image(
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes) noexcept;

bool is_valid_semantic_image_sampling_options(
    const progpu_native_scene_image_sampling_options& options) noexcept;

bool is_valid_semantic_image_color_matrix(
    const progpu_native_scene_image_color_matrix& matrix) noexcept;

bool is_valid_semantic_image_effect(
    const progpu_native_scene_image_effect& effect) noexcept;

bool is_valid_semantic_layer(
    const progpu_native_scene_layer& layer) noexcept;

bool is_valid_semantic_effect(
    const progpu_native_group_effect& effect) noexcept;

bool is_valid_semantic_camera_3d(
    const progpu_native_scene_camera_3d& camera) noexcept;

bool is_valid_semantic_line_3d(
    const progpu_native_scene_line_3d& line) noexcept;

bool is_valid_semantic_mesh_3d(
    const progpu_native_scene_mesh_3d& mesh,
    std::size_t vertex_count,
    std::size_t index_count) noexcept;

bool is_valid_semantic_mesh_3d_vertex(
    const progpu_native_scene_mesh_3d_vertex& vertex) noexcept;

} // namespace progpu::native::semantic
