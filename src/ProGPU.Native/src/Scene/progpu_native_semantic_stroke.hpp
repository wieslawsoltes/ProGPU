#pragma once

#include "progpu_native_geometry_spline.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>

namespace progpu::native {

inline constexpr std::uint32_t semantic_stroke_base_flags =
    PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED |
    PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
    PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE |
    PROGPU_NATIVE_POLYLINE_FLAG_CLOSED;

inline bool semantic_stroke_resource_layout(
    const progpu_native_scene_stroke* strokes,
    std::size_t stroke_count,
    std::size_t auxiliary_size,
    std::size_t& point_count,
    std::size_t& double_count) noexcept {
    point_count = 0U;
    double_count = 0U;
    if (strokes == nullptr || stroke_count == 0U) {
        return false;
    }
    std::uint64_t expected_points = 0U;
    std::uint64_t expected_doubles = 0U;
    const auto try_add = [](std::uint64_t& total,
                            std::uint64_t value) noexcept {
        if (value > std::numeric_limits<std::uint64_t>::max() - total) {
            return false;
        }
        total += value;
        return true;
    };
    for (std::size_t index = 0U; index < stroke_count; ++index) {
        const auto& stroke = strokes[index];
        if (stroke.struct_size != sizeof(stroke) ||
            stroke.kind > PROGPU_NATIVE_SCENE_STROKE_SPLINE ||
            (stroke.flags & ~semantic_stroke_base_flags) != 0U ||
            stroke.point_offset != expected_points ||
            stroke.point_count < 2U ||
            stroke.start_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
            stroke.end_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
            stroke.dash_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
            stroke.line_join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
            !is_finite(stroke.color) || !is_finite(stroke.transform) ||
            !std::isfinite(stroke.stroke_thickness) ||
            !std::isfinite(stroke.miter_limit) ||
            stroke.miter_limit < 1.0F ||
            !std::isfinite(stroke.dash_offset) ||
            stroke.reserved[0] != 0U || stroke.reserved[1] != 0U ||
            !try_add(expected_points, stroke.point_count)) {
            return false;
        }
        if (stroke.kind == PROGPU_NATIVE_SCENE_STROKE_SPLINE) {
            if (stroke.knot_offset != expected_doubles ||
                stroke.knot_count == 0U ||
                stroke.degree > (1U << 20U) ||
                !try_add(expected_doubles, stroke.knot_count) ||
                stroke.weight_offset != expected_doubles ||
                (stroke.weight_count != 0U &&
                    stroke.weight_count != stroke.point_count) ||
                !try_add(expected_doubles, stroke.weight_count)) {
                return false;
            }
        } else if (stroke.degree != 0U || stroke.knot_offset != 0U ||
            stroke.knot_count != 0U || stroke.weight_offset != 0U ||
            stroke.weight_count != 0U) {
            return false;
        }
        if (stroke.dash_interval_offset != expected_doubles ||
            !try_add(expected_doubles, stroke.dash_interval_count)) {
            return false;
        }
    }
    if (expected_points > std::numeric_limits<std::uint32_t>::max() ||
        expected_doubles > std::numeric_limits<std::uint32_t>::max()) {
        return false;
    }
    const std::uint64_t expected_size =
        expected_points * sizeof(progpu_native_point) +
        expected_doubles * sizeof(double);
    if (expected_size != auxiliary_size) {
        return false;
    }
    point_count = static_cast<std::size_t>(expected_points);
    double_count = static_cast<std::size_t>(expected_doubles);
    return true;
}

inline progpu_native_polyline make_semantic_polyline(
    const progpu_native_scene_stroke& stroke) noexcept {
    const std::uint32_t flags = stroke.flags |
        (stroke.start_cap << PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT) |
        (stroke.end_cap << PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT) |
        (stroke.line_join << PROGPU_NATIVE_POLYLINE_JOIN_SHIFT);
    return {
        0U,
        static_cast<std::size_t>(stroke.point_count),
        stroke.color,
        stroke.transform,
        stroke.stroke_thickness,
        stroke.miter_limit,
        flags,
        stroke.dash_interval_count == 0U ? 0U : 1U};
}

inline progpu_native_dash_style make_semantic_dash_style(
    const progpu_native_scene_stroke& stroke) noexcept {
    return {
        static_cast<std::size_t>(stroke.dash_interval_offset),
        static_cast<std::size_t>(stroke.dash_interval_count),
        stroke.dash_offset,
        stroke.dash_cap,
        0U};
}

inline bool semantic_stroke_is_collapsed(
    const progpu_native_scene_stroke& stroke) noexcept {
    float maximum_scale = 0.0F;
    float minimum_scale = 0.0F;
    return is_finite(stroke.transform) &&
        !try_get_stroke_scales(
            stroke.transform,
            maximum_scale,
            minimum_scale);
}

inline bool semantic_stroke_capacity(
    const progpu_native_scene_stroke& stroke,
    const progpu_native_point* points,
    const double* doubles,
    std::size_t double_count,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::size_t& vertex_count,
    std::size_t& index_count) {
    if (semantic_stroke_is_collapsed(stroke)) {
        vertex_count = 0U;
        index_count = 0U;
        return true;
    }
    auto polyline = make_semantic_polyline(stroke);
    const auto dash = make_semantic_dash_style(stroke);
    const auto* dash_styles =
        stroke.dash_interval_count == 0U ? nullptr : &dash;
    const std::size_t dash_count = dash_styles == nullptr ? 0U : 1U;
    if (stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE) {
        return polyline_capacity(
            polyline,
            points,
            dash_styles,
            dash_count,
            doubles,
            double_count,
            vertex_count,
            index_count);
    }
    const progpu_native_spline spline{
        polyline,
        0U,
        static_cast<std::size_t>(stroke.knot_count),
        0U,
        static_cast<std::size_t>(stroke.weight_count),
        stroke.degree,
        0U};
    std::size_t segment_count = 0U;
    return spline_capacity(
        spline,
        points,
        doubles + stroke.knot_offset,
        stroke.weight_count == 0U
            ? nullptr
            : doubles + stroke.weight_offset,
        dash_styles,
        dash_count,
        doubles,
        double_count,
        segment_count,
        sampled_points,
        work,
        vertex_count,
        index_count);
}

inline bool append_semantic_stroke(
    const progpu_native_scene_stroke& stroke,
    const progpu_native_point* points,
    const double* doubles,
    std::size_t double_count,
    float brush_index,
    std::array<progpu_native_point, 101U>& sampled_points,
    std::vector<spline_homogeneous_point>& work,
    std::vector<vector_vertex>& vertices,
    std::vector<std::uint32_t>& indices) {
    if (semantic_stroke_is_collapsed(stroke)) {
        return true;
    }
    auto polyline = make_semantic_polyline(stroke);
    const auto dash = make_semantic_dash_style(stroke);
    const auto* dash_styles =
        stroke.dash_interval_count == 0U ? nullptr : &dash;
    const std::size_t dash_count = dash_styles == nullptr ? 0U : 1U;
    if (stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE) {
        return append_polyline(
            polyline,
            points,
            brush_index,
            vertices,
            indices,
            dash_styles,
            dash_count,
            doubles,
            double_count);
    }
    const progpu_native_spline spline{
        polyline,
        0U,
        static_cast<std::size_t>(stroke.knot_count),
        0U,
        static_cast<std::size_t>(stroke.weight_count),
        stroke.degree,
        0U};
    std::size_t segment_count = 0U;
    std::size_t vertex_count = 0U;
    std::size_t index_count = 0U;
    if (!spline_capacity(
            spline,
            points,
            doubles + stroke.knot_offset,
            stroke.weight_count == 0U
                ? nullptr
                : doubles + stroke.weight_offset,
            dash_styles,
            dash_count,
            doubles,
            double_count,
            segment_count,
            sampled_points,
            work,
            vertex_count,
            index_count)) {
        return false;
    }
    (void)vertex_count;
    (void)index_count;
    return append_spline(
        spline,
        points,
        doubles + stroke.knot_offset,
        stroke.weight_count == 0U
            ? nullptr
            : doubles + stroke.weight_offset,
        segment_count,
        brush_index,
        sampled_points,
        work,
        vertices,
        indices,
        dash_styles,
        dash_count,
        doubles,
        double_count);
}

} // namespace progpu::native
