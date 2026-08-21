#pragma once

#include "progpu_native.h"

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace progpu::native::hit_testing {

struct hit_test_page_layout final {
    std::uint64_t primitive_offset = 0U;
    std::uint64_t node_offset = 0U;
    std::uint64_t primitive_index_offset = 0U;
    std::uint64_t path_segment_offset = 0U;
    std::uint64_t auxiliary_size = 0U;
};

constexpr std::uint64_t align16(std::uint64_t value) noexcept {
    return (value + 15U) & ~std::uint64_t{15U};
}

inline bool finite4(const progpu_native_float_4& value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y) &&
        std::isfinite(value.z) && std::isfinite(value.w);
}

inline bool valid_hit_test_primitive(
    const progpu_native_hit_test_primitive& value,
    std::uint64_t path_segment_count) noexcept {
    const auto clip_end = static_cast<std::uint64_t>(
        value.clip_start_segment) + value.clip_segment_count;
    const auto path_start = value.data1.x;
    const auto path_count = value.data1.y;
    const auto is_path = value.kind == PROGPU_NATIVE_HIT_TEST_PATH_FILL ||
        value.kind == PROGPU_NATIVE_HIT_TEST_PATH_STROKE;
    auto valid_path = true;
    if (is_path) {
        valid_path = path_start >= 0.0F && path_count > 0.0F &&
            path_start <= static_cast<float>(
                std::numeric_limits<std::uint32_t>::max()) &&
            path_count <= static_cast<float>(
                std::numeric_limits<std::uint32_t>::max()) &&
            std::trunc(path_start) == path_start &&
            std::trunc(path_count) == path_count;
        if (valid_path) {
            const auto path_end = static_cast<std::uint64_t>(path_start) +
                static_cast<std::uint64_t>(path_count);
            valid_path = path_end <= path_segment_count &&
            (value.kind != PROGPU_NATIVE_HIT_TEST_PATH_FILL ||
                (value.data1.z == 0.0F || value.data1.z == 1.0F)) &&
            (value.kind != PROGPU_NATIVE_HIT_TEST_PATH_STROKE ||
                (value.data1.z >= 0.0F && value.data1.w >= 0.0F &&
                    value.data2.x >= 0.0F && value.data2.x <= 3.0F &&
                    value.data2.y >= 0.0F && value.data2.y <= 3.0F));
        }
    }
    return std::isfinite(value.bounds_min.x) &&
        std::isfinite(value.bounds_min.y) &&
        std::isfinite(value.bounds_max.x) &&
        std::isfinite(value.bounds_max.y) &&
        value.bounds_min.x <= value.bounds_max.x &&
        value.bounds_min.y <= value.bounds_max.y &&
        finite4(value.data0) && finite4(value.data1) &&
        finite4(value.data2) && finite4(value.inverse_transform0) &&
        finite4(value.inverse_transform1) &&
        std::isfinite(value.z_index) &&
        value.kind <= PROGPU_NATIVE_HIT_TEST_PATH_STROKE &&
        (value.flags & ~(PROGPU_NATIVE_HIT_TEST_VISIBLE |
            PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT)) == 0U &&
        value.clip_fill_rule <= PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
        value.clip_flags <= 1U &&
        ((value.clip_flags == 0U && value.clip_segment_count == 0U) ||
            (value.clip_flags == 1U && value.clip_segment_count != 0U)) &&
        clip_end <= path_segment_count && valid_path;
}

inline bool valid_hit_test_node(
    const progpu_native_hit_test_node& value,
    std::uint64_t node_count,
    std::uint64_t primitive_index_count) noexcept {
    const auto child_end =
        static_cast<std::uint64_t>(value.first_child) + value.child_count;
    const auto primitive_end =
        static_cast<std::uint64_t>(value.first_primitive) +
        value.primitive_count;
    return std::isfinite(value.bounds_min.x) &&
        std::isfinite(value.bounds_min.y) &&
        std::isfinite(value.bounds_max.x) &&
        std::isfinite(value.bounds_max.y) &&
        value.bounds_min.x <= value.bounds_max.x &&
        value.bounds_min.y <= value.bounds_max.y &&
        value.child_count <= 4U && child_end <= node_count &&
        primitive_end <= primitive_index_count &&
        (value.child_count != 0U || value.first_child == 0U);
}

inline bool try_get_hit_test_page_layout(
    std::uint64_t primitive_count,
    std::uint64_t node_count,
    std::uint64_t primitive_index_count,
    std::uint64_t path_segment_count,
    hit_test_page_layout& layout) noexcept {
    constexpr auto maximum = std::numeric_limits<std::uint32_t>::max();
    if (primitive_count > maximum || node_count > maximum ||
        primitive_index_count > maximum || path_segment_count > maximum) {
        return false;
    }
    layout = {};
    const auto primitive_bytes = primitive_count *
        sizeof(progpu_native_hit_test_primitive);
    const auto node_bytes = node_count *
        sizeof(progpu_native_hit_test_node);
    const auto primitive_index_bytes = primitive_index_count *
        sizeof(std::uint32_t);
    const auto path_segment_bytes = path_segment_count *
        sizeof(progpu_native_path_segment);
    layout.node_offset = align16(primitive_bytes);
    layout.primitive_index_offset = align16(
        layout.node_offset + node_bytes);
    layout.path_segment_offset = align16(
        layout.primitive_index_offset + primitive_index_bytes);
    layout.auxiliary_size = layout.path_segment_offset + path_segment_bytes;
    return layout.auxiliary_size <= PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES;
}

} // namespace progpu::native::hit_testing
