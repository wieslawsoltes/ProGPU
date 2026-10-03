#pragma once

#include "progpu_native.h"
#include "../src/Backend/progpu_native_geometry_stroke.hpp"
#include "../src/Scene/progpu_native_semantic_path_stroke.hpp"

#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <span>
#include <vector>

namespace progpu::native::tests {

// Authored source/transport controls, not executed original-WPF pixel receipts.
// The existing WPF triangle kernel is the reference for the additive PathJoin
// policy; literal counts distinguish clipping and reversal from generic joins.
inline bool wpf_path_join_triangles_and_flags()
{
    static_assert(PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS == (1U << 8U));
    const auto matches_coverage_vertices = [](const progpu_native_geometry_primitive& primitive,
        std::span<const vector_vertex> vertices) {
        path_join_bounds actual{91, 92, 93, 94};
        bool has_coverage = true;
        if (!try_get_path_join_bounds(primitive, actual, has_coverage) ||
            has_coverage != !vertices.empty()) return false;
        if (vertices.empty()) return actual.left == 0 && actual.top == 0 &&
            actual.right == 0 && actual.bottom == 0;
        path_join_bounds expected{std::numeric_limits<float>::infinity(),
            std::numeric_limits<float>::infinity(), -std::numeric_limits<float>::infinity(),
            -std::numeric_limits<float>::infinity()};
        // The payload carries each exact coverage triangle. position is the
        // expanded raster quad and must NOT become a source allocation bound.
        for (const auto& vertex : vertices) {
            for (const auto point : {progpu_native_point{vertex.color[0], vertex.color[1]},
                    progpu_native_point{vertex.color[2], vertex.color[3]},
                    progpu_native_point{vertex.shape_size[0], vertex.shape_size[1]}}) {
                expected.left = std::min(expected.left, point.x);
                expected.top = std::min(expected.top, point.y);
                expected.right = std::max(expected.right, point.x);
                expected.bottom = std::max(expected.bottom, point.y);
            }
        }
        return actual.left == expected.left && actual.top == expected.top &&
            actual.right == expected.right && actual.bottom == expected.bottom;
    };
    constexpr std::array<progpu_native_affine_2d, 2U> transforms{{
        {1, 0, 0, 1, 0, 0}, {2, 0, .5F, 1, 3, 5}}};
    for (const auto& transform : transforms) {
        for (std::uint32_t join : {0U, 1U, 2U}) {
            for (const bool reversal : {false, true}) {
                for (const bool wpf : {false, true}) {
                    for (const float limit : {1.0F, 1.4F, 1.5F}) {
                        progpu_native_geometry_primitive primitive{};
                        primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
                        primitive.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
                            (join << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
                            (wpf ? PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS : 0U);
                        primitive.p0 = {10, 10};
                        primitive.p1 = {1, 0};
                        primitive.p2 = reversal ? progpu_native_point{-1, 0} : progpu_native_point{0, 1};
                        primitive.p3.x = limit;
                        primitive.stroke_thickness = 2;
                        primitive.color = {1, 0, 0, 1};
                        primitive.transform = transform;
                        const std::size_t expected = reversal ? (wpf ? (join == 2U ? 8U : 3U) : 0U)
                            : join == 0U ? (limit < 1.5F ? (wpf ? 3U : 1U) : 2U)
                            : join == 1U ? 1U : 4U;
                        std::array<stroke_triangle, 8U> triangles{};
                        if (create_join_triangles(triangles, join, 2, limit, primitive.p0,
                                primitive.p1, primitive.p2, wpf) != expected) return false;
                        std::vector<vector_vertex> vertices;
                        std::vector<std::uint32_t> indices;
                        if (!is_valid_geometry_primitive(primitive) ||
                            !append_geometry_primitive(primitive, 7, vertices, indices) ||
                            vertices.size() != expected * 4U || indices.size() != expected * 6U) return false;
                        if (!matches_coverage_vertices(primitive, vertices)) return false;
                        for (std::size_t index = 0U; index < expected; ++index) {
                            const auto p0 = transformed_point(transform, triangles[index].p0);
                            const auto p1 = transformed_point(transform, triangles[index].p1);
                            const auto p2 = transformed_point(transform, triangles[index].p2);
                            const std::uint32_t exterior = expected == 1U ? 7U
                                : join == 0U && expected == 2U ? 3U
                                : 2U | (index == 0U ? 1U : 0U) | (index + 1U == expected ? 4U : 0U);
                            const std::uint32_t internal = index + 1U < expected ? 4U : 0U;
                            for (std::size_t corner = 0U; corner < 4U; ++corner) {
                                const auto& vertex = vertices[index * 4U + corner];
                                if (vertex.color[0] != p0.x || vertex.color[1] != p0.y ||
                                    vertex.color[2] != p1.x || vertex.color[3] != p1.y ||
                                    vertex.shape_size[0] != p2.x || vertex.shape_size[1] != p2.y ||
                                    vertex.corner_radius != static_cast<float>(exterior) ||
                                    vertex.stroke_thickness != static_cast<float>(internal) ||
                                    vertex.brush_index != 7 || vertex.shape_type != 1013) return false;
                            }
                        }
                    }
                }
            }
        }
    }

    progpu_native_geometry_primitive valid{};
    valid.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
    valid.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS;
    valid.p1 = {1, 0}; valid.p2 = {-1, 0}; valid.p3.x = 1;
    valid.stroke_thickness = 2; valid.color = {1, 0, 0, 1}; valid.transform = transforms[0];
    // The independent clipped-miter flag still does not select WPF reversal.
    auto clip_only = valid;
    clip_only.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT;
    std::vector<vector_vertex> empty_vertices;
    std::vector<std::uint32_t> empty_indices;
    if (!append_geometry_primitive(clip_only, 7, empty_vertices, empty_indices) ||
        !empty_vertices.empty() || !empty_indices.empty() ||
        !matches_coverage_vertices(clip_only, empty_vertices)) return false;

    // Independent square-overhang bounds for radius one at (10,10), including
    // both local-affine and world-uniform routes. No AA inflation is included.
    constexpr std::array<progpu_native_affine_2d, 3U> bound_transforms{{
        {1, 0, 0, 1, 0, 0}, {2, 0, .5F, 1, 3, 5}, {2, 0, 0, 2, 3, 5}}};
    constexpr std::array<path_join_bounds, 3U> literal_bounds{{
        {10, 9, 11, 11}, {27.5F, 14, 30.5F, 16}, {23, 23, 25, 27}}};
    for (std::size_t index = 0U; index < bound_transforms.size(); ++index) {
        for (const bool aliased : {false, true}) {
            auto reversal = valid;
            reversal.p0 = {10, 10}; reversal.transform = bound_transforms[index];
            if (aliased) reversal.flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
            path_join_bounds actual{91, 92, 93, 94}; bool has_coverage = false;
            const auto expected = literal_bounds[index];
            if (!try_get_path_join_bounds(reversal, actual, has_coverage) || !has_coverage ||
                actual.left != expected.left || actual.top != expected.top ||
                actual.right != expected.right || actual.bottom != expected.bottom) return false;
            std::vector<vector_vertex> vertices; std::vector<std::uint32_t> indices;
            if (!append_geometry_primitive(reversal, 7, vertices, indices) ||
                !matches_coverage_vertices(reversal, vertices)) return false;
        }
    }

    vector_vertex sentinel{};
    sentinel.position[0] = 91; sentinel.color[3] = 92;
    const std::vector<vector_vertex> before_vertices{sentinel};
    const std::vector<std::uint32_t> before_indices{93U};
    for (unsigned defect = 0U; defect < 8U; ++defect) {
        auto invalid = valid;
        switch (defect) {
        case 0U: invalid.flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE; break;
        case 1U: invalid.flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE; invalid.stroke_thickness = 0; break;
        case 2U: invalid.flags |= 3U << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT; break;
        case 3U: invalid.kind = PROGPU_NATIVE_GEOMETRY_LINE; break;
        case 4U: invalid.flags |= 1U << 31U; break;
        case 5U: invalid.flags |= 1U << PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT; break;
        case 6U: invalid.p3.y = 1; break;
        case 7U: invalid.reserved = 1; break;
        }
        auto vertices = before_vertices;
        auto indices = before_indices;
        for (const bool prior_coverage : {false, true}) {
            const path_join_bounds before{91, 92, 93, 94};
            auto bounds = before; bool has_coverage = prior_coverage;
            if (try_get_path_join_bounds(invalid, bounds, has_coverage) ||
                std::memcmp(&bounds, &before, sizeof(bounds)) != 0 || has_coverage != prior_coverage)
                return false;
        }
        std::size_t vertex_capacity = 94U, index_capacity = 95U;
        if (is_valid_geometry_primitive(invalid) ||
            geometry_primitive_capacity(invalid, vertex_capacity, index_capacity) ||
            vertex_capacity != 94U || index_capacity != 95U ||
            append_geometry_primitive(invalid, 7, vertices, indices) ||
            vertices.size() != before_vertices.size() || indices != before_indices ||
            std::memcmp(vertices.data(), before_vertices.data(), sizeof(vector_vertex)) != 0) return false;
    }
    return true;
}

inline std::array<progpu_native_path_segment, 4U> wpf_path_join_source_rectangle()
{
    constexpr std::array<progpu_native_point, 4U> points{{{10, 10}, {30, 10}, {30, 30}, {10, 30}}};
    std::array<progpu_native_path_segment, 4U> segments{};
    for (std::size_t index = 0U; index < segments.size(); ++index) {
        segments[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        segments[index].p0 = points[index];
        segments[index].p1 = points[(index + 1U) % points.size()];
    }
    return segments;
}

inline bool wpf_path_join_semantic_atomic()
{
    constexpr std::array<double, 2U> dashes{1000.0, 1.0};
    for (std::uint32_t join : {0U, 1U, 2U}) {
        for (const bool dashed : {false, true}) {
            auto segments = wpf_path_join_source_rectangle();
            const auto source_before = segments;
            std::array<std::uint8_t, 4U> smooth{};
            semantic_path_stroke::style style{};
            style.transform = {1, 0, 0, 1, 0, 0};
            style.thickness = 2; style.miter_limit = 1; style.line_join = join;
            style.primitive_flags = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
            style.wpf_join_semantics = true;
            mil::curve_dash::run_buffer scratch;
            std::vector<progpu_native_geometry_primitive> primitives;
            std::vector<std::uint32_t> brushes;
            const std::span<const double> pattern = dashed ? std::span<const double>(dashes)
                : std::span<const double>{};
            if (semantic_path_stroke::compile(segments, smooth, true, pattern, style, 37U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::success ||
                primitives.size() != 8U || brushes.size() != 8U ||
                std::memcmp(segments.data(), source_before.data(), sizeof(segments)) != 0 ||
                smooth != std::array<std::uint8_t, 4U>{}) return false;
            std::size_t joins = 0U;
            for (std::size_t index = 0U; index < primitives.size(); ++index) {
                const auto& primitive = primitives[index];
                const bool is_join = primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
                if (!is_valid_geometry_primitive(primitive) || brushes[index] != 37U ||
                    ((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS) != 0U) != is_join ||
                    (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) == 0U ||
                    (primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT) != 0U) return false;
                if (is_join) {
                    ++joins;
                    if (((primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) != join || primitive.p3.x != 1) return false;
                } else if (primitive.kind != PROGPU_NATIVE_GEOMETRY_LINE) return false;
            }
            if (joins != 4U) return false;
            const auto retained = primitives;
            const auto retained_brushes = brushes;
            const auto unchanged = [&] {
                return primitives.size() == retained.size() && brushes == retained_brushes &&
                    std::memcmp(primitives.data(), retained.data(), retained.size() * sizeof(primitives[0])) == 0;
            };
            for (unsigned defect = 0U; defect < 7U; ++defect) {
                auto invalid = style;
                switch (defect) {
                case 0U: invalid.primitive_flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE; break;
                case 1U: invalid.primitive_flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE; invalid.thickness = 0; break;
                case 2U: invalid.line_join = 3U; break;
                case 3U: invalid.primitive_flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS; break;
                case 4U: invalid.primitive_flags |= 1U << 31U; break;
                case 5U: invalid.start_cap = 4U; break;
                case 6U: invalid.thickness = std::numeric_limits<float>::quiet_NaN(); break;
                }
                if (semantic_path_stroke::compile(segments, smooth, true, pattern, invalid, 41U,
                        scratch, primitives, brushes) != semantic_path_stroke::result::invalid || !unchanged()) return false;
            }
            // A late bad segment must roll back any preceding body/join append.
            auto malformed = segments;
            malformed.back().kind = std::numeric_limits<std::uint32_t>::max();
            if (semantic_path_stroke::compile(malformed, smooth, true, {}, style, 41U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::invalid || !unchanged()) return false;

            // Policy belongs to this source generation, not a global adapter
            // default or borrowed style. A later generic compile leaves it intact.
            style.wpf_join_semantics = false;
            if (semantic_path_stroke::compile(segments, smooth, true, pattern, style, 41U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::success ||
                primitives.size() != 16U || brushes.size() != 16U ||
                std::memcmp(primitives.data(), retained.data(), retained.size() * sizeof(primitives[0])) != 0) return false;
            for (std::size_t index = 0U; index < primitives.size(); ++index) {
                if (brushes[index] != (index < retained.size() ? 37U : 41U) ||
                    (index >= retained.size() &&
                        (primitives[index].flags & PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS) != 0U)) return false;
            }
            const auto generations = primitives;
            segments = {}; smooth.fill(1U); style.line_join = 3U;
            if (std::memcmp(primitives.data(), generations.data(),
                    primitives.size() * sizeof(primitives[0])) != 0) return false;
        }
    }

    // Original WPF's smooth marker selects Round; it does not suppress the
    // join. At an exact reversal, only the selected WPF policy owns the
    // semicircle (or the nonsmooth Miter square) beyond the two line bodies.
    std::array<progpu_native_path_segment, 2U> reversal{};
    reversal[0].kind = reversal[1].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    reversal[0].p0 = {0, 0}; reversal[0].p1 = {10, 0};
    reversal[1].p0 = {10, 0}; reversal[1].p1 = {0, 0};
    for (const bool wpf : {false, true}) {
        for (const bool smooth_corner : {false, true}) {
            const std::array<std::uint8_t, 2U> smooth{static_cast<std::uint8_t>(smooth_corner), 0U};
            semantic_path_stroke::style style{};
            style.transform = {1, 0, 0, 1, 0, 0};
            style.thickness = 2; style.miter_limit = 1;
            style.wpf_join_semantics = wpf;
            mil::curve_dash::run_buffer scratch;
            std::vector<progpu_native_geometry_primitive> primitives;
            std::vector<std::uint32_t> brushes;
            if (semantic_path_stroke::compile(reversal, smooth, false, {}, style, 43U,
                    scratch, primitives, brushes) != semantic_path_stroke::result::success ||
                primitives.size() != 3U || brushes != std::vector<std::uint32_t>(3U, 43U)) return false;
            const auto& corner = primitives[1U];
            const std::uint32_t effective_join = smooth_corner ? PROGPU_NATIVE_STROKE_JOIN_ROUND
                : PROGPU_NATIVE_STROKE_JOIN_MITER;
            if (corner.kind != PROGPU_NATIVE_GEOMETRY_PATH_JOIN ||
                ((corner.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >>
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) != effective_join ||
                ((corner.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_WPF_JOIN_SEMANTICS) != 0U) != wpf) return false;
            std::vector<vector_vertex> vertices;
            std::vector<std::uint32_t> indices;
            const std::size_t triangles = wpf ? (smooth_corner ? 8U : 3U) : 0U;
            if (!append_geometry_primitive(corner, 43, vertices, indices) ||
                vertices.size() != triangles * 4U || indices.size() != triangles * 6U) return false;
        }
    }
    return true;
}

} // namespace progpu::native::tests
