#pragma once

#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>

namespace progpu::native::semantic {

struct scissor final {
    std::uint32_t x = 0U;
    std::uint32_t y = 0U;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    bool drawable = true;

    bool operator==(const scissor&) const noexcept = default;
};

inline constexpr std::uint32_t max_draw_passes = 16U * 1024U;
inline constexpr std::uint32_t max_effect_passes = 16U * 1024U;
inline constexpr std::uint32_t effect_uniform_alignment = 256U;
inline constexpr std::uint64_t max_vertex_bytes =
    256ULL * 1024ULL * 1024ULL;
inline constexpr std::uint64_t max_index_bytes =
    64ULL * 1024ULL * 1024ULL;
inline constexpr std::uint64_t max_texture_bytes =
    256ULL * 1024ULL * 1024ULL;
inline constexpr std::uint64_t max_coverage_bytes =
    256ULL * 1024ULL * 1024ULL;
inline constexpr std::uint64_t max_total_compiled_bytes =
    512ULL * 1024ULL * 1024ULL;

struct compilation_budget {
    std::uint32_t draw_passes = 0U;
    std::uint64_t vertex_bytes = 0U;
    std::uint64_t index_bytes = 0U;
    std::uint64_t texture_bytes = 0U;
    std::uint64_t coverage_bytes = 0U;

    bool add(
        std::uint64_t vertices,
        std::uint64_t indices,
        std::uint64_t textures,
        std::uint64_t coverage) noexcept {
        const auto checked_add = [](std::uint64_t current,
                                    std::uint64_t value,
                                    std::uint64_t limit,
                                    std::uint64_t& result) noexcept {
            if (value > limit || current > limit - value) {
                return false;
            }
            result = current + value;
            return true;
        };
        std::uint64_t next_vertices = 0U;
        std::uint64_t next_indices = 0U;
        std::uint64_t next_textures = 0U;
        std::uint64_t next_coverage = 0U;
        if (draw_passes == max_draw_passes ||
            !checked_add(vertex_bytes, vertices,
                max_vertex_bytes, next_vertices) ||
            !checked_add(index_bytes, indices,
                max_index_bytes, next_indices) ||
            !checked_add(texture_bytes, textures,
                max_texture_bytes, next_textures) ||
            !checked_add(coverage_bytes, coverage,
                max_coverage_bytes, next_coverage)) {
            return false;
        }
        const std::uint64_t total = next_vertices + next_indices +
            next_textures + next_coverage;
        if (total > max_total_compiled_bytes) {
            return false;
        }
        ++draw_passes;
        vertex_bytes = next_vertices;
        index_bytes = next_indices;
        texture_bytes = next_textures;
        coverage_bytes = next_coverage;
        return true;
    }

    std::uint64_t total_bytes() const noexcept {
        return vertex_bytes + index_bytes + texture_bytes + coverage_bytes;
    }
};

struct layer_budget {
    std::array<std::uint64_t,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> live_bytes{};
    std::array<bool,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> live_materialized{};
    std::uint32_t scope_depth = 0U;
    std::uint32_t materialized_depth = 0U;
    std::uint32_t peak_materialized_depth = 0U;
    std::uint64_t current_bytes = 0U;
    std::uint64_t peak_bytes = 0U;
    std::array<std::uint32_t,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> slot_widths{};
    std::array<std::uint32_t,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> slot_heights{};
    std::array<bool,
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> slot_effected{};

    bool push(
        const scissor& extent,
        bool materialized,
        bool effected = false) noexcept {
        const std::uint64_t width = materialized
            ? std::max(extent.width, 1U)
            : 0U;
        const std::uint64_t height = materialized
            ? std::max(extent.height, 1U)
            : 0U;
        if (materialized &&
            (height > std::numeric_limits<std::uint64_t>::max() / width ||
                width * height >
                    std::numeric_limits<std::uint64_t>::max() / 4U)) {
            return false;
        }
        const std::uint64_t bytes = width * height * 4U;
        if (scope_depth == PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH ||
            (materialized && materialized_depth ==
                PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS) ||
            (materialized &&
                (bytes > PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES ||
                    current_bytes >
                        PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES - bytes))) {
            return false;
        }
        if (materialized) {
            slot_widths[materialized_depth] = std::max(
                slot_widths[materialized_depth],
                static_cast<std::uint32_t>(width));
            slot_heights[materialized_depth] = std::max(
                slot_heights[materialized_depth],
                static_cast<std::uint32_t>(height));
            slot_effected[materialized_depth] =
                slot_effected[materialized_depth] || effected;
        }
        live_bytes[scope_depth] = materialized ? bytes : 0U;
        live_materialized[scope_depth++] = materialized;
        materialized_depth += materialized ? 1U : 0U;
        peak_materialized_depth = std::max(
            peak_materialized_depth,
            materialized_depth);
        current_bytes += materialized ? bytes : 0U;
        peak_bytes = std::max(peak_bytes, current_bytes);
        return pooled_bytes() <= PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES;
    }

    void pop() noexcept {
        --scope_depth;
        current_bytes -= live_bytes[scope_depth];
        materialized_depth -= live_materialized[scope_depth] ? 1U : 0U;
    }

    std::uint64_t pooled_bytes() const noexcept {
        return pooled_bytes_per_pixel(4U, false);
    }

    std::uint64_t pooled_effect_bytes() const noexcept {
        return pooled_bytes_per_pixel(12U, true);
    }

    std::uint32_t maximum_width() const noexcept {
        return *std::max_element(slot_widths.begin(), slot_widths.end());
    }

    std::uint32_t maximum_height() const noexcept {
        return *std::max_element(slot_heights.begin(), slot_heights.end());
    }

private:
    std::uint64_t pooled_bytes_per_pixel(
        std::uint64_t bytes_per_pixel,
        bool only_effected) const noexcept {
        std::uint64_t result = 0U;
        for (std::uint32_t index = 0U;
             index < peak_materialized_depth;
             ++index) {
            if (only_effected && !slot_effected[index]) {
                continue;
            }
            const std::uint64_t width = slot_widths[index];
            const std::uint64_t height = slot_heights[index];
            if (height != 0U &&
                (width > std::numeric_limits<std::uint64_t>::max() /
                        height ||
                    width * height >
                        std::numeric_limits<std::uint64_t>::max() /
                            bytes_per_pixel)) {
                return std::numeric_limits<std::uint64_t>::max();
            }
            const std::uint64_t bytes =
                width * height * bytes_per_pixel;
            if (result >
                std::numeric_limits<std::uint64_t>::max() - bytes) {
                return std::numeric_limits<std::uint64_t>::max();
            }
            result += bytes;
        }
        return result;
    }
};

} // namespace progpu::native::semantic
