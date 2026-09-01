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

bool valid_transform(
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

com::result compose_transform(
    const progpu_native_direct2d_matrix_3x2_f& first,
    const progpu_native_direct2d_matrix_3x2_f* second,
    progpu_native_direct2d_matrix_3x2_f* result) noexcept
{
    if (result == nullptr) {
        return com::pointer_error;
    }
    *result = {};
    if (!valid_transform(&first) || !valid_transform(second)) {
        return com::invalid_argument;
    }
    const progpu_native_direct2d_matrix_3x2_f identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const auto& right = second == nullptr ? identity : *second;
    const std::array<double, 6U> values{{
        static_cast<double>(first.m11) * right.m11 +
            static_cast<double>(first.m12) * right.m21,
        static_cast<double>(first.m11) * right.m12 +
            static_cast<double>(first.m12) * right.m22,
        static_cast<double>(first.m21) * right.m11 +
            static_cast<double>(first.m22) * right.m21,
        static_cast<double>(first.m21) * right.m12 +
            static_cast<double>(first.m22) * right.m22,
        static_cast<double>(first.m31) * right.m11 +
            static_cast<double>(first.m32) * right.m21 + right.m31,
        static_cast<double>(first.m31) * right.m12 +
            static_cast<double>(first.m32) * right.m22 + right.m32}};
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    if (!std::all_of(values.begin(), values.end(), [&](double value) {
            return std::isfinite(value) && value >= -maximum &&
                value <= maximum;
        })) {
        return com::invalid_argument;
    }
    *result = {
        static_cast<float>(values[0U]),
        static_cast<float>(values[1U]),
        static_cast<float>(values[2U]),
        static_cast<float>(values[3U]),
        static_cast<float>(values[4U]),
        static_cast<float>(values[5U])};
    return com::ok;
}

bool valid_arc_segment(const arc_segment_f& arc) noexcept
{
    return finite_point(arc.point) && std::isfinite(arc.size.width) &&
        std::isfinite(arc.size.height) && arc.size.width >= 0.0F &&
        arc.size.height >= 0.0F && std::isfinite(arc.rotation_angle) &&
        (arc.sweep == arc_sweep_direction::counter_clockwise ||
            arc.sweep == arc_sweep_direction::clockwise) &&
        (arc.size_kind == arc_size_kind::small_value ||
            arc.size_kind == arc_size_kind::large_value);
}

com::result arc_to_cubics(
    progpu_native_direct2d_point_2f start,
    const arc_segment_f& arc,
    std::array<cubic_bezier_segment_f, 4U>* cubics,
    std::uint32_t* cubic_count) noexcept
{
    if (cubics == nullptr || cubic_count == nullptr) {
        return com::pointer_error;
    }
    *cubics = {};
    *cubic_count = 0U;
    if (!finite_point(start) || !valid_arc_segment(arc)) {
        return com::invalid_argument;
    }
    if ((start.x == arc.point.x && start.y == arc.point.y) ||
        arc.size.width == 0.0F || arc.size.height == 0.0F) {
        return com::ok;
    }

    constexpr double pi = 3.141592653589793238462643383279502884;
    const double phi = std::remainder(
        static_cast<double>(arc.rotation_angle), 360.0) * pi / 180.0;
    const double cosine = std::cos(phi);
    const double sine = std::sin(phi);
    const double half_dx =
        (static_cast<double>(start.x) - arc.point.x) * 0.5;
    const double half_dy =
        (static_cast<double>(start.y) - arc.point.y) * 0.5;
    const double x1_prime = cosine * half_dx + sine * half_dy;
    const double y1_prime = -sine * half_dx + cosine * half_dy;
    double radius_x = std::abs(static_cast<double>(arc.size.width));
    double radius_y = std::abs(static_cast<double>(arc.size.height));
    double radius_x_squared = radius_x * radius_x;
    double radius_y_squared = radius_y * radius_y;
    const double scale =
        x1_prime * x1_prime / radius_x_squared +
        y1_prime * y1_prime / radius_y_squared;
    if (!std::isfinite(scale)) {
        return com::invalid_argument;
    }
    if (scale > 1.0) {
        const double factor = std::sqrt(scale);
        radius_x *= factor;
        radius_y *= factor;
        radius_x_squared = radius_x * radius_x;
        radius_y_squared = radius_y * radius_y;
    }

    const bool large = arc.size_kind == arc_size_kind::large_value;
    const bool clockwise =
        arc.sweep == arc_sweep_direction::clockwise;
    const double numerator = std::max(
        0.0,
        radius_x_squared * radius_y_squared -
            radius_x_squared * y1_prime * y1_prime -
            radius_y_squared * x1_prime * x1_prime);
    const double denominator =
        radius_x_squared * y1_prime * y1_prime +
        radius_y_squared * x1_prime * x1_prime;
    const double sign = large == clockwise ? -1.0 : 1.0;
    const double coefficient = denominator == 0.0
        ? 0.0
        : sign * std::sqrt(numerator / denominator);
    const double center_x_prime =
        coefficient * radius_x * y1_prime / radius_y;
    const double center_y_prime =
        -coefficient * radius_y * x1_prime / radius_x;
    const double center_x = cosine * center_x_prime -
        sine * center_y_prime +
        (static_cast<double>(start.x) + arc.point.x) * 0.5;
    const double center_y = sine * center_x_prime +
        cosine * center_y_prime +
        (static_cast<double>(start.y) + arc.point.y) * 0.5;

    const auto vector_angle = [](double ux, double uy, double vx, double vy) {
        return std::atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    };
    const double ux = (x1_prime - center_x_prime) / radius_x;
    const double uy = (y1_prime - center_y_prime) / radius_y;
    const double vx = (-x1_prime - center_x_prime) / radius_x;
    const double vy = (-y1_prime - center_y_prime) / radius_y;
    const double start_angle = std::atan2(uy, ux);
    double delta = vector_angle(ux, uy, vx, vy);
    if (!clockwise && delta > 0.0) {
        delta -= 2.0 * pi;
    } else if (clockwise && delta < 0.0) {
        delta += 2.0 * pi;
    }
    *cubic_count = static_cast<std::uint32_t>(std::clamp(
        std::ceil(std::abs(delta) / (pi * 0.5)),
        1.0,
        4.0));
    const double step = delta / *cubic_count;
    progpu_native_direct2d_point_2f current = start;
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    for (std::uint32_t index = 0U; index < *cubic_count; ++index) {
        const double angle0 = start_angle + step * index;
        const double angle1 = angle0 + step;
        const double alpha = 4.0 / 3.0 * std::tan(step * 0.25);
        const auto evaluate = [&](double angle) {
            const double local_x = radius_x * std::cos(angle);
            const double local_y = radius_y * std::sin(angle);
            return std::array<double, 2U>{
                center_x + cosine * local_x - sine * local_y,
                center_y + sine * local_x + cosine * local_y};
        };
        const auto derivative = [&](double angle) {
            const double local_x = -radius_x * std::sin(angle);
            const double local_y = radius_y * std::cos(angle);
            return std::array<double, 2U>{
                cosine * local_x - sine * local_y,
                sine * local_x + cosine * local_y};
        };
        const auto evaluated_end = evaluate(angle1);
        const std::array<double, 2U> end = index + 1U == *cubic_count
            ? std::array<double, 2U>{arc.point.x, arc.point.y}
            : evaluated_end;
        const auto tangent0 = derivative(angle0);
        const auto tangent1 = derivative(angle1);
        const std::array<std::array<double, 2U>, 3U> values{{
            {static_cast<double>(current.x) + alpha * tangent0[0U],
                static_cast<double>(current.y) + alpha * tangent0[1U]},
            {end[0U] - alpha * tangent1[0U],
                end[1U] - alpha * tangent1[1U]},
            end}};
        if (!std::all_of(values.begin(), values.end(), [&](const auto& point) {
                return std::isfinite(point[0U]) &&
                    std::isfinite(point[1U]) &&
                    std::abs(point[0U]) <= maximum &&
                    std::abs(point[1U]) <= maximum;
            })) {
            *cubics = {};
            *cubic_count = 0U;
            return com::invalid_argument;
        }
        (*cubics)[index] = {
            {static_cast<float>(values[0U][0U]),
                static_cast<float>(values[0U][1U])},
            {static_cast<float>(values[1U][0U]),
                static_cast<float>(values[1U][1U])},
            {static_cast<float>(values[2U][0U]),
                static_cast<float>(values[2U][1U])}};
        current = (*cubics)[index].point3;
    }
    return com::ok;
}

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
        !valid_transform(world_transform)) {
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
