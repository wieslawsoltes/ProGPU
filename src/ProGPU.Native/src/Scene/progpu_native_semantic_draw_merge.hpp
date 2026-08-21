#pragma once

#include "progpu_native_gpu_records.hpp"

#include <cstdint>
#include <limits>

struct semantic_analytic_draw {
    std::uint64_t vertex_offset_bytes = 0U;
    std::uint64_t index_offset_bytes = 0U;
    std::uint32_t vertex_count = 0U;
    std::uint32_t index_count = 0U;

    bool operator==(const semantic_analytic_draw&) const = default;
};

inline bool try_merge_semantic_analytic_draw(
    semantic_analytic_draw& retained,
    const semantic_analytic_draw& next) noexcept {
    constexpr std::uint64_t vertex_stride =
        sizeof(progpu::native::vector_vertex);
    constexpr std::uint64_t index_stride = sizeof(std::uint32_t);
    if (retained.vertex_count == 0U || retained.index_count == 0U ||
        next.vertex_count == 0U || next.index_count == 0U ||
        retained.vertex_count >
            std::numeric_limits<std::uint32_t>::max() - next.vertex_count ||
        retained.index_count >
            std::numeric_limits<std::uint32_t>::max() - next.index_count ||
        retained.vertex_offset_bytes +
                static_cast<std::uint64_t>(retained.vertex_count) *
                    vertex_stride !=
            next.vertex_offset_bytes ||
        retained.index_offset_bytes +
                static_cast<std::uint64_t>(retained.index_count) *
                    index_stride !=
            next.index_offset_bytes) {
        return false;
    }
    retained.vertex_count += next.vertex_count;
    retained.index_count += next.index_count;
    return true;
}

struct semantic_path_draw {
    std::uint32_t first_index = 0U;
    std::uint32_t index_count = 0U;
};

inline bool try_merge_semantic_path_draw(
    semantic_path_draw& retained,
    const semantic_path_draw& next) noexcept {
    if (retained.index_count == 0U || next.index_count == 0U ||
        retained.index_count >
            std::numeric_limits<std::uint32_t>::max() - next.index_count ||
        retained.first_index + retained.index_count != next.first_index) {
        return false;
    }
    retained.index_count += next.index_count;
    return true;
}

struct semantic_glyph_draw {
    std::uint32_t first_instance = 0U;
    std::uint32_t instance_count = 0U;

    bool operator==(const semantic_glyph_draw&) const = default;
};

inline bool try_merge_semantic_glyph_draw(
    semantic_glyph_draw& retained,
    const semantic_glyph_draw& next) noexcept {
    if (retained.instance_count == 0U || next.instance_count == 0U ||
        retained.instance_count >
            std::numeric_limits<std::uint32_t>::max() - next.instance_count ||
        retained.first_instance + retained.instance_count !=
            next.first_instance) {
        return false;
    }
    retained.instance_count += next.instance_count;
    return true;
}
