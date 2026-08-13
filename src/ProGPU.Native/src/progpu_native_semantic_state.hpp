#pragma once

#include "progpu_native.h"
#include "progpu_native_semantic_budget.hpp"

#include <array>
#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

progpu_native_scene_state semantic_identity_state() noexcept;

class semantic_state_cursor final {
public:
    semantic_state_cursor(
        const std::byte* bytes,
        const progpu_native_scene_header& header) noexcept;

    progpu_native_scene_state advance(
        const progpu_native_scene_command& command) noexcept;

private:
    progpu_native_scene_state read_state(std::uint32_t index) const noexcept;

    const std::byte* bytes_;
    const progpu_native_scene_header& header_;
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
