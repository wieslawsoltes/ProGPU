#pragma once

#include "progpu_native.h"

#include <cstdint>

namespace progpu::native::semantic {

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

bool is_valid_semantic_text_style(
    const progpu_native_scene_text_style& style) noexcept;

bool is_valid_semantic_image(
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes) noexcept;

bool is_valid_semantic_image_sampling_options(
    const progpu_native_scene_image_sampling_options& options) noexcept;

bool is_valid_semantic_image_color_matrix(
    const progpu_native_scene_image_color_matrix& matrix) noexcept;

} // namespace progpu::native::semantic
