#pragma once

#include "progpu_native.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <numbers>
#include <span>
#include <vector>

namespace progpu::native {

// Algorithm: Encode base-level image reconstruction for canonical Texture.wgsl;
// explicit Fant replaces bilinear taps without changing its bounded footprint.
// Time complexity: O(1). Space complexity: O(1); no resources or allocations.
inline constexpr float base_image_sampling_coefficient(
    std::uint64_t engine_flags, std::uint32_t sampling) noexcept {
    const bool explicit_sampling =
        (engine_flags & PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING) != 0U;
    if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT)
        return explicit_sampling ? -256.0F : -32.0F;
    if (!explicit_sampling) return 0.0F;
    if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST) return -128.0F;
    if (sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR) return -64.0F;
    return 0.0F;
}

struct vector_vertex {
    float position[2];
    float color[4];
    float texture_coordinate[2];
    float brush_index;
    float shape_size[2];
    float corner_radius;
    float stroke_thickness;
    float shape_type;
};

static_assert(sizeof(vector_vertex) == 56U);
static_assert(offsetof(vector_vertex, color) == 8U);
static_assert(offsetof(vector_vertex, texture_coordinate) == 24U);
static_assert(offsetof(vector_vertex, brush_index) == 32U);
static_assert(offsetof(vector_vertex, shape_size) == 36U);
static_assert(offsetof(vector_vertex, shape_type) == 52U);
static_assert(sizeof(progpu_native_affine_2d) == 24U);
static_assert(sizeof(progpu_native_image_rect) == 16U);
static_assert(sizeof(progpu_native_scene_state) == 64U);
static_assert(offsetof(progpu_native_scene_state, transform) == 8U);
static_assert(offsetof(progpu_native_scene_state, opacity) == 32U);
static_assert(offsetof(progpu_native_scene_state, clip_rect) == 40U);
static_assert(sizeof(progpu_native_scene_layer_mask) == 104U);
static_assert(sizeof(progpu_native_scene_layer_mask_chain) == 432U);
static_assert(sizeof(progpu_native_scene_layer_brush_mask) == 320U);
static_assert(offsetof(progpu_native_scene_layer_mask, bounds) == 16U);
static_assert(offsetof(progpu_native_scene_layer_mask, transform) == 32U);
static_assert(offsetof(progpu_native_scene_layer_mask, opacity) == 88U);
static_assert(offsetof(progpu_native_scene_layer_brush_mask, bounds) == 16U);
static_assert(offsetof(progpu_native_scene_layer_brush_mask, transform) == 32U);
static_assert(offsetof(progpu_native_scene_layer_brush_mask, brush) == 64U);
static_assert(sizeof(progpu_native_scene_effect_chain) == 16U);
static_assert(sizeof(progpu_native_group_effect) == 56U);
static_assert(sizeof(progpu_native_analytic_primitive) == 72U);
static_assert(sizeof(progpu_native_point) == 8U);
static_assert(sizeof(progpu_native_geometry_primitive) == 88U);
static_assert(sizeof(progpu_native_scene_point_batch) == 64U);
static_assert(offsetof(progpu_native_scene_point_batch, color) == 24U);
static_assert(offsetof(progpu_native_scene_point_batch, transform) == 40U);
static_assert(sizeof(progpu_native_scene_vertex_mesh) == 64U);
static_assert(offsetof(progpu_native_scene_vertex_mesh, transform) == 32U);
static_assert(sizeof(progpu_native_scene_mesh_vertex) == 32U);
static_assert(offsetof(progpu_native_scene_mesh_vertex, color) == 16U);
static_assert(sizeof(progpu_native_scene_stroke) == 160U);
static_assert(offsetof(progpu_native_scene_stroke, color) == 80U);
static_assert(offsetof(progpu_native_scene_stroke, transform) == 96U);
static_assert(offsetof(progpu_native_scene_stroke, dash_offset) == 128U);
static_assert(sizeof(progpu_native_polyline) ==
    (sizeof(std::size_t) == 8U ? 72U : 64U));
static_assert(sizeof(progpu_native_dash_style) ==
    (sizeof(std::size_t) == 8U ? 32U : 24U));
static_assert(sizeof(progpu_native_spline) ==
    (sizeof(std::size_t) == 8U ? 112U : 88U));
static_assert(sizeof(progpu_native_path_segment) == 48U);
static_assert(sizeof(progpu_native_path_fill) ==
    (sizeof(std::size_t) == 8U ? 96U : 80U));
static_assert(sizeof(progpu_native_geometry_frame) ==
    (sizeof(std::size_t) == 8U ? 152U : 96U));
static_assert(offsetof(progpu_native_geometry_frame, reserved) ==
    (sizeof(std::size_t) == 8U ? 60U : 48U));
static_assert(sizeof(progpu_native_path_frame) ==
    (sizeof(std::size_t) == 8U ? 104U : 72U));
static_assert(sizeof(progpu_native_image_frame) ==
    (sizeof(std::size_t) == 8U ? 224U : 200U));
static_assert(sizeof(progpu_native_image_frame_metrics) == 72U);
static_assert(sizeof(progpu_native_group_mask) ==
    (sizeof(std::uintptr_t) == 8U ? 152U : 144U));
static_assert(sizeof(progpu_native_clip_path) ==
    (sizeof(std::size_t) == 8U ? 88U : 72U));
static_assert(sizeof(progpu_native_clip_chain) ==
    (sizeof(std::uintptr_t) == 8U ? 56U : 32U));
static_assert(sizeof(progpu_native_path_boolean_node) ==
    (sizeof(std::size_t) == 8U ? 48U : 40U));
static_assert(sizeof(progpu_native_scene_path_boolean_node) == 48U);
static_assert(sizeof(progpu_native_group_effect) == 56U);
static_assert(sizeof(progpu_native_group_effect_chain) ==
    (sizeof(std::uintptr_t) == 8U ? 24U : 20U));
static_assert(offsetof(progpu_native_draw_state, group_mask) == 40U);
static_assert(offsetof(progpu_native_draw_state, group_effect) ==
    (sizeof(std::uintptr_t) == 8U ? 48U : 44U));
static_assert(offsetof(progpu_native_draw_state, group_effect_chain) ==
    (sizeof(std::uintptr_t) == 8U ? 56U : 48U));
static_assert(sizeof(progpu_native_draw_state) ==
    (sizeof(std::uintptr_t) == 8U ? 72U : 60U));
static_assert(sizeof(progpu_native_layer_metrics) == 200U);

inline bool is_finite(const progpu_native_color& color) noexcept {
    return std::isfinite(color.r) &&
        std::isfinite(color.g) &&
        std::isfinite(color.b) &&
        std::isfinite(color.a);
}

inline bool is_finite(const progpu_native_affine_2d& transform) noexcept {
    return std::isfinite(transform.m11) &&
        std::isfinite(transform.m12) &&
        std::isfinite(transform.m21) &&
        std::isfinite(transform.m22) &&
        std::isfinite(transform.m31) &&
        std::isfinite(transform.m32);
}

inline bool is_finite(const progpu_native_point& point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

inline bool is_finite(const progpu_native_rect& rect) noexcept {
    return std::isfinite(rect.x) &&
        std::isfinite(rect.y) &&
        std::isfinite(rect.width) &&
        std::isfinite(rect.height) &&
        is_finite(rect.color);
}

inline void transform_point(
    const progpu_native_affine_2d& transform,
    float x,
    float y,
    float& result_x,
    float& result_y) noexcept {
    result_x = x * transform.m11 + y * transform.m21 + transform.m31;
    result_y = x * transform.m12 + y * transform.m22 + transform.m32;
}

// Canonical Texture.wgsl occupied-tile-page encoding. O(1), four vertices,
// no GPU ownership transfer or allocation. The caller leases the premultiplied
// page through submission and explicitly selects this shader-sampling path.
inline bool try_write_tile_page_quad(
    std::span<vector_vertex> destination,
    const progpu_native_image_rect& output,
    const progpu_native_affine_2d& output_to_tile,
    std::uint32_t tile_width, std::uint32_t tile_height,
    std::uint32_t texture_width, std::uint32_t texture_height,
    std::uint32_t address_u, std::uint32_t address_v,
    std::uint32_t sampling, float opacity) noexcept {
    constexpr std::uint32_t maximum_exact_extent = 1U << 24U;
    if (destination.size() < 4U || tile_width == 0U || tile_height == 0U ||
        tile_width > texture_width || tile_height > texture_height ||
        tile_width > maximum_exact_extent || tile_height > maximum_exact_extent ||
        address_u > PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT ||
        address_v > PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT ||
        (sampling > PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR && sampling != PROGPU_NATIVE_IMAGE_SAMPLING_FANT) ||
        !std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F ||
        !is_finite(output_to_tile) || !std::isfinite(output.x) ||
        !std::isfinite(output.y) || !std::isfinite(output.width) ||
        !std::isfinite(output.height) || output.width <= 0.0F || output.height <= 0.0F) {
        return false;
    }
    std::array<vector_vertex, 4U> vertices{};
    constexpr std::array<std::array<float, 2U>, 4U> corners{{
        {0.0F, 0.0F}, {1.0F, 0.0F}, {1.0F, 1.0F}, {0.0F, 1.0F}}};
    for (std::size_t index = 0U; index < vertices.size(); ++index) {
        auto& vertex = vertices[index];
        vertex.position[0] = output.x + corners[index][0] * output.width;
        vertex.position[1] = output.y + corners[index][1] * output.height;
        transform_point(output_to_tile, vertex.position[0], vertex.position[1],
            vertex.texture_coordinate[0], vertex.texture_coordinate[1]);
        if (!std::isfinite(vertex.position[0]) || !std::isfinite(vertex.position[1]) ||
            !std::isfinite(vertex.texture_coordinate[0]) || !std::isfinite(vertex.texture_coordinate[1])) {
            return false;
        }
        vertex.color[0] = static_cast<float>(tile_width);
        vertex.color[1] = static_cast<float>(tile_height);
        vertex.color[3] = opacity;
        vertex.brush_index = -2.0F;
        vertex.shape_size[0] = sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ? -128.0F :
            sampling == PROGPU_NATIVE_IMAGE_SAMPLING_FANT ? -32.0F : -64.0F;
        vertex.corner_radius = static_cast<float>(address_u);
        vertex.stroke_thickness = static_cast<float>(address_v);
    }
    std::copy(vertices.begin(), vertices.end(), destination.begin());
    return true;
}

inline void transform_vector(
    const progpu_native_affine_2d& transform,
    float x,
    float y,
    float& result_x,
    float& result_y) noexcept {
    result_x = x * transform.m11 + y * transform.m21;
    result_y = x * transform.m12 + y * transform.m22;
}

inline progpu_native_affine_2d compose_affine(
    const progpu_native_affine_2d& first,
    const progpu_native_affine_2d& second) noexcept {
    return {
        first.m11 * second.m11 + first.m12 * second.m21,
        first.m11 * second.m12 + first.m12 * second.m22,
        first.m21 * second.m11 + first.m22 * second.m21,
        first.m21 * second.m12 + first.m22 * second.m22,
        first.m31 * second.m11 + first.m32 * second.m21 + second.m31,
        first.m31 * second.m12 + first.m32 * second.m22 + second.m32};
}

inline bool try_get_stroke_scales(
    const progpu_native_affine_2d& transform,
    float& maximum_scale,
    float& minimum_scale) noexcept {
    if (!is_finite(transform)) {
        return false;
    }
    const double m11 = transform.m11;
    const double m12 = transform.m12;
    const double m21 = transform.m21;
    const double m22 = transform.m22;
    const double sum = m11 * m11 + m12 * m12 + m21 * m21 + m22 * m22;
    const double determinant = m11 * m22 - m12 * m21;
    const double discriminant = std::max(
        0.0,
        sum * sum - 4.0 * determinant * determinant);
    const double maximum_squared = std::max(
        0.0,
        (sum + std::sqrt(discriminant)) * 0.5);
    const double minimum_squared = std::max(
        0.0,
        (sum - std::sqrt(discriminant)) * 0.5);
    const double maximum_value = std::sqrt(maximum_squared);
    const double minimum_value = std::sqrt(minimum_squared);
    if (!std::isfinite(maximum_value) || maximum_value <= 0.000001 ||
        !std::isfinite(minimum_value) || minimum_value <= 0.000001) {
        return false;
    }
    maximum_scale = static_cast<float>(maximum_value);
    minimum_scale = static_cast<float>(minimum_value);
    return std::isfinite(maximum_scale) && maximum_scale > 0.000001F &&
        std::isfinite(minimum_scale) && minimum_scale > 0.000001F;
}

inline bool try_get_minimum_scale(
    const progpu_native_affine_2d& transform,
    float& minimum_scale) noexcept {
    float maximum_scale = 0.0F;
    return try_get_stroke_scales(
        transform,
        maximum_scale,
        minimum_scale);
}

inline bool requires_affine_stroke_geometry(
    const progpu_native_affine_2d& transform) noexcept {
    const float length_x = std::hypot(transform.m11, transform.m12);
    const float length_y = std::hypot(transform.m21, transform.m22);
    if (!std::isfinite(length_x) || !std::isfinite(length_y) ||
        length_x <= 0.000001F || length_y <= 0.000001F) {
        return false;
    }
    const float scale = std::max(length_x, length_y);
    const float dot = transform.m11 * transform.m21 +
        transform.m12 * transform.m22;
    return std::abs(length_x - length_y) > scale * 0.0001F ||
        std::abs(dot) > length_x * length_y * 0.0001F;
}

inline bool geometry_uses_payload_brush(
    const progpu_native_geometry_primitive& primitive) noexcept {
    if (primitive.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_ARC ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_CAP ||
        primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
        return true;
    }
    return primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE &&
        ((primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK |
            PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK)) != 0U ||
        ((primitive.flags & (
            PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)) == 0U &&
        requires_affine_stroke_geometry(primitive.transform)));
}

inline void set_color(
    vector_vertex& vertex,
    const progpu_native_color& color) noexcept {
    vertex.color[0] = color.r;
    vertex.color[1] = color.g;
    vertex.color[2] = color.b;
    vertex.color[3] = color.a;
}

constexpr std::size_t direct_curve_segment_count = 24U;
constexpr std::size_t minimum_affine_curve_segment_count = 24U;
constexpr std::size_t maximum_affine_curve_segment_count = 1024U;
constexpr double affine_curve_max_device_error = 0.25;

inline progpu_native_point transformed_point(
    const progpu_native_affine_2d& transform,
    const progpu_native_point& point) noexcept {
    progpu_native_point result{};
    transform_point(transform, point.x, point.y, result.x, result.y);
    return result;
}

inline progpu_native_point transformed_direction(
    const progpu_native_affine_2d& transform,
    const progpu_native_point& direction) noexcept {
    return {
        direction.x * transform.m11 + direction.y * transform.m21,
        direction.x * transform.m12 + direction.y * transform.m22
    };
}

inline progpu_native_point evaluate_quadratic(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        inverse * inverse * p0.x +
            2.0F * inverse * t * p1.x + t * t * p2.x,
        inverse * inverse * p0.y +
            2.0F * inverse * t * p1.y + t * t * p2.y
    };
}

inline progpu_native_point evaluate_cubic(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    const progpu_native_point& p3,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        inverse * inverse * inverse * p0.x +
            3.0F * inverse * inverse * t * p1.x +
            3.0F * inverse * t * t * p2.x + t * t * t * p3.x,
        inverse * inverse * inverse * p0.y +
            3.0F * inverse * inverse * t * p1.y +
            3.0F * inverse * t * t * p2.y + t * t * t * p3.y
    };
}

inline progpu_native_point quadratic_tangent(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    float t) noexcept {
    return {
        2.0F * ((1.0F - t) * (p1.x - p0.x) + t * (p2.x - p1.x)),
        2.0F * ((1.0F - t) * (p1.y - p0.y) + t * (p2.y - p1.y))
    };
}

inline progpu_native_point cubic_tangent(
    const progpu_native_point& p0,
    const progpu_native_point& p1,
    const progpu_native_point& p2,
    const progpu_native_point& p3,
    float t) noexcept {
    const float inverse = 1.0F - t;
    return {
        3.0F * (inverse * inverse * (p1.x - p0.x) +
            2.0F * inverse * t * (p2.x - p1.x) +
            t * t * (p3.x - p2.x)),
        3.0F * (inverse * inverse * (p1.y - p0.y) +
            2.0F * inverse * t * (p2.y - p1.y) +
            t * t * (p3.y - p2.y))
    };
}

inline std::size_t resolve_affine_curve_segment_count(
    double squared_count) noexcept {
    if (!std::isfinite(squared_count) ||
        squared_count >=
            static_cast<double>(maximum_affine_curve_segment_count) *
                maximum_affine_curve_segment_count) {
        return maximum_affine_curve_segment_count;
    }
    if (squared_count <=
        static_cast<double>(minimum_affine_curve_segment_count) *
            minimum_affine_curve_segment_count) {
        return minimum_affine_curve_segment_count;
    }
    return static_cast<std::size_t>(std::ceil(std::sqrt(squared_count)));
}

inline std::size_t affine_quadratic_segment_count(
    const progpu_native_geometry_primitive& primitive) noexcept {
    const auto p0 = transformed_point(primitive.transform, primitive.p0);
    const auto p1 = transformed_point(primitive.transform, primitive.p1);
    const auto p2 = transformed_point(primitive.transform, primitive.p2);
    const double x = static_cast<double>(p0.x) - 2.0 * p1.x + p2.x;
    const double y = static_cast<double>(p0.y) - 2.0 * p1.y + p2.y;
    return resolve_affine_curve_segment_count(
        std::hypot(x, y) / (4.0 * affine_curve_max_device_error));
}

inline std::size_t affine_cubic_segment_count(
    const progpu_native_geometry_primitive& primitive) noexcept {
    const auto p0 = transformed_point(primitive.transform, primitive.p0);
    const auto p1 = transformed_point(primitive.transform, primitive.p1);
    const auto p2 = transformed_point(primitive.transform, primitive.p2);
    const auto p3 = transformed_point(primitive.transform, primitive.p3);
    const double x0 = static_cast<double>(p0.x) - 2.0 * p1.x + p2.x;
    const double y0 = static_cast<double>(p0.y) - 2.0 * p1.y + p2.y;
    const double x1 = static_cast<double>(p1.x) - 2.0 * p2.x + p3.x;
    const double y1 = static_cast<double>(p1.y) - 2.0 * p2.y + p3.y;
    return resolve_affine_curve_segment_count(
        0.75 * std::max(std::hypot(x0, y0), std::hypot(x1, y1)) /
            affine_curve_max_device_error);
}

inline bool try_normalize(
    progpu_native_point direction,
    const progpu_native_point& fallback,
    progpu_native_point& normalized) noexcept {
    float length = std::hypot(direction.x, direction.y);
    if (!std::isfinite(length) || length <= 0.0001F) {
        direction = fallback;
        length = std::hypot(direction.x, direction.y);
    }
    if (!std::isfinite(length) || length <= 0.0001F) {
        normalized = {};
        return false;
    }
    normalized = {direction.x / length, direction.y / length};
    return true;
}

inline bool try_select_direction(
    const progpu_native_point& first,
    const progpu_native_point& second,
    const progpu_native_point& third,
    progpu_native_point& direction) noexcept {
    const progpu_native_point candidates[3] = {first, second, third};
    for (const auto& candidate : candidates) {
        const float length = std::hypot(candidate.x, candidate.y);
        if (std::isfinite(length) && length > 0.0001F) {
            direction = candidate;
            return true;
        }
    }
    direction = {};
    return false;
}

} // namespace progpu::native
