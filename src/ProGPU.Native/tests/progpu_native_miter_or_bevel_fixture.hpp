#pragma once

#include "progpu_native.h"
#include "../src/Backend/progpu_native_geometry_stroke.hpp"
#include "../src/Scene/progpu_native_semantic_stroke.hpp"
#include "../src/Scene/progpu_native_semantic_path_stroke.hpp"

#include <array>
#include <cstdint>
#include <cstring>
#include <vector>

namespace progpu::native::tests {

// These are original ProGPU contract controls, not executed Microsoft receipts.
// Axis-aligned coordinates make the admitted triangle vertices exact literals.
inline bool miter_or_bevel_join_triangles_and_masks() {
    const auto same_point = [](progpu_native_point a, progpu_native_point b) {
        return a.x == b.x && a.y == b.y;
    };
    const auto same_triangle = [&](const stroke_triangle& a, const stroke_triangle& b) {
        return same_point(a.p0, b.p0) && same_point(a.p1, b.p1) && same_point(a.p2, b.p2);
    };
    constexpr stroke_triangle bevel{{1.0F, -1.0F}, {1.0F, 0.0F}, {2.0F, 0.0F}};
    constexpr stroke_triangle tip{{1.0F, -1.0F}, {2.0F, -1.0F}, {2.0F, 0.0F}};
    constexpr stroke_triangle sentinel{{91.0F, 92.0F}, {93.0F, 94.0F}, {95.0F, 96.0F}};
    constexpr std::array limits{1.0F, 1.4F, 1.5F, 2.0F};
    for (bool wpf : {false, true}) {
        for (std::size_t index = 0U; index < limits.size(); ++index) {
            std::array<stroke_triangle, 8U> triangles{};
            triangles.fill(sentinel);
            const std::size_t expected_count = index < 2U ? 1U : 2U;
            if (create_join_triangles(triangles, PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL,
                    2.0F, limits[index], {1.0F, 0.0F}, {1.0F, 0.0F}, {0.0F, 1.0F}, wpf) != expected_count ||
                !same_triangle(triangles[0U], bevel) ||
                (expected_count == 2U && !same_triangle(triangles[1U], tip))) return false;
            for (std::size_t triangle = 0U; triangle < triangles.size(); ++triangle) {
                if (triangle >= expected_count && !same_triangle(triangles[triangle], sentinel)) return false;
                if (triangle >= expected_count) continue;
                std::uint32_t exterior = 99U, internal = 99U;
                classify_join_triangle_edges(PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL,
                    expected_count, triangle, exterior, internal);
                if (exterior != (expected_count == 1U ? 7U : 3U) ||
                    internal != (expected_count == 2U && triangle == 0U ? 4U : 0U)) return false;
            }
        }
        std::array<stroke_triangle, 8U> triangles{};
        triangles.fill(sentinel);
        if (create_join_triangles(triangles, PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL,
                2.0F, 1.0F, {}, {1.0F, 0.0F}, {-1.0F, 0.0F}, wpf) != 0U ||
            create_join_triangles(triangles, 4U, 0.0F, 1.0F, {}, {}, {}, wpf) != 0U ||
            create_join_triangles(triangles, 4U, 2.0F, 1.0F,
                {1.0F, 0.0F}, {1.0F, 0.0F}, {0.0F, 1.0F}, wpf) != 0U) return false;
        for (const auto& triangle : triangles) if (!same_triangle(triangle, sentinel)) return false;
    }
    // Preserve the established WPF policy on old enum values; explicit 3 is
    // not an excuse to change value 0's clipped miter or reversal square.
    std::array<stroke_triangle, 8U> old{};
    if (create_join_triangles(old, PROGPU_NATIVE_STROKE_JOIN_MITER, 2.0F, 1.0F,
            {1.0F, 0.0F}, {1.0F, 0.0F}, {0.0F, 1.0F}, true) != 3U) return false;
    for (std::uint32_t join : {0U, 1U})
        if (create_join_triangles(old, join, 2.0F, 1.0F, {},
                {1.0F, 0.0F}, {-1.0F, 0.0F}, true) != 3U) return false;
    return create_join_triangles(old, PROGPU_NATIVE_STROKE_JOIN_ROUND, 2.0F, 1.0F,
        {}, {1.0F, 0.0F}, {-1.0F, 0.0F}, true) == 8U;
}

inline std::array<progpu_native_path_segment, 4U> miter_or_bevel_rectangle(std::size_t first) {
    constexpr std::array<progpu_native_point, 4U> corners{{{10, 10}, {30, 10}, {30, 30}, {10, 30}}};
    std::array<progpu_native_path_segment, 4U> segments{};
    for (std::size_t index = 0U; index < segments.size(); ++index) {
        segments[index].p0 = corners[(first + index) % corners.size()];
        segments[index].p1 = corners[(first + index + 1U) % corners.size()];
        segments[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    }
    return segments;
}

inline bool miter_or_bevel_query_contract() {
    constexpr std::array<std::uint8_t, 4U> stroked{1U, 1U, 1U, 1U};
    constexpr std::array<float, 2U> dashes{100.0F, 1.0F};
    for (std::size_t first = 0U; first < 4U; ++first) {
        const auto segments = miter_or_bevel_rectangle(first);
        const progpu_native_geometry_query_figure figure{segments[0U].p0, 0U, 4U, 1U};
        for (bool dashed : {false, true}) {
            for (float limit : {1.0F, 2.0F}) {
                progpu_native_geometry_query_pen pen{8.0F, limit, 0.0F, 0U, 0U, 0U,
                    PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL};
                progpu_native_image_rect bounds{};
                std::uint32_t has = 0U, contains = 0U;
                const auto query = [&](const progpu_native_point* point) {
                    return progpu_native_geometry_stroke_query(&figure, 1U, segments.data(), stroked.data(), 4U,
                        &pen, dashed ? dashes.data() : nullptr, dashed ? 2U : 0U, nullptr, point,
                        0.01F, &bounds, &has, &contains);
                };
                if (query(nullptr) != PROGPU_NATIVE_STATUS_SUCCESS || has != 1U ||
                    bounds.x != 6.0F || bounds.y != 6.0F ||
                    bounds.width != 28.0F || bounds.height != 28.0F) return false;
                const progpu_native_point body{31.0F, 9.0F}, tip{33.0F, 7.0F}, empty{20.0F, 20.0F};
                if (query(&body) != PROGPU_NATIVE_STATUS_SUCCESS || contains != 1U ||
                    query(&tip) != PROGPU_NATIVE_STATUS_SUCCESS || contains != (limit == 1.0F ? 0U : 1U) ||
                    query(&empty) != PROGPU_NATIVE_STATUS_SUCCESS || contains != 0U) return false;
                pen.line_join = 4U;
                bounds = {91.0F, 92.0F, 93.0F, 94.0F};
                has = contains = 99U;
                if (query(&body) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || has != 0U || contains != 0U ||
                    bounds.x != 0.0F || bounds.y != 0.0F || bounds.width != 0.0F || bounds.height != 0.0F) return false;
            }
        }
    }
    return true;
}

inline bool miter_or_bevel_semantic_resources() {
    constexpr progpu_native_affine_2d identity{1, 0, 0, 1, 0, 0};
    constexpr std::array modes{0U, static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS),
        static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE),
        static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE)};
    for (std::uint32_t mode : modes) {
        progpu_native_scene_stroke stroke{};
        stroke.struct_size = sizeof(stroke);
        stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
        stroke.flags = mode | PROGPU_NATIVE_POLYLINE_FLAG_CLOSED;
        stroke.point_count = 4U;
        stroke.dash_interval_count = 2U;
        stroke.color = {1, 0, 0, 1};
        stroke.transform = identity;
        stroke.stroke_thickness = mode == PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE ? 0.0F : 8.0F;
        stroke.miter_limit = 1.0F;
        stroke.line_join = PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL;
        std::size_t points = 99U, doubles = 99U;
        constexpr std::size_t auxiliary = 4U * sizeof(progpu_native_point) + 2U * sizeof(double);
        if (!semantic_stroke_resource_layout(&stroke, 1U, auxiliary, points, doubles) || points != 4U || doubles != 2U) return false;
        const auto retained = make_semantic_polyline(stroke);
        if (((retained.flags & PROGPU_NATIVE_POLYLINE_JOIN_MASK) >> PROGPU_NATIVE_POLYLINE_JOIN_SHIFT) != 3U ||
            (retained.flags & mode) != mode || retained.dash_style != 1U) return false;
        stroke.line_join = 4U;
        if (semantic_stroke_resource_layout(&stroke, 1U, auxiliary, points, doubles) || points != 0U || doubles != 0U) return false;
    }

    const auto segments = miter_or_bevel_rectangle(0U);
    constexpr std::array<std::uint8_t, 4U> smooth{0U, 0U, 0U, 0U};
    constexpr std::array<double, 2U> dashes{100.0, 1.0};
    constexpr std::array primitive_modes{0U, static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE),
        static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE)};
    for (std::uint32_t mode : primitive_modes) {
        for (bool dashed : {false, true}) {
            semantic_path_stroke::style style{};
            style.transform = identity;
            style.thickness = mode == PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE ? 0.0F : 8.0F;
            style.miter_limit = 1.0F;
            style.line_join = PROGPU_NATIVE_STROKE_JOIN_MITER_OR_BEVEL;
            style.primitive_flags = mode;
            mil::curve_dash::run_buffer scratch;
            std::vector<progpu_native_geometry_primitive> primitives;
            std::vector<std::uint32_t> brushes;
            const std::span<const double> pattern = dashed ? std::span<const double>(dashes) : std::span<const double>{};
            if (semantic_path_stroke::compile(segments, smooth, true, pattern, style, 7U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::success ||
                primitives.size() != 8U || brushes.size() != primitives.size()) return false;
            std::size_t joins = 0U;
            for (std::size_t index = 0U; index < primitives.size(); ++index) {
                const auto& primitive = primitives[index];
                if (brushes[index] != 7U || (primitive.flags & mode) != mode) return false;
                if (primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
                    ++joins;
                    if (((primitive.flags >> PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) & 3U) != 3U ||
                        primitive.p3.x != 1.0F) return false;
                }
            }
            if (joins != 4U) return false;
            const auto prior_primitives = primitives;
            const auto prior_brushes = brushes;
            style.line_join = 4U;
            if (semantic_path_stroke::compile(segments, smooth, true, pattern, style, 7U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::invalid ||
                primitives.size() != prior_primitives.size() || brushes != prior_brushes ||
                std::memcmp(primitives.data(), prior_primitives.data(),
                    primitives.size() * sizeof(progpu_native_geometry_primitive)) != 0) return false;
        }
    }
    return true;
}

} // namespace progpu::native::tests
