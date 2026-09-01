#include "progpu_native_direct2d_core.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace progpu::native::direct2d::core {
namespace {

[[nodiscard]] bool finite_point(
    const progpu_native_direct2d_point_2f& point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

[[nodiscard]] bool finite_transform(
    const progpu_native_direct2d_matrix_3x2_f* transform) noexcept
{
    return transform == nullptr ||
        (std::isfinite(transform->m11) &&
            std::isfinite(transform->m12) &&
            std::isfinite(transform->m21) &&
            std::isfinite(transform->m22) &&
            std::isfinite(transform->m31) &&
            std::isfinite(transform->m32));
}

[[nodiscard]] bool valid_tolerance(float value) noexcept
{
    return std::isfinite(value) && value > 0.0F;
}

[[nodiscard]] double twice_area(
    const std::array<progpu_native_direct2d_point_2f, 4U>& points) noexcept
{
    double result = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        result += static_cast<double>(start.x) * end.y -
            static_cast<double>(start.y) * end.x;
    }
    return result;
}

} // namespace

rectangle_geometry::rectangle_geometry(
    rectangle_edges_f rectangle) noexcept
    : rectangle_(rectangle)
{
}

bool rectangle_geometry::valid_rectangle(
    const rectangle_edges_f& rectangle) noexcept
{
    return std::isfinite(rectangle.left) &&
        std::isfinite(rectangle.top) && std::isfinite(rectangle.right) &&
        std::isfinite(rectangle.bottom) &&
        rectangle.right >= rectangle.left &&
        rectangle.bottom >= rectangle.top;
}

com::result rectangle_geometry::vertices(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    std::array<progpu_native_direct2d_point_2f, 4U>& value) const noexcept
{
    value = {};
    if (!valid_rectangle(rectangle_) ||
        !finite_transform(world_transform)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& matrix = world_transform == nullptr
        ? identity
        : *world_transform;
    const std::array<progpu_native_direct2d_point_2f, 4U> source{{
        {rectangle_.left, rectangle_.top},
        {rectangle_.right, rectangle_.top},
        {rectangle_.right, rectangle_.bottom},
        {rectangle_.left, rectangle_.bottom}}};
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    for (std::size_t index = 0U; index < source.size(); ++index) {
        const double x = static_cast<double>(source[index].x) * matrix.m11 +
            static_cast<double>(source[index].y) * matrix.m21 + matrix.m31;
        const double y = static_cast<double>(source[index].x) * matrix.m12 +
            static_cast<double>(source[index].y) * matrix.m22 + matrix.m32;
        if (!std::isfinite(x) || !std::isfinite(y) ||
            std::abs(x) > maximum || std::abs(y) > maximum) {
            value = {};
            return com::invalid_argument;
        }
        value[index] = {
            static_cast<float>(x), static_cast<float>(y)};
    }
    return com::ok;
}

com::result rectangle_geometry::bounds(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    rectangle_edges_f* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = {};
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    float left = points[0U].x;
    float top = points[0U].y;
    float right = left;
    float bottom = top;
    for (std::size_t index = 1U; index < points.size(); ++index) {
        left = std::min(left, points[index].x);
        top = std::min(top, points[index].y);
        right = std::max(right, points[index].x);
        bottom = std::max(bottom, points[index].y);
    }
    *value = {left, top, right, bottom};
    return com::ok;
}

com::result rectangle_geometry::fill_contains_point(
    progpu_native_direct2d_point_2f point,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::uint32_t* contains) const noexcept
{
    if (contains == nullptr) {
        return com::pointer_error;
    }
    *contains = 0U;
    if (!finite_point(point) || !valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    if (twice_area(points) == 0.0) {
        return com::ok;
    }
    bool positive = false;
    bool negative = false;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        const double cross =
            (static_cast<double>(end.x) - start.x) *
                (static_cast<double>(point.y) - start.y) -
            (static_cast<double>(end.y) - start.y) *
                (static_cast<double>(point.x) - start.x);
        positive = positive || cross > 0.0;
        negative = negative || cross < 0.0;
    }
    *contains = positive && negative ? 0U : 1U;
    return com::ok;
}

com::result rectangle_geometry::tessellate(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::array<progpu_native_direct2d_triangle, 2U>* triangles) const
    noexcept
{
    if (triangles == nullptr) {
        return com::pointer_error;
    }
    *triangles = {};
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result result = vertices(world_transform, points);
    if (com::failed(result)) {
        return result;
    }
    *triangles = {{
        {points[0U], points[1U], points[2U]},
        {points[0U], points[2U], points[3U]}}};
    return com::ok;
}

com::result rectangle_geometry::area(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = 0.0F;
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    const double result = std::abs(twice_area(points)) * 0.5;
    if (!std::isfinite(result) ||
        result > std::numeric_limits<float>::max()) {
        return com::invalid_argument;
    }
    *value = static_cast<float>(result);
    return com::ok;
}

com::result rectangle_geometry::length(
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* value) const noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = 0.0F;
    if (!valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    double result = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        result += std::hypot(
            static_cast<double>(end.x) - start.x,
            static_cast<double>(end.y) - start.y);
    }
    if (!std::isfinite(result) ||
        result > std::numeric_limits<float>::max()) {
        return com::invalid_argument;
    }
    *value = static_cast<float>(result);
    return com::ok;
}

com::result rectangle_geometry::point_at_length(
    float length_value,
    const progpu_native_direct2d_matrix_3x2_f* world_transform,
    float flattening_tolerance,
    progpu_native_direct2d_point_2f* point,
    progpu_native_direct2d_point_2f* unit_tangent) const noexcept
{
    if (point == nullptr && unit_tangent == nullptr) {
        return com::pointer_error;
    }
    if (point != nullptr) {
        *point = {};
    }
    if (unit_tangent != nullptr) {
        *unit_tangent = {};
    }
    if (!std::isfinite(length_value) || length_value < 0.0F ||
        !valid_tolerance(flattening_tolerance)) {
        return com::invalid_argument;
    }
    std::array<progpu_native_direct2d_point_2f, 4U> points{};
    const com::result status = vertices(world_transform, points);
    if (com::failed(status)) {
        return status;
    }
    std::array<double, 4U> edge_lengths{};
    double perimeter = 0.0;
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        edge_lengths[index] = std::hypot(
            static_cast<double>(end.x) - start.x,
            static_cast<double>(end.y) - start.y);
        perimeter += edge_lengths[index];
    }
    if (perimeter == 0.0) {
        if (point != nullptr) {
            *point = points[0U];
        }
        return com::ok;
    }
    double remaining = std::min(
        static_cast<double>(length_value), perimeter);
    for (std::size_t index = 0U; index < points.size(); ++index) {
        const auto& start = points[index];
        const auto& end = points[(index + 1U) % points.size()];
        const double edge_length = edge_lengths[index];
        if (edge_length == 0.0) {
            continue;
        }
        if (remaining <= edge_length || index + 1U == points.size()) {
            const double ratio = std::min(remaining / edge_length, 1.0);
            if (point != nullptr) {
                point->x = static_cast<float>(
                    start.x + (end.x - start.x) * ratio);
                point->y = static_cast<float>(
                    start.y + (end.y - start.y) * ratio);
            }
            if (unit_tangent != nullptr) {
                unit_tangent->x = static_cast<float>(
                    (end.x - start.x) / edge_length);
                unit_tangent->y = static_cast<float>(
                    (end.y - start.y) / edge_length);
            }
            return com::ok;
        }
        remaining -= edge_length;
    }
    return com::invalid_argument;
}

const rectangle_edges_f& rectangle_geometry::rectangle() const
    noexcept
{
    return rectangle_;
}

} // namespace progpu::native::direct2d::core
