#pragma once

#include "progpu_native.h"

#include <array>
#include <cstddef>
#include <cstdint>

struct resolved_draw_state {
    float opacity = 1.0F;
    float group_opacity = 1.0F;
    std::uint32_t group_revision = 0U;
    std::uint32_t group_blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    bool has_clip = false;
    bool has_drawable_clip = true;
    std::uint32_t clip_x = 0U;
    std::uint32_t clip_y = 0U;
    std::uint32_t clip_width = 0U;
    std::uint32_t clip_height = 0U;
    bool has_group_mask = false;
    progpu_native_group_mask group_mask{};
    bool has_group_effect = false;
    progpu_native_group_effect group_effect{};
    std::uint32_t effect_count = 0U;
    std::uint32_t effect_chain_revision = 0U;
    std::array<progpu_native_group_effect,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS> group_effects{};
};

std::uint64_t append_fnv1a64(
    std::uint64_t hash,
    const void* data,
    std::size_t size) noexcept;

void normalize_group_mask_radii(
    progpu_native_group_mask& mask) noexcept;

bool resolve_draw_state(
    const progpu_native_draw_state* state,
    std::uintptr_t target_view,
    std::uint32_t target_width,
    std::uint32_t target_height,
    float dpi_scale,
    resolved_draw_state& resolved) noexcept;
