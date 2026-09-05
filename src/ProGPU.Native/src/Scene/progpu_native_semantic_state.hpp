#pragma once

#include "progpu_native.h"
#include "progpu_native_semantic_budget.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cmath>
#include <cstring>
#include <span>

namespace progpu::native::semantic {

// Algorithm: WPF half-integer rounding toward the numerically larger integer.
// Time/space: O(1); floats without fractional bits have zero displacement.
inline float wpf_guideline_offset(float value) noexcept {
    if (!(std::abs(value) < 8388608.0F)) return 0.0F;
    float offset = std::floor(value + 0.5F) - value;
    if (offset <= -0.5F) offset += 1.0F;
    return offset;
}

// Algorithm: resolve at most one guideline per axis, including pre-resolved
// dynamic physical offsets. Time/space: O(1), alignment-safe fixed-size reads.
// Multi-coordinate deformation is deliberately not a uniform translation.
inline bool try_uniform_guideline_translation(std::span<const std::byte> payload,
    float dpi, progpu_native_point& translation) noexcept {
    if (payload.size() < sizeof(progpu_native_scene_guideline_set) ||
        !std::isfinite(dpi) || dpi <= 0.0F) return false;
    progpu_native_scene_guideline_set header{};
    std::memcpy(&header, payload.data(), sizeof(header));
    if (header.struct_size != sizeof(header) || header.guideline_x_count > 1U || header.guideline_y_count > 1U ||
        (header.flags & ~PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U) return false;
    const bool explicit_offsets = (header.flags & PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS) != 0U;
    const auto count = header.guideline_x_count + header.guideline_y_count;
    if (payload.size() != sizeof(header) + count * sizeof(double) * (explicit_offsets ? 2U : 1U)) return false;
    const auto axis = [&](std::uint32_t index) {
        double value = 0.0;
        std::memcpy(&value, payload.data() + sizeof(header) +
            (index + (explicit_offsets ? count : 0U)) * sizeof(double), sizeof(value));
        return (explicit_offsets ? static_cast<float>(value) : wpf_guideline_offset(static_cast<float>(value) * dpi)) / dpi;
    };
    const progpu_native_point result{header.guideline_x_count == 0U ? 0.0F : axis(0U),
        header.guideline_y_count == 0U ? 0.0F : axis(header.guideline_x_count)};
    if (!std::isfinite(result.x) || !std::isfinite(result.y)) return false;
    translation = result;
    return true;
}

progpu_native_scene_state semantic_identity_state() noexcept;

class semantic_state_cursor final {
public:
    semantic_state_cursor(
        const std::byte* bytes,
        const progpu_native_scene_header& header,
        float dpi_scale = 0.0F) noexcept;

    progpu_native_scene_state advance(
        const progpu_native_scene_command& command) noexcept;

    progpu_native_scene_state resolve_state(
        std::uint32_t index) const noexcept;

    progpu_native_scene_state read_composite_state(
        std::uint32_t index) const noexcept;

    void snap_composite_point(
        const progpu_native_scene_state& state,
        float& target_x,
        float& target_y) const noexcept;

    bool try_composite_rectangle_inverse(
        const progpu_native_scene_state& state,
        progpu_native_image_rect bounds,
        progpu_native_affine_2d& inverse,
        bool& visible) const noexcept;

    bool has_per_point_guidelines(
        const progpu_native_scene_state& state) const noexcept;

    void snap_draw_point(
        const progpu_native_scene_state& state,
        float& target_x,
        float& target_y) const noexcept;

private:
    std::uint32_t read_guideline_flags(
        const progpu_native_scene_state& state) const noexcept;
    void snap_point(
        const progpu_native_scene_state& state,
        float& target_x,
        float& target_y) const noexcept;
    progpu_native_scene_state read_state(std::uint32_t index) const noexcept;
    progpu_native_scene_state resolve_guidelines(
        progpu_native_scene_state state) const noexcept;

    const std::byte* bytes_;
    const progpu_native_scene_header& header_;
    float dpi_scale_ = 0.0F;
    std::array<progpu_native_scene_state,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> stack_{};
    std::uint32_t depth_ = 0U;
    progpu_native_scene_state current_{};
};

void apply_semantic_state(
    progpu_native_analytic_primitive& primitive,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_analytic_primitive& primitive,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_geometry_primitive& primitive,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_geometry_primitive& primitive,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_scene_point_batch& batch,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_scene_point_batch& batch,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_scene_vertex_mesh& mesh,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_scene_stroke& stroke,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_scene_path_fill& path,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_scene_path_fill& path,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_path_fill& path,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_path_fill& path,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_positioned_glyph& glyph,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_transform(
    progpu_native_positioned_glyph& glyph,
    const progpu_native_scene_state& state) noexcept;

void apply_semantic_state(
    progpu_native_scene_image_draw& image,
    const progpu_native_scene_state& state) noexcept;

void snap_semantic_image_point(
    float& x,
    float& y,
    float dpi_scale) noexcept;

scissor resolve_semantic_scissor(
    const progpu_native_scene_state& state,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale) noexcept;

progpu_native_scene_layer semantic_default_layer() noexcept;

scissor resolve_semantic_layer_scissor(
    const progpu_native_scene_layer& layer,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale) noexcept;

scissor intersect_semantic_scissors(
    const scissor& first,
    const scissor& second) noexcept;

scissor resolve_semantic_target_scissor(
    const progpu_native_scene_state& state,
    const scissor& target,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    float dpi_scale) noexcept;

progpu_native_scene_state localize_semantic_state(
    progpu_native_scene_state state,
    const scissor& target,
    float dpi_scale) noexcept;

class semantic_layer_target_cursor final {
public:
    semantic_layer_target_cursor(
        const std::byte* bytes,
        std::uint32_t frame_width,
        std::uint32_t frame_height,
        float dpi_scale) noexcept;

    scissor advance(
        const progpu_native_scene_command& command) noexcept;

    scissor current() const noexcept;

private:
    const std::byte* bytes_;
    scissor frame_extent_{};
    std::uint32_t frame_width_ = 0U;
    std::uint32_t frame_height_ = 0U;
    float dpi_scale_ = 1.0F;
    std::array<bool,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> scope_materialized_{};
    std::array<scissor,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> extents_{};
    std::uint32_t scope_depth_ = 0U;
    std::uint32_t materialized_depth_ = 0U;
};

} // namespace progpu::native::semantic
